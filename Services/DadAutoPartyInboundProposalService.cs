using System.Collections.Immutable;
using System.Text.Json;
using AutoParty.Contracts;
using dad.Models;

namespace dad.Services;

internal sealed record DadAutoPartyInboundProposalState(
    RunProposal Proposal,
    ImmutableArray<ParticipantRequest> OwnedParticipants,
    DateTimeOffset RetainedAt,
    ImmutableArray<Reservation> Reservations,
    PreflightResult? Preflight,
    SessionLease? Lease,
    ImmutableArray<Guid> AcknowledgedMessageIds,
    long StateGeneration,
    string SafeCode,
    long RenewalGeneration = 0)
{
    public bool ResponsesPrepared => !Reservations.IsDefaultOrEmpty && Preflight != null;

    public bool AdmissionReady => ResponsesPrepared && Preflight!.Ready && Lease != null;

    public IEnumerable<IAutoPartyContract> Responses()
    {
        foreach (var reservation in Reservations.IsDefault ? [] : Reservations)
            yield return reservation;
        if (Preflight != null)
            yield return Preflight;
        if (Lease != null)
            yield return Lease;
    }
}

internal interface IDadAutoPartyInboundProposalStore
{
    IReadOnlyList<DadAutoPartyInboundProposalState> Load();
    void Save(IReadOnlyList<DadAutoPartyInboundProposalState> states);
}

internal sealed class DadAutoPartyMemoryInboundProposalStore : IDadAutoPartyInboundProposalStore
{
    private IReadOnlyList<DadAutoPartyInboundProposalState> states = [];

    public IReadOnlyList<DadAutoPartyInboundProposalState> Load() => states;

    public void Save(IReadOnlyList<DadAutoPartyInboundProposalState> value)
        => states = value.ToArray();
}

internal sealed class DadAutoPartyFileInboundProposalStore : IDadAutoPartyInboundProposalStore
{
    private const int MaximumRecords = 64;
    private const int MaximumRecordBytes = 64 * 1024;
    private const int MaximumStateBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string statePath;
    private readonly DadAtomicFileStore atomicFileStore;

    public DadAutoPartyFileInboundProposalStore(
        string rootPath,
        DadAtomicFileStore? atomicFileStore = null)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("An AutoParty inbound-proposal root is required.", nameof(rootPath));
        var root = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(root);
        statePath = Path.GetFullPath(Path.Combine(root, "pending-inbound-proposals.json"));
        var expectedRoot = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!statePath.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The inbound-proposal path escaped its configured root.");
        this.atomicFileStore = atomicFileStore ?? new DadAtomicFileStore();
    }

    public IReadOnlyList<DadAutoPartyInboundProposalState> Load()
    {
        try
        {
            var file = new FileInfo(statePath);
            if (!file.Exists || file.Length is <= 0 or > MaximumStateBytes)
                return [];
            var states = JsonSerializer.Deserialize<List<DadAutoPartyInboundProposalState>>(
                File.ReadAllText(statePath),
                JsonOptions);
            if (states == null || states.Count > MaximumRecords || states.Any(state => state == null))
                return [];
            return states.All(state => JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions).Length <= MaximumRecordBytes)
                ? states
                : [];
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return [];
        }
    }

    public void Save(IReadOnlyList<DadAutoPartyInboundProposalState> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        if (states.Count > MaximumRecords)
            throw new InvalidOperationException("The inbound-proposal record bound was exceeded.");
        if (states.Any(state => state == null ||
                JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions).Length > MaximumRecordBytes))
            throw new InvalidOperationException("An inbound proposal exceeded its storage bound.");
        var json = JsonSerializer.Serialize(states, JsonOptions);
        if (json.Length is <= 0 or > MaximumStateBytes)
            throw new InvalidOperationException("The inbound-proposal state exceeded its storage bound.");
        atomicFileStore.Write(statePath, json);
    }
}

