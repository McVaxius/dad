using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutoParty.Contracts;
using AutoParty.Core.Authentication;
using dad.Models;

namespace dad.Services;

internal sealed record DadAutoPartyRelayPumpSnapshot(
    bool Running,
    string SafeCode,
    DateTimeOffset ObservedAt,
    DateTimeOffset? LastAuthenticatedInboundAt,
    int PendingOutboundCount,
    int AwaitingRelayReceiptCount,
    int PendingExecutionCount);

internal sealed record DadAutoPartyFormExecutionContext(
    ExecutionOperation Operation,
    DadExpectedPartyInviter? ExpectedInviter,
    IReadOnlyList<DadNativePartyInviteTarget> PartyInviteTargets);

internal sealed record DadAutoPartyTransientRouteSnapshot(
    string RequesterOwnerId,
    string RequesterIslandId,
    string SharingOwnerId,
    string SharingIslandId,
    string PolicyHash,
    DateTimeOffset ValidUntil);

internal sealed record DadAllianceCentralOperationContext(
    Guid OperationId,
    string SenderIslandId,
    DadAllianceRecruitmentInstructionDto? Instruction,
    DadAllianceRecruitmentCancellationDto? Cancellation);

internal sealed record DadAllianceCentralReceiptContext(
    Guid OperationId,
    DadAllianceRecruitmentInstructionDto Instruction,
    DadAllianceRecruitmentCancellationDto? Cancellation,
    DadAllianceRecruitmentResultDto Result);

internal readonly record struct DadAllianceCentralSendResult(
    bool Sent,
    Guid MessageId,
    string SafeCode);

internal sealed class DadAutoPartyRelayPump : IAsyncDisposable
{
    private const string RelayIsland = DadAutoPartyIdentityPackageService.RegistrationRecipient;
    private const int MaximumInboundPerCycle = 8;
    private const int MaximumOutboundPerCycle = 8;
    private const int MaximumPendingOutbound = 64;
    private const int MaximumAwaitingReceipts = 128;
    private const int MaximumPendingExecutions = 64;
    private const int MaximumReplayEntries = 4096;
    private static readonly TimeSpan CycleDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ControlLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ParticipantLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DispatchLease = TimeSpan.FromSeconds(30);

    private readonly object gate = new();
    private readonly DadAutoPartyConfiguration configuration;
    private readonly IDadAutoPartyEndpointIdentityStore identityStore;
    private readonly DadDiscordCourierConnector connector;
    private readonly DadAutoPartyService service;
    private readonly DadAutoPartyParticipantBridge participantBridge;
    private readonly IDadAutoPartyPendingOperationStore pendingOperationStore;
    private readonly DadAutoPartyInboundProposalService inboundProposalService;
    private readonly Func<DateTime, DadAutoPartyListingPublication>? inboundListingPublicationProvider;
    private readonly Func<RunProposal, DadAutoPartyInboundAdmissionResult>? inboundAdmission;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly Action<string> diagnostic;
    private readonly Queue<PendingOutboundContract> pendingOutbound = [];
    private readonly Dictionary<Guid, PendingOutboundContract> awaitingRelayReceipts = [];
    private readonly Dictionary<Guid, IAutoPartyContract> participantContracts = [];
    private readonly Queue<PendingExecution> pendingExecutions = [];
    private readonly Dictionary<Guid, IntegrationProfile> pendingProfiles = [];
    private readonly Dictionary<Guid, PendingDirectoryQuery> directoryQueries = [];
    private readonly Dictionary<Guid, PendingAccessRequest> pendingAccessRequests = [];
    private readonly Dictionary<RouteKey, AttestedRoute> attestedRoutes = [];
    private readonly Dictionary<Guid, PendingAllianceOutbound> pendingAllianceOutbound = [];
    private readonly Dictionary<Guid, AllianceRecruitmentOperation> pendingAllianceInbound = [];
    private readonly Dictionary<Guid, DateTimeOffset> replayedMessages = [];
    private readonly Queue<PendingInboundProposalEvaluation> pendingInboundProposalEvaluations = [];
    private readonly HashSet<Guid> pendingInboundProposalIds = [];
    private readonly HashSet<Guid> runtimeAdmissionValidatedProposalIds = [];
    private readonly Dictionary<InboundRuntimeTargetKey, InboundRuntimeTarget> inboundRuntimeTargets = [];
    private readonly CancellationTokenSource shutdown = new();
    private Func<RegistrationReceipt, DadAutoPartyPolicyDecision>? registrationReceiptHandler;
    private Func<DeregistrationReceipt, DadAutoPartyPendingDeregistration, CancellationToken,
        ValueTask<DadAutoPartyPrivacyResult>>? deregistrationReceiptHandler;
    private Func<DadAutoPartyFormExecutionContext, CancellationToken,
        ValueTask<DadAutoPartyExecutionResult>>? formExecutionHandler;
    private Action<DadAllianceCentralOperationContext>? allianceOperationHandler;
    private Action<DadAllianceCentralReceiptContext>? allianceReceiptHandler;
    private DadAutoPartySemanticKeyResolver? keyResolver;
    private ProductionContractAuthenticator? authenticator;
    private Task? pumpTask;
    private Task<DadAutoPartyExecutionResult>? activeExecution;
    private ExecutionOperation? activeExecutionOperation;
    private PendingExecution? activeExecutionPending;
    private string loadedIdentityReference = string.Empty;
    private long nextSequence = Math.Max(1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    private bool disposed;

    public DadAutoPartyRelayPump(
        DadAutoPartyConfiguration configuration,
        IDadAutoPartyEndpointIdentityStore identityStore,
        DadDiscordCourierConnector connector,
        DadAutoPartyService service,
        DadAutoPartyParticipantBridge participantBridge,
        IDadAutoPartyPendingOperationStore pendingOperationStore,
        IDadAutoPartyInboundProposalStore? inboundProposalStore = null,
        Func<DateTime, DadAutoPartyListingPublication>? inboundListingPublicationProvider = null,
        Func<RunProposal, DadAutoPartyInboundAdmissionResult>? inboundAdmission = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Action<string>? diagnostic = null)
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.identityStore = identityStore ?? throw new ArgumentNullException(nameof(identityStore));
        this.connector = connector ?? throw new ArgumentNullException(nameof(connector));
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        this.participantBridge = participantBridge ?? throw new ArgumentNullException(nameof(participantBridge));
        this.pendingOperationStore = pendingOperationStore ?? throw new ArgumentNullException(nameof(pendingOperationStore));
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        inboundProposalService = new(configuration, inboundProposalStore, this.utcNow);
        this.inboundListingPublicationProvider = inboundListingPublicationProvider;
        this.inboundAdmission = inboundAdmission;
        this.delay = delay ?? Task.Delay;
        this.diagnostic = diagnostic ?? (_ => { });
        snapshot = new(false, "dad-relay-pump-stopped", this.utcNow(), null, 0, 0, 0);
    }

    private DadAutoPartyRelayPumpSnapshot snapshot;

    public DadAutoPartyRelayPumpSnapshot Snapshot => Volatile.Read(ref snapshot);

    internal bool TryGetInboundRuntimeTarget(
        Guid proposalId,
        OpaqueCharacterId characterId,
        out string slotId,
        out DadNativePartyInviteTarget target,
        out string safeCode)
    {
        slotId = string.Empty;
        target = new DadNativePartyInviteTarget();
        safeCode = "dad-inbound-runtime-target-unavailable";
        if (proposalId == Guid.Empty ||
            !IsBoundedLocatorValue(characterId.Value, AutoPartyProtocol.MaximumIdentifierLength))
            return false;
        var now = utcNow();
        lock (gate)
        {
            var key = new InboundRuntimeTargetKey(proposalId, characterId.Value);
            if (!inboundRuntimeTargets.TryGetValue(key, out var retained) || retained.ExpiresAt <= now)
            {
                inboundRuntimeTargets.Remove(key);
                return false;
            }
            slotId = retained.SlotId;
            target = retained.Target.Clone();
            safeCode = "dad-inbound-runtime-target-ready";
            return true;
        }
    }

    internal bool TryGetInboundExecutionContext(
        Guid proposalId,
        OpaqueCharacterId characterId,
        out DadAutoPartyInboundExecutionContext context,
        out string safeCode)
    {
        context = null!;
        safeCode = "dad-inbound-runtime-target-unavailable";
        if (proposalId == Guid.Empty ||
            !IsBoundedLocatorValue(characterId.Value, AutoPartyProtocol.MaximumIdentifierLength))
            return false;
        var now = utcNow();
        lock (gate)
        {
            var key = new InboundRuntimeTargetKey(proposalId, characterId.Value);
            if (!inboundRuntimeTargets.TryGetValue(key, out var retained) || retained.ExpiresAt <= now)
            {
                inboundRuntimeTargets.Remove(key);
                return false;
            }
            context = new DadAutoPartyInboundExecutionContext(
                retained.ExecutionPlan,
                retained.Target.Clone(),
                retained.SenderIslandId,
                retained.OwnerId,
                retained.ExpiresAt,
                retained.FrozenInviter?.Clone(),
                retained.PartyInviteTargets?.Select(static target => target.Clone()).ToArray());
            safeCode = "dad-inbound-runtime-target-ready";
            return true;
        }
    }

    internal void RemoveInboundExecutionContext(Guid proposalId, OpaqueCharacterId characterId)
    {
        if (proposalId == Guid.Empty || string.IsNullOrWhiteSpace(characterId.Value))
            return;
        lock (gate)
            inboundRuntimeTargets.Remove(new InboundRuntimeTargetKey(proposalId, characterId.Value));
    }

    public DadAutoPartyPairingChallenge? LastPairingChallenge { get; private set; }

    internal IReadOnlyList<DadAutoPartyTransientRouteSnapshot> GetTransientRoutes()
    {
        var now = utcNow();
        lock (gate)
            return attestedRoutes
                .Where(pair => pair.Value.ValidUntil > now)
                .Select(static pair => new DadAutoPartyTransientRouteSnapshot(
                    pair.Value.RequesterOwnerId,
                    pair.Key.FirstIslandId,
                    pair.Value.SharingOwnerId,
                    pair.Key.SecondIslandId,
                    pair.Value.PolicyHash,
                    pair.Value.ValidUntil))
                .ToArray();
    }

    internal bool IsListingRouteCurrent(DadAutoPartyListing listing, DateTimeOffset? observedAt = null)
    {
        ArgumentNullException.ThrowIfNull(listing);
        return GetListingRouteExpiry(listing, observedAt ?? utcNow()) != null;
    }

