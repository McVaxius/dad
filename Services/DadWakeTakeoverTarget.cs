using Dalamud.Game.Command;
using Dalamud.Plugin.Services;
using dad.Models;

namespace dad.Services;

public sealed class DadWakeTakeoverTarget : IDadWakeTakeoverTarget
{
    private readonly Configuration configuration;
    private readonly ConfigManager configManager;
    private readonly DadPresenceService presenceService;
    private readonly DadAutoRetainerIpcService autoRetainer;
    private readonly DadLifestreamIpcService lifestream;
    private readonly DadVermaxionIpcService vermaxion;
    private readonly ICommandManager commandManager;
    private readonly IPluginLog log;
    private int loggedCanonicalV2Authority;
    private int loggedLegacyNumericV2Authority;
    private int loggedCompatibilityAuthority;

    public DadWakeTakeoverTarget(
        Configuration configuration,
        ConfigManager configManager,
        DadPresenceService presenceService,
        DadAutoRetainerIpcService autoRetainer,
        DadLifestreamIpcService lifestream,
        DadVermaxionIpcService vermaxion,
        ICommandManager commandManager,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.configManager = configManager;
        this.presenceService = presenceService;
        this.autoRetainer = autoRetainer;
        this.lifestream = lifestream;
        this.vermaxion = vermaxion;
        this.commandManager = commandManager;
        this.log = log;
    }

    public DadWakeTakeoverTargetSnapshot Capture(DadWakeTakeoverRequestDto request, bool forceExternalRefresh = false)
    {
        var participant = forceExternalRefresh
            ? presenceService.BuildLiveSafetySnapshot()
            : presenceService.BuildSnapshotCopy();
        var currentAccountKey = new DadAccountKey(configManager.GetCurrentAccountKey());
        var requestedAccount = configManager.GetAccount(request.AccountKey);
        var identity = DadWakeTakeoverIdentityRules.Evaluate(
            configuration.ClientAccountId,
            request.AccountKey,
            request.CharacterKey,
            requestedAccount,
            configuration.RosterCatalog?.KnownCharacters,
            currentAccountKey,
            participant.ManagedAccountKey);
        var ar = autoRetainer.Inspect();
        var lifestreamState = lifestream.Inspect();
        var external = vermaxion.Inspect(forceExternalRefresh);
        var reservation = vermaxion.Reservation;
        var compatibilityEvidence = DadVermaxionCompatibilityEvidence.Evaluate(
            external,
            ar.Available,
            ar.IsBusy,
            ar.MultiModeEnabled,
            ar.SuppressionReadable,
            ar.IsSuppressed,
            ar.SuppressionOwnedByDad);
        var authority = DadVermaxionAuthorityRules.Resolve(
            request.OperationToken,
            reservation,
            external,
            compatibilityEvidence);
        LogHandoffAuthorityOnce(authority, reservation);

        return new DadWakeTakeoverTargetSnapshot
        {
            DadEnabled = configuration.PluginEnabled,
            // Typed takeover never accepts peer-supplied command text. The normal DAD remote-mutation
            // boundary (non-local-only mode) is sufficient; the legacy arbitrary-command opt-in remains
            // scoped to the compatibility character-load transport.
            RemoteMutationAllowed = !configuration.LocalOnlyModeEnabled,
            AccountMatches = identity.AccountMatches,
            CharacterKnownToAccount = identity.CharacterKnownToAccount,
            CorrectCharacter = string.Equals(
                participant.ActiveCharacterKey.Value,
                request.CharacterKey.Value,
                StringComparison.OrdinalIgnoreCase),
            PostArReady = participant.WorldReadyStable && !authority.Held,
            ExternalAutomationHeld = authority.Held,
            VermaxionReservationAuthoritative = authority.Authoritative,
            VermaxionMutationAuthorization = authority.MutationAuthorization,
            VermaxionCompatibilityEvidence = authority.CompatibilityEvidence,
            ExternalAutomationActivity = authority.Activity,
            ExternalAutomationState = authority.State,
            ExternalAutomationSummary = authority.Summary,
            VermaxionReservationState = reservation.State,
            VermaxionReservationSummary = reservation.Summary,
            VermaxionReservationCreatedAtUtc = reservation.CreatedAtUtc == DateTime.MinValue
                ? null
                : reservation.CreatedAtUtc,
            VermaxionReservationUpdatedAtUtc = reservation.UpdatedAtUtc == DateTime.MinValue
                ? reservation.ObservedAtUtc
                : reservation.UpdatedAtUtc,
            AutoRetainerAvailable = ar.Available,
            AutoRetainerBusy = ar.IsBusy,
            LifestreamAvailable = lifestreamState.Available,
            LifestreamBusy = lifestreamState.IsBusy,
            LifestreamStatus = lifestreamState.Summary,
            SuppressionReadable = ar.SuppressionReadable,
            AutoRetainerSuppressed = ar.IsSuppressed,
            DadOwnsSuppression = ar.SuppressionOwnedByDad,
            DadOwnsCharacterPostprocess = ar.CharacterPostprocessOwnedByDad,
            MultiModeEnabled = ar.MultiModeEnabled,
            AutoRetainerStatus = ar.Summary,
            Participant = participant,
        };
    }