internal sealed class DadAutoPartyInboundProposalService
{
    private const int MaximumSessions = 64;
    private readonly object gate = new();
    private readonly DadAutoPartyConfiguration configuration;
    private readonly IDadAutoPartyInboundProposalStore store;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly Func<Guid, string, bool> restoreBeforeRemoval;
    private readonly Func<RunProposal, DateTimeOffset, DateTimeOffset, bool> renewPolicy;
    private readonly Dictionary<Guid, DadAutoPartyInboundProposalState> states = [];

    public DadAutoPartyInboundProposalService(
        DadAutoPartyConfiguration configuration,
        IDadAutoPartyInboundProposalStore? store = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<Guid, string, bool>? restoreBeforeRemoval = null,
        Func<RunProposal, DateTimeOffset, DateTimeOffset, bool>? renewPolicy = null)
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.store = store ?? new DadAutoPartyMemoryInboundProposalStore();
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        this.restoreBeforeRemoval = restoreBeforeRemoval ?? ((_, _) => true);
        this.renewPolicy = renewPolicy ?? ((_, _, _) => true);
        var now = this.utcNow();
        foreach (var state in this.store.Load().Take(MaximumSessions))
        {
            if (IsValidState(state, now))
                states[state.Proposal.ProposalId] = state;
        }
        Persist();
    }

    public bool TryRetain(
        RunProposal proposal,
        out DadAutoPartyInboundProposalState state,
        out string safeCode)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        try
        {
            _ = CanonicalCborCodec.EncodeUnsigned(proposal);
        }
        catch (ProtocolException)
        {
            state = default!;
            safeCode = "dad-inbound-proposal-invalid";
            return false;
        }
        Sweep(utcNow());
        lock (gate)
        {
            if (!TryResolveOwnedParticipants(proposal, out var owned, out safeCode))
            {
                state = default!;
                return false;
            }
            if (states.TryGetValue(proposal.ProposalId, out var existing))
            {
                if (!CanonicalCborCodec.EncodeUnsigned(existing.Proposal)
                        .AsSpan().SequenceEqual(CanonicalCborCodec.EncodeUnsigned(proposal)))
                {
                    state = default!;
                    safeCode = "dad-inbound-proposal-conflict";
                    return false;
                }
                state = existing;
                safeCode = "dad-inbound-proposal-idempotent";
                return true;
            }
            if (states.Count >= MaximumSessions)
            {
                state = default!;
                safeCode = "dad-inbound-proposal-capacity";
                return false;
            }

            state = new(
                proposal,
                owned,
                utcNow(),
                [],
                null,
                null,
                [],
                0,
                "dad-inbound-proposal-retained");
            states.Add(proposal.ProposalId, state);
            try
            {
                Persist();
            }
            catch
            {
                states.Remove(proposal.ProposalId);
                state = default!;
                safeCode = "dad-inbound-proposal-store-failed";
                return false;
            }
            safeCode = state.SafeCode;
            return true;
        }
    }

    public IReadOnlyList<DadAutoPartyInboundProposalState> Retained(int maximum)
    {
        lock (gate)
        {
            var now = utcNow();
            return states.Values
                .Where(state => !state.AdmissionReady && state.Proposal.Header.ExpiresAt > now)
                .OrderBy(static state => state.RetainedAt)
                .Take(Math.Clamp(maximum, 1, 8))
                .ToArray();
        }
    }

    public IReadOnlyList<DadAutoPartyInboundProposalState> Active(int maximum)
    {
        lock (gate)
        {
            var now = utcNow();
            return states.Values
                .Where(state => state.Proposal.Header.ExpiresAt > now)
                .OrderBy(static state => state.RetainedAt)
                .Take(Math.Clamp(maximum, 1, 8))
                .ToArray();
        }
    }

    public bool TryGetRetained(Guid proposalId, out DadAutoPartyInboundProposalState state)
    {
        lock (gate)
        {
            if (states.TryGetValue(proposalId, out state!) &&
                !state.ResponsesPrepared && state.Proposal.Header.ExpiresAt > utcNow())
                return true;
            state = default!;
            return false;
        }
    }

    public bool TryGetActive(Guid proposalId, out DadAutoPartyInboundProposalState state)
    {
        lock (gate)
        {
            if (states.TryGetValue(proposalId, out state!) &&
                state.Proposal.Header.ExpiresAt > utcNow())
                return true;
            state = default!;
            return false;
        }
    }

    public bool TryApplyRenewal(
        ProposalRenewal renewal,
        out DadAutoPartyInboundProposalState renewed,
        out string safeCode)
    {
        ArgumentNullException.ThrowIfNull(renewal);
        var now = utcNow();
        lock (gate)
        {
            if (!states.TryGetValue(renewal.ProposalId, out var state))
            {
                renewed = default!;
                safeCode = "dad-inbound-proposal-renewal-no-session";
                return false;
            }

            var proposal = state.Proposal;
            var targetOwnerMatches = state.OwnedParticipants.Any(participant =>
                participant.OwnerId == renewal.TargetOwnerId &&
                participant.OwnerIslandId == renewal.TargetIslandId);
            if (proposal.Header.ExpiresAt <= now)
            {
                renewed = default!;
                safeCode = "dad-inbound-proposal-renewal-expired";
                return false;
            }

            if (renewal.Header.SenderIslandId != proposal.Header.SenderIslandId ||
                renewal.RequesterOwnerId != proposal.RequesterOwnerId ||
                renewal.TargetIslandId.Value != configuration.RegisteredIslandId ||
                renewal.TargetOwnerId.Value != configuration.RegisteredOwnerId ||
                !targetOwnerMatches ||
                renewal.PreviousExpiresAt != proposal.Header.ExpiresAt ||
                renewal.NewExpiresAt != renewal.PreviousExpiresAt + TimeSpan.FromMinutes(30) ||
                renewal.Header.ExpiresAt != renewal.NewExpiresAt ||
                renewal.RenewalGeneration != state.RenewalGeneration + 1)
            {
                renewed = default!;
                safeCode = "dad-inbound-proposal-renewal-identity-mismatch";
                return false;
            }

            if (!renewPolicy(proposal, renewal.PreviousExpiresAt, renewal.NewExpiresAt))
            {
                renewed = default!;
                safeCode = "dad-inbound-proposal-renewal-policy-rejected";
                return false;
            }

            var reissuedResponses = state.Responses()
                .Select(response => ReissueResponse(response, renewal.NewExpiresAt, now, renewal.RenewalGeneration))
                .ToArray();
            var reservations = reissuedResponses.OfType<Reservation>().ToImmutableArray();
            var preflight = reissuedResponses.OfType<PreflightResult>().SingleOrDefault();
            var lease = reissuedResponses.OfType<SessionLease>().SingleOrDefault();
            if (lease is not null)
            {
                lease = lease with
                {
                    LeaseExpiresAt = Min(renewal.NewExpiresAt, now + TimeSpan.FromMinutes(30)),
                };
            }

            renewed = state with
            {
                Proposal = proposal with
                {
                    Header = proposal.Header with { ExpiresAt = renewal.NewExpiresAt },
                },
                Reservations = reservations,
                Preflight = preflight,
                Lease = lease,
                AcknowledgedMessageIds = [],
                RenewalGeneration = renewal.RenewalGeneration,
                SafeCode = "dad-inbound-proposal-renewed",
            };
            states[renewal.ProposalId] = renewed;
            try
            {
                Persist();
                safeCode = renewed.SafeCode;
                return true;
            }
            catch
            {
                states[renewal.ProposalId] = state;
                renewed = default!;
                safeCode = "dad-inbound-proposal-store-failed";
                return false;
            }
        }
    }

    public bool TryPrepareResponses(
        Guid proposalId,
        IReadOnlyList<Reservation> reservations,
        PreflightResult preflight,
        SessionLease? lease,
        long stateGeneration,
        string safeCode,
        out DadAutoPartyInboundProposalState prepared)
    {
        ArgumentNullException.ThrowIfNull(reservations);
        ArgumentNullException.ThrowIfNull(preflight);
        lock (gate)
        {
            if (!states.TryGetValue(proposalId, out var state))
            {
                prepared = default!;
                return false;
            }
            if (state.ResponsesPrepared)
            {
                if (SameResponses(state, reservations, preflight, lease))
                {
                    prepared = state;
                    return true;
                }
                if (!CanAdvanceResponses(state, reservations, preflight, lease, stateGeneration))
                {
                    prepared = default!;
                    return false;
                }
            }
            if (reservations.Count != state.OwnedParticipants.Length ||
                reservations.Select(static reservation => reservation.CharacterId.Value)
                    .Distinct(StringComparer.Ordinal).Count() != reservations.Count ||
                reservations.Any(reservation => reservation.ProposalId != proposalId) ||
                preflight.ProposalId != proposalId || lease != null && lease.ProposalId != proposalId ||
                stateGeneration < 1)
            {
                prepared = default!;
                return false;
            }
            try
            {
                foreach (var response in reservations.Cast<IAutoPartyContract>()
                             .Append(preflight)
                             .Concat(lease == null ? [] : [lease]))
                    _ = EncodeResponse(response);
            }
            catch (ProtocolException)
            {
                prepared = default!;
                return false;
            }

            prepared = state with
            {
                Reservations = reservations.ToImmutableArray(),
                Preflight = preflight,
                Lease = lease,
                StateGeneration = stateGeneration,
                SafeCode = DadAutoPartyConfiguration.NormalizeSafeCode(safeCode),
            };
            states[proposalId] = prepared;
            try
            {
                Persist();
                return true;
            }
            catch
            {
                states[proposalId] = state;
                prepared = default!;
                return false;
            }
        }
    }

    public IReadOnlyList<IAutoPartyContract> UnacknowledgedResponses(int maximum)
    {
        Sweep(utcNow());
        lock (gate)
        {
            return states.Values
                .OrderBy(static state => state.RetainedAt)
                .SelectMany(static state => state.Responses())
                .Where(response => !states[response switch
                    {
                        Reservation value => value.ProposalId,
                        PreflightResult value => value.ProposalId,
                        SessionLease value => value.ProposalId,
                        _ => Guid.Empty,
                    }].AcknowledgedMessageIds.Contains(response.Header.MessageId))
                .Take(Math.Clamp(maximum, 1, 64))
                .ToArray();
        }
    }

    public bool ObserveRelayReceipt(Guid messageId, bool accepted)
    {
        if (messageId == Guid.Empty)
            return false;
        lock (gate)
        {
            var pair = states.FirstOrDefault(pair => pair.Value.Responses()
                .Any(response => response.Header.MessageId == messageId));
            if (pair.Value == null)
                return false;
            if (!accepted || pair.Value.AcknowledgedMessageIds.Contains(messageId))
                return true;
            states[pair.Key] = pair.Value with
            {
                AcknowledgedMessageIds = pair.Value.AcknowledgedMessageIds.Add(messageId),
            };
            Persist();
            return true;
        }
    }

    public void Remove(Guid proposalId)
    {
        if (proposalId == Guid.Empty)
            return;
        lock (gate)
        {
            if (!states.ContainsKey(proposalId))
                return;
        }
        if (!restoreBeforeRemoval(proposalId, "dad-inbound-proposal-removed"))
            return;
        lock (gate)
        {
            if (states.Remove(proposalId))
                Persist();
        }
    }

    public void RemoveSender(string senderIslandId)
    {
        if (string.IsNullOrWhiteSpace(senderIslandId))
            return;
        Guid[] candidates;
        lock (gate)
        {
            candidates = states.Where(pair => string.Equals(
                    pair.Value.Proposal.Header.SenderIslandId.Value,
                    senderIslandId,
                    StringComparison.Ordinal))
                .Select(static pair => pair.Key)
                .ToArray();
        }
        var restored = candidates.Where(proposalId => restoreBeforeRemoval(
                proposalId,
                "dad-inbound-proposal-sender-removed"))
            .ToArray();
        lock (gate)
        {
            var changed = false;
            foreach (var proposalId in restored)
                changed |= states.Remove(proposalId);
            if (changed)
                Persist();
        }
    }

    public void Clear()
    {
        Guid[] candidates;
        lock (gate)
        {
            if (states.Count == 0)
                return;
            candidates = states.Keys.ToArray();
        }
        var restored = candidates.Where(proposalId => restoreBeforeRemoval(
                proposalId,
                "dad-inbound-proposals-cleared"))
            .ToArray();
        lock (gate)
        {
            var changed = false;
            foreach (var proposalId in restored)
                changed |= states.Remove(proposalId);
            if (changed)
                Persist();
        }
    }

    private bool TryResolveOwnedParticipants(
        RunProposal proposal,
        out ImmutableArray<ParticipantRequest> owned,
        out string safeCode)
    {
        owned = [];
        if (!configuration.IsRegistrationActive || proposal.ExecutionPlan == null ||
            proposal.Header.ExpiresAt <= utcNow() ||
            string.IsNullOrWhiteSpace(configuration.RegisteredOwnerId) ||
            string.IsNullOrWhiteSpace(configuration.RegisteredIslandId))
        {
            safeCode = "dad-inbound-proposal-invalid";
            return false;
        }
        var executionParticipants = proposal.ExecutionPlan.Participants;
        if (executionParticipants.IsDefaultOrEmpty || executionParticipants.Any(participant =>
                string.Equals(participant.OwnerIslandId.Value, configuration.RegisteredIslandId, StringComparison.Ordinal) !=
                string.Equals(participant.OwnerId.Value, configuration.RegisteredOwnerId, StringComparison.Ordinal)))
        {
            safeCode = "dad-inbound-proposal-local-route-conflict";
            return false;
        }
        owned = executionParticipants
            .Where(participant =>
                string.Equals(participant.OwnerId.Value, configuration.RegisteredOwnerId, StringComparison.Ordinal) &&
                string.Equals(participant.OwnerIslandId.Value, configuration.RegisteredIslandId, StringComparison.Ordinal))
            .Select(static participant => new ParticipantRequest(
                participant.OwnerId,
                participant.OwnerIslandId,
                participant.CharacterId,
                participant.RequestedJob))
            .ToImmutableArray();
        if (owned.IsDefaultOrEmpty || owned.Length > 8 ||
            owned.Select(static participant =>
                    $"{participant.CharacterId.Value}\n{participant.RequestedJob.Value}")
                .Distinct(StringComparer.Ordinal).Count() != owned.Length)
        {
            safeCode = "dad-inbound-proposal-owned-participants-invalid";
            return false;
        }
        safeCode = "dad-inbound-proposal-owned-participants-valid";
        return true;
    }

    private bool IsValidState(DadAutoPartyInboundProposalState state, DateTimeOffset now)
    {
        if (state == null || state.RetainedAt.Offset != TimeSpan.Zero || state.RetainedAt > now + TimeSpan.FromMinutes(2) ||
            state.Proposal.Header.ExpiresAt <= now ||
            !TryResolveOwnedParticipants(state.Proposal, out var owned, out _) ||
            !owned.SequenceEqual(state.OwnedParticipants))
            return false;
        try
        {
            _ = CanonicalCborCodec.EncodeUnsigned(state.Proposal);
        }
        catch (ProtocolException)
        {
            return false;
        }
        if (!state.ResponsesPrepared)
            return state.Reservations.IsDefaultOrEmpty && state.Preflight == null && state.Lease == null;
        try
        {
            return state.Responses().All(response =>
            {
                _ = EncodeResponse(response);
                return response.Header.ExpiresAt <= state.Proposal.Header.ExpiresAt;
            });
        }
        catch (ProtocolException)
        {
            return false;
        }
    }

    private static bool SameResponses(
        DadAutoPartyInboundProposalState state,
        IReadOnlyList<Reservation> reservations,
        PreflightResult preflight,
        SessionLease? lease)
    {
        var expected = state.Responses().Select(EncodeResponse).ToArray();
        var observed = reservations.Cast<IAutoPartyContract>()
            .Append(preflight)
            .Concat(lease == null ? [] : [lease])
            .Select(EncodeResponse)
            .ToArray();
        return expected.Length == observed.Length && expected.Zip(observed).All(pair => pair.First.SequenceEqual(pair.Second));
    }

    private static bool CanAdvanceResponses(
        DadAutoPartyInboundProposalState state,
        IReadOnlyList<Reservation> reservations,
        PreflightResult preflight,
        SessionLease? lease,
        long stateGeneration)
    {
        if (!state.ResponsesPrepared || state.Preflight == null || state.Preflight.Ready ||
            !preflight.Ready || lease == null || stateGeneration <= state.StateGeneration ||
            preflight.ExpectedStateGeneration != state.StateGeneration ||
            preflight.ObservedStateGeneration != stateGeneration ||
            lease.ExpectedStateGeneration != stateGeneration ||
            lease.ObservedStateGeneration <= lease.ExpectedStateGeneration ||
            lease.LeaseExpiresAt > lease.Header.ExpiresAt ||
            state.AcknowledgedMessageIds.Contains(preflight.Header.MessageId) ||
            state.AcknowledgedMessageIds.Contains(lease.Header.MessageId))
            return false;

        var existingReservations = state.Reservations.Select(EncodeResponse).ToArray();
        var incomingReservations = reservations.Select(static value => EncodeResponse(value)).ToArray();
        return existingReservations.Length == incomingReservations.Length &&
               existingReservations.Zip(incomingReservations)
                   .All(static pair => pair.First.SequenceEqual(pair.Second));
    }

    private static IAutoPartyContract ReissueResponse(
        IAutoPartyContract response,
        DateTimeOffset expiresAt,
        DateTimeOffset issuedAt,
        long renewalGeneration)
    {
        var header = response.Header with
        {
            MessageId = Guid.NewGuid(),
            IdempotencyKey = $"proposal-renewal-response-{renewalGeneration}-{Guid.NewGuid():N}",
            IssuedAt = issuedAt,
            ExpiresAt = expiresAt,
        };
        return response switch
        {
            Reservation value => value with { Header = header },
            PreflightResult value => value with { Header = header },
            SessionLease value => value with { Header = header },
            _ => throw new InvalidOperationException("dad-inbound-response-type-invalid"),
        };
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) =>
        left <= right ? left : right;

    private void Sweep(DateTimeOffset now)
    {
        Guid[] expired;
        lock (gate)
        {
            expired = states.Where(pair => pair.Value.Proposal.Header.ExpiresAt <= now)
                .Select(static pair => pair.Key)
                .ToArray();
        }
        var restored = expired.Where(proposalId => restoreBeforeRemoval(
                proposalId,
                "dad-inbound-proposal-expired"))
            .ToArray();
        lock (gate)
        {
            var changed = false;
            foreach (var proposalId in restored)
            {
                if (states.TryGetValue(proposalId, out var state) &&
                    state.Proposal.Header.ExpiresAt <= now)
                    changed |= states.Remove(proposalId);
            }
            if (changed)
                Persist();
        }
    }

    private void Persist()
        => store.Save(states.Values.OrderBy(static state => state.RetainedAt).ToArray());

    private static byte[] EncodeResponse(IAutoPartyContract response)
        => response switch
        {
            Reservation value => CanonicalCborCodec.EncodeUnsigned(value),
            PreflightResult value => CanonicalCborCodec.EncodeUnsigned(value),
            SessionLease value => CanonicalCborCodec.EncodeUnsigned(value),
            _ => throw new ProtocolException(
                ProtocolFailureCode.InvalidContractBody,
                "dad-inbound-response-type-invalid"),
        };
}