    public void ConfigureLifecycleHandlers(
        Func<RegistrationReceipt, DadAutoPartyPolicyDecision> onRegistrationReceipt,
        Func<DeregistrationReceipt, DadAutoPartyPendingDeregistration, CancellationToken,
            ValueTask<DadAutoPartyPrivacyResult>> onDeregistrationReceipt)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        registrationReceiptHandler = onRegistrationReceipt ?? throw new ArgumentNullException(nameof(onRegistrationReceipt));
        deregistrationReceiptHandler = onDeregistrationReceipt ?? throw new ArgumentNullException(nameof(onDeregistrationReceipt));
    }

    public void ConfigureFormExecutionHandler(
        Func<DadAutoPartyFormExecutionContext, CancellationToken,
            ValueTask<DadAutoPartyExecutionResult>> handler)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        formExecutionHandler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    internal DadAutoPartyPolicyDecision QueueParticipantInviteLocator(
        RunProposal proposal,
        OpaqueCharacterId characterId,
        DadNativePartyInviteTarget target,
        long observedStateGeneration)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(target);
        var now = utcNow();
        var executionPlan = proposal.ExecutionPlan;
        if (!configuration.Enabled || !configuration.IsRegistrationActive ||
            observedStateGeneration < 1 || proposal.Header.ExpiresAt <= now ||
            !string.Equals(proposal.Header.RecipientIslandId.Value, configuration.RegisteredIslandId,
                StringComparison.Ordinal) ||
            !IsParticipantRouteAllowed(
                proposal.Header.SenderIslandId,
                proposal.RequesterOwnerId,
                proposal.Header.SenderKeyVersion,
                now) ||
            executionPlan == null ||
            !IsBoundedLocatorValue(characterId.Value, AutoPartyProtocol.MaximumIdentifierLength) ||
            !IsValidNativeInviteTarget(target, executionPlan.FormationOnly) ||
            !string.Equals(target.RunId, executionPlan.RunId, StringComparison.Ordinal) ||
            executionPlan.Participants.Count(participant =>
                string.Equals(participant.OwnerId.Value, configuration.RegisteredOwnerId, StringComparison.Ordinal) &&
                string.Equals(participant.OwnerIslandId.Value, configuration.RegisteredIslandId, StringComparison.Ordinal) &&
                string.Equals(participant.CharacterId.Value, characterId.Value, StringComparison.Ordinal) &&
                string.Equals(participant.SlotId, target.SlotId, StringComparison.OrdinalIgnoreCase)) != 1 ||
            (!executionPlan.FormationOnly && executionPlan.Modules.Count(module =>
                string.Equals(module.ModuleId, target.ModuleId.ToString(), StringComparison.Ordinal)) != 1))
            return Decision(false, "dad-participant-invite-locator-invalid");

        byte[]? encoded = null;
        try
        {
            encoded = JsonSerializer.SerializeToUtf8Bytes(new InviteLocatorPayload(
                target.RunId,
                target.WorkerSessionId.Value,
                target.AccountKey.Value,
                target.CharacterKey.Value,
                target.ContentId,
                target.CharacterName,
                target.WorldId,
                target.ModuleId.ToString(),
                target.SlotId));
            if (encoded.Length is <= 0 or > 1024)
                return Decision(false, "dad-participant-invite-locator-invalid");
            var messageId = Guid.NewGuid();
            var validUntil = Min(proposal.Header.ExpiresAt, now + ParticipantLifetime);
            var contract = new ParticipantInviteLocator(
                CreateHeader(
                    proposal.Header.SenderIslandId,
                    $"participant-invite-locator-{messageId:N}",
                    validUntil,
                    messageId,
                    observedStateGeneration),
                proposal.ProposalId,
                new OwnerId(configuration.RegisteredOwnerId),
                characterId,
                new InviteLocator(
                    $"participant-{messageId:N}",
                    new OwnerId(configuration.RegisteredOwnerId),
                    new IslandId(configuration.RegisteredIslandId),
                    validUntil,
                    ImmutableArray.CreateRange(encoded)),
                observedStateGeneration);
            lock (gate)
            {
                if (!TryEnqueueControl(contract))
                    return Decision(false, "dad-relay-outbound-full");
            }
            UpdateSnapshot("dad-participant-invite-locator-queued");
            return Decision(true, "dad-participant-invite-locator-queued");
        }
        catch (Exception exception) when (exception is ProtocolException or ArgumentException or
                                           InvalidOperationException or JsonException)
        {
            return Decision(false, "dad-participant-invite-locator-invalid");
        }
        finally
        {
            if (encoded != null)
                CryptographicOperations.ZeroMemory(encoded);
        }
    }

    public void ConfigureAllianceHandlers(
        Action<DadAllianceCentralOperationContext> onOperation,
        Action<DadAllianceCentralReceiptContext> onReceipt)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        allianceOperationHandler = onOperation ?? throw new ArgumentNullException(nameof(onOperation));
        allianceReceiptHandler = onReceipt ?? throw new ArgumentNullException(nameof(onReceipt));
    }

    public DadAllianceCentralSendResult QueueAllianceInstruction(
        DadAllianceRecruitmentInstructionDto instruction)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(instruction);
        if (!configuration.Enabled || !configuration.IsRegistrationActive ||
            authenticator == null || keyResolver == null)
            return new(false, Guid.Empty, "dad-alliance-central-not-ready");

        try
        {
            var operationId = Guid.NewGuid();
            var operation = DadAllianceAutoPartyContractMapping.ToRecruitOperation(
                instruction,
                CreateHeader(
                    new IslandId(instruction.TargetIslandId),
                    $"alliance-recruit-{operationId:N}",
                    utcNow() + ParticipantLifetime,
                    operationId),
                operationId);
            lock (gate)
            {
                TrimAllianceOutbound();
                if (!TryEnqueueControl(operation))
                    return new(false, Guid.Empty, "dad-relay-outbound-full");
                pendingAllianceOutbound[operationId] = new(
                    operation,
                    instruction.Clone(),
                    null);
            }
            UpdateSnapshot("dad-alliance-central-queued");
            return new(true, operation.Header.MessageId, "dad-alliance-central-queued");
        }
        catch (Exception exception) when (exception is ProtocolException or ArgumentException or
                                           InvalidOperationException or FormatException)
        {
            return new(false, Guid.Empty, "dad-alliance-central-invalid");
        }
    }

    public DadAllianceCentralSendResult QueueAllianceCancellation(
        DadAllianceRecruitmentCancellationDto cancellation,
        DadAllianceRecruitmentInstructionDto instruction)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(cancellation);
        ArgumentNullException.ThrowIfNull(instruction);
        if (!configuration.Enabled || !configuration.IsRegistrationActive ||
            authenticator == null || keyResolver == null)
            return new(false, Guid.Empty, "dad-alliance-central-not-ready");
        if (!string.Equals(cancellation.RecruitmentId, instruction.RecruitmentId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(cancellation.TargetIslandId, instruction.TargetIslandId, StringComparison.Ordinal) ||
            !string.Equals(cancellation.TargetOwnerId, instruction.TargetOwnerId, StringComparison.Ordinal) ||
            !string.Equals(
                cancellation.TargetOpaqueCharacterId,
                instruction.TargetOpaqueCharacterId,
                StringComparison.Ordinal))
            return new(false, Guid.Empty, "dad-alliance-central-cancellation-invalid");

        try
        {
            var operationId = Guid.NewGuid();
            var operation = DadAllianceAutoPartyContractMapping.ToCancelOperation(
                cancellation,
                CreateHeader(
                    new IslandId(cancellation.TargetIslandId),
                    $"alliance-cancel-{operationId:N}",
                    utcNow() + ParticipantLifetime,
                    operationId),
                operationId);
            lock (gate)
            {
                TrimAllianceOutbound();
                if (!TryEnqueueControl(operation))
                    return new(false, Guid.Empty, "dad-relay-outbound-full");
                pendingAllianceOutbound[operationId] = new(
                    operation,
                    instruction.Clone(),
                    CloneCancellation(cancellation));
            }
            UpdateSnapshot("dad-alliance-central-cancellation-queued");
            return new(true, operation.Header.MessageId, "dad-alliance-central-cancellation-queued");
        }
        catch (Exception exception) when (exception is ProtocolException or ArgumentException or
                                           InvalidOperationException or FormatException)
        {
            return new(false, Guid.Empty, "dad-alliance-central-cancellation-invalid");
        }
    }

    public DadAutoPartyPolicyDecision QueueAllianceReceipt(
        Guid operationId,
        DadAllianceRecruitmentResultDto result)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(result);
        AllianceRecruitmentOperation operation;
        lock (gate)
        {
            if (!pendingAllianceInbound.TryGetValue(operationId, out operation!))
                return Decision(false, "dad-alliance-central-operation-missing");
        }
        if (!string.Equals(result.RecruitmentId, operation.RecruitmentId.ToString("N"), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(result.ParticipantOwnerId, configuration.RegisteredOwnerId, StringComparison.Ordinal) ||
            !string.Equals(result.TargetOpaqueCharacterId, operation.TargetCharacterId.Value, StringComparison.Ordinal) ||
            result.StopGeneration != operation.StopGeneration)
            return Decision(false, "dad-alliance-central-receipt-mismatch");

        try
        {
            var messageId = DeriveGuid(operationId.ToString("N"), "alliance-receipt");
            var receipt = DadAllianceAutoPartyContractMapping.ToReceipt(
                result,
                CreateHeader(
                    operation.Header.SenderIslandId,
                    $"alliance-receipt-{operationId:N}",
                    Min(operation.Header.ExpiresAt, utcNow() + ParticipantLifetime),
                    messageId,
                    operation.Header.Generation),
                operationId);
            lock (gate)
            {
                if (!TryEnqueueControl(receipt))
                    return Decision(false, "dad-relay-outbound-full");
                pendingAllianceInbound.Remove(operationId);
            }
            UpdateSnapshot("dad-alliance-central-receipt-queued");
            return Decision(true, "dad-alliance-central-receipt-queued");
        }
        catch (Exception exception) when (exception is ProtocolException or ArgumentException or
                                           InvalidOperationException or FormatException)
        {
            return Decision(false, "dad-alliance-central-receipt-invalid");
        }
    }

    public void ForgetAllianceDeliveries(IEnumerable<Guid> messageIds)
    {
        var ids = (messageIds ?? []).Where(static id => id != Guid.Empty).ToHashSet();
        if (ids.Count == 0)
            return;
        lock (gate)
        {
            foreach (var operationId in pendingAllianceOutbound
                         .Where(pair => ids.Contains(pair.Value.Operation.Header.MessageId))
                         .Select(static pair => pair.Key)
                         .ToList())
                pendingAllianceOutbound.Remove(operationId);
            var retained = pendingOutbound
                .Where(item => !ids.Contains(item.Contract.Header.MessageId))
                .ToArray();
            pendingOutbound.Clear();
            foreach (var item in retained)
                pendingOutbound.Enqueue(item);
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (pumpTask != null)
            return;
        pumpTask = Task.Run(() => RunAsync(shutdown.Token));
    }

    public ValueTask<DadAutoPartyPolicyDecision> RequestDirectoryAsync(
        string searchText,
        bool includePromiscuous,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!configuration.IsRegistrationActive)
            return ValueTask.FromResult(Decision(false, "dad-directory-registration-not-active"));
        var normalized = (searchText ?? string.Empty).Trim();
        if (normalized.Length > AutoPartyProtocol.MaximumDisplayLabelLength ||
            normalized.Any(char.IsControl))
            return ValueTask.FromResult(Decision(false, "dad-directory-query-invalid"));

        var queryId = Guid.NewGuid();
        var query = new PendingDirectoryQuery(queryId, normalized, includePromiscuous, 32);
        lock (gate)
        {
            directoryQueries[queryId] = query;
            if (!TryEnqueueControl(BuildDirectoryQuery(query, string.Empty)))
            {
                directoryQueries.Remove(queryId);
                return ValueTask.FromResult(Decision(false, "dad-relay-outbound-full"));
            }
        }
        UpdateSnapshot("dad-directory-query-queued");
        return ValueTask.FromResult(Decision(true, "dad-directory-query-queued"));
    }

    public ValueTask<DadAutoPartyPolicyDecision> InitiatePairingAsync(
        string peerIslandId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var peer = DadAutoPartyConfiguration.NormalizeIdentifier(peerIslandId);
        if (!configuration.IsRegistrationActive || string.IsNullOrWhiteSpace(peer) ||
            string.Equals(peer, configuration.RegisteredIslandId, StringComparison.Ordinal))
            return ValueTask.FromResult(Decision(false, "dad-pairing-initiation-invalid"));

        var now = utcNow();
        var pairingId = Guid.NewGuid();
        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var codeHash = HashText(code);
        var transcript = HashText(
            $"dad.autoparty.pairing/v1|{pairingId:N}|{configuration.RegisteredIslandId}|{peer}|" +
            $"{configuration.RegistrationFingerprint}|{codeHash}");
        var expiresAt = now + TimeSpan.FromMinutes(10);
        PairingNotice notice;
        try
        {
            notice = new PairingNotice(
                CreateHeader(new IslandId(RelayIsland), $"pairing-notice-{pairingId:N}", expiresAt),
                pairingId,
                new OwnerId(configuration.RegisteredOwnerId),
                new IslandId(configuration.RegisteredIslandId),
                new IslandId(peer),
                configuration.HomeGuildScope,
                LocalPublicKeys(),
                configuration.RegistrationFingerprint,
                transcript,
                codeHash,
                expiresAt);
            ValidateOutbound(notice);
        }
        catch (Exception exception) when (exception is ProtocolException or ArgumentException or FormatException)
        {
            return ValueTask.FromResult(Decision(false, "dad-pairing-initiation-invalid"));
        }

        lock (gate)
        {
            if (!TryEnqueueControl(notice))
                return ValueTask.FromResult(Decision(false, "dad-relay-outbound-full"));
            LastPairingChallenge = new(
                pairingId,
                configuration.RegisteredOwnerId,
                configuration.RegisteredIslandId,
                configuration.RegistrationFingerprint,
                configuration.EndpointKeyGeneration,
                code,
                expiresAt.UtcDateTime);
        }
        UpdateSnapshot("dad-pairing-notice-queued");
        return ValueTask.FromResult(Decision(true, "dad-pairing-notice-queued"));
    }

    public DadAutoPartyPolicyDecision QueuePairingApproval(
        DadAutoPartyPairing pairing,
        DadAutoPartySharePolicy localSharePolicy,
        bool accepted)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        ArgumentNullException.ThrowIfNull(localSharePolicy);
        if (!configuration.IsRegistrationActive || !pairing.IsValid || !pairing.LocalApproved ||
            !Guid.TryParse(pairing.PairingId, out var pairingId) ||
            !Guid.TryParse(pairing.LocalApprovalRelayMessageId, out var messageId) ||
            messageId != DadAutoPartyService.DerivePairingApprovalMessageId(
                pairingId,
                configuration.RegisteredIslandId) ||
            localSharePolicy.Clone().Normalize() is not { IsValid: true } policy ||
            !DadAutoPartyService.SamePolicy(pairing.LocalSharePolicy, policy))
            return Decision(false, "dad-pairing-approval-invalid");
        PairingApproval approval;
        try
        {
            approval = new PairingApproval(
                CreateHeader(
                    new IslandId(RelayIsland),
                    $"pairing-approval-{pairing.PairingId}",
                    utcNow() + ControlLifetime,
                    messageId),
                pairingId,
                new IslandId(configuration.RegisteredIslandId),
                new IslandId(pairing.IslandId),
                pairing.TranscriptHash,
                pairing.ConfirmationCodeHash,
                configuration.RegistrationFingerprint,
                pairing.PublicKeyFingerprint,
                ToProtocolPolicy(policy),
                accepted);
            ValidateOutbound(approval);
        }
        catch (Exception exception) when (exception is ProtocolException or ArgumentException or FormatException)
        {
            return Decision(false, "dad-pairing-approval-invalid");
        }
        lock (gate)
        {
            if (pendingOutbound.Any(item => item.Contract.Header.MessageId == messageId) ||
                awaitingRelayReceipts.ContainsKey(messageId))
                return Decision(true, "dad-pairing-approval-already-queued");
            if (!TryEnqueueControl(approval))
                return Decision(false, "dad-relay-outbound-full");
        }
        UpdateSnapshot("dad-pairing-approval-queued");
        return Decision(true, "dad-pairing-approval-queued");
    }

    public DadAutoPartyPolicyDecision QueueListingUpdate(
        DadAutoPartySharePolicy sharePolicy,
        IEnumerable<DadAutoPartyListing> listings)
    {
        ArgumentNullException.ThrowIfNull(sharePolicy);
        if (!configuration.IsRegistrationActive ||
            sharePolicy.Clone().Normalize() is not { IsValid: true } policy)
            return Decision(false, "dad-listing-update-invalid");
        var now = utcNow();
        var protocolListings = (listings ?? [])
            .Where(listing => listing is { IsValid: true } &&
                              listing.ExpiresAtUtc > now.UtcDateTime &&
                              listing.ExpiresAtUtc <= now.UtcDateTime + TimeSpan.FromHours(24))
            .Take(AutoPartyProtocol.MaximumCollectionItems)
            .Select(static listing => new PrivateCharacterListing(
                new OpaqueCharacterId(listing.OpaqueCharacterId),
                listing.DisplayLabel,
                listing.AllowedJobIds.Select(static value => new JobId(value)).ToImmutableArray(),
                listing.AllowedActivityIds.Select(static value => new ActivityId(value)).ToImmutableArray(),
                listing.Available,
                listing.Revision,
                new DateTimeOffset(DateTime.SpecifyKind(listing.ExpiresAtUtc, DateTimeKind.Utc))))
            .ToImmutableArray();
        try
        {
            var updateId = Guid.NewGuid();
            var update = new PrivateListingUpdate(
                CreateHeader(new IslandId(RelayIsland), $"listing-update-{updateId:N}", now + ControlLifetime),
                updateId,
                new IslandId(configuration.RegisteredIslandId),
                ToProtocolPolicy(policy),
                protocolListings,
                Math.Max(1, configuration.StateGeneration));
            ValidateOutbound(update);
            lock (gate)
            {
                if (!TryEnqueueControl(update))
                    return Decision(false, "dad-relay-outbound-full");
            }
        }
        catch (Exception exception) when (exception is ProtocolException or ArgumentException)
        {
            return Decision(false, "dad-listing-update-invalid");
        }
        UpdateSnapshot("dad-listing-update-queued");
        return Decision(true, "dad-listing-update-queued");
    }

    public DadAutoPartyPolicyDecision RequestPromiscuousAccess(
        string sharingIslandId,
        IEnumerable<string> requestedCharacterHandles,
        string requestedPolicyHash)
    {
        var sharing = DadAutoPartyConfiguration.NormalizeIdentifier(sharingIslandId);
        var policyHash = DadAutoPartyConfiguration.NormalizeIdentifier(requestedPolicyHash);
        var handles = (requestedCharacterHandles ?? [])
            .Select(DadAutoPartyConfiguration.NormalizeIdentifier)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Take(AutoPartyProtocol.MaximumCollectionItems)
            .Select(static value => new OpaqueCharacterId(value))
            .ToImmutableArray();
        var matchingListings = configuration.Listings
            .Where(listing =>
                listing is { IsValid: true, Available: true } &&
                listing.EffectiveShareMode == DadAutoPartyCharacterShareMode.CharacterList &&
                string.Equals(listing.SharingIslandId, sharing, StringComparison.Ordinal) &&
                string.Equals(listing.EffectivePolicyHash, policyHash, StringComparison.Ordinal) &&
                handles.Any(handle => string.Equals(
                    handle.Value,
                    listing.OpaqueCharacterId,
                    StringComparison.Ordinal)))
            .ToList();
        var owners = matchingListings
            .Select(static listing => listing.OwnerId)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToList();
        var coveredHandleCount = matchingListings
            .Select(static listing => listing.OpaqueCharacterId)
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (!configuration.IsRegistrationActive || string.IsNullOrWhiteSpace(sharing) ||
            string.Equals(sharing, configuration.RegisteredIslandId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(policyHash) || handles.IsDefaultOrEmpty || owners.Count != 1 ||
            coveredHandleCount != handles.Length)
            return Decision(false, "dad-promiscuous-request-invalid");
        try
        {
            var requestId = Guid.NewGuid();
            var request = new RegisteredRequesterAccessRequest(
                CreateHeader(new IslandId(RelayIsland), $"access-request-{requestId:N}", utcNow() + ControlLifetime),
                requestId,
                new IslandId(sharing),
                handles,
                policyHash);
            ValidateOutbound(request);
            lock (gate)
            {
                pendingAccessRequests[requestId] = new(
                    requestId,
                    true,
                    configuration.RegisteredOwnerId,
                    owners[0],
                    sharing,
                    request.Header.Generation,
                    handles.Select(static item => item.Value).ToImmutableArray(),
                    policyHash,
                    request.Header.ExpiresAt);
                if (!TryEnqueueControl(request))
                {
                    pendingAccessRequests.Remove(requestId);
                    return Decision(false, "dad-relay-outbound-full");
                }
            }
        }
        catch (Exception exception) when (exception is ProtocolException or ArgumentException)
        {
            return Decision(false, "dad-promiscuous-request-invalid");
        }
        UpdateSnapshot("dad-promiscuous-request-queued");
        return Decision(true, "dad-promiscuous-request-queued");
    }

    public DadAutoPartyPolicyDecision QueueDeauthentication(
        DadAutoPartyPairing pairing,
        long revocationGeneration,
        string safeReason)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        var reason = DadAutoPartyConfiguration.NormalizeSafeCode(safeReason);
        if (!configuration.HasImportedBootstrap || !pairing.IsValid || revocationGeneration < 1 ||
            string.IsNullOrWhiteSpace(reason))
            return Decision(false, "dad-deauthentication-request-invalid");
        try
        {
            var noticeId = Guid.NewGuid();
            var notice = new DeauthenticationNotice(
                CreateHeader(new IslandId(RelayIsland), $"deauthentication-{noticeId:N}", utcNow() + ControlLifetime),
                noticeId,
                new IslandId(pairing.IslandId),
                pairing.TranscriptHash,
                revocationGeneration,
                reason);
            ValidateOutbound(notice);
            lock (gate)
            {
                if (!TryEnqueueControl(notice))
                    return Decision(false, "dad-relay-outbound-full");
            }
        }
        catch (Exception exception) when (exception is ProtocolException or ArgumentException)
        {
            return Decision(false, "dad-deauthentication-request-invalid");
        }
        UpdateSnapshot("dad-deauthentication-queued");
        return Decision(true, "dad-deauthentication-queued");
    }

    public DadAutoPartyPolicyDecision Deauthenticate(string peerIslandId, string safeReason)
    {
        var islandId = DadAutoPartyConfiguration.NormalizeIdentifier(peerIslandId);
        var reason = DadAutoPartyConfiguration.NormalizeSafeCode(safeReason) is { Length: > 0 } normalized
            ? normalized
            : "dad-owner-deauthenticated";
        if (string.IsNullOrWhiteSpace(islandId))
            return Decision(false, "dad-deauthentication-request-invalid");

        var pairing = configuration.Pairings.FirstOrDefault(item =>
            item.IsActive && string.Equals(item.IslandId, islandId, StringComparison.Ordinal))?.Clone();
        var local = service.Deauthenticate(islandId, reason);
        if (!local.Allowed)
            return local;

        var generation = Math.Max(1, configuration.RevocationGeneration);
        participantBridge.DeauthenticateIsland(islandId, generation, reason, utcNow());
        inboundProposalService.RemoveSender(islandId);
        RemoveInboundRuntimeTargets((_, target) =>
            string.Equals(target.SenderIslandId, islandId, StringComparison.Ordinal));
        RemoveTransientRoutes((route, _) =>
            string.Equals(route.FirstIslandId, islandId, StringComparison.Ordinal) ||
            string.Equals(route.SecondIslandId, islandId, StringComparison.Ordinal));

        if (pairing == null)
            return local;
        var propagated = QueueDeauthentication(pairing, generation, reason);
        return propagated.Allowed
            ? Decision(true, "dad-deauthentication-applied")
            : Decision(true, "dad-deauthentication-applied-relay-pending");
    }

    public DadAutoPartyPolicyDecision BeginDeregistration(
        bool deleteEndpointIdentity,
        string safeReason = "dad-owner-deregistered")
    {
        var reason = DadAutoPartyConfiguration.NormalizeSafeCode(safeReason);
        if (!configuration.HasImportedBootstrap || string.IsNullOrWhiteSpace(reason))
            return Decision(false, "dad-deregister-not-registered");
        var existing = pendingOperationStore.LoadDeregistration();
        var pending = existing ?? new DadAutoPartyPendingDeregistration(
            Guid.NewGuid(),
            Math.Max(1, configuration.RevocationGeneration + 1),
            reason,
            utcNow(),
            deleteEndpointIdentity);
        try
        {
            pendingOperationStore.SaveDeregistration(pending);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Decision(false, "dad-deregister-pending-store-failed");
        }
        service.StopAll("dad-deregister-pending");
        lock (gate)
        {
            if (!pendingOutbound.Any(item => item.Contract is DeregistrationRequest request &&
                    request.DeregistrationId == pending.DeregistrationId) &&
                !TryEnqueueControl(BuildDeregistrationRequest(pending)))
                return Decision(false, "dad-relay-outbound-full");
        }
        UpdateSnapshot("dad-deregister-request-queued");
        return Decision(true, "dad-deregister-request-queued");
    }

    public void UpdateFramework()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        PrepareInboundResponsesFramework();
        ObserveActiveExecution();
        if (activeExecution != null)
            return;
        PendingExecution? pending;
        lock (gate)
            pending = pendingExecutions.Count > 0 ? pendingExecutions.Dequeue() : null;
        if (pending == null)
            return;
        var operation = pending.Operation;
        activeExecutionOperation = operation;
        activeExecutionPending = pending;
        try
        {
            var execution = ExecuteAsync(pending);
            activeExecution = execution.IsCompletedSuccessfully
                ? Task.FromResult(execution.Result)
                : execution.AsTask();
        }
        catch (Exception)
        {
            diagnostic("dad-relay-execution-dispatch-failed");
            activeExecution = Task.FromResult(new DadAutoPartyExecutionResult(
                operation.OperationId,
                operation.ProposalId,
                operation.Kind,
                ExecutionOutcome.Denied,
                DadRunPhase.Idle,
                "dad-relay-execution-dispatch-failed",
                operation.ExpectedStateGeneration));
        }
        UpdateSnapshot("dad-relay-execution-dispatched");
    }

    internal async ValueTask ProcessOnceAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ExpireState();
        if (!configuration.HasImportedBootstrap)
        {
            ResetSecurity();
            UpdateSnapshot("dad-relay-not-registered");
            return;
        }
        if (!await EnsureSecurityAsync(cancellationToken).ConfigureAwait(false))
        {
            UpdateSnapshot("dad-relay-identity-unavailable");
            return;
        }
        keyResolver!.RefreshPublicKeys();
        EnsureRegistrationHelloQueued();
        EnsureDeregistrationQueued();
        EnsurePairingApprovalsQueued();
        EnsureInboundResponsesQueued();
        await ReceiveBoundedAsync(cancellationToken).ConfigureAwait(false);
        ProcessInboundProposalEvaluations();
        EnsureInboundResponsesQueued();
        await SendControlBoundedAsync(cancellationToken).ConfigureAwait(false);
        await SendParticipantCommandsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;
        shutdown.Cancel();
        UpdateSnapshot("dad-relay-pump-stopping", running: false);
        if (pumpTask != null)
        {
            try
            {
                await pumpTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        ResetSecurity();
        shutdown.Dispose();
        LastPairingChallenge = null;
        UpdateSnapshot("dad-relay-pump-disposed", running: false);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (disposed || cancellationToken.IsCancellationRequested)
                return;
            UpdateSnapshot("dad-relay-pump-running", running: true);
        }
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ProcessOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                diagnostic("dad-relay-pump-cycle-failed");
                UpdateSnapshot("dad-relay-pump-cycle-failed");
            }
            await delay(CycleDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<bool> EnsureSecurityAsync(CancellationToken cancellationToken)
    {
        if (authenticator != null &&
            string.Equals(loadedIdentityReference, configuration.EndpointIdentityReference, StringComparison.Ordinal))
            return true;
        ResetSecurity();
        byte[]? identityMaterial = null;
        try
        {
            identityMaterial = await identityStore.LoadAsync(
                configuration.EndpointIdentityReference,
                cancellationToken).ConfigureAwait(false);
            var identity = JsonSerializer.Deserialize<DadAutoPartyPrivateIdentityPackage>(identityMaterial);
            if (identity == null)
                return false;
            keyResolver = new DadAutoPartySemanticKeyResolver(configuration, identity);
            authenticator = new ProductionContractAuthenticator(keyResolver);
            loadedIdentityReference = configuration.EndpointIdentityReference;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           JsonException or FormatException or InvalidOperationException or
                                           CryptographicException or ArgumentException)
        {
            ResetSecurity();
            return false;
        }
        finally
        {
            if (identityMaterial != null)
                CryptographicOperations.ZeroMemory(identityMaterial);
        }
    }

    private void ResetSecurity()
    {
        authenticator = null;
        RemoveTransientRoutes(static (_, _) => true);
        lock (gate)
        {
            var retainedOutbound = pendingOutbound
                .Where(static item => item.Contract is not AllianceRecruitmentOperation and
                                      not AllianceRecruitmentReceipt)
                .ToArray();
            pendingOutbound.Clear();
            foreach (var item in retainedOutbound)
                pendingOutbound.Enqueue(item);
            foreach (var messageId in awaitingRelayReceipts
                         .Where(static pair => pair.Value.Contract is AllianceRecruitmentOperation or
                                              AllianceRecruitmentReceipt)
                         .Select(static pair => pair.Key)
                         .ToList())
                awaitingRelayReceipts.Remove(messageId);
            pendingAllianceOutbound.Clear();
            pendingAllianceInbound.Clear();
            inboundRuntimeTargets.Clear();
        }
        keyResolver?.Dispose();
        keyResolver = null;
        loadedIdentityReference = string.Empty;
    }

    private void EnsureRegistrationHelloQueued()
    {
        if (configuration.RegistrationState != DadAutoPartyRegistrationState.BootstrapImported ||
            !Guid.TryParse(configuration.RegistrationId, out var registrationId) ||
            !Guid.TryParse(configuration.UplinkEpochId, out var uplinkEpochId))
            return;
        lock (gate)
        {
            if (pendingOutbound.Any(static item => item.Contract is RegistrationHello) ||
                awaitingRelayReceipts.Values.Any(static item => item.Contract is RegistrationHello))
                return;
            var hello = new RegistrationHello(
                CreateHeader(new IslandId(RelayIsland), $"registration-hello-{registrationId:N}", utcNow() + ControlLifetime),
                registrationId,
                configuration.RouteId,
                uplinkEpochId,
                configuration.MailboxEpochGeneration);
            _ = TryEnqueueControl(hello);
        }
    }

    private void EnsureDeregistrationQueued()
    {
        var pending = pendingOperationStore.LoadDeregistration();
        if (pending == null || !configuration.HasImportedBootstrap)
            return;
        lock (gate)
        {
            if (pendingOutbound.Any(item => item.Contract is DeregistrationRequest request &&
                    request.DeregistrationId == pending.DeregistrationId) ||
                awaitingRelayReceipts.Values.Any(item => item.Contract is DeregistrationRequest request &&
                    request.DeregistrationId == pending.DeregistrationId))
                return;
            _ = TryEnqueueControl(BuildDeregistrationRequest(pending));
        }
    }

    private void EnsurePairingApprovalsQueued()
    {
        var now = utcNow().UtcDateTime;
        foreach (var pairing in configuration.PendingPairings.Where(item =>
                     item.LocalApproved && item.LocalApprovalRelayAcceptedAtUtc == null &&
                     item.ExpiresAtUtc > now).ToArray())
            _ = QueuePairingApproval(pairing, pairing.LocalSharePolicy, accepted: true);
    }

    private void PrepareInboundResponsesFramework()
    {
        var active = inboundProposalService.Active(MaximumInboundPerCycle);
        if (active.Count == 0 || inboundListingPublicationProvider == null)
            return;

        DadAutoPartyListingPublication publication;
        var now = utcNow();
        try
        {
            publication = inboundListingPublicationProvider(now.UtcDateTime);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or FormatException)
        {
            diagnostic("dad-inbound-listing-snapshot-invalid");
            return;
        }

        foreach (var state in active)
        {
            lock (gate)
            {
                if (state.AdmissionReady && runtimeAdmissionValidatedProposalIds.Contains(state.Proposal.ProposalId))
                    continue;
                if (pendingInboundProposalIds.Contains(state.Proposal.ProposalId))
                    continue;
                if (pendingInboundProposalEvaluations.Count >= MaximumPendingExecutions)
                    return;
            }

            var authorized = TryValidateInboundPublication(state, publication, now, out var safeCode);
            var admission = DadAutoPartyInboundAdmissionResult.Blocked(
                state.Proposal.ExecutionPlan?.RunId ?? string.Empty,
                authorized ? "dad-inbound-execution-admission-not-wired" : safeCode);
            if (authorized && inboundAdmission != null)
            {
                try
                {
                    admission = inboundAdmission(state.Proposal) ??
                                DadAutoPartyInboundAdmissionResult.Blocked(
                                    state.Proposal.ExecutionPlan?.RunId ?? string.Empty,
                                    DadAutoPartyInboundAdmissionService.InvalidProposal);
                }
                catch
                {
                    admission = DadAutoPartyInboundAdmissionResult.Blocked(
                        state.Proposal.ExecutionPlan?.RunId ?? string.Empty,
                        "dad-inbound-admission-runtime-failed");
                }
            }
            var evaluation = new PendingInboundProposalEvaluation(
                state.Proposal.ProposalId,
                Math.Max(1, configuration.StateGeneration),
                authorized,
                authorized ? admission.SafeBlocker : safeCode,
                admission);
            lock (gate)
            {
                if (pendingInboundProposalIds.Add(state.Proposal.ProposalId))
                    pendingInboundProposalEvaluations.Enqueue(evaluation);
            }
        }
    }

    private bool TryValidateInboundPublication(
        DadAutoPartyInboundProposalState state,
        DadAutoPartyListingPublication publication,
        DateTimeOffset now,
        out string safeCode)
    {
        var proposal = state.Proposal;
        var pairings = configuration.Pairings.Where(pairing =>
                pairing.IsActive &&
                string.Equals(pairing.IslandId, proposal.Header.SenderIslandId.Value, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (pairings.Length > 1)
        {
            safeCode = "dad-inbound-pairing-ambiguous";
            return false;
        }
        var paired = pairings.Length == 1;
        if (paired && !string.Equals(
                pairings[0].OwnerId,
                proposal.RequesterOwnerId.Value,
                StringComparison.Ordinal))
        {
            safeCode = "dad-inbound-sharing-route-denied";
            return false;
        }
        var policy = paired ? pairings[0].LocalSharePolicy : publication.StandingPolicy;
        if (!paired && (policy.Mode != DadAutoPartyCharacterShareMode.CharacterList ||
                        !IsDirectSenderAllowed(
                            proposal.Header.SenderIslandId,
                            proposal.Header.SenderKeyVersion,
                            now)))
        {
            safeCode = "dad-inbound-sharing-route-denied";
            return false;
        }

        foreach (var participant in state.OwnedParticipants)
        {
            var listings = publication.Listings.Where(listing =>
                    string.Equals(listing.OwnerId, configuration.RegisteredOwnerId, StringComparison.Ordinal) &&
                    string.Equals(listing.SharingIslandId, configuration.RegisteredIslandId, StringComparison.Ordinal) &&
                    string.Equals(listing.OpaqueCharacterId, participant.CharacterId.Value, StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            if (listings.Length != 1)
            {
                safeCode = "dad-inbound-listing-route-ambiguous";
                return false;
            }
            var listing = listings[0];
            if (!listing.Available || listing.ExpiresAtUtc <= now.UtcDateTime ||
                !listing.AllowedJobIds.Contains(participant.RequestedJob.Value, StringComparer.Ordinal) ||
                !listing.AllowedActivityIds.Contains(proposal.ActivityId.Value, StringComparer.Ordinal) ||
                !DadAutoPartyShareRules.Allows(
                    policy,
                    participant.CharacterId.Value,
                    paired,
                    sameHomeGuild: !paired))
            {
                safeCode = "dad-inbound-listing-policy-denied";
                return false;
            }
        }

        safeCode = "dad-inbound-listing-authorized";
        return true;
    }

    private void ProcessInboundProposalEvaluations()
    {
        for (var processed = 0; processed < MaximumInboundPerCycle; processed++)
        {
            PendingInboundProposalEvaluation evaluation;
            lock (gate)
            {
                if (pendingInboundProposalEvaluations.Count == 0)
                    return;
                evaluation = pendingInboundProposalEvaluations.Dequeue();
                pendingInboundProposalIds.Remove(evaluation.ProposalId);
            }

            if (!evaluation.Allowed)
            {
                inboundProposalService.Remove(evaluation.ProposalId);
                diagnostic(evaluation.SafeCode);
                continue;
            }
            if (evaluation.ConfigurationGeneration != Math.Max(1, configuration.StateGeneration) ||
                !inboundProposalService.TryGetActive(evaluation.ProposalId, out var state))
                continue;
            var now = utcNow();
            if (!IsDirectSenderAllowed(
                    state.Proposal.Header.SenderIslandId,
                    state.Proposal.Header.SenderKeyVersion,
                    now))
            {
                inboundProposalService.Remove(evaluation.ProposalId);
                diagnostic("dad-inbound-sharing-route-expired");
                continue;
            }

            var admission = evaluation.Admission;
            var responseCount = state.OwnedParticipants.Length + 1 +
                                (admission.Ready ? 1 + admission.InviteTargets.Length : 0);
            lock (gate)
            {
                if (pendingOutbound.Count + responseCount > MaximumPendingOutbound)
                {
                    if (pendingInboundProposalIds.Add(evaluation.ProposalId))
                        pendingInboundProposalEvaluations.Enqueue(evaluation);
                    return;
                }
            }

            var requiredPermissions = SessionPermission.Reserve | SessionPermission.Preflight |
                                      SessionPermission.FormParty | SessionPermission.Cancel |
                                      SessionPermission.Complete;
            if (state.Proposal.ExecutionPlan is { FormationOnly: false })
                requiredPermissions |= SessionPermission.Queue | SessionPermission.Execute;

            if (state.AdmissionReady)
            {
                var restored = service.RestoreOwnedProposalSession(state, requiredPermissions);
                var locatorSafeCode = DadAutoPartyInboundAdmissionService.InvalidProposal;
                if (!restored.Allowed || !admission.Ready || state.Lease == null ||
                    restored.StateGeneration != state.Lease.ObservedStateGeneration ||
                    !TryQueueInboundInviteLocators(
                        state,
                        admission,
                        restored.StateGeneration,
                        out locatorSafeCode))
                {
                    diagnostic(!restored.Allowed
                        ? restored.SafeCode
                        : admission.Ready ? locatorSafeCode : admission.SafeBlocker);
                    continue;
                }
                lock (gate)
                    runtimeAdmissionValidatedProposalIds.Add(evaluation.ProposalId);
                continue;
            }

            var accepted = service.AcceptOwnedProposal(
                state.Proposal,
                state.OwnedParticipants,
                requiredPermissions);
            if (!accepted.Allowed)
            {
                inboundProposalService.Remove(evaluation.ProposalId);
                diagnostic(accepted.SafeCode);
                continue;
            }

            var firstParticipant = state.OwnedParticipants[0];
            var reserved = service.Reserve(
                new Reservation(
                    state.Proposal.Header,
                    DeriveGuid(evaluation.ProposalId.ToString("N"), "policy-reservation"),
                    evaluation.ProposalId,
                    new OwnerId(configuration.RegisteredOwnerId),
                    firstParticipant.CharacterId,
                    accepted.StateGeneration,
                    accepted.StateGeneration),
                DadAutoPartySessionMode.MultiOwner);
            if (!reserved.Allowed)
            {
                inboundProposalService.Remove(evaluation.ProposalId);
                diagnostic(reserved.SafeCode);
                continue;
            }

            var responseExpiry = Min(state.Proposal.Header.ExpiresAt, now + ParticipantLifetime);
            IReadOnlyList<Reservation> reservations = state.ResponsesPrepared
                ? state.Reservations
                : state.OwnedParticipants.Select((participant, index) =>
                {
                    var routeKey = $"{participant.CharacterId.Value}:{participant.RequestedJob.Value}";
                    var messageId = DeriveGuid(
                        evaluation.ProposalId.ToString("N"),
                        $"reservation-message:{routeKey}");
                    return new Reservation(
                        CreateHeader(
                            state.Proposal.Header.SenderIslandId,
                            $"inbound-reservation-{evaluation.ProposalId:N}-{index}",
                            responseExpiry,
                            messageId,
                            reserved.StateGeneration),
                        DeriveGuid(evaluation.ProposalId.ToString("N"), $"reservation:{routeKey}"),
                        evaluation.ProposalId,
                        new OwnerId(configuration.RegisteredOwnerId),
                        participant.CharacterId,
                        accepted.StateGeneration,
                        reserved.StateGeneration);
                }).ToArray();

            PreflightResult preflight;
            SessionLease? lease = null;
            long responseStateGeneration;
            string responseSafeCode;
            if (admission.Ready)
            {
                var expectedGeneration = state.ResponsesPrepared
                    ? state.StateGeneration
                    : reserved.StateGeneration;
                var preflightMessageId = DeriveGuid(
                    evaluation.ProposalId.ToString("N"),
                    $"ready-preflight-message:{expectedGeneration}");
                var candidate = new PreflightResult(
                    CreateHeader(
                        state.Proposal.Header.SenderIslandId,
                        $"inbound-ready-preflight-{evaluation.ProposalId:N}",
                        responseExpiry,
                        preflightMessageId,
                        expectedGeneration),
                    evaluation.ProposalId,
                    new OwnerId(configuration.RegisteredOwnerId),
                    Ready: true,
                    ReadinessGeneration: evaluation.ConfigurationGeneration,
                    ExpectedStateGeneration: expectedGeneration,
                    SafeBlockers: [],
                    ObservedStateGeneration: expectedGeneration);
                var verified = service.VerifyPreflight(candidate);
                if (!verified.Allowed)
                {
                    diagnostic(verified.SafeCode);
                    continue;
                }
                preflight = candidate with { ObservedStateGeneration = verified.StateGeneration };

                var leaseMessageId = DeriveGuid(
                    evaluation.ProposalId.ToString("N"),
                    $"lease-message:{verified.StateGeneration}");
                var leaseExpiry = Min(
                    responseExpiry,
                    now + TimeSpan.FromSeconds(state.Proposal.ExecutionPlan!.LeaseDurationSeconds));
                var leaseCandidate = new SessionLease(
                    CreateHeader(
                        state.Proposal.Header.SenderIslandId,
                        $"inbound-lease-{evaluation.ProposalId:N}",
                        responseExpiry,
                        leaseMessageId,
                        verified.StateGeneration),
                    DeriveGuid(evaluation.ProposalId.ToString("N"), $"lease:{verified.StateGeneration}"),
                    evaluation.ProposalId,
                    new OwnerId(configuration.RegisteredOwnerId),
                    leaseExpiry,
                    requiredPermissions,
                    verified.StateGeneration,
                    verified.StateGeneration);
                var acquired = service.AcquireLease(leaseCandidate);
                if (!acquired.Allowed)
                {
                    diagnostic(acquired.SafeCode);
                    continue;
                }
                lease = leaseCandidate with { ObservedStateGeneration = acquired.StateGeneration };
                responseStateGeneration = verified.StateGeneration;
                responseSafeCode = "dad-inbound-admission-ready";
            }
            else
            {
                if (state.ResponsesPrepared)
                {
                    diagnostic(admission.SafeBlocker);
                    continue;
                }
                var preflightMessageId = DeriveGuid(evaluation.ProposalId.ToString("N"), "preflight-message");
                preflight = new PreflightResult(
                    CreateHeader(
                        state.Proposal.Header.SenderIslandId,
                        $"inbound-preflight-{evaluation.ProposalId:N}",
                        responseExpiry,
                        preflightMessageId,
                        reserved.StateGeneration),
                    evaluation.ProposalId,
                    new OwnerId(configuration.RegisteredOwnerId),
                    Ready: false,
                    ReadinessGeneration: evaluation.ConfigurationGeneration,
                    ExpectedStateGeneration: reserved.StateGeneration,
                    SafeBlockers: [admission.SafeBlocker],
                    ObservedStateGeneration: reserved.StateGeneration);
                responseStateGeneration = reserved.StateGeneration;
                responseSafeCode = admission.SafeBlocker;
            }
            if (!inboundProposalService.TryPrepareResponses(
                    evaluation.ProposalId,
                    reservations,
                    preflight,
                    lease,
                    responseStateGeneration,
                    responseSafeCode,
                    out var prepared))
            {
                lock (gate)
                {
                    if (pendingInboundProposalIds.Add(evaluation.ProposalId))
                        pendingInboundProposalEvaluations.Enqueue(evaluation);
                }
                diagnostic("dad-inbound-response-store-failed");
                continue;
            }

            var responsesQueued = true;
            lock (gate)
            {
                foreach (var response in prepared.Responses())
                {
                    if (TryEnqueueControl(response))
                        continue;
                    responsesQueued = false;
                    break;
                }
            }
            if (!responsesQueued)
            {
                diagnostic("dad-relay-outbound-full");
                continue;
            }
            if (!admission.Ready)
                continue;
            var inviteSafeCode = DadAutoPartyInboundAdmissionService.InvalidProposal;
            if (lease == null || !TryQueueInboundInviteLocators(
                    prepared,
                    admission,
                    lease.ObservedStateGeneration,
                    out inviteSafeCode))
            {
                diagnostic(inviteSafeCode);
                continue;
            }
            lock (gate)
                runtimeAdmissionValidatedProposalIds.Add(evaluation.ProposalId);
        }
    }

    private bool TryQueueInboundInviteLocators(
        DadAutoPartyInboundProposalState state,
        DadAutoPartyInboundAdmissionResult admission,
        long observedStateGeneration,
        out string safeCode)
    {
        safeCode = DadAutoPartyInboundAdmissionService.InvalidProposal;
        var plan = state.Proposal.ExecutionPlan;
        if (!admission.Ready || plan == null ||
            !string.Equals(admission.RunId, plan.RunId, StringComparison.Ordinal) ||
            admission.InviteTargets.Length != state.OwnedParticipants.Length ||
            admission.OwnedSlotIds.Length != admission.InviteTargets.Length ||
            admission.OwnedSlotIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            admission.OwnedSlotIds.Length)
            return false;

        var bindings = new List<(ParticipantRequest Participant, DadNativePartyInviteTarget Target)>(
            admission.InviteTargets.Length);
        for (var index = 0; index < admission.InviteTargets.Length; index++)
        {
            var target = admission.InviteTargets[index];
            if (!string.Equals(target.SlotId, admission.OwnedSlotIds[index], StringComparison.OrdinalIgnoreCase))
                return false;
            var matches = plan.Participants.Where(participant =>
                    string.Equals(participant.OwnerId.Value, configuration.RegisteredOwnerId, StringComparison.Ordinal) &&
                    string.Equals(participant.OwnerIslandId.Value, configuration.RegisteredIslandId, StringComparison.Ordinal) &&
                    string.Equals(participant.SlotId, target.SlotId, StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();
            if (matches.Length != 1 || !state.OwnedParticipants.Any(participant =>
                    string.Equals(participant.CharacterId.Value, matches[0].CharacterId.Value, StringComparison.Ordinal)))
                return false;
            bindings.Add((
                state.OwnedParticipants.Single(participant => string.Equals(
                    participant.CharacterId.Value,
                    matches[0].CharacterId.Value,
                    StringComparison.Ordinal)),
                target));
        }

        var runtimeExpiry = state.Lease == null
            ? state.Proposal.Header.ExpiresAt
            : Min(state.Proposal.Header.ExpiresAt, state.Lease.LeaseExpiresAt);
        if (runtimeExpiry <= utcNow())
            return false;
        lock (gate)
        {
            foreach (var binding in bindings)
            {
                inboundRuntimeTargets[
                    new InboundRuntimeTargetKey(state.Proposal.ProposalId, binding.Participant.CharacterId.Value)] =
                    new InboundRuntimeTarget(
                        binding.Target.SlotId,
                        binding.Target.Clone(),
                        plan,
                        state.Proposal.Header.SenderIslandId.Value,
                        state.Proposal.RequesterOwnerId.Value,
                        runtimeExpiry);
            }
        }

        foreach (var binding in bindings)
        {
            var queued = QueueParticipantInviteLocator(
                state.Proposal,
                binding.Participant.CharacterId,
                binding.Target,
                observedStateGeneration);
            if (!queued.Allowed)
            {
                lock (gate)
                {
                    foreach (var retained in bindings)
                    {
                        inboundRuntimeTargets.Remove(new InboundRuntimeTargetKey(
                            state.Proposal.ProposalId,
                            retained.Participant.CharacterId.Value));
                    }
                }
                safeCode = queued.SafeCode;
                return false;
            }
        }

        safeCode = "dad-inbound-invite-locators-queued";
        return true;
    }

    private void EnsureInboundResponsesQueued()
    {
        var now = utcNow();
        foreach (var response in inboundProposalService.UnacknowledgedResponses(MaximumPendingOutbound))
        {
            if (response.Header.ExpiresAt <= now)
                continue;
            lock (gate)
            {
                if (pendingOutbound.Any(item => item.Contract.Header.MessageId == response.Header.MessageId) ||
                    awaitingRelayReceipts.ContainsKey(response.Header.MessageId))
                    continue;
                if (!TryEnqueueControl(response))
                    return;
            }
        }
    }

    private async ValueTask ReceiveBoundedAsync(CancellationToken cancellationToken)
    {
        var received = 0;
        await foreach (var envelope in connector.ReceiveAsync(cancellationToken).ConfigureAwait(false))
        {
            received++;
            var dispatched = await DispatchInboundAsync(envelope, cancellationToken).ConfigureAwait(false);
            if (!dispatched.Accepted)
            {
                UpdateSnapshot(dispatched.SafeCode);
                if (received >= MaximumInboundPerCycle)
                    break;
                continue;
            }
            await connector.AcknowledgeAsync(
                new AutoPartyTransportAcknowledgement(envelope.EnvelopeId, dispatched.SafeCode),
                cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref snapshot, Snapshot with
            {
                SafeCode = dispatched.SafeCode,
                ObservedAt = utcNow(),
                LastAuthenticatedInboundAt = utcNow(),
            });
            if (received >= MaximumInboundPerCycle)
                break;
        }
    }

    private async ValueTask SendControlBoundedAsync(CancellationToken cancellationToken)
    {
        for (var sent = 0; sent < MaximumOutboundPerCycle; sent++)
        {
            PendingOutboundContract? pending;
            lock (gate)
                pending = pendingOutbound.Count > 0 ? pendingOutbound.Peek() : null;
            if (pending == null)
                return;
            if (pending.Contract.Header.ExpiresAt <= utcNow())
            {
                lock (gate)
                    _ = pendingOutbound.Dequeue();
                continue;
            }
            var result = await SendContractAsync(pending.Contract, cancellationToken).ConfigureAwait(false);
            if (!result.Accepted)
            {
                UpdateSnapshot(result.SafeCode);
                return;
            }
            lock (gate)
            {
                _ = pendingOutbound.Dequeue();
                if (awaitingRelayReceipts.Count >= MaximumAwaitingReceipts)
                {
                    var oldest = awaitingRelayReceipts.MinBy(static pair => pair.Value.QueuedAt).Key;
                    awaitingRelayReceipts.Remove(oldest);
                }
                if (IsRelayControl(pending.Contract))
                    awaitingRelayReceipts[pending.Contract.Header.MessageId] = pending;
            }
            UpdateSnapshot(result.SafeCode);
        }
    }

    private async ValueTask SendParticipantCommandsAsync(CancellationToken cancellationToken)
    {
        var now = utcNow();
        var batch = participantBridge.LeasePendingCommands(MaximumOutboundPerCycle, DispatchLease, now);
        if (batch.Commands.Count == 0)
            return;
        var accepted = new List<Guid>(batch.Commands.Count);
        foreach (var command in batch.Commands)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await SendParticipantCommandAsync(command, cancellationToken).ConfigureAwait(false);
            if (!result.Accepted)
            {
                UpdateSnapshot(result.SafeCode);
                break;
            }
            accepted.Add(command.CommandId);
        }
        if (accepted.Count > 0)
            participantBridge.AcknowledgePendingCommands(batch.DispatchLeaseId, accepted, utcNow());
        participantBridge.ReleasePendingCommands(batch.DispatchLeaseId, utcNow());
    }

    private async ValueTask<AutoPartyTransportSendResult> SendParticipantCommandAsync(
        DadAutoPartyParticipantCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            IAutoPartyContract contract;
            lock (gate)
            {
                if (!participantContracts.TryGetValue(command.CommandId, out contract!))
                {
                    contract = command.CommandKind switch
                    {
                        DadAutoPartyParticipantCommandKind.Proposal => BuildRunProposal(command),
                        DadAutoPartyParticipantCommandKind.Execution => BuildExecutionOperation(command),
                        DadAutoPartyParticipantCommandKind.Revocation => BuildRevocation(command),
                        _ => throw new InvalidOperationException("dad-relay-participant-command-invalid"),
                    };
                    participantContracts[command.CommandId] = contract;
                }
            }
            var result = await SendContractAsync(contract, cancellationToken).ConfigureAwait(false);
            if (result.Accepted)
            {
                lock (gate)
                    participantContracts.Remove(command.CommandId);
            }
            return result;
        }
        catch (Exception exception) when (exception is ProtocolException or ArgumentException or
                                           InvalidOperationException or FormatException)
        {
            return Denied(command.CommandId, "dad-relay-participant-command-invalid");
        }
    }

    private async ValueTask<AutoPartyTransportSendResult> SendContractAsync(
        IAutoPartyContract contract,
        CancellationToken cancellationToken)
    {
        if (authenticator == null)
            return Denied(contract.Header.MessageId, "dad-relay-authenticator-not-ready");
        try
        {
            return contract switch
            {
                RegistrationHello value => await SealAndSendAsync(value, cancellationToken).ConfigureAwait(false),
                DeregistrationRequest value => await SealAndSendAsync(value, cancellationToken).ConfigureAwait(false),
                PairingNotice value => await SealAndSendAsync(value, cancellationToken).ConfigureAwait(false),
                PairingApproval value => await SealAndSendAsync(value, cancellationToken).ConfigureAwait(false),
                PrivateListingUpdate value => await SealAndSendAsync(value, cancellationToken).ConfigureAwait(false),
                DirectoryQuery value => await SealAndSendAsync(value, cancellationToken).ConfigureAwait(false),
                RegisteredRequesterAccessRequest value => await SealAndSendAsync(value, cancellationToken).ConfigureAwait(false),
                DeauthenticationNotice value => await SealAndSendAsync(value, cancellationToken).ConfigureAwait(false),
                Revocation value => await SealAndSendAsync(value, cancellationToken).ConfigureAwait(false),
                RunProposal value => await SealAndSendAsync(value, cancellationToken).ConfigureAwait(false),
                Reservation value => await SealAndSendAsync(value, cancellationToken).ConfigureAwait(false),
                PreflightResult value => await SealAndSendAsync(value, cancellationToken).ConfigureAwait(false),
                SessionLease value => await SealAndSendAsync(value, cancellationToken).ConfigureAwait(false),
                ParticipantInviteLocator value => await SealAndSendAsync(value, cancellationToken).ConfigureAwait(false),
                ExecutionOperation value => await SealAndSendAsync(value, cancellationToken).ConfigureAwait(false),
                ExecutionOperationReceipt value => await SealAndSendAsync(value, cancellationToken).ConfigureAwait(false),
                AllianceRecruitmentOperation value => await SealAndSendAsync(value, cancellationToken).ConfigureAwait(false),
                AllianceRecruitmentReceipt value => await SealAndSendAsync(value, cancellationToken).ConfigureAwait(false),
                _ => Denied(contract.Header.MessageId, "dad-relay-contract-type-unsupported"),
            };
        }
        catch (Exception exception) when (exception is ProtocolException or CryptographicException or ArgumentException)
        {
            return Denied(contract.Header.MessageId, "dad-relay-contract-seal-failed");
        }
    }

    private async ValueTask<AutoPartyTransportSendResult> SealAndSendAsync<T>(
        T contract,
        CancellationToken cancellationToken)
        where T : IAutoPartyContract
    {
        ValidateOutbound(contract);
        var sealedContract = authenticator!.Seal(authenticator.Sign(contract));
        var encoded = SealedContractCodec.Encode(sealedContract);
        try
        {
            if (encoded.Length is <= 0 or > AutoPartyProtocol.MaximumSemanticEnvelopeBytes)
                throw new ProtocolException(
                    ProtocolFailureCode.SemanticEnvelopeLimitExceeded,
                    "sealed-contract-too-large");
            var envelope = OpaqueEnvelope.Create(
                AutoPartyProtocol.CurrentVersion,
                contract.Header.MessageId,
                contract.Header.SenderIslandId,
                contract.Header.RecipientIslandId,
                contract.Header.IssuedAt,
                contract.Header.ExpiresAt,
                contract.Header.Generation,
                ProtocolContractRegistry.GetTypeId<T>(),
                encoded);
            return await connector.SendAsync(envelope, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    private async ValueTask<DispatchResult> DispatchInboundAsync(
        OpaqueEnvelope delivery,
        CancellationToken cancellationToken)
    {
        if (authenticator == null || delivery.EnvelopeVersion != AutoPartyProtocol.CurrentVersion ||
            delivery.EnvelopeId == Guid.Empty || delivery.ExpiresAt <= utcNow() ||
            delivery.PayloadLength is <= 0 or > AutoPartyProtocol.MaximumSemanticEnvelopeBytes ||
            !string.Equals(delivery.RecipientIslandId.Value, configuration.RegisteredIslandId, StringComparison.Ordinal))
            return DispatchResult.Deny("dad-relay-inbound-envelope-invalid");

        SealedContract sealedContract;
        try
        {
            sealedContract = SealedContractCodec.Decode(delivery.Ciphertext.AsMemory());
        }
        catch (ProtocolException)
        {
            return DispatchResult.Deny("dad-relay-inbound-sealed-invalid");
        }
        if (sealedContract.MessageId != delivery.EnvelopeId ||
            !string.Equals(sealedContract.RecipientIslandId.Value, configuration.RegisteredIslandId, StringComparison.Ordinal) ||
            sealedContract.RecipientKeyVersion != configuration.EndpointKeyGeneration)
            return DispatchResult.Deny("dad-relay-inbound-route-invalid");

        var type = delivery.PayloadType;
        if (SameType<RegistrationReceipt>(type))
            return await OpenAndDispatchAsync<RegistrationReceipt>(sealedContract, true, DispatchRegistrationReceiptAsync)
                .ConfigureAwait(false);
        if (SameType<DeregistrationReceipt>(type))
            return await OpenAndDispatchAsync<DeregistrationReceipt>(sealedContract, true, value =>
                    DispatchDeregistrationReceiptAsync(value, cancellationToken))
                .ConfigureAwait(false);
        if (SameType<PairingNotice>(type))
            return await OpenAndDispatchAsync<PairingNotice>(sealedContract, true, DispatchPairingNoticeAsync)
                .ConfigureAwait(false);
        if (SameType<PairingApproval>(type))
            return await OpenAndDispatchAsync<PairingApproval>(sealedContract, true, DispatchPairingApprovalAsync)
                .ConfigureAwait(false);
        if (SameType<DirectoryPage>(type))
            return await OpenAndDispatchAsync<DirectoryPage>(sealedContract, true, DispatchDirectoryPageAsync)
                .ConfigureAwait(false);
        if (SameType<RegisteredRequesterAccessRequest>(type))
            return await OpenAndDispatchAsync<RegisteredRequesterAccessRequest>(
                    sealedContract, true, DispatchAccessRequestAsync)
                .ConfigureAwait(false);
        if (SameType<RegisteredRequesterAttestation>(type))
            return await OpenAndDispatchAsync<RegisteredRequesterAttestation>(
                    sealedContract, true, DispatchAttestationAsync)
                .ConfigureAwait(false);
        if (SameType<DeauthenticationNotice>(type))
            return await OpenAndDispatchAsync<DeauthenticationNotice>(
                    sealedContract, true, DispatchDeauthenticationAsync)
                .ConfigureAwait(false);
        if (SameType<RelayReceipt>(type))
            return await OpenAndDispatchAsync<RelayReceipt>(sealedContract, true, DispatchRelayReceiptAsync)
                .ConfigureAwait(false);
        if (SameType<Revocation>(type))
            return await OpenAndDispatchAsync<Revocation>(sealedContract, true, DispatchRevocationAsync)
                .ConfigureAwait(false);
        if (SameType<CapabilityGrant>(type))
            return await OpenAndDispatchAsync<CapabilityGrant>(sealedContract, false, DispatchCapabilityGrantAsync)
                .ConfigureAwait(false);
        if (SameType<RunProposal>(type))
            return await OpenAndDispatchAsync<RunProposal>(sealedContract, false, DispatchRunProposalAsync)
                .ConfigureAwait(false);
        if (SameType<Reservation>(type))
            return await OpenAndDispatchAsync<Reservation>(sealedContract, false, DispatchReservationAsync)
                .ConfigureAwait(false);
        if (SameType<PreflightResult>(type))
            return await OpenAndDispatchAsync<PreflightResult>(sealedContract, false, DispatchPreflightAsync)
                .ConfigureAwait(false);
        if (SameType<SessionLease>(type))
            return await OpenAndDispatchAsync<SessionLease>(sealedContract, false, DispatchLeaseAsync)
                .ConfigureAwait(false);
        if (SameType<ParticipantInviteLocator>(type))
            return await OpenAndDispatchAsync<ParticipantInviteLocator>(
                    sealedContract, false, DispatchParticipantInviteLocatorAsync)
                .ConfigureAwait(false);
        if (SameType<ExecutionReceipt>(type))
            return await OpenAndDispatchAsync<ExecutionReceipt>(sealedContract, false, DispatchExecutionReceiptAsync)
                .ConfigureAwait(false);
        if (SameType<ExecutionOperation>(type))
            return await OpenAndDispatchAsync<ExecutionOperation>(sealedContract, false, DispatchExecutionOperationAsync)
                .ConfigureAwait(false);
        if (SameType<ExecutionOperationReceipt>(type))
            return await OpenAndDispatchAsync<ExecutionOperationReceipt>(
                    sealedContract, false, DispatchExecutionOperationReceiptAsync)
                .ConfigureAwait(false);
        if (SameType<IntegrationProfile>(type))
            return await OpenAndDispatchAsync<IntegrationProfile>(sealedContract, false, DispatchIntegrationProfileAsync)
                .ConfigureAwait(false);
        if (SameType<IntegrationProfileReceipt>(type))
            return await OpenAndDispatchAsync<IntegrationProfileReceipt>(
                    sealedContract, false, DispatchIntegrationProfileReceiptAsync)
                .ConfigureAwait(false);
        if (SameType<AllianceRecruitmentOperation>(type))
            return await OpenAndDispatchAsync<AllianceRecruitmentOperation>(
                    sealedContract, false, DispatchAllianceRecruitmentOperationAsync)
                .ConfigureAwait(false);
        if (SameType<AllianceRecruitmentReceipt>(type))
            return await OpenAndDispatchAsync<AllianceRecruitmentReceipt>(
                    sealedContract, false, DispatchAllianceRecruitmentReceiptAsync)
                .ConfigureAwait(false);
        return DispatchResult.Deny("dad-relay-contract-type-unsupported");
    }

    private async ValueTask<DispatchResult> OpenAndDispatchAsync<T>(
        SealedContract sealedContract,
        bool relaySigned,
        Func<T, ValueTask<DispatchResult>> dispatch)
        where T : IAutoPartyContract
    {
        var opened = authenticator!.Open<T>(sealedContract);
        if (!opened.Succeeded || opened.Message is null)
            return DispatchResult.Deny("dad-relay-contract-open-rejected");
        var contract = opened.Message.Contract;
        var now = utcNow();
        var header = contract.Header;
        if (header.IssuedAt > now + TimeSpan.FromMinutes(2) || header.ExpiresAt <= now ||
            !string.Equals(header.RecipientIslandId.Value, configuration.RegisteredIslandId, StringComparison.Ordinal) ||
            replayedMessages.ContainsKey(header.MessageId))
            return DispatchResult.Deny(replayedMessages.ContainsKey(header.MessageId)
                ? "dad-relay-contract-replay"
                : "dad-relay-contract-route-invalid");
        if (relaySigned)
        {
            if (!string.Equals(header.SenderIslandId.Value, RelayIsland, StringComparison.Ordinal) ||
                header.SenderKeyVersion != configuration.RelayKeyGeneration)
                return DispatchResult.Deny("dad-relay-signer-invalid");
        }
        else if (!IsDirectSenderAllowed(header.SenderIslandId, header.SenderKeyVersion, now))
        {
            return DispatchResult.Deny("dad-relay-peer-route-invalid");
        }

        var result = await dispatch(contract).ConfigureAwait(false);
        if (result.Accepted)
            CommitReplay(header);
        return result;
    }

    private ValueTask<DispatchResult> DispatchRegistrationReceiptAsync(RegistrationReceipt receipt)
    {
        if (registrationReceiptHandler == null || !receipt.Activated ||
            !Guid.TryParse(configuration.RegistrationId, out var registrationId) ||
            receipt.RegistrationId != registrationId ||
            !string.Equals(receipt.OwnerId.Value, configuration.RegisteredOwnerId, StringComparison.Ordinal) ||
            !string.Equals(receipt.IslandId.Value, configuration.RegisteredIslandId, StringComparison.Ordinal))
            return ValueTask.FromResult(DispatchResult.Deny("dad-registration-receipt-mismatch"));
        var decision = registrationReceiptHandler(receipt);
        return ValueTask.FromResult(decision.Allowed
            ? DispatchResult.Allow(decision.SafeCode)
            : DispatchResult.Deny(decision.SafeCode));
    }

    private async ValueTask<DispatchResult> DispatchDeregistrationReceiptAsync(
        DeregistrationReceipt receipt,
        CancellationToken cancellationToken)
    {
        var pending = pendingOperationStore.LoadDeregistration();
        if (deregistrationReceiptHandler == null || pending == null ||
            receipt.DeregistrationId != pending.DeregistrationId ||
            !string.Equals(receipt.IslandId.Value, configuration.RegisteredIslandId, StringComparison.Ordinal))
            return DispatchResult.Deny("dad-deregister-receipt-mismatch");
        if (!receipt.Deregistered)
        {
            lock (gate)
            {
                foreach (var messageId in awaitingRelayReceipts
                             .Where(pair => pair.Value.Contract is DeregistrationRequest request &&
                                            request.DeregistrationId == pending.DeregistrationId)
                             .Select(static pair => pair.Key)
                             .ToList())
                    awaitingRelayReceipts.Remove(messageId);
            }
            return DispatchResult.Allow(receipt.SafeCode);
        }
        var result = await deregistrationReceiptHandler(receipt, pending, cancellationToken).ConfigureAwait(false);
        if (!result.Purged)
            return DispatchResult.Deny(result.SafeCode);
        pendingOperationStore.ClearDeregistration(pending.DeregistrationId);
        inboundProposalService.Clear();
        lock (gate)
        {
            pendingOutbound.Clear();
            awaitingRelayReceipts.Clear();
            participantContracts.Clear();
            directoryQueries.Clear();
            pendingAccessRequests.Clear();
            attestedRoutes.Clear();
            pendingAllianceOutbound.Clear();
            pendingAllianceInbound.Clear();
            pendingInboundProposalEvaluations.Clear();
            pendingInboundProposalIds.Clear();
            inboundRuntimeTargets.Clear();
        }
        ResetSecurity();
        return DispatchResult.Allow(result.SafeCode);
    }

    private ValueTask<DispatchResult> DispatchPairingNoticeAsync(PairingNotice notice)
    {
        if (notice.PeerIslandId.Value != configuration.RegisteredIslandId ||
            notice.InitiatorIslandId.Value == configuration.RegisteredIslandId)
            return ValueTask.FromResult(DispatchResult.Deny("dad-pairing-notice-route-invalid"));
        var decision = service.ReceivePairingNotice(
            notice.PairingId,
            notice.InitiatorOwnerId.Value,
            notice.InitiatorIslandId.Value,
            notice.InitiatorHomeGuildScope,
            notice.InitiatorPublicKeys,
            notice.InitiatorFingerprint,
            notice.TranscriptHash,
            notice.ConfirmationCodeHash,
            notice.PairingExpiresAt.UtcDateTime);
        return ValueTask.FromResult(decision.Allowed
            ? DispatchResult.Allow(decision.SafeCode)
            : DispatchResult.Deny(decision.SafeCode));
    }

    private ValueTask<DispatchResult> DispatchPairingApprovalAsync(PairingApproval approval)
    {
        if (approval.PeerIslandId.Value != configuration.RegisteredIslandId)
            return ValueTask.FromResult(DispatchResult.Deny("dad-pairing-approval-denied"));
        if (!approval.Accepted)
            return ValueTask.FromResult(DispatchResult.Allow("dad-pairing-peer-declined"));
        var decision = service.ConfirmPeerApproval(
            approval.PairingId,
            approval.TranscriptHash,
            approval.ConfirmationCodeHash,
            approval.ApprovingFingerprint,
            approval.PeerFingerprint,
            ToDadPolicy(approval.SharePolicy));
        return ValueTask.FromResult(decision.Allowed
            ? DispatchResult.Allow(decision.SafeCode)
            : DispatchResult.Deny(decision.SafeCode));
    }

    private ValueTask<DispatchResult> DispatchDirectoryPageAsync(DirectoryPage page)
    {
        PendingDirectoryQuery query;
        lock (gate)
        {
            if (!directoryQueries.TryGetValue(page.QueryId, out query!))
                return ValueTask.FromResult(DispatchResult.Deny("dad-directory-page-unsolicited"));
        }

        foreach (var entry in page.Entries)
        {
            var pairing = configuration.Pairings.FirstOrDefault(item =>
                item.IsActive && string.Equals(item.IslandId, entry.IslandId.Value, StringComparison.Ordinal));
            if (pairing != null && string.IsNullOrWhiteSpace(pairing.HomeGuildScope))
                pairing.HomeGuildScope = entry.HomeGuildScope;
            if (!entry.Online)
            {
                if (pairing != null)
                    service.ApplyDirectoryPresence(entry.IslandId.Value, false);
                continue;
            }
            var effectiveMode = (DadAutoPartyCharacterShareMode)(int)entry.EffectiveShareMode;
            var effectivePolicyHash = DadAutoPartyConfiguration.NormalizeIdentifier(entry.EffectivePolicyHash);
            if (!Enum.IsDefined(effectiveMode) ||
                (pairing == null &&
                 (effectiveMode != DadAutoPartyCharacterShareMode.CharacterList ||
                  string.IsNullOrWhiteSpace(effectivePolicyHash))))
                return ValueTask.FromResult(DispatchResult.Deny("dad-directory-entry-policy-invalid"));
            var policy = new DadAutoPartySharePolicy
            {
                Enabled = true,
                Mode = pairing != null
                    ? DadAutoPartyCharacterShareMode.AllCharactersForPeer
                    : DadAutoPartyCharacterShareMode.CharacterList,
                Revision = Math.Max(1, entry.DirectoryGeneration),
                UpdatedAtUtc = page.Header.IssuedAt.UtcDateTime,
            };
            var listings = entry.Listings.Select(listing => new DadAutoPartyListing
            {
                ListingId = DeriveGuid(entry.IslandId.Value, listing.CharacterHandle.Value).ToString("D"),
                OwnerId = entry.OwnerId.Value,
                SharingIslandId = entry.IslandId.Value,
                EffectiveShareMode = effectiveMode,
                EffectivePolicyHash = effectivePolicyHash,
                OpaqueCharacterId = listing.CharacterHandle.Value,
                DisplayLabel = listing.DisplayLabel,
                AllowedJobIds = listing.PermittedJobs.Select(static value => value.Value).ToList(),
                AllowedActivityIds = listing.PermittedActivities.Select(static value => value.Value).ToList(),
                Available = listing.Available,
                Revision = listing.Revision,
                ExpiresAtUtc = listing.ExpiresAt.UtcDateTime,
            }).Select(static listing => listing.Normalize()).ToList();
            if (pairing != null)
            {
                var decision = service.ApplyRemoteListings(
                    entry.IslandId.Value,
                    entry.HomeGuildScope,
                    policy,
                    listings,
                    registeredRequesterAttested: false);
                if (!decision.Allowed)
                    return ValueTask.FromResult(DispatchResult.Deny(decision.SafeCode));
                service.ApplyDirectoryPresence(entry.IslandId.Value, true);
            }
            else
            {
                var now = utcNow();
                var visible = listings
                    .Where(listing => listing.IsValid && listing.Available &&
                                      listing.ExpiresAtUtc > now.UtcDateTime &&
                                      listing.ExpiresAtUtc <= now.UtcDateTime + TimeSpan.FromHours(24))
                    .Take(AutoPartyProtocol.MaximumCollectionItems)
                    .ToList();
                configuration.Listings.RemoveAll(listing =>
                    string.Equals(listing.SharingIslandId, entry.IslandId.Value, StringComparison.Ordinal));
                foreach (var listing in visible)
                {
                    if (IsListingRouteCurrent(listing, now))
                        listing.TransientRouteExpiresAtUtc = GetListingRouteExpiry(listing, now)?.UtcDateTime;
                    configuration.Listings.Add(listing);
                }
                configuration.StateGeneration++;
                service.ApplyDirectoryPresence(entry.IslandId.Value, true);
            }
        }

        lock (gate)
        {
            if (page.HasMore)
            {
                if (!TryEnqueueControl(BuildDirectoryQuery(query, page.ContinuationToken)))
                    return ValueTask.FromResult(DispatchResult.Deny("dad-relay-outbound-full"));
            }
            else
            {
                directoryQueries.Remove(page.QueryId);
            }
        }
        return ValueTask.FromResult(DispatchResult.Allow("dad-directory-page-applied"));
    }

    private ValueTask<DispatchResult> DispatchAccessRequestAsync(RegisteredRequesterAccessRequest request)
    {
        if (!string.Equals(request.SharingIslandId.Value, configuration.RegisteredIslandId, StringComparison.Ordinal))
            return ValueTask.FromResult(DispatchResult.Deny("dad-promiscuous-request-route-invalid"));
        lock (gate)
        {
            if (pendingAccessRequests.Count >= MaximumPendingOutbound)
                pendingAccessRequests.Remove(pendingAccessRequests.MinBy(static pair => pair.Value.ExpiresAt).Key);
            pendingAccessRequests[request.AccessRequestId] = new(
                request.AccessRequestId,
                false,
                string.Empty,
                configuration.RegisteredOwnerId,
                request.SharingIslandId.Value,
                request.Header.Generation,
                request.RequestedCharacters.Select(static item => item.Value).ToImmutableArray(),
                request.RequestedPolicyHash,
                request.Header.ExpiresAt);
        }
        return ValueTask.FromResult(DispatchResult.Allow("dad-promiscuous-request-recorded"));
    }

    private ValueTask<DispatchResult> DispatchAttestationAsync(RegisteredRequesterAttestation attestation)
    {
        var now = utcNow();
        var localIsRequester =
            string.Equals(attestation.RequesterIslandId.Value, configuration.RegisteredIslandId, StringComparison.Ordinal) &&
            string.Equals(attestation.RequesterOwnerId.Value, configuration.RegisteredOwnerId, StringComparison.Ordinal);
        var localIsSharer =
            string.Equals(attestation.SharingIslandId.Value, configuration.RegisteredIslandId, StringComparison.Ordinal) &&
            string.Equals(attestation.SharingOwnerId.Value, configuration.RegisteredOwnerId, StringComparison.Ordinal);
        if (localIsRequester == localIsSharer ||
            string.Equals(attestation.RequesterIslandId.Value, attestation.SharingIslandId.Value, StringComparison.Ordinal) ||
            string.Equals(attestation.RequesterOwnerId.Value, attestation.SharingOwnerId.Value, StringComparison.Ordinal) ||
            !string.Equals(attestation.SharedHomeGuildScope, configuration.HomeGuildScope, StringComparison.Ordinal) ||
            attestation.ValidUntil <= now ||
            attestation.ValidUntil > attestation.Header.ExpiresAt)
            return ValueTask.FromResult(DispatchResult.Deny("dad-promiscuous-attestation-invalid"));
        lock (gate)
        {
            if (!pendingAccessRequests.TryGetValue(attestation.AccessRequestId, out var request) ||
                request.LocalRequester != localIsRequester ||
                !string.Equals(
                    request.ExpectedRequesterOwnerId,
                    localIsRequester ? attestation.RequesterOwnerId.Value : string.Empty,
                    StringComparison.Ordinal) ||
                !string.Equals(request.ExpectedSharingOwnerId, attestation.SharingOwnerId.Value, StringComparison.Ordinal) ||
                !string.Equals(request.SharingIslandId, attestation.SharingIslandId.Value, StringComparison.Ordinal) ||
                !string.Equals(request.PolicyHash, attestation.RequestedPolicyHash, StringComparison.Ordinal) ||
                attestation.ValidUntil > request.ExpiresAt)
                return ValueTask.FromResult(DispatchResult.Deny("dad-promiscuous-attestation-unmatched"));
            var localKeysMatch = localIsRequester
                ? MatchesLocalEndpoint(
                    attestation.RequesterOwnerId,
                    attestation.RequesterIslandId,
                    attestation.RequesterPublicKeys,
                    attestation.RequesterFingerprint)
                : MatchesLocalEndpoint(
                    attestation.SharingOwnerId,
                    attestation.SharingIslandId,
                    attestation.SharingPublicKeys,
                    attestation.SharingFingerprint);
            var oppositeOwner = localIsRequester ? attestation.SharingOwnerId : attestation.RequesterOwnerId;
            var oppositeIsland = localIsRequester ? attestation.SharingIslandId : attestation.RequesterIslandId;
            var oppositeKeys = localIsRequester ? attestation.SharingPublicKeys : attestation.RequesterPublicKeys;
            var oppositeFingerprint = localIsRequester
                ? attestation.SharingFingerprint
                : attestation.RequesterFingerprint;
            if (!localKeysMatch || keyResolver == null || !keyResolver.TryAddTransientPublicKeys(
                    oppositeOwner,
                    oppositeIsland,
                    oppositeKeys,
                    oppositeFingerprint,
                    attestation.ValidUntil,
                    now))
                return ValueTask.FromResult(DispatchResult.Deny("dad-promiscuous-attestation-keys-invalid"));
            pendingAccessRequests.Remove(attestation.AccessRequestId);
            var routeKey = new RouteKey(
                attestation.RequesterIslandId.Value,
                attestation.SharingIslandId.Value);
            attestedRoutes[routeKey] = new(
                attestation.RequesterOwnerId.Value,
                attestation.SharingOwnerId.Value,
                attestation.RequesterPublicKeys.KeyVersion,
                attestation.SharingPublicKeys.KeyVersion,
                attestation.RequestedPolicyHash,
                request.RequestedCharacters,
                attestation.ValidUntil);
            if (localIsRequester)
            {
                foreach (var listing in configuration.Listings.Where(listing =>
                             string.Equals(listing.OwnerId, attestation.SharingOwnerId.Value, StringComparison.Ordinal) &&
                             string.Equals(listing.SharingIslandId, attestation.SharingIslandId.Value, StringComparison.Ordinal) &&
                             string.Equals(listing.EffectivePolicyHash, attestation.RequestedPolicyHash, StringComparison.Ordinal)))
                    listing.TransientRouteExpiresAtUtc = attestation.ValidUntil.UtcDateTime;
            }
        }
        return ValueTask.FromResult(DispatchResult.Allow("dad-promiscuous-attestation-recorded"));
    }

    private ValueTask<DispatchResult> DispatchDeauthenticationAsync(DeauthenticationNotice notice)
    {
        var pairing = configuration.Pairings.FirstOrDefault(item =>
            item.IsActive && string.Equals(item.IslandId, notice.PeerIslandId.Value, StringComparison.Ordinal) &&
            string.Equals(item.TranscriptHash, notice.PairingTranscriptHash, StringComparison.Ordinal));
        if (pairing == null)
        {
            var removed = RemoveTransientRoutes((route, _) =>
                string.Equals(route.FirstIslandId, notice.PeerIslandId.Value, StringComparison.Ordinal) ||
                string.Equals(route.SecondIslandId, notice.PeerIslandId.Value, StringComparison.Ordinal));
            if (removed == 0)
                return ValueTask.FromResult(DispatchResult.Deny("dad-deauthentication-route-mismatch"));
            participantBridge.DeauthenticateIsland(
                notice.PeerIslandId.Value,
                notice.RevocationGeneration,
                notice.SafeReason,
                utcNow());
            inboundProposalService.RemoveSender(notice.PeerIslandId.Value);
            RemoveInboundRuntimeTargets((_, target) =>
                string.Equals(target.SenderIslandId, notice.PeerIslandId.Value, StringComparison.Ordinal));
            return ValueTask.FromResult(DispatchResult.Allow("dad-deauthentication-applied"));
        }
        var decision = service.Deauthenticate(pairing.IslandId, notice.SafeReason);
        participantBridge.DeauthenticateIsland(
            pairing.IslandId,
            notice.RevocationGeneration,
            notice.SafeReason,
            utcNow());
        inboundProposalService.RemoveSender(pairing.IslandId);
        RemoveInboundRuntimeTargets((_, target) =>
            string.Equals(target.SenderIslandId, pairing.IslandId, StringComparison.Ordinal));
        RemoveTransientRoutes((route, _) =>
            string.Equals(route.FirstIslandId, pairing.IslandId, StringComparison.Ordinal) ||
            string.Equals(route.SecondIslandId, pairing.IslandId, StringComparison.Ordinal));
        return ValueTask.FromResult(decision.Allowed
            ? DispatchResult.Allow(decision.SafeCode)
            : DispatchResult.Deny(decision.SafeCode));
    }

    private ValueTask<DispatchResult> DispatchRelayReceiptAsync(RelayReceipt receipt)
    {
        PendingOutboundContract? related;
        lock (gate)
        {
            _ = awaitingRelayReceipts.Remove(receipt.RelatedMessageId, out related);
            if (!receipt.Accepted && related != null && related.Contract.Header.ExpiresAt > utcNow())
                _ = TryEnqueueControl(related.Contract);
        }
        try
        {
            if (inboundProposalService.ObserveRelayReceipt(receipt.RelatedMessageId, receipt.Accepted))
                return ValueTask.FromResult(DispatchResult.Allow(receipt.Accepted
                    ? "dad-inbound-response-relayed"
                    : "dad-inbound-response-retry"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ValueTask.FromResult(DispatchResult.Deny("dad-inbound-response-store-failed"));
        }
        if (service.TryApplyPairingApprovalRelayReceipt(
                receipt.RelatedMessageId,
                receipt.Accepted,
                out var pairingDecision))
        {
            if (!pairingDecision.Allowed)
                return ValueTask.FromResult(DispatchResult.Deny(pairingDecision.SafeCode));
            if (receipt.Accepted)
                RemovePendingOutbound(receipt.RelatedMessageId);
            return ValueTask.FromResult(DispatchResult.Allow(pairingDecision.SafeCode));
        }
        if (related == null)
            return ValueTask.FromResult(DispatchResult.Allow("dad-relay-receipt-idempotent"));
        return ValueTask.FromResult(DispatchResult.Allow(receipt.SafeCode));
    }

    private void RemovePendingOutbound(Guid messageId)
    {
        lock (gate)
        {
            var retained = pendingOutbound
                .Where(item => item.Contract.Header.MessageId != messageId)
                .ToArray();
            pendingOutbound.Clear();
            foreach (var pending in retained)
                pendingOutbound.Enqueue(pending);
        }
    }

    private ValueTask<DispatchResult> DispatchRevocationAsync(Revocation revocation)
    {
        var decision = service.Revoke(revocation);
        if (revocation.TargetKind == RevocationTargetKind.Session &&
            Guid.TryParse(revocation.TargetId, out var revokedProposalId))
        {
            inboundProposalService.Remove(revokedProposalId);
            RemoveInboundRuntimeTargets((key, _) => key.ProposalId == revokedProposalId);
        }
        if (revocation.TargetKind == RevocationTargetKind.Identity)
        {
            RemoveTransientRoutes((_, route) =>
                string.Equals(route.RequesterOwnerId, revocation.OwnerId.Value, StringComparison.Ordinal) ||
                string.Equals(route.SharingOwnerId, revocation.OwnerId.Value, StringComparison.Ordinal));
            var pairing = configuration.Pairings.FirstOrDefault(item =>
                item.IsActive && string.Equals(item.OwnerId, revocation.OwnerId.Value, StringComparison.Ordinal));
            if (pairing != null)
            {
                _ = service.Deauthenticate(pairing.IslandId, revocation.SafeReason);
                participantBridge.DeauthenticateIsland(
                    pairing.IslandId,
                    revocation.RevocationGeneration,
                    revocation.SafeReason,
                    utcNow());
                inboundProposalService.RemoveSender(pairing.IslandId);
                RemoveInboundRuntimeTargets((_, target) =>
                    string.Equals(target.OwnerId, revocation.OwnerId.Value, StringComparison.Ordinal));
            }
        }
        return ValueTask.FromResult(decision.Allowed
            ? DispatchResult.Allow(decision.SafeCode)
            : DispatchResult.Deny(decision.SafeCode));
    }

    private ValueTask<DispatchResult> DispatchCapabilityGrantAsync(CapabilityGrant grant)
    {
        var decision = service.AddImmutableGrant(grant);
        return ValueTask.FromResult(decision.Allowed
            ? DispatchResult.Allow(decision.SafeCode)
            : DispatchResult.Deny(decision.SafeCode));
    }

    private ValueTask<DispatchResult> DispatchRunProposalAsync(RunProposal proposal)
    {
        return ValueTask.FromResult(inboundProposalService.TryRetain(proposal, out _, out var safeCode)
            ? DispatchResult.Allow(safeCode)
            : DispatchResult.Deny(safeCode));
    }

    private ValueTask<DispatchResult> DispatchReservationAsync(Reservation reservation)
    {
        if (participantBridge.ObserveReservation(reservation, utcNow(), out var safeCode))
            return ValueTask.FromResult(DispatchResult.Allow(safeCode));
        var decision = service.Reserve(reservation, DadAutoPartySessionMode.MultiOwner);
        return ValueTask.FromResult(decision.Allowed
            ? DispatchResult.Allow(decision.SafeCode)
            : DispatchResult.Deny(safeCode));
    }

    private ValueTask<DispatchResult> DispatchPreflightAsync(PreflightResult preflight)
    {
        if (participantBridge.ObservePreflight(preflight, utcNow(), out var safeCode))
            return ValueTask.FromResult(DispatchResult.Allow(safeCode));
        var decision = service.VerifyPreflight(preflight);
        return ValueTask.FromResult(decision.Allowed
            ? DispatchResult.Allow(decision.SafeCode)
            : DispatchResult.Deny(safeCode));
    }

    private ValueTask<DispatchResult> DispatchLeaseAsync(SessionLease lease)
    {
        if (participantBridge.ObserveLease(lease, utcNow(), out var safeCode))
            return ValueTask.FromResult(DispatchResult.Allow(safeCode));
        var decision = service.AcquireLease(lease);
        return ValueTask.FromResult(decision.Allowed
            ? DispatchResult.Allow(decision.SafeCode)
            : DispatchResult.Deny(safeCode));
    }

    private ValueTask<DispatchResult> DispatchParticipantInviteLocatorAsync(ParticipantInviteLocator message)
    {
        var now = utcNow();
        var locator = message.Locator;
        if (!IsParticipantRouteAllowed(
                message.Header.SenderIslandId,
                message.OwnerId,
                message.Header.SenderKeyVersion,
                now) ||
            locator.OwnerId != message.OwnerId || locator.IslandId != message.Header.SenderIslandId ||
            locator.ValidUntil <= now || locator.ValidUntil > message.Header.ExpiresAt ||
            locator.ValidUntil > now + ParticipantLifetime || locator.OpaqueLocator.IsDefaultOrEmpty ||
            locator.OpaqueLocator.Length > 1024)
            return ValueTask.FromResult(DispatchResult.Deny("dad-participant-invite-locator-invalid"));

        var encoded = locator.OpaqueLocator.ToArray();
        try
        {
            var payload = JsonSerializer.Deserialize<InviteLocatorPayload>(encoded);
            if (payload == null ||
                !IsBoundedLocatorValue(payload.RunId, AutoPartyProtocol.MaximumIdentifierLength) ||
                !IsBoundedLocatorValue(payload.ModuleId, AutoPartyProtocol.MaximumIdentifierLength) ||
                !IsBoundedLocatorValue(payload.SlotId, AutoPartyProtocol.MaximumIdentifierLength) ||
                !IsBoundedLocatorValue(payload.WorkerSessionId, AutoPartyProtocol.MaximumIdentifierLength) ||
                !IsBoundedLocatorValue(payload.AccountKey, AutoPartyProtocol.MaximumIdentifierLength) ||
                !IsBoundedLocatorValue(payload.CharacterKey, AutoPartyProtocol.MaximumIdentifierLength) ||
                !IsBoundedLocatorValue(payload.CharacterName, AutoPartyProtocol.MaximumDisplayLabelLength) ||
                payload.ContentId == 0 || payload.WorldId == 0 ||
                !Enum.TryParse<DadModuleId>(payload.ModuleId, ignoreCase: false, out var moduleId))
                return ValueTask.FromResult(DispatchResult.Deny("dad-participant-invite-locator-invalid"));

            var target = new DadNativePartyInviteTarget
            {
                RunId = payload.RunId,
                ModuleId = moduleId,
                SlotId = payload.SlotId,
                WorkerSessionId = new DadWorkerSessionId(payload.WorkerSessionId),
                AccountKey = new DadAccountKey(payload.AccountKey),
                CharacterKey = new DadCharacterKey(payload.CharacterKey),
                ContentId = payload.ContentId,
                CharacterName = payload.CharacterName,
                WorldId = payload.WorldId,
            };
            return ValueTask.FromResult(participantBridge.ObserveInviteTarget(
                message.Header,
                message.ProposalId,
                message.OwnerId,
                message.CharacterId,
                target,
                locator.ValidUntil,
                now,
                out var safeCode)
                ? DispatchResult.Allow(safeCode)
                : DispatchResult.Deny(safeCode));
        }
        catch (JsonException)
        {
            return ValueTask.FromResult(DispatchResult.Deny("dad-participant-invite-locator-invalid"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    private ValueTask<DispatchResult> DispatchExecutionReceiptAsync(ExecutionReceipt receipt)
        => ValueTask.FromResult(DispatchResult.Allow(receipt.SafeCode));

    private ValueTask<DispatchResult> DispatchExecutionOperationAsync(ExecutionOperation operation)
    {
        if (!configuration.Enabled)
            return ValueTask.FromResult(DispatchResult.Deny("dad-execution-disabled"));
        DadExpectedPartyInviter? expectedInviter = null;
        IReadOnlyList<DadNativePartyInviteTarget> partyInviteTargets = [];
        if (operation.Kind == ExecutionOperationKind.Form)
        {
            if (operation.InviteLocator != null && !operation.PartyInviteTargets.IsDefaultOrEmpty)
                return ValueTask.FromResult(DispatchResult.Deny("dad-relay-form-locator-mode-invalid"));
            if (operation.InviteLocator != null &&
                !TryOpenExpectedInviter(operation, out expectedInviter, out var safeCode))
                return ValueTask.FromResult(DispatchResult.Deny(safeCode));
            if (!operation.PartyInviteTargets.IsDefaultOrEmpty &&
                !TryOpenPartyInviteTargets(operation, out partyInviteTargets, out safeCode))
                return ValueTask.FromResult(DispatchResult.Deny(safeCode));
        }
        else if (operation.Kind == ExecutionOperationKind.Restore)
        {
            if (!operation.PartyInviteTargets.IsDefaultOrEmpty)
                return ValueTask.FromResult(DispatchResult.Deny("dad-relay-restore-locator-mode-invalid"));
            if (operation.InviteLocator != null &&
                !TryOpenPartyTeardownContext(operation, out expectedInviter, out partyInviteTargets, out var safeCode))
                return ValueTask.FromResult(DispatchResult.Deny(safeCode));
        }
        else if (operation.InviteLocator != null || !operation.PartyInviteTargets.IsDefaultOrEmpty)
        {
            return ValueTask.FromResult(DispatchResult.Deny("dad-relay-invite-locator-unexpected"));
        }
        lock (gate)
        {
            if (pendingExecutions.Count >= MaximumPendingExecutions)
                return ValueTask.FromResult(DispatchResult.Deny("dad-relay-execution-queue-full"));
            if (operation.Kind == ExecutionOperationKind.Restore && expectedInviter != null)
            {
                var key = new InboundRuntimeTargetKey(operation.ProposalId, operation.CharacterId.Value);
                if (!inboundRuntimeTargets.TryGetValue(key, out var retained) ||
                    retained.ExpiresAt <= utcNow() ||
                    !string.Equals(retained.SenderIslandId, operation.Header.SenderIslandId.Value, StringComparison.Ordinal) ||
                    !string.Equals(retained.OwnerId, operation.OwnerId.Value, StringComparison.Ordinal))
                    return ValueTask.FromResult(DispatchResult.Deny("dad-relay-restore-runtime-route-invalid"));
                inboundRuntimeTargets[key] = retained with
                {
                    FrozenInviter = expectedInviter.Clone(),
                    PartyInviteTargets = partyInviteTargets.Select(static target => target.Clone()).ToArray(),
                };
            }
            pendingExecutions.Enqueue(new(operation, expectedInviter, partyInviteTargets));
        }
        return ValueTask.FromResult(DispatchResult.Allow("dad-relay-execution-queued"));
    }

    private ValueTask<DispatchResult> DispatchExecutionOperationReceiptAsync(ExecutionOperationReceipt receipt)
        => ValueTask.FromResult(participantBridge.ObserveOperationReceipt(receipt, utcNow(), out var safeCode)
            ? DispatchResult.Allow(safeCode)
            : DispatchResult.Deny(safeCode));

    private ValueTask<DispatchResult> DispatchIntegrationProfileAsync(IntegrationProfile profile)
    {
        lock (gate)
        {
            if (pendingProfiles.Count >= MaximumPendingOutbound)
                pendingProfiles.Remove(pendingProfiles.Keys.First());
            pendingProfiles[profile.ProposalId] = profile;
        }
        return ValueTask.FromResult(DispatchResult.Allow("dad-integration-profile-recorded"));
    }

    private ValueTask<DispatchResult> DispatchIntegrationProfileReceiptAsync(IntegrationProfileReceipt receipt)
        => ValueTask.FromResult(DispatchResult.Allow(receipt.SafeCode));

    private ValueTask<DispatchResult> DispatchAllianceRecruitmentOperationAsync(
        AllianceRecruitmentOperation operation)
    {
        if (allianceOperationHandler == null ||
            !string.Equals(operation.TargetOwnerId.Value, configuration.RegisteredOwnerId, StringComparison.Ordinal) ||
            (operation.Kind == AllianceRecruitmentOperationKind.Recruit &&
             !IsAllianceRecruitmentAuthorized(operation, utcNow())))
            return ValueTask.FromResult(DispatchResult.Deny("dad-alliance-central-route-denied"));

        DadAllianceCentralOperationContext context;
        try
        {
            context = operation.Kind switch
            {
                AllianceRecruitmentOperationKind.Recruit => new(
                    operation.OperationId,
                    operation.Header.SenderIslandId.Value,
                    DadAllianceAutoPartyContractMapping.FromRecruitOperation(operation),
                    null),
                AllianceRecruitmentOperationKind.Cancel => new(
                    operation.OperationId,
                    operation.Header.SenderIslandId.Value,
                    null,
                    DadAllianceAutoPartyContractMapping.FromCancelOperation(operation)),
                _ => throw new InvalidOperationException("dad-alliance-central-kind-invalid"),
            };
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return ValueTask.FromResult(DispatchResult.Deny("dad-alliance-central-contract-invalid"));
        }

        lock (gate)
        {
            if (pendingAllianceInbound.Count >= MaximumPendingOutbound)
            {
                var oldest = pendingAllianceInbound.MinBy(static pair => pair.Value.Header.ExpiresAt).Key;
                pendingAllianceInbound.Remove(oldest);
            }
            pendingAllianceInbound[operation.OperationId] = operation;
        }
        try
        {
            allianceOperationHandler(context);
            return ValueTask.FromResult(DispatchResult.Allow("dad-alliance-central-operation-queued"));
        }
        catch
        {
            lock (gate)
                pendingAllianceInbound.Remove(operation.OperationId);
            return ValueTask.FromResult(DispatchResult.Deny("dad-alliance-central-handler-failed"));
        }
    }

    private ValueTask<DispatchResult> DispatchAllianceRecruitmentReceiptAsync(
        AllianceRecruitmentReceipt receipt)
    {
        if (allianceReceiptHandler == null)
            return ValueTask.FromResult(DispatchResult.Deny("dad-alliance-central-receipt-handler-missing"));

        PendingAllianceOutbound pending;
        lock (gate)
        {
            if (!pendingAllianceOutbound.TryGetValue(receipt.OperationId, out pending!))
                return ValueTask.FromResult(DispatchResult.Deny("dad-alliance-central-receipt-unsolicited"));
        }
        var expectedStopGeneration = pending.Cancellation?.StopGeneration ?? pending.Instruction.StopGeneration;
        if (receipt.RecruitmentId != pending.Operation.RecruitmentId ||
            receipt.Header.SenderIslandId != pending.Operation.Header.RecipientIslandId ||
            receipt.ParticipantOwnerId.Value != pending.Instruction.TargetOwnerId ||
            receipt.TargetCharacterId.Value != pending.Instruction.TargetOpaqueCharacterId ||
            receipt.ExpectedAlliance != (AllianceAssignment)(int)pending.Instruction.AssignedAlliance ||
            receipt.Attempt != pending.Instruction.Attempt ||
            receipt.StopGeneration != expectedStopGeneration)
            return ValueTask.FromResult(DispatchResult.Deny("dad-alliance-central-receipt-mismatch"));

        try
        {
            allianceReceiptHandler(new(
                receipt.OperationId,
                pending.Instruction.Clone(),
                pending.Cancellation == null ? null : CloneCancellation(pending.Cancellation),
                DadAllianceAutoPartyContractMapping.FromReceipt(receipt)));
        }
        catch
        {
            return ValueTask.FromResult(DispatchResult.Deny("dad-alliance-central-receipt-handler-failed"));
        }
        lock (gate)
            pendingAllianceOutbound.Remove(receipt.OperationId);
        return ValueTask.FromResult(DispatchResult.Allow("dad-alliance-central-receipt-applied"));
    }

    private ValueTask<DadAutoPartyExecutionResult> ExecuteAsync(PendingExecution pending)
    {
        var operation = pending.Operation;
        IntegrationProfile? profile;
        lock (gate)
            pendingProfiles.TryGetValue(operation.ProposalId, out profile);
        return operation.Kind switch
        {
            ExecutionOperationKind.Prepare => service.Execution.PrepareAsync(operation, profile),
            ExecutionOperationKind.Reserve => service.Execution.ReserveAsync(operation),
            ExecutionOperationKind.Form when formExecutionHandler != null =>
                formExecutionHandler(
                    new DadAutoPartyFormExecutionContext(
                        operation,
                        pending.ExpectedInviter?.Clone(),
                        pending.PartyInviteTargets.Select(static target => target.Clone()).ToArray()),
                    shutdown.Token),
            ExecutionOperationKind.Form => ValueTask.FromResult(new DadAutoPartyExecutionResult(
                operation.OperationId,
                operation.ProposalId,
                operation.Kind,
                ExecutionOutcome.Denied,
                DadRunPhase.Idle,
                "dad-partylist-proof-required",
                operation.ExpectedStateGeneration)),
            ExecutionOperationKind.Queue => service.Execution.QueueAsync(operation),
            ExecutionOperationKind.Cancel => service.Execution.CancelAsync(operation),
            ExecutionOperationKind.Settle => service.Execution.SettleAsync(operation),
            ExecutionOperationKind.Restore => service.Execution.RestoreAsync(operation),
            _ => ValueTask.FromResult(new DadAutoPartyExecutionResult(
                operation.OperationId,
                operation.ProposalId,
                operation.Kind,
                ExecutionOutcome.Denied,
                DadRunPhase.Idle,
                "dad-execution-kind-unsupported",
                operation.ExpectedStateGeneration)),
        };
    }

    private void ObserveActiveExecution()
    {
        if (activeExecution is not { IsCompleted: true } completed || activeExecutionOperation == null)
            return;
        var operation = activeExecutionOperation;
        var pending = activeExecutionPending;
        DadAutoPartyExecutionResult result;
        if (completed.IsCompletedSuccessfully)
        {
            result = completed.Result;
        }
        else
        {
            _ = completed.Exception;
            result = new DadAutoPartyExecutionResult(
                operation.OperationId,
                operation.ProposalId,
                operation.Kind,
                ExecutionOutcome.Denied,
                DadRunPhase.Idle,
                "dad-relay-execution-failed",
                operation.ExpectedStateGeneration);
        }
        activeExecution = null;
        activeExecutionOperation = null;
        activeExecutionPending = null;
        if (result.Outcome == ExecutionOutcome.Accepted && pending != null)
        {
            if (operation.Header.ExpiresAt > utcNow())
            {
                lock (gate)
                    pendingExecutions.Enqueue(pending);
                UpdateSnapshot(result.SafeCode);
                return;
            }
            result = new DadAutoPartyExecutionResult(
                operation.OperationId,
                operation.ProposalId,
                operation.Kind,
                ExecutionOutcome.Denied,
                DadRunPhase.Idle,
                $"dad-inbound-{operation.Kind.ToString().ToLowerInvariant()}-expired",
                operation.ExpectedStateGeneration);
        }
        try
        {
            var messageId = DeriveGuid(operation.OperationId.ToString("N"), "receipt");
            var outcome = result.Outcome;
            var resultSafeCode = result.SafeCode;
            var observedPartyContentIds = ImmutableArray<ulong>.Empty;
            if (operation.Kind == ExecutionOperationKind.Form && outcome == ExecutionOutcome.Completed)
            {
                if (result.PartyReceipt == null ||
                    result.PartyReceipt.MemberCount != result.PartyReceipt.ContentIds.Length ||
                    result.PartyReceipt.ContentIds.IsDefaultOrEmpty ||
                    result.PartyReceipt.ContentIds.Length > 8 ||
                    result.PartyReceipt.ContentIds.Any(static contentId => contentId == 0) ||
                    result.PartyReceipt.ContentIds.Distinct().Count() != result.PartyReceipt.ContentIds.Length)
                {
                    outcome = ExecutionOutcome.Denied;
                    resultSafeCode = "dad-partylist-proof-required";
                }
                else
                {
                    observedPartyContentIds = result.PartyReceipt.ContentIds;
                }
            }
            var receipt = new ExecutionOperationReceipt(
                CreateHeader(
                    operation.Header.SenderIslandId,
                    $"operation-receipt-{operation.OperationId:N}",
                    utcNow() + ParticipantLifetime,
                    messageId),
                operation.OperationId,
                operation.ProposalId,
                operation.OwnerId,
                operation.Kind,
                outcome,
                Math.Max(1, result.ObservedStateGeneration),
                DadAutoPartyConfiguration.NormalizeSafeCode(resultSafeCode) is { Length: > 0 } safeCode
                    ? safeCode
                    : "dad-relay-execution-complete",
                observedPartyContentIds,
                operation.ModuleReference);
            lock (gate)
                _ = TryEnqueueControl(receipt);
        }
        catch (Exception exception) when (exception is ProtocolException or ArgumentException or InvalidOperationException)
        {
            diagnostic("dad-relay-execution-receipt-failed");
        }
        UpdateSnapshot(result.SafeCode);
    }

    private bool TryOpenExpectedInviter(
        ExecutionOperation operation,
        out DadExpectedPartyInviter? expectedInviter,
        out string safeCode)
    {
        expectedInviter = null;
        var locator = operation.InviteLocator;
        var now = utcNow();
        var pairings = configuration.Pairings.Where(item =>
                item.IsActive &&
                string.Equals(item.IslandId, operation.Header.SenderIslandId.Value, StringComparison.Ordinal))
            .Take(2)
            .ToList();
        var pairing = pairings.Count == 1 ? pairings[0] : null;
        if (locator == null || pairing == null ||
            !string.Equals(operation.OwnerId.Value, configuration.RegisteredOwnerId, StringComparison.Ordinal) ||
            !string.Equals(locator.OwnerId.Value, pairing.OwnerId, StringComparison.Ordinal) ||
            !string.Equals(locator.IslandId.Value, operation.Header.SenderIslandId.Value, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(locator.LocatorId) ||
            locator.LocatorId.Length > AutoPartyProtocol.MaximumIdentifierLength ||
            locator.ValidUntil <= now || locator.ValidUntil > operation.Header.ExpiresAt ||
            locator.ValidUntil > now + ParticipantLifetime ||
            locator.OpaqueLocator.IsDefaultOrEmpty || locator.OpaqueLocator.Length > 1024)
        {
            safeCode = "dad-relay-inviter-locator-invalid";
            return false;
        }

        var encoded = locator.OpaqueLocator.ToArray();
        try
        {
            var payload = JsonSerializer.Deserialize<InviteLocatorPayload>(encoded);
            if (payload == null ||
                !IsBoundedLocatorValue(payload.RunId, AutoPartyProtocol.MaximumIdentifierLength) ||
                !IsBoundedLocatorValue(payload.WorkerSessionId, AutoPartyProtocol.MaximumIdentifierLength) ||
                !IsBoundedLocatorValue(payload.AccountKey, AutoPartyProtocol.MaximumIdentifierLength) ||
                !IsBoundedLocatorValue(payload.CharacterKey, AutoPartyProtocol.MaximumIdentifierLength) ||
                !IsBoundedLocatorValue(payload.CharacterName, AutoPartyProtocol.MaximumDisplayLabelLength))
            {
                safeCode = "dad-relay-inviter-locator-invalid";
                return false;
            }

            var parsed = new DadExpectedPartyInviter
            {
                RunId = payload.RunId,
                WorkerSessionId = new DadWorkerSessionId(payload.WorkerSessionId),
                AccountKey = new DadAccountKey(payload.AccountKey),
                CharacterKey = new DadCharacterKey(payload.CharacterKey),
                ContentId = payload.ContentId,
                CharacterName = payload.CharacterName,
                WorldId = payload.WorldId,
            };
            if (DadPartyInvitationAcceptanceTracker.Validate(parsed).Length > 0)
            {
                safeCode = "dad-relay-inviter-locator-invalid";
                return false;
            }
            expectedInviter = parsed;
            safeCode = "dad-relay-inviter-locator-ready";
            return true;
        }
        catch (JsonException)
        {
            safeCode = "dad-relay-inviter-locator-invalid";
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    private bool TryOpenPartyTeardownContext(
        ExecutionOperation operation,
        out DadExpectedPartyInviter? expectedInviter,
        out IReadOnlyList<DadNativePartyInviteTarget> partyInviteTargets,
        out string safeCode)
    {
        expectedInviter = null;
        partyInviteTargets = [];
        safeCode = "dad-relay-restore-locator-invalid";
        var locator = operation.InviteLocator;
        var now = utcNow();
        var pairings = configuration.Pairings.Where(item =>
                item.IsActive &&
                string.Equals(item.IslandId, operation.Header.SenderIslandId.Value, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (locator == null || pairings.Length != 1 ||
            !string.Equals(operation.OwnerId.Value, configuration.RegisteredOwnerId, StringComparison.Ordinal) ||
            !string.Equals(locator.OwnerId.Value, pairings[0].OwnerId, StringComparison.Ordinal) ||
            !string.Equals(locator.IslandId.Value, operation.Header.SenderIslandId.Value, StringComparison.Ordinal) ||
            !IsBoundedLocatorValue(locator.LocatorId, AutoPartyProtocol.MaximumIdentifierLength) ||
            locator.ValidUntil <= now || locator.ValidUntil > operation.Header.ExpiresAt ||
            locator.ValidUntil > now + ParticipantLifetime || locator.OpaqueLocator.IsDefaultOrEmpty ||
            locator.OpaqueLocator.Length > AutoPartyProtocol.MaximumTextValueLength)
            return false;

        var encoded = locator.OpaqueLocator.ToArray();
        try
        {
            var payload = JsonSerializer.Deserialize<PartyTeardownLocatorPayload>(encoded);
            if (payload == null || payload.InviteTargets.IsDefaultOrEmpty || payload.InviteTargets.Length > 7 ||
                !TryMapExpectedInviter(payload.FrozenInviter, out var inviter) ||
                payload.InviteTargets.Any(target => !TryMapInviteTarget(target, operation.FormationOnly, out _)))
                return false;

            var targets = new List<DadNativePartyInviteTarget>(payload.InviteTargets.Length);
            foreach (var targetPayload in payload.InviteTargets)
            {
                if (!TryMapInviteTarget(targetPayload, operation.FormationOnly, out var target))
                    return false;
                targets.Add(target);
            }
            if (!string.Equals(inviter.RunId, targets[0].RunId, StringComparison.Ordinal) ||
                targets.Any(target => !string.Equals(target.RunId, inviter.RunId, StringComparison.Ordinal)) ||
                targets.Select(static target => target.ContentId).Distinct().Count() != targets.Count ||
                targets.Select(static target => target.SlotId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Count ||
                targets.Any(target => target.ContentId == inviter.ContentId ||
                                      string.Equals(target.CharacterKey.Value, inviter.CharacterKey.Value, StringComparison.OrdinalIgnoreCase)))
                return false;

            expectedInviter = inviter;
            partyInviteTargets = targets;
            safeCode = "dad-relay-restore-locator-ready";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    private static bool TryMapExpectedInviter(
        InviteLocatorPayload payload,
        out DadExpectedPartyInviter inviter)
    {
        inviter = new DadExpectedPartyInviter();
        if (payload == null ||
            !IsBoundedLocatorValue(payload.RunId, AutoPartyProtocol.MaximumIdentifierLength) ||
            !IsBoundedLocatorValue(payload.WorkerSessionId, AutoPartyProtocol.MaximumIdentifierLength) ||
            !IsBoundedLocatorValue(payload.AccountKey, AutoPartyProtocol.MaximumIdentifierLength) ||
            !IsBoundedLocatorValue(payload.CharacterKey, AutoPartyProtocol.MaximumIdentifierLength) ||
            !IsBoundedLocatorValue(payload.CharacterName, AutoPartyProtocol.MaximumDisplayLabelLength))
            return false;
        inviter = new DadExpectedPartyInviter
        {
            RunId = payload.RunId,
            WorkerSessionId = new DadWorkerSessionId(payload.WorkerSessionId),
            AccountKey = new DadAccountKey(payload.AccountKey),
            CharacterKey = new DadCharacterKey(payload.CharacterKey),
            ContentId = payload.ContentId,
            CharacterName = payload.CharacterName,
            WorldId = payload.WorldId,
        };
        return DadPartyInvitationAcceptanceTracker.Validate(inviter).Length == 0;
    }

    private static bool TryMapInviteTarget(
        InviteLocatorPayload payload,
        bool formationOnly,
        out DadNativePartyInviteTarget target)
    {
        target = new DadNativePartyInviteTarget();
        if (payload == null || !Enum.TryParse<DadModuleId>(payload.ModuleId, ignoreCase: false, out var moduleId))
            return false;
        target = new DadNativePartyInviteTarget
        {
            RunId = payload.RunId,
            ModuleId = moduleId,
            SlotId = payload.SlotId,
            WorkerSessionId = new DadWorkerSessionId(payload.WorkerSessionId),
            AccountKey = new DadAccountKey(payload.AccountKey),
            CharacterKey = new DadCharacterKey(payload.CharacterKey),
            ContentId = payload.ContentId,
            CharacterName = payload.CharacterName,
            WorldId = payload.WorldId,
        };
        return IsValidNativeInviteTarget(target, formationOnly);
    }

    private bool TryOpenPartyInviteTargets(
        ExecutionOperation operation,
        out IReadOnlyList<DadNativePartyInviteTarget> partyInviteTargets,
        out string safeCode)
    {
        partyInviteTargets = [];
        safeCode = "dad-relay-party-invite-targets-invalid";
        var locators = operation.PartyInviteTargets;
        var now = utcNow();
        var pairings = configuration.Pairings.Where(item =>
                item.IsActive &&
                string.Equals(item.IslandId, operation.Header.SenderIslandId.Value, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (pairings.Length != 1 ||
            !string.Equals(operation.OwnerId.Value, configuration.RegisteredOwnerId, StringComparison.Ordinal) ||
            locators.IsDefaultOrEmpty || locators.Length > 7 ||
            locators.Select(static locator => locator.LocatorId).Distinct(StringComparer.Ordinal).Count() !=
            locators.Length)
            return false;

        var targets = new List<DadNativePartyInviteTarget>(locators.Length);
        foreach (var locator in locators)
        {
            if (!string.Equals(locator.OwnerId.Value, pairings[0].OwnerId, StringComparison.Ordinal) ||
                !string.Equals(locator.IslandId.Value, operation.Header.SenderIslandId.Value, StringComparison.Ordinal) ||
                !IsBoundedLocatorValue(locator.LocatorId, AutoPartyProtocol.MaximumIdentifierLength) ||
                locator.ValidUntil <= now || locator.ValidUntil > operation.Header.ExpiresAt ||
                locator.ValidUntil > now + ParticipantLifetime ||
                locator.OpaqueLocator.IsDefaultOrEmpty || locator.OpaqueLocator.Length > 1024)
                return false;

            var encoded = locator.OpaqueLocator.ToArray();
            try
            {
                var payload = JsonSerializer.Deserialize<InviteLocatorPayload>(encoded);
                if (payload == null ||
                    !Enum.TryParse<DadModuleId>(payload.ModuleId, ignoreCase: false, out var moduleId))
                    return false;
                var target = new DadNativePartyInviteTarget
                {
                    RunId = payload.RunId,
                    ModuleId = moduleId,
                    SlotId = payload.SlotId,
                    WorkerSessionId = new DadWorkerSessionId(payload.WorkerSessionId),
                    AccountKey = new DadAccountKey(payload.AccountKey),
                    CharacterKey = new DadCharacterKey(payload.CharacterKey),
                    ContentId = payload.ContentId,
                    CharacterName = payload.CharacterName,
                    WorldId = payload.WorldId,
                };
                if (!IsValidNativeInviteTarget(target, operation.FormationOnly))
                    return false;
                targets.Add(target);
            }
            catch (JsonException)
            {
                return false;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encoded);
            }
        }

        if (targets.Select(static target => target.ContentId).Distinct().Count() != targets.Count ||
            targets.Select(static target => target.SlotId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Count ||
            targets.Select(static target => target.RunId).Distinct(StringComparer.Ordinal).Count() != 1)
            return false;
        partyInviteTargets = targets;
        safeCode = "dad-relay-party-invite-targets-ready";
        return true;
    }

    private DirectoryQuery BuildDirectoryQuery(PendingDirectoryQuery query, string continuation)
    {
        var messageId = Guid.NewGuid();
        return new DirectoryQuery(
            CreateHeader(new IslandId(RelayIsland), $"directory-query-{messageId:N}", utcNow() + ControlLifetime, messageId),
            query.QueryId,
            query.SearchText,
            query.IncludePromiscuous,
            continuation,
            query.MaximumEntries);
    }

    private DeregistrationRequest BuildDeregistrationRequest(DadAutoPartyPendingDeregistration pending)
    {
        var messageId = Guid.NewGuid();
        return new DeregistrationRequest(
            CreateHeader(new IslandId(RelayIsland), $"deregister-{pending.DeregistrationId:N}", utcNow() + ControlLifetime, messageId),
            pending.DeregistrationId,
            new OwnerId(configuration.RegisteredOwnerId),
            new IslandId(configuration.RegisteredIslandId),
            pending.RevocationGeneration,
            pending.SafeReason);
    }

    private RunProposal BuildRunProposal(DadAutoPartyParticipantCommand command)
    {
        var source = command.Participants is { Count: > 0 }
            ? command.Participants
            : [new DadAutoPartyParticipantRequest(
                command.SlotId,
                command.OwnerId,
                command.IslandId,
                command.OpaqueCharacterId,
                command.RequestedJobId,
                false,
                false)];
        var participants = source.Select(static participant => new ParticipantRequest(
            new OwnerId(participant.OwnerId),
            new IslandId(participant.IslandId),
            new OpaqueCharacterId(participant.OpaqueCharacterId),
            new JobId(participant.RequestedJobId.ToString(System.Globalization.CultureInfo.InvariantCulture))))
            .ToImmutableArray();
        var hashMaterial = string.Join('|', participants.Select(static participant =>
            $"{participant.OwnerId.Value}:{participant.OwnerIslandId.Value}:{participant.CharacterId.Value}:{participant.RequestedJob.Value}"));
        var executionMaterial = command.ExecutionPlan == null
            ? string.Empty
            : JsonSerializer.Serialize(command.ExecutionPlan);
        return new RunProposal(
            CreateHeader(
                new IslandId(command.IslandId),
                $"run-proposal-{command.CommandId:N}",
                Min(command.ExpiresAt, utcNow() + ParticipantLifetime),
                command.CommandId,
                command.ExpectedStateGeneration),
            command.ProposalId,
            new OwnerId(configuration.RegisteredOwnerId),
            new ActivityId(command.ActivityId),
            participants,
            $"sha256:{HashText($"{command.ProposalId:N}|{command.RunId}|{command.ActivityId}|{hashMaterial}|{executionMaterial}").ToLowerInvariant()}",
            command.ExecutionPlan);
    }

    private ExecutionOperation BuildExecutionOperation(DadAutoPartyParticipantCommand command)
    {
        if (command.OperationKind == null)
            throw new InvalidOperationException("dad-relay-execution-kind-missing");
        InviteLocator? locator = null;
        var partyInviteTargets = ImmutableArray<InviteLocator>.Empty;
        var validUntil = Min(command.ExpiresAt, utcNow() + ParticipantLifetime);
        if (command.OperationKind == ExecutionOperationKind.Form)
        {
            var targets = command.PartyInviteTargets ?? [];
            if (command.Inviter != null && targets.Count > 0 || targets.Count > 7)
                throw new InvalidOperationException("dad-relay-form-locator-mode-invalid");
            if (command.Inviter != null)
            {
                var encoded = JsonSerializer.SerializeToUtf8Bytes(new InviteLocatorPayload(
                    command.Inviter.RunId,
                    command.Inviter.WorkerSessionId.Value,
                    command.Inviter.AccountKey.Value,
                    command.Inviter.CharacterKey.Value,
                    command.Inviter.ContentId,
                    command.Inviter.CharacterName,
                    command.Inviter.WorldId));
                try
                {
                    if (encoded.Length > 1024)
                        throw new InvalidOperationException("dad-relay-invite-locator-too-large");
                    locator = new InviteLocator(
                        $"invite-{command.CommandId:N}",
                        new OwnerId(configuration.RegisteredOwnerId),
                        new IslandId(configuration.RegisteredIslandId),
                        validUntil,
                        ImmutableArray.CreateRange(encoded));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(encoded);
                }
            }
            else if (targets.Count > 0)
            {
                var builder = ImmutableArray.CreateBuilder<InviteLocator>(targets.Count);
                for (var index = 0; index < targets.Count; index++)
                {
                    var target = targets[index];
                    if (target == null || !IsValidNativeInviteTarget(target, command.FormationOnly))
                        throw new InvalidOperationException("dad-relay-party-invite-target-invalid");
                    var encoded = JsonSerializer.SerializeToUtf8Bytes(new InviteLocatorPayload(
                        target.RunId,
                        target.WorkerSessionId.Value,
                        target.AccountKey.Value,
                        target.CharacterKey.Value,
                        target.ContentId,
                        target.CharacterName,
                        target.WorldId,
                        target.ModuleId.ToString(),
                        target.SlotId));
                    try
                    {
                        if (encoded.Length > 1024)
                            throw new InvalidOperationException("dad-relay-party-invite-target-too-large");
                        builder.Add(new InviteLocator(
                            $"party-invite-{command.CommandId:N}-{index}",
                            new OwnerId(configuration.RegisteredOwnerId),
                            new IslandId(configuration.RegisteredIslandId),
                            validUntil,
                            ImmutableArray.CreateRange(encoded)));
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(encoded);
                    }
                }
                partyInviteTargets = builder.MoveToImmutable();
            }
        }
        else if (command.OperationKind == ExecutionOperationKind.Restore &&
                 (command.Inviter != null || command.PartyInviteTargets is { Count: > 0 }))
        {
            var targets = command.PartyInviteTargets ?? [];
            if (command.Inviter == null || targets.Count is < 1 or > 7 ||
                DadPartyInvitationAcceptanceTracker.Validate(command.Inviter).Length > 0 ||
                targets.Any(target => target == null || !IsValidNativeInviteTarget(target, command.FormationOnly)))
                throw new InvalidOperationException("dad-relay-restore-locator-invalid");
            var payload = new PartyTeardownLocatorPayload(
                ToInviteLocatorPayload(command.Inviter),
                targets.Select(ToInviteLocatorPayload).ToImmutableArray());
            var encoded = JsonSerializer.SerializeToUtf8Bytes(payload);
            try
            {
                if (encoded.Length > AutoPartyProtocol.MaximumTextValueLength)
                    throw new InvalidOperationException("dad-relay-restore-locator-too-large");
                locator = new InviteLocator(
                    $"restore-{command.CommandId:N}",
                    new OwnerId(configuration.RegisteredOwnerId),
                    new IslandId(configuration.RegisteredIslandId),
                    validUntil,
                    ImmutableArray.CreateRange(encoded));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encoded);
            }
        }
        else if (command.Inviter != null || command.PartyInviteTargets is { Count: > 0 })
        {
            throw new InvalidOperationException("dad-relay-invite-locator-unexpected");
        }
        return new ExecutionOperation(
            CreateHeader(
                new IslandId(command.IslandId),
                $"execution-{command.CommandId:N}",
                Min(command.ExpiresAt, utcNow() + ParticipantLifetime),
                command.CommandId,
                command.ExpectedStateGeneration),
            command.CommandId,
            command.ProposalId,
            new OwnerId(command.OwnerId),
            command.OperationKind.Value,
            new ActivityId(command.ActivityId),
            new OpaqueCharacterId(command.OpaqueCharacterId),
            new JobId(command.RequestedJobId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            locator,
            Math.Max(1, command.ExpectedStateGeneration),
            command.FormationOnly,
            partyInviteTargets,
            command.ExecutionModuleReference);
    }

    private static InviteLocatorPayload ToInviteLocatorPayload(DadExpectedPartyInviter inviter)
        => new(
            inviter.RunId,
            inviter.WorkerSessionId.Value,
            inviter.AccountKey.Value,
            inviter.CharacterKey.Value,
            inviter.ContentId,
            inviter.CharacterName,
            inviter.WorldId);

    private static InviteLocatorPayload ToInviteLocatorPayload(DadNativePartyInviteTarget target)
        => new(
            target.RunId,
            target.WorkerSessionId.Value,
            target.AccountKey.Value,
            target.CharacterKey.Value,
            target.ContentId,
            target.CharacterName,
            target.WorldId,
            target.ModuleId.ToString(),
            target.SlotId);

    private Revocation BuildRevocation(DadAutoPartyParticipantCommand command)
        => new(
            CreateHeader(
                new IslandId(RelayIsland),
                $"revocation-{command.CommandId:N}",
                Min(command.ExpiresAt, utcNow() + ParticipantLifetime),
                command.CommandId,
                command.RevocationGeneration),
            command.CommandId,
            new OwnerId(configuration.RegisteredOwnerId),
            RevocationTargetKind.Identity,
            command.IslandId,
            Math.Max(1, command.RevocationGeneration),
            DadAutoPartyConfiguration.NormalizeSafeCode(command.SafeCode) is { Length: > 0 } reason
                ? reason
                : "dad-remote-route-revoked");

    private ContractHeader CreateHeader(
        IslandId recipient,
        string idempotencyKey,
        DateTimeOffset expiresAt,
        Guid? messageId = null,
        long? generation = null)
    {
        var now = utcNow();
        if (expiresAt <= now)
            throw new InvalidOperationException("dad-relay-contract-expired");
        long recipientVersion;
        if (string.Equals(recipient.Value, RelayIsland, StringComparison.Ordinal))
        {
            recipientVersion = configuration.RelayKeyGeneration;
        }
        else
        {
            recipientVersion = configuration.Pairings
                .Where(static pairing => pairing.IsActive)
                .SingleOrDefault(pairing => string.Equals(pairing.IslandId, recipient.Value, StringComparison.Ordinal))
                ?.KeyGeneration ?? ResolveAttestedRecipientKeyVersion(recipient, now) ??
                throw new InvalidOperationException("dad-relay-recipient-key-unavailable");
        }
        var nonce = RandomNumberGenerator.GetBytes(AutoPartyProtocol.ContractNonceBytes);
        try
        {
            return new ContractHeader(
                AutoPartyProtocol.CurrentVersion,
                messageId ?? Guid.NewGuid(),
                idempotencyKey,
                new IslandId(configuration.RegisteredIslandId),
                recipient,
                now,
                expiresAt,
                Interlocked.Increment(ref nextSequence),
                Math.Max(1, generation ?? configuration.StateGeneration),
                configuration.EndpointKeyGeneration,
                recipientVersion,
                ContractHeader.CreateNonce(nonce),
                ImmutableArray<int>.Empty);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
        }
    }

    private EndpointPublicKeys LocalPublicKeys()
        => new(
            configuration.EndpointKeyGeneration,
            $"dad-ed25519-{configuration.EndpointKeyGeneration}",
            ImmutableArray.CreateRange(Convert.FromBase64String(configuration.SigningPublicKey)),
            $"dad-x25519-{configuration.EndpointKeyGeneration}",
            ImmutableArray.CreateRange(Convert.FromBase64String(configuration.EncryptionPublicKey)));

    private bool TryEnqueueControl(IAutoPartyContract contract)
    {
        if (pendingOutbound.Count >= MaximumPendingOutbound)
            return false;
        ValidateOutbound(contract);
        if (pendingOutbound.Any(item => item.Contract.Header.MessageId == contract.Header.MessageId))
            return true;
        pendingOutbound.Enqueue(new PendingOutboundContract(contract, utcNow()));
        return true;
    }

    private static void ValidateOutbound(IAutoPartyContract contract)
    {
        byte[] encoded = contract switch
        {
            RegistrationHello value => CanonicalCborCodec.EncodeUnsigned(value),
            DeregistrationRequest value => CanonicalCborCodec.EncodeUnsigned(value),
            PairingNotice value => CanonicalCborCodec.EncodeUnsigned(value),
            PairingApproval value => CanonicalCborCodec.EncodeUnsigned(value),
            PrivateListingUpdate value => CanonicalCborCodec.EncodeUnsigned(value),
            DirectoryQuery value => CanonicalCborCodec.EncodeUnsigned(value),
            RegisteredRequesterAccessRequest value => CanonicalCborCodec.EncodeUnsigned(value),
            DeauthenticationNotice value => CanonicalCborCodec.EncodeUnsigned(value),
            Revocation value => CanonicalCborCodec.EncodeUnsigned(value),
            RunProposal value => CanonicalCborCodec.EncodeUnsigned(value),
            Reservation value => CanonicalCborCodec.EncodeUnsigned(value),
            PreflightResult value => CanonicalCborCodec.EncodeUnsigned(value),
            SessionLease value => CanonicalCborCodec.EncodeUnsigned(value),
            ParticipantInviteLocator value => CanonicalCborCodec.EncodeUnsigned(value),
            ExecutionOperation value => CanonicalCborCodec.EncodeUnsigned(value),
            ExecutionOperationReceipt value => CanonicalCborCodec.EncodeUnsigned(value),
            AllianceRecruitmentOperation value => CanonicalCborCodec.EncodeUnsigned(value),
            AllianceRecruitmentReceipt value => CanonicalCborCodec.EncodeUnsigned(value),
            _ => throw new ProtocolException(ProtocolFailureCode.InvalidContractType, "unknown-contract-type"),
        };
        CryptographicOperations.ZeroMemory(encoded);
    }

    private bool IsAllianceRecruitmentAuthorized(
        AllianceRecruitmentOperation operation,
        DateTimeOffset now)
    {
        var senderIslandId = operation.Header.SenderIslandId.Value;
        var handle = operation.TargetCharacterId.Value;
        var pairings = configuration.Pairings
            .Where(pairing => pairing.IsActive &&
                              string.Equals(pairing.IslandId, senderIslandId, StringComparison.Ordinal))
            .Take(2)
            .ToList();
        if (pairings.Count == 1)
        {
            var pairing = pairings[0];
            return DadAutoPartyShareRules.Allows(
                pairing.LocalSharePolicy,
                handle,
                paired: true,
                sameHomeGuild: string.Equals(
                    pairing.HomeGuildScope,
                    configuration.HomeGuildScope,
                    StringComparison.Ordinal));
        }

        lock (gate)
        {
            var route = attestedRoutes.SingleOrDefault(pair =>
                pair.Value.ValidUntil > now &&
                string.Equals(pair.Key.FirstIslandId, senderIslandId, StringComparison.Ordinal) &&
                string.Equals(pair.Key.SecondIslandId, configuration.RegisteredIslandId, StringComparison.Ordinal) &&
                string.Equals(pair.Value.SharingOwnerId, configuration.RegisteredOwnerId, StringComparison.Ordinal) &&
                pair.Value.AuthorizedCharacters.Contains(handle, StringComparer.Ordinal));
            if (route.Equals(default(KeyValuePair<RouteKey, AttestedRoute>)))
                return false;
            return configuration.Listings.Any(listing =>
                listing.IsValid && listing.Available && listing.ExpiresAtUtc > now.UtcDateTime &&
                string.Equals(listing.OwnerId, configuration.RegisteredOwnerId, StringComparison.Ordinal) &&
                string.Equals(listing.SharingIslandId, configuration.RegisteredIslandId, StringComparison.Ordinal) &&
                listing.EffectiveShareMode == DadAutoPartyCharacterShareMode.CharacterList &&
                string.Equals(listing.EffectivePolicyHash, route.Value.PolicyHash, StringComparison.Ordinal) &&
                string.Equals(listing.OpaqueCharacterId, handle, StringComparison.Ordinal));
        }
    }

    private bool IsDirectSenderAllowed(IslandId sender, long keyVersion, DateTimeOffset now)
    {
        if (configuration.Deauthentications.Any(item =>
                string.Equals(item.PeerIslandId, sender.Value, StringComparison.Ordinal)))
            return false;
        if (configuration.Pairings.Any(pairing => pairing.IsActive &&
                pairing.KeyGeneration == keyVersion &&
                string.Equals(pairing.IslandId, sender.Value, StringComparison.Ordinal)))
            return true;
        lock (gate)
            return attestedRoutes.Any(pair => pair.Value.ValidUntil > now &&
                ((string.Equals(pair.Key.FirstIslandId, sender.Value, StringComparison.Ordinal) &&
                  string.Equals(pair.Key.SecondIslandId, configuration.RegisteredIslandId, StringComparison.Ordinal) &&
                  pair.Value.RequesterKeyVersion == keyVersion) ||
                 (string.Equals(pair.Key.SecondIslandId, sender.Value, StringComparison.Ordinal) &&
                  string.Equals(pair.Key.FirstIslandId, configuration.RegisteredIslandId, StringComparison.Ordinal) &&
                  pair.Value.SharingKeyVersion == keyVersion)));
    }

    private bool IsParticipantRouteAllowed(
        IslandId islandId,
        OwnerId ownerId,
        long keyVersion,
        DateTimeOffset now)
    {
        if (configuration.Deauthentications.Any(item =>
                string.Equals(item.PeerIslandId, islandId.Value, StringComparison.Ordinal)))
            return false;
        if (configuration.Pairings.Count(pairing => pairing.IsActive && pairing.KeyGeneration == keyVersion &&
                string.Equals(pairing.IslandId, islandId.Value, StringComparison.Ordinal) &&
                string.Equals(pairing.OwnerId, ownerId.Value, StringComparison.Ordinal)) == 1)
            return true;
        lock (gate)
            return attestedRoutes.Count(pair => pair.Value.ValidUntil > now &&
                ((string.Equals(pair.Key.FirstIslandId, islandId.Value, StringComparison.Ordinal) &&
                  string.Equals(pair.Value.RequesterOwnerId, ownerId.Value, StringComparison.Ordinal) &&
                  pair.Value.RequesterKeyVersion == keyVersion) ||
                 (string.Equals(pair.Key.SecondIslandId, islandId.Value, StringComparison.Ordinal) &&
                  string.Equals(pair.Value.SharingOwnerId, ownerId.Value, StringComparison.Ordinal) &&
                  pair.Value.SharingKeyVersion == keyVersion))) == 1;
    }

    private long? ResolveAttestedRecipientKeyVersion(IslandId recipient, DateTimeOffset now)
    {
        lock (gate)
        {
            var matches = attestedRoutes.Where(pair => pair.Value.ValidUntil > now &&
                    ((string.Equals(pair.Key.FirstIslandId, configuration.RegisteredIslandId, StringComparison.Ordinal) &&
                      string.Equals(pair.Key.SecondIslandId, recipient.Value, StringComparison.Ordinal)) ||
                     (string.Equals(pair.Key.SecondIslandId, configuration.RegisteredIslandId, StringComparison.Ordinal) &&
                      string.Equals(pair.Key.FirstIslandId, recipient.Value, StringComparison.Ordinal))))
                .Take(2)
                .ToList();
            if (matches.Count != 1)
                return null;
            var match = matches[0];
            return string.Equals(match.Key.FirstIslandId, recipient.Value, StringComparison.Ordinal)
                ? match.Value.RequesterKeyVersion
                : match.Value.SharingKeyVersion;
        }
    }

    private DateTimeOffset? GetListingRouteExpiry(DadAutoPartyListing listing, DateTimeOffset now)
    {
        if (listing.EffectiveShareMode != DadAutoPartyCharacterShareMode.CharacterList ||
            string.IsNullOrWhiteSpace(listing.OwnerId) ||
            string.IsNullOrWhiteSpace(listing.SharingIslandId) ||
            string.IsNullOrWhiteSpace(listing.EffectivePolicyHash))
            return null;
        lock (gate)
        {
            var route = attestedRoutes.SingleOrDefault(pair =>
                pair.Value.ValidUntil > now &&
                string.Equals(pair.Key.FirstIslandId, configuration.RegisteredIslandId, StringComparison.Ordinal) &&
                string.Equals(pair.Key.SecondIslandId, listing.SharingIslandId, StringComparison.Ordinal) &&
                string.Equals(pair.Value.RequesterOwnerId, configuration.RegisteredOwnerId, StringComparison.Ordinal) &&
                string.Equals(pair.Value.SharingOwnerId, listing.OwnerId, StringComparison.Ordinal) &&
                string.Equals(pair.Value.PolicyHash, listing.EffectivePolicyHash, StringComparison.Ordinal));
            return route.Equals(default(KeyValuePair<RouteKey, AttestedRoute>))
                ? null
                : route.Value.ValidUntil;
        }
    }

    private bool MatchesLocalEndpoint(
        OwnerId ownerId,
        IslandId islandId,
        EndpointPublicKeys keys,
        string fingerprint)
    {
        if (!string.Equals(ownerId.Value, configuration.RegisteredOwnerId, StringComparison.Ordinal) ||
            !string.Equals(islandId.Value, configuration.RegisteredIslandId, StringComparison.Ordinal) ||
            keys.KeyVersion != configuration.EndpointKeyGeneration ||
            !string.Equals(fingerprint, configuration.RegistrationFingerprint, StringComparison.Ordinal))
            return false;
        byte[]? signing = null;
        byte[]? agreement = null;
        try
        {
            signing = Convert.FromBase64String(configuration.SigningPublicKey);
            agreement = Convert.FromBase64String(configuration.EncryptionPublicKey);
            return signing.AsSpan().SequenceEqual(keys.Ed25519PublicKey.AsSpan()) &&
                   agreement.AsSpan().SequenceEqual(keys.X25519PublicKey.AsSpan());
        }
        catch (FormatException)
        {
            return false;
        }
        finally
        {
            if (signing != null) CryptographicOperations.ZeroMemory(signing);
            if (agreement != null) CryptographicOperations.ZeroMemory(agreement);
        }
    }

    private int RemoveTransientRoutes(Func<RouteKey, AttestedRoute, bool> predicate)
    {
        lock (gate)
        {
            var removed = attestedRoutes
                .Where(pair => predicate(pair.Key, pair.Value))
                .ToList();
            foreach (var pair in removed)
            {
                attestedRoutes.Remove(pair.Key);
                keyResolver?.RemoveTransientPublicKeys(pair.Key.FirstIslandId);
                keyResolver?.RemoveTransientPublicKeys(pair.Key.SecondIslandId);
                foreach (var listing in configuration.Listings.Where(listing =>
                             string.Equals(listing.OwnerId, pair.Value.SharingOwnerId, StringComparison.Ordinal) &&
                             string.Equals(listing.SharingIslandId, pair.Key.SecondIslandId, StringComparison.Ordinal) &&
                             string.Equals(listing.EffectivePolicyHash, pair.Value.PolicyHash, StringComparison.Ordinal)))
                    listing.TransientRouteExpiresAtUtc = null;
            }
            return removed.Count;
        }
    }

    private int RemoveInboundRuntimeTargets(
        Func<InboundRuntimeTargetKey, InboundRuntimeTarget, bool> predicate)
    {
        lock (gate)
        {
            var keys = inboundRuntimeTargets
                .Where(pair => predicate(pair.Key, pair.Value))
                .Select(static pair => pair.Key)
                .ToList();
            foreach (var key in keys)
                inboundRuntimeTargets.Remove(key);
            return keys.Count;
        }
    }

    private void CommitReplay(ContractHeader header)
    {
        while (replayedMessages.Count >= MaximumReplayEntries)
            replayedMessages.Remove(replayedMessages.MinBy(static pair => pair.Value).Key);
        replayedMessages[header.MessageId] = header.ExpiresAt;
    }

    private void ExpireState()
    {
        var now = utcNow();
        foreach (var messageId in replayedMessages.Where(pair => pair.Value <= now).Select(static pair => pair.Key).ToList())
            replayedMessages.Remove(messageId);
        lock (gate)
        {
            foreach (var messageId in awaitingRelayReceipts
                         .Where(pair => pair.Value.Contract.Header.ExpiresAt <= now)
                         .Select(static pair => pair.Key)
                         .ToList())
                awaitingRelayReceipts.Remove(messageId);
            foreach (var accessId in pendingAccessRequests
                         .Where(pair => pair.Value.ExpiresAt <= now)
                         .Select(static pair => pair.Key)
                         .ToList())
                pendingAccessRequests.Remove(accessId);
            RemoveTransientRoutes((_, route) => route.ValidUntil <= now);
            foreach (var commandId in participantContracts
                         .Where(pair => pair.Value.Header.ExpiresAt <= now)
                         .Select(static pair => pair.Key)
                         .ToList())
                participantContracts.Remove(commandId);
            foreach (var proposalId in pendingProfiles
                         .Where(pair => pair.Value.Header.ExpiresAt <= now)
                         .Select(static pair => pair.Key)
                         .ToList())
                pendingProfiles.Remove(proposalId);
            foreach (var operationId in pendingAllianceOutbound
                         .Where(pair => pair.Value.Operation.Header.ExpiresAt <= now)
                         .Select(static pair => pair.Key)
                         .ToList())
                pendingAllianceOutbound.Remove(operationId);
            foreach (var operationId in pendingAllianceInbound
                         .Where(pair => pair.Value.Header.ExpiresAt <= now)
                         .Select(static pair => pair.Key)
                         .ToList())
                pendingAllianceInbound.Remove(operationId);
            foreach (var key in inboundRuntimeTargets
                         .Where(pair => pair.Value.ExpiresAt <= now)
                         .Select(static pair => pair.Key)
                         .ToList())
                inboundRuntimeTargets.Remove(key);
            if (LastPairingChallenge is { } challenge && challenge.ExpiresAtUtc <= now.UtcDateTime)
                LastPairingChallenge = null;
            while (pendingOutbound.Count > 0 && pendingOutbound.Peek().Contract.Header.ExpiresAt <= now)
                _ = pendingOutbound.Dequeue();
        }
    }

    private void UpdateSnapshot(string safeCode, bool? running = null)
    {
        lock (gate)
        {
            Volatile.Write(ref snapshot, Snapshot with
            {
                Running = running ?? Snapshot.Running,
                SafeCode = DadAutoPartyConfiguration.NormalizeSafeCode(safeCode) is { Length: > 0 } normalized
                    ? normalized
                    : "dad-relay-status-invalid",
                ObservedAt = utcNow(),
                PendingOutboundCount = pendingOutbound.Count,
                AwaitingRelayReceiptCount = awaitingRelayReceipts.Count,
                PendingExecutionCount = pendingExecutions.Count + (activeExecution == null ? 0 : 1),
            });
        }
    }

    private DadAutoPartyPolicyDecision Decision(bool allowed, string safeCode)
        => new(allowed, safeCode, Math.Max(1, configuration.StateGeneration));

    private static CharacterSharePolicy ToProtocolPolicy(DadAutoPartySharePolicy policy)
        => new(
            (CharacterShareMode)(int)policy.Mode,
            policy.CharacterHandles.Select(static value => new OpaqueCharacterId(value)).ToImmutableArray(),
            policy.Enabled,
            policy.Revision,
            new DateTimeOffset(DateTime.SpecifyKind(policy.UpdatedAtUtc, DateTimeKind.Utc)));

    private static DadAutoPartySharePolicy ToDadPolicy(CharacterSharePolicy policy)
        => new()
        {
            Mode = (DadAutoPartyCharacterShareMode)(int)policy.Mode,
            CharacterHandles = policy.CharacterHandles.Select(static value => value.Value).ToList(),
            Enabled = policy.Enabled,
            Revision = policy.Revision,
            UpdatedAtUtc = policy.UpdatedAt.UtcDateTime,
        };

    private void TrimAllianceOutbound()
    {
        while (pendingAllianceOutbound.Count >= MaximumPendingOutbound)
        {
            var oldest = pendingAllianceOutbound
                .MinBy(static pair => pair.Value.Operation.Header.ExpiresAt).Key;
            pendingAllianceOutbound.Remove(oldest);
        }
    }

    private static DadAllianceRecruitmentCancellationDto CloneCancellation(
        DadAllianceRecruitmentCancellationDto source)
        => new()
        {
            RecruitmentId = source.RecruitmentId,
            CoordinatorWorkerSessionId = source.CoordinatorWorkerSessionId,
            TargetWorkerSessionId = source.TargetWorkerSessionId,
            TargetIslandId = source.TargetIslandId,
            TargetOwnerId = source.TargetOwnerId,
            TargetOpaqueCharacterId = source.TargetOpaqueCharacterId,
            TargetCharacterKey = source.TargetCharacterKey,
            StopGeneration = source.StopGeneration,
            RequestedAtUtc = source.RequestedAtUtc,
            Reason = source.Reason,
        };

    private static bool SameType<T>(string payloadType)
        where T : IAutoPartyContract
        => string.Equals(payloadType, ProtocolContractRegistry.GetTypeId<T>(), StringComparison.Ordinal);

    private static bool IsRelayControl(IAutoPartyContract contract)
        => string.Equals(contract.Header.RecipientIslandId.Value, RelayIsland, StringComparison.Ordinal);

    private static string HashText(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static Guid DeriveGuid(string first, string second)
    {
        var bytes = Encoding.UTF8.GetBytes($"dad.autoparty.semantic/v1|{first}|{second}");
        try
        {
            var digest = SHA256.HashData(bytes);
            try
            {
                return new Guid(digest.AsSpan(0, 16));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static DateTimeOffset Min(DateTimeOffset first, DateTimeOffset second) => first <= second ? first : second;

    private static bool IsBoundedLocatorValue(string? value, int maximumLength)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && value == value.Trim() &&
           value.All(static character => !char.IsControl(character));

    private static bool IsValidNativeInviteTarget(DadNativePartyInviteTarget target, bool allowNoModule = false)
        => (allowNoModule || target.ModuleId != DadModuleId.None) && target.ContentId != 0 && target.WorldId != 0 &&
           IsBoundedLocatorValue(target.RunId, AutoPartyProtocol.MaximumIdentifierLength) &&
           IsBoundedLocatorValue(target.ModuleId.ToString(), AutoPartyProtocol.MaximumIdentifierLength) &&
           IsBoundedLocatorValue(target.SlotId, AutoPartyProtocol.MaximumIdentifierLength) &&
           IsBoundedLocatorValue(target.WorkerSessionId.Value, AutoPartyProtocol.MaximumIdentifierLength) &&
           IsBoundedLocatorValue(target.AccountKey.Value, AutoPartyProtocol.MaximumIdentifierLength) &&
           IsBoundedLocatorValue(target.CharacterKey.Value, AutoPartyProtocol.MaximumIdentifierLength) &&
           IsBoundedLocatorValue(target.CharacterName, AutoPartyProtocol.MaximumDisplayLabelLength);

    private static AutoPartyTransportSendResult Denied(Guid envelopeId, string safeCode)
        => new(false, safeCode, envelopeId);

    private readonly record struct DispatchResult(bool Accepted, string SafeCode)
    {
        public static DispatchResult Allow(string safeCode) => new(true, safeCode);
        public static DispatchResult Deny(string safeCode) => new(false, safeCode);
    }

    private sealed record PendingOutboundContract(IAutoPartyContract Contract, DateTimeOffset QueuedAt);

    private sealed record PendingAllianceOutbound(
        AllianceRecruitmentOperation Operation,
        DadAllianceRecruitmentInstructionDto Instruction,
        DadAllianceRecruitmentCancellationDto? Cancellation);

    private sealed record PendingExecution(
        ExecutionOperation Operation,
        DadExpectedPartyInviter? ExpectedInviter,
        IReadOnlyList<DadNativePartyInviteTarget> PartyInviteTargets);

    private sealed record PendingDirectoryQuery(
        Guid QueryId,
        string SearchText,
        bool IncludePromiscuous,
        int MaximumEntries);

    private sealed record PendingAccessRequest(
        Guid AccessRequestId,
        bool LocalRequester,
        string ExpectedRequesterOwnerId,
        string ExpectedSharingOwnerId,
        string SharingIslandId,
        long Generation,
        ImmutableArray<string> RequestedCharacters,
        string PolicyHash,
        DateTimeOffset ExpiresAt);

    private readonly record struct RouteKey(string FirstIslandId, string SecondIslandId);

    private readonly record struct PendingInboundProposalEvaluation(
        Guid ProposalId,
        long ConfigurationGeneration,
        bool Allowed,
        string SafeCode,
        DadAutoPartyInboundAdmissionResult Admission);

    private readonly record struct AttestedRoute(
        string RequesterOwnerId,
        string SharingOwnerId,
        long RequesterKeyVersion,
        long SharingKeyVersion,
        string PolicyHash,
        ImmutableArray<string> AuthorizedCharacters,
        DateTimeOffset ValidUntil);

    private readonly record struct InboundRuntimeTargetKey(Guid ProposalId, string OpaqueCharacterId);

    private sealed record InboundRuntimeTarget(
        string SlotId,
        DadNativePartyInviteTarget Target,
        EndpointExecutionPlan ExecutionPlan,
        string SenderIslandId,
        string OwnerId,
        DateTimeOffset ExpiresAt,
        DadExpectedPartyInviter? FrozenInviter = null,
        IReadOnlyList<DadNativePartyInviteTarget>? PartyInviteTargets = null);

    private sealed record InviteLocatorPayload(
        string RunId,
        string WorkerSessionId,
        string AccountKey,
        string CharacterKey,
        ulong ContentId,
        string CharacterName,
        ushort WorldId,
        string ModuleId = "",
        string SlotId = "");

    private sealed record PartyTeardownLocatorPayload(
        InviteLocatorPayload FrozenInviter,
        ImmutableArray<InviteLocatorPayload> InviteTargets);
}
