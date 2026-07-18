using dad.Models;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace dad.Services;

/// <summary>
/// Runs the local Duty Finder reward inspection exclusively on the framework thread. The transport
/// request is poll-based: the first exact request starts an operation and later identical requests
/// receive Pending until the owned UI has been restored and a terminal result is available.
/// </summary>
public sealed unsafe class DadRouletteRewardProbeService : IDisposable
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SelectionTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaximumRequestAge = TimeSpan.FromMinutes(2);

    private readonly IFramework framework;
    private readonly DadPresenceService presenceService;
    private readonly IPluginLog log;
    private ProbeSession? session;
    private bool disposed;

    public DadRouletteRewardProbeService(
        IFramework framework,
        DadPresenceService presenceService,
        IPluginLog log)
    {
        this.framework = framework;
        this.presenceService = presenceService;
        this.log = log;
        framework.Update += OnFrameworkUpdate;
    }

    public DadRouletteRewardProbeResultDto Handle(DadRouletteRewardProbeRequestDto request)
    {
        var now = DateTime.UtcNow;
        if (!DadRouletteRewardProbeIdentityRules.IsValid(request) ||
            request.RequestedAtUtc > now ||
            now - request.RequestedAtUtc > MaximumRequestAge)
        {
            return DadRouletteRewardProbeResultDto.FromRequest(
                request,
                DadRouletteRewardProbeOutcome.Unknown,
                "Roulette reward probe rejected an incomplete or stale request identity.",
                now);
        }

        if (request.Operation == DadRouletteRewardProbeOperation.Cancel)
        {
            if (session != null && SameCoreIdentity(session.Request, request))
                BeginFinish(session, DadRouletteRewardProbeOutcome.Unknown, 0, 0, "Roulette reward probe cancelled by scheduler.", now);
            return DadRouletteRewardProbeResultDto.FromRequest(
                request,
                session is { State: ProbeState.Closing }
                    ? DadRouletteRewardProbeOutcome.Pending
                    : DadRouletteRewardProbeOutcome.Unknown,
                "Roulette reward probe cancellation accepted.",
                now,
                dutyFinderOpenedByDad: session?.OpenedByDad ?? false);
        }

        if (session == null || session.State == ProbeState.Terminal && !SameCoreIdentity(session.Request, request))
        {
            session = new ProbeSession(request.Clone(), now);
            log.Information(
                "[dad] Started exact Daily Roulette reward probe operation={OperationId} schedule={ScheduleId}/{ScheduleRunId}/{ScheduleEntryId} slot={SlotId} route={WorkerSessionId} character={CharacterKey} roulette={RouletteId}.",
                request.OperationId,
                request.ScheduleId,
                request.ScheduleRunId,
                request.ScheduleEntryId,
                request.SlotId,
                request.RouteWorkerSessionId,
                request.CharacterKey,
                request.RouletteId);
        }
        else if (!SameCoreIdentity(session.Request, request) || session.Request.Operation != request.Operation)
        {
            return DadRouletteRewardProbeResultDto.FromRequest(
                request,
                DadRouletteRewardProbeOutcome.Unknown,
                "Another roulette reward probe owns the local Duty Finder operation.",
                now);
        }

        return BuildResult(session, now);
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (disposed || session is null || session.State == ProbeState.Terminal)
            return;

        var now = DateTime.UtcNow;
        try
        {
            if (session.State == ProbeState.Closing)
            {
                AdvanceClose(session, now);
                return;
            }

            if (now >= session.DeadlineUtc)
            {
                BeginFinish(session, DadRouletteRewardProbeOutcome.Unknown, 0, 0, "Roulette reward probe timed out.", now);
                return;
            }

            if (!TryValidateLocalIdentity(session.Request, out var identityFailure))
            {
                BeginFinish(session, DadRouletteRewardProbeOutcome.Unknown, 0, 0, identityFailure, now);
                return;
            }

            if (IsContentsFinderQueueStateActive())
            {
                BeginFinish(session, DadRouletteRewardProbeOutcome.Unknown, 0, 0, "Duty Finder has an active queue state; reward truth is unknown.", now);
                return;
            }

            switch (session.State)
            {
                case ProbeState.Initial:
                    AdvanceInitial(session, now);
                    break;
                case ProbeState.Opening:
                    AdvanceOpening(session, now);
                    break;
                case ProbeState.Mapping:
                    AdvanceMapping(session, now);
                    break;
                case ProbeState.Observing:
                    AdvanceObservation(session, now);
                    break;
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[dad] Daily Roulette reward probe failed safely for {OperationId}.", session.Request.OperationId);
            BeginFinish(session, DadRouletteRewardProbeOutcome.Unknown, 0, 0, $"Roulette reward probe failed safely: {ex.Message}", now);
        }
    }

    private void AdvanceInitial(ProbeSession active, DateTime now)
    {
        var agent = AgentContentsFinder.Instance();
        var manager = RaptureAtkUnitManager.Instance();
        if (agent == null || manager == null)
        {
            BeginFinish(active, DadRouletteRewardProbeOutcome.Unknown, 0, 0, "Duty Finder agent or addon manager is unavailable.", now);
            return;
        }

        var addon = manager->GetAddonByName("ContentsFinder");
        var visible = addon != null && addon->IsVisible;
        if (visible)
        {
            active.WasAlreadyOpen = true;
            if (!IsExactSelectedRoulette(agent, active.Request.RouletteId))
            {
                BeginFinish(active, DadRouletteRewardProbeOutcome.Unknown, 0, 0, "Duty Finder was already open on a different or unresolved selection; DAD did not navigate it.", now);
                return;
            }

            active.State = ProbeState.Observing;
            active.Summary = "Duty Finder was already open on the exact roulette; reading without navigation.";
            return;
        }

        if (agent->IsAgentActive())
        {
            BeginFinish(active, DadRouletteRewardProbeOutcome.Unknown, 0, 0, "Duty Finder agent/addon visibility was contradictory; DAD did not navigate it.", now);
            return;
        }

        var hud = AgentHUD.Instance();
        if (hud == null || !hud->IsMainCommandEnabled(33))
        {
            BeginFinish(active, DadRouletteRewardProbeOutcome.Unknown, 0, 0, "Duty Finder is closed but its main command is unavailable.", now);
            return;
        }

        // Ownership is recorded before the mutation so every subsequent failure path knows it may close.
        active.OpenedByDad = true;
        active.OpenIssuedAtUtc = now;
        active.State = ProbeState.Opening;
        active.Summary = "DAD opened Duty Finder for an exact roulette reward inspection.";
        agent->Show();
    }

    private void AdvanceOpening(ProbeSession active, DateTime now)
    {
        var agent = AgentContentsFinder.Instance();
        var manager = RaptureAtkUnitManager.Instance();
        if (agent == null || manager == null)
        {
            BeginFinish(active, DadRouletteRewardProbeOutcome.Unknown, 0, 0, "Duty Finder disappeared after DAD opened it.", now);
            return;
        }

        var addon = manager->GetAddonByName("ContentsFinder");
        if (addon == null || !addon->IsVisible)
        {
            if (now - active.OpenIssuedAtUtc >= OpenTimeout)
                BeginFinish(active, DadRouletteRewardProbeOutcome.Unknown, 0, 0, "Duty Finder did not become visible after DAD opened it.", now);
            return;
        }

        // Navigation is legal only because this operation proved the window was closed and opened it.
        if (!DadRouletteRewardProbeUiOwnershipRules.CanNavigate(active.WasAlreadyOpen))
        {
            BeginFinish(active, DadRouletteRewardProbeOutcome.Unknown, 0, 0, "Duty Finder navigation ownership was lost.", now);
            return;
        }

        active.HydrationIssued = true;
        active.HydrationIssuedAtUtc = now;
        active.MappingGate.Reset();
        active.State = ProbeState.Mapping;
        active.Summary = $"Hydrating exact Daily Roulette #{active.Request.RouletteId}.";
        agent->OpenRouletteDuty(checked((byte)active.Request.RouletteId));
    }

    private void AdvanceMapping(ProbeSession active, DateTime now)
    {
        if (now - active.HydrationIssuedAtUtc >= SelectionTimeout)
        {
            BeginFinish(active, DadRouletteRewardProbeOutcome.Unknown, 0, 0, "Exact roulette selection could not be proven before the probe timeout.", now);
            return;
        }

        if (!TryCaptureMapping(active, out var agent, out var addon, out var mapping, out var failure))
        {
            active.Summary = failure;
            return;
        }

        if (!mapping.IsReady)
        {
            if (mapping.Status is DadDutyFinderMappingStatus.Absent or
                DadDutyFinderMappingStatus.Disabled or
                DadDutyFinderMappingStatus.Ambiguous or
                DadDutyFinderMappingStatus.Mismatch)
            {
                BeginFinish(active, DadRouletteRewardProbeOutcome.Unknown, 0, 0, mapping.Reason, now);
            }
            else
            {
                active.Summary = mapping.Reason;
            }
            return;
        }

        var resolved = mapping.Entry!;
        if (resolved.SelectionToken.CharacterContentId != active.Request.CharacterContentId ||
            resolved.SelectionToken.Target != new DadDutyFinderLiveTarget(DadDutyFinderLiveContentType.Roulette, active.Request.RouletteId))
        {
            BeginFinish(active, DadRouletteRewardProbeOutcome.Unknown, 0, 0, "Stable Duty Finder mapping contradicted the exact character or roulette identity.", now);
            return;
        }

        // Selection ownership is recorded before firing the callback.
        active.SelectionIssued = true;
        active.State = ProbeState.Observing;
        active.RewardGate.Reset();
        active.Summary = $"Selected exact Daily Roulette #{active.Request.RouletteId}; waiting for stable reward truth.";
        FireAddonIntCallback(addon, 3, resolved.UiRow.CallbackOrdinal);
    }

    private void AdvanceObservation(ProbeSession active, DateTime now)
    {
        if (!TryCaptureMapping(active, out var agent, out _, out var mapping, out var failure))
        {
            active.Summary = failure;
            return;
        }

        if (!mapping.IsReady)
        {
            if (mapping.Status is DadDutyFinderMappingStatus.Absent or
                DadDutyFinderMappingStatus.Disabled or
                DadDutyFinderMappingStatus.Ambiguous or
                DadDutyFinderMappingStatus.Mismatch)
            {
                BeginFinish(active, DadRouletteRewardProbeOutcome.Unknown, 0, 0, mapping.Reason, now);
            }
            else
            {
                active.Summary = mapping.Reason;
            }
            return;
        }

        if (!IsExactSelectedRoulette(agent, active.Request.RouletteId))
        {
            if (agent->HasRouletteSelected)
            {
                BeginFinish(active, DadRouletteRewardProbeOutcome.Unknown, 0, 0, "Duty Finder selected a different roulette during the probe.", now);
            }
            else
            {
                active.Summary = "Waiting for exact Duty Finder roulette selection proof.";
            }
            return;
        }

        var instanceContent = InstanceContent.Instance();
        if (instanceContent == null)
        {
            BeginFinish(active, DadRouletteRewardProbeOutcome.Unknown, 0, 0, "InstanceContent reward state is unavailable.", now);
            return;
        }

        var received = instanceContent->IsRouletteComplete(checked((byte)active.Request.RouletteId)) ? 1 : 0;
        const int maximum = 1;
        var observation = new DadRouletteRewardObservation(
            Plugin.PlayerState.ContentId,
            active.Request.RouletteId,
            mapping.Entry!.SelectionToken.ListFingerprint,
            ExactRouletteSelected: true,
            received,
            maximum,
            now);
        var status = active.RewardGate.Observe(
            observation,
            active.Request.CharacterContentId,
            active.Request.RouletteId,
            out var observationReason);
        active.Summary = observationReason;
        if (status == DadRouletteRewardObservationStatus.Invalid)
        {
            BeginFinish(active, DadRouletteRewardProbeOutcome.Unknown, 0, 0, observationReason, now);
        }
        else if (status == DadRouletteRewardObservationStatus.Received)
        {
            BeginFinish(active, DadRouletteRewardProbeOutcome.Received, received, maximum, "Exact stable roulette reward count is 1/1 (received).", now);
        }
        else if (status == DadRouletteRewardObservationStatus.NotReceived)
        {
            BeginFinish(active, DadRouletteRewardProbeOutcome.NotReceived, received, maximum, "Exact stable roulette reward count is 0/1 (not received).", now);
        }
    }

    private bool TryCaptureMapping(
        ProbeSession active,
        out AgentContentsFinder* agent,
        out AtkUnitBase* addon,
        out DadDutyFinderMappingResult mapping,
        out string failure)
    {
        agent = AgentContentsFinder.Instance();
        var manager = RaptureAtkUnitManager.Instance();
        addon = manager == null ? null : manager->GetAddonByName("ContentsFinder");
        if (agent == null || addon == null || !addon->IsVisible)
        {
            mapping = new DadDutyFinderMappingResult(DadDutyFinderMappingStatus.Unstable, "Duty Finder is not visibly available.");
            failure = mapping.Reason;
            return false;
        }

        if (!DadDutyFinderLiveEntryScanner.TryCapture(agent, addon, out var snapshot, out failure))
        {
            mapping = new DadDutyFinderMappingResult(DadDutyFinderMappingStatus.Unstable, failure);
            return false;
        }

        mapping = active.MappingGate.Observe(
            snapshot,
            new DadDutyFinderLiveTarget(DadDutyFinderLiveContentType.Roulette, active.Request.RouletteId));
        failure = mapping.Reason;
        return true;
    }

    private bool TryValidateLocalIdentity(DadRouletteRewardProbeRequestDto request, out string failure)
    {
        var live = presenceService.BuildLiveSafetySnapshot();
        if (!string.Equals(live.WorkerSessionId.Value, request.RouteWorkerSessionId.Value, StringComparison.OrdinalIgnoreCase) ||
            !DadRosterIdentity.SameAccount(live.ManagedAccountKey, request.AccountKey) ||
            !string.Equals(live.ActiveCharacterKey.Value, request.CharacterKey.Value, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(live.Character.CharacterKey, request.CharacterKey.Value, StringComparison.OrdinalIgnoreCase) ||
            live.Character.ContentId != request.CharacterContentId ||
            !live.IsAvailable ||
            !live.WorldReadyStable)
        {
            failure = "The local route is not world-ready on the exact requested account, character, and Content ID.";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    private void BeginFinish(
        ProbeSession active,
        DadRouletteRewardProbeOutcome outcome,
        int received,
        int maximum,
        string summary,
        DateTime now)
    {
        if (active.State is ProbeState.Closing or ProbeState.Terminal)
            return;

        active.PendingOutcome = outcome;
        active.PendingReceived = received;
        active.PendingMaximum = maximum;
        active.Summary = summary;
        if (!DadRouletteRewardProbeUiOwnershipRules.ShouldClose(active.OpenedByDad))
        {
            Complete(active, outcome, received, maximum, summary, now);
            return;
        }

        active.State = ProbeState.Closing;
        active.CloseIssuedAtUtc = now;
        try
        {
            var agent = AgentContentsFinder.Instance();
            if (agent == null)
            {
                Complete(active, DadRouletteRewardProbeOutcome.Unknown, 0, 0, $"{summary} DAD could not resolve its owned Duty Finder agent for close.", now);
                return;
            }
            agent->Hide();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[dad] Failed to close DAD-owned Duty Finder for reward probe {OperationId}.", active.Request.OperationId);
            Complete(active, DadRouletteRewardProbeOutcome.Unknown, 0, 0, $"{summary} DAD could not close its owned Duty Finder window.", now);
        }
    }

    private void AdvanceClose(ProbeSession active, DateTime now)
    {
        var manager = RaptureAtkUnitManager.Instance();
        var addon = manager == null ? null : manager->GetAddonByName("ContentsFinder");
        if (addon == null || !addon->IsVisible)
        {
            Complete(
                active,
                active.PendingOutcome,
                active.PendingReceived,
                active.PendingMaximum,
                active.Summary,
                now);
            return;
        }

        if (now - active.CloseIssuedAtUtc >= CloseTimeout)
        {
            Complete(
                active,
                DadRouletteRewardProbeOutcome.Unknown,
                0,
                0,
                $"{active.Summary} DAD-owned Duty Finder close could not be verified.",
                now);
        }
    }

    private void Complete(
        ProbeSession active,
        DadRouletteRewardProbeOutcome outcome,
        int received,
        int maximum,
        string summary,
        DateTime now)
    {
        active.State = ProbeState.Terminal;
        active.TerminalResult = DadRouletteRewardProbeResultDto.FromRequest(
            active.Request,
            outcome,
            summary,
            now,
            received,
            maximum,
            active.OpenedByDad);
        log.Information(
            "[dad] Daily Roulette reward probe operation={OperationId} slot={SlotId} outcome={Outcome} rewards={Received}/{Maximum} openedByDad={OpenedByDad}: {Summary}",
            active.Request.OperationId,
            active.Request.SlotId,
            outcome,
            received,
            maximum,
            active.OpenedByDad,
            summary);
    }

    private static DadRouletteRewardProbeResultDto BuildResult(ProbeSession active, DateTime now)
        => active.TerminalResult?.Clone() ?? DadRouletteRewardProbeResultDto.FromRequest(
            active.Request,
            DadRouletteRewardProbeOutcome.Pending,
            string.IsNullOrWhiteSpace(active.Summary) ? "Roulette reward probe is pending." : active.Summary,
            now,
            dutyFinderOpenedByDad: active.OpenedByDad);

    private static bool IsExactSelectedRoulette(AgentContentsFinder* agent, uint rouletteId)
        => agent != null && DadRouletteSelectionProof.IsExact(
            agent->HasRouletteSelected,
            agent->SelectedDuty.ContentType == ContentsType.Roulette,
            agent->SelectedDuty.Id,
            rouletteId);

    private static bool IsContentsFinderQueueStateActive()
    {
        var contentsFinder = ContentsFinder.Instance();
        return contentsFinder != null && contentsFinder->QueueInfo.QueueState is
            ContentsFinderQueueState.Pending or
            ContentsFinderQueueState.Queued or
            ContentsFinderQueueState.Ready or
            ContentsFinderQueueState.Accepted;
    }

    private static void FireAddonIntCallback(AtkUnitBase* addon, int first, int second)
    {
        var values = stackalloc AtkValue[2];
        values[0].Type = AtkValueType.Int;
        values[0].Int = first;
        values[1].Type = AtkValueType.Int;
        values[1].Int = second;
        addon->FireCallback(2, values, true);
    }

    private static bool SameCoreIdentity(
        DadRouletteRewardProbeRequestDto left,
        DadRouletteRewardProbeRequestDto right)
        => string.Equals(left.OperationId, right.OperationId, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.SchedulerRunId, right.SchedulerRunId, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.ScheduleId, right.ScheduleId, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.ScheduleRunId, right.ScheduleRunId, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.ScheduleEntryId, right.ScheduleEntryId, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.SlotId, right.SlotId, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.RouteWorkerSessionId.Value, right.RouteWorkerSessionId.Value, StringComparison.OrdinalIgnoreCase) &&
           DadRosterIdentity.SameAccount(left.AccountKey, right.AccountKey) &&
           string.Equals(left.CharacterKey.Value, right.CharacterKey.Value, StringComparison.OrdinalIgnoreCase) &&
           left.CharacterContentId == right.CharacterContentId &&
           left.RouletteId == right.RouletteId &&
           string.Equals(left.RouletteKey, right.RouletteKey, StringComparison.OrdinalIgnoreCase) &&
           left.RequestedAtUtc == right.RequestedAtUtc;

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        framework.Update -= OnFrameworkUpdate;
        if (session is { OpenedByDad: true, State: not ProbeState.Terminal })
        {
            try
            {
                var agent = AgentContentsFinder.Instance();
                if (agent != null)
                    agent->Hide();
            }
            catch
            {
                // Best-effort shutdown only; normal operation verifies close before returning truth.
            }
        }
        session = null;
    }

    private enum ProbeState
    {
        Initial,
        Opening,
        Mapping,
        Observing,
        Closing,
        Terminal,
    }

    private sealed class ProbeSession
    {
        public ProbeSession(DadRouletteRewardProbeRequestDto request, DateTime now)
        {
            Request = request;
            DeadlineUtc = now + ProbeTimeout;
        }

        public DadRouletteRewardProbeRequestDto Request { get; }
        public DateTime DeadlineUtc { get; }
        public ProbeState State { get; set; }
        public bool WasAlreadyOpen { get; set; }
        public bool OpenedByDad { get; set; }
        public DateTime OpenIssuedAtUtc { get; set; }
        public bool HydrationIssued { get; set; }
        public DateTime HydrationIssuedAtUtc { get; set; }
        public bool SelectionIssued { get; set; }
        public DateTime CloseIssuedAtUtc { get; set; }
        public DadRouletteRewardProbeOutcome PendingOutcome { get; set; } = DadRouletteRewardProbeOutcome.Unknown;
        public int PendingReceived { get; set; }
        public int PendingMaximum { get; set; }
        public string Summary { get; set; } = "Waiting to inspect exact Daily Roulette reward truth.";
        public DadDutyFinderStableMappingGate MappingGate { get; } = new();
        public DadRouletteRewardObservationGate RewardGate { get; } = new();
        public DadRouletteRewardProbeResultDto? TerminalResult { get; set; }
    }
}