    public DadWakeTakeoverActionResult ArmCharacterPostprocess(string operationToken)
        => autoRetainer.ArmCharacterPostprocessRequest(operationToken)
            ? DadWakeTakeoverActionResult.Accepted()
            : DadWakeTakeoverActionResult.Rejected("Another DAD takeover operation already owns the AutoRetainer handoff request.");

    private void LogHandoffAuthorityOnce(
        DadVermaxionAuthorityView authority,
        DadVermaxionReservationStatus reservation)
    {
        if (authority.Authoritative &&
            reservation.WireFormat == DadVermaxionReservationWireFormat.CanonicalString &&
            System.Threading.Interlocked.Exchange(ref loggedCanonicalV2Authority, 1) == 0)
        {
            log.Information(
                "[dad][VERMAXION] Canonical v2 string reservation is authoritative for this DAD handoff.");
        }

        if (authority.Authoritative &&
            reservation.WireFormat == DadVermaxionReservationWireFormat.LegacyNumeric &&
            System.Threading.Interlocked.Exchange(ref loggedLegacyNumericV2Authority, 1) == 0)
        {
            log.Information(
                "[dad][VERMAXION] Accepted legacy numeric v2 reservation state from an older VERMAXION build.");
        }

        if (authority.MutationAuthorization == DadVermaxionMutationAuthorization.CompatibilityIdle &&
            System.Threading.Interlocked.Exchange(ref loggedCompatibilityAuthority, 1) == 0)
        {
            log.Information(
                "[dad][VERMAXION] Using verified-idle compatibility evidence because v2 reservation IPC is genuinely unavailable.");
        }
    }

    public DadVermaxionReservationStatus ReserveVermaxion(DadWakeTakeoverRequestDto request)
        => vermaxion.Reserve(new DadVermaxionReservationRequest
        {
            OperationToken = request.OperationToken,
            SchedulerRunId = request.SchedulerRunId,
            SlotId = request.SlotId,
            AccountKey = request.AccountKey.Value,
            CharacterKey = request.CharacterKey.Value,
            RequestedAtUtc = request.RequestedAtUtc,
        });

    public bool ReleaseVermaxion(string operationToken)
        => vermaxion.Release(operationToken);

    public DadWakeTakeoverActionResult AcquireSuppression()
        => autoRetainer.TryAcquireSuppression();

    public bool FinishCharacterPostprocess(bool retryAtNextBoundary)
        => autoRetainer.FinishCharacterPostprocess(retryAtNextBoundary);

    public bool ReleaseSuppressionIfOwned(bool force = false)
        => autoRetainer.ReleaseSuppressionIfOwned(force);

    public DadWakeTakeoverActionResult SetMultiModeEnabled(bool enabled)
        => autoRetainer.SetMultiModeAndVerify(enabled);

    public DadWakeTakeoverActionResult ExecuteCommand(
        DadWakeTakeoverCommand command,
        DadWakeTakeoverRequestDto request)
    {
        var commandText = command switch
        {
            DadWakeTakeoverCommand.DisableAutoRetainer => "/ays d",
            DadWakeTakeoverCommand.ResetAutoRetainer => "/ays reset",
            DadWakeTakeoverCommand.RelogCharacter when DadWakePolicyRules.IsValidCharacterKey(request.CharacterKey)
                => $"/ays relog {request.CharacterKey.Value.Trim()}",
            _ => string.Empty,
        };
        if (string.IsNullOrWhiteSpace(commandText))
            return DadWakeTakeoverActionResult.Rejected($"Unsupported wake takeover command {command}.");

        try
        {
            var accepted = commandManager.ProcessCommand(commandText);
            if (!accepted)
                return DadWakeTakeoverActionResult.Rejected($"Command manager rejected typed wake command {command}.");

            log.Information(
                "[dad] Wake takeover {SchedulerRunId}/{SlotId}: executed {Command} for {CharacterKey}.",
                request.SchedulerRunId,
                request.SlotId,
                command,
                request.CharacterKey);
            return DadWakeTakeoverActionResult.Accepted();
        }
        catch (Exception ex)
        {
            return DadWakeTakeoverActionResult.Rejected($"Typed wake command {command} failed: {ex.Message}");
        }
    }
}
