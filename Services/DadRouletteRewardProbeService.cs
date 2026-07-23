using dad.Models;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace dad.Services;

/// <summary>
/// Runs local Duty Finder reward inspection exclusively on the framework thread. Scheduled probes
/// and the debug all-roulette diagnostic share the same exact-row hydration, mapping, selection,
/// and stable native-read path. Only an operation that opened Duty Finder may navigate or close it.
/// </summary>
public sealed unsafe class DadRouletteRewardProbeService : IDisposable
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DiagnosticTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SelectionTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaximumRequestAge = TimeSpan.FromMinutes(2);

    private readonly IFramework framework;
    private readonly DadPresenceService presenceService;
    private readonly IPluginLog log;
    private ProbeSession? session;
    private DiagnosticSession? diagnosticSession;
    private DadRouletteRewardDiagnosticStatus latestDiagnosticStatus =
        DadRouletteRewardDiagnosticStatus.NeverRun;
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
            {
                BeginProbeFinish(
                    session,
                    DadRouletteRewardProbeOutcome.Unknown,
                    0,
                    0,
                    "Roulette reward probe cancelled by scheduler.",
                    now);
            }

            return DadRouletteRewardProbeResultDto.FromRequest(
                request,
                session is { State: ProbeState.Closing }
                    ? DadRouletteRewardProbeOutcome.Pending
                    : DadRouletteRewardProbeOutcome.Unknown,
                "Roulette reward probe cancellation accepted.",
                now,
                dutyFinderOpenedByDad: session?.OpenedByDad ?? false);
        }

        if (diagnosticSession is { State: not DiagnosticState.Terminal })
        {
            return DadRouletteRewardProbeResultDto.FromRequest(
                request,
                DadRouletteRewardProbeOutcome.Unknown,
                "The local Duty Roulette diagnostic currently owns Duty Finder.",
                now);
        }

        if (session == null || session.State == ProbeState.Terminal && !SameCoreIdentity(session.Request, request))
        {
            session = new ProbeSession(request.Clone(), now);
            log.Information(
                "[dad] Started UI-hydrated Daily Roulette reward probe operation={OperationId} schedule={ScheduleId}/{ScheduleRunId}/{ScheduleEntryId} slot={SlotId} route={WorkerSessionId} roulette={RouletteId}.",
                request.OperationId,
                request.ScheduleId,
                request.ScheduleRunId,
                request.ScheduleEntryId,
                request.SlotId,
                request.RouteWorkerSessionId,
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

        return BuildProbeResult(session, now);
    }

    internal DadRouletteRewardDiagnosticStatus GetDiagnosticStatus()
        => latestDiagnosticStatus;

    internal bool TryStartDiagnostic(bool dadOtherwiseIdle, out string failure)
    {
        failure = string.Empty;
        if (disposed)
        {
            failure = "The roulette reward service is disposed.";
            return false;
        }

        if (!dadOtherwiseIdle)
        {
            failure = "DAD must be otherwise idle before the Duty Roulette reward diagnostic can start.";
            return false;
        }

        if (session is { State: not ProbeState.Terminal })
        {
            failure = "A scheduled roulette reward inspection is already active.";
            return false;
        }

        if (diagnosticSession is { State: not DiagnosticState.Terminal })
        {
            failure = "The Duty Roulette reward diagnostic is already pending.";
            return false;
        }

        var now = DateTime.UtcNow;
        var characterContentId = Plugin.PlayerState.ContentId;
        if (characterContentId == 0 ||
            !TryValidateDiagnosticIdentity(characterContentId, out failure))
        {
            if (string.IsNullOrWhiteSpace(failure))
                failure = "A world-ready current character is required.";
            return false;
        }

        if (IsContentsFinderQueueStateActive())
        {
            failure = "Duty Finder has an active queue state.";
            return false;
        }

        diagnosticSession = new DiagnosticSession(characterContentId, now);
        latestDiagnosticStatus = new DadRouletteRewardDiagnosticStatus(
            DadRouletteRewardDiagnosticRunState.Pending,
            "Pending: opening Duty Finder and freezing live roulette rows.",
            now);
        log.Information(
            "[dad] Starting Duty Roulette reward-state diagnostic for the current character's live Duty Roulette rows.");
        return true;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (disposed)
            return;

        var now = DateTime.UtcNow;
        if (session is { State: not ProbeState.Terminal })
        {
            AdvanceProbe(session, now);
            return;
        }

        if (diagnosticSession is { State: not DiagnosticState.Terminal })
            AdvanceDiagnostic(diagnosticSession, now);
    }

    private void AdvanceProbe(ProbeSession active, DateTime now)
    {
        try
        {
            if (active.State == ProbeState.Closing)
            {
                AdvanceProbeClose(active, now);
                return;
            }

            if (now >= active.DeadlineUtc)
            {
                BeginProbeFinish(
                    active,
                    DadRouletteRewardProbeOutcome.Unknown,
                    0,
                    0,
                    "Roulette reward probe timed out.",
                    now);
                return;
            }

            if (!TryValidateLocalIdentity(active.Request, out var identityFailure))
            {
                BeginProbeFinish(
                    active,
                    DadRouletteRewardProbeOutcome.Unknown,
                    0,
                    0,
                    identityFailure,
                    now);
                return;
            }

            if (IsContentsFinderQueueStateActive())
            {
                BeginProbeFinish(
                    active,
                    DadRouletteRewardProbeOutcome.Unknown,
                    0,
                    0,
                    "Duty Finder has an active queue state; reward truth is unknown.",
                    now);
                return;
            }

            switch (active.State)
            {
                case ProbeState.Initial:
                    AdvanceProbeInitial(active, now);
                    break;
                case ProbeState.Opening:
                    AdvanceProbeOpening(active, now);
                    break;
                case ProbeState.Inspecting:
                    AdvanceProbeInspection(active, now);
                    break;
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[dad] Daily Roulette reward probe failed safely for {OperationId}.", active.Request.OperationId);
            BeginProbeFinish(
                active,
                DadRouletteRewardProbeOutcome.Unknown,
                0,
                0,
                $"Roulette reward probe failed safely: {ex.Message}",
                now);
        }
    }

    private void AdvanceProbeInitial(ProbeSession active, DateTime now)
    {
        var agent = AgentContentsFinder.Instance();
        var manager = RaptureAtkUnitManager.Instance();
        if (agent == null || manager == null)
        {
            BeginProbeFinish(
                active,
                DadRouletteRewardProbeOutcome.Unknown,
                0,
                0,
                "Duty Finder agent or addon manager is unavailable.",
                now);
            return;
        }

        var addon = manager->GetAddonByName("ContentsFinder");
        var visible = addon != null && addon->IsVisible;
        if (visible)
        {
            active.WasAlreadyOpen = true;
            if (!IsExactSelectedRoulette(agent, active.Request.RouletteId))
            {
                BeginProbeFinish(
                    active,
                    DadRouletteRewardProbeOutcome.Unknown,
                    0,
                    0,
                    "Duty Finder was already open on a different or unresolved selection; DAD preserved it without navigation.",
                    now);
                return;
            }

            active.Inspection = new RewardInspection(
                active.Request.CharacterContentId,
                active.Request.RouletteId,
                $"Roulette #{active.Request.RouletteId}",
                canNavigate: false,
                now);
            active.State = ProbeState.Inspecting;
            active.Summary = "Duty Finder was already open on the exact roulette; reading without navigation.";
            return;
        }

        if (agent->IsAgentActive())
        {
            BeginProbeFinish(
                active,
                DadRouletteRewardProbeOutcome.Unknown,
                0,
                0,
                "Duty Finder agent/addon visibility was contradictory; DAD preserved it without navigation.",
                now);
            return;
        }

        var hud = AgentHUD.Instance();
        if (hud == null || !hud->IsMainCommandEnabled(33))
        {
            BeginProbeFinish(
                active,
                DadRouletteRewardProbeOutcome.Unknown,
                0,
                0,
                "Duty Finder is closed but its main command is unavailable.",
                now);
            return;
        }

        active.OpenedByDad = true;
        active.OpenIssuedAtUtc = now;
        active.State = ProbeState.Opening;
        active.Summary = "DAD opened Duty Finder for an exact roulette reward inspection.";
        agent->Show();
    }

    private void AdvanceProbeOpening(ProbeSession active, DateTime now)
    {
        if (!TryGetVisibleDutyFinder(out _, out _, out _))
        {
            if (now - active.OpenIssuedAtUtc >= OpenTimeout)
            {
                BeginProbeFinish(
                    active,
                    DadRouletteRewardProbeOutcome.Unknown,
                    0,
                    0,
                    "Duty Finder did not become visible after DAD opened it.",
                    now);
            }
            return;
        }

        if (!DadRouletteRewardProbeUiOwnershipRules.CanNavigate(active.WasAlreadyOpen))
        {
            BeginProbeFinish(
                active,
                DadRouletteRewardProbeOutcome.Unknown,
                0,
                0,
                "Duty Finder navigation ownership was lost.",
                now);
            return;
        }

        active.Inspection = new RewardInspection(
            active.Request.CharacterContentId,
            active.Request.RouletteId,
            $"Roulette #{active.Request.RouletteId}",
            canNavigate: true,
            now);
        active.State = ProbeState.Inspecting;
        active.Summary = $"Hydrating exact Daily Roulette #{active.Request.RouletteId}.";
    }

    private void AdvanceProbeInspection(ProbeSession active, DateTime now)
    {
        if (active.Inspection == null)
        {
            BeginProbeFinish(
                active,
                DadRouletteRewardProbeOutcome.Unknown,
                0,
                0,
                "The exact roulette inspection state is unavailable.",
                now);
            return;
        }

        AdvanceRewardInspection(active.Inspection, frozenRows: null, now);
        active.Summary = active.Inspection.Summary;
        if (active.Inspection.Result == null)
            return;

        var result = active.Inspection.Result;
        var received = result.Outcome == DadRouletteRewardProbeOutcome.Received ? 1 : 0;
        var maximum = result.Outcome is
            DadRouletteRewardProbeOutcome.Received or
            DadRouletteRewardProbeOutcome.NotReceived
                ? 1
                : 0;
        BeginProbeFinish(
            active,
            result.Outcome,
            received,
            maximum,
            result.Summary,
            now);
    }

    private void BeginProbeFinish(
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
            CompleteProbe(active, outcome, received, maximum, summary, now);
            return;
        }

        active.State = ProbeState.Closing;
        active.CloseIssuedAtUtc = now;
        try
        {
            var agent = AgentContentsFinder.Instance();
            if (agent == null)
            {
                CompleteProbe(
                    active,
                    DadRouletteRewardProbeOutcome.Unknown,
                    0,
                    0,
                    $"{summary} DAD could not resolve its owned Duty Finder agent for close.",
                    now);
                return;
            }
            agent->Hide();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[dad] Failed to close DAD-owned Duty Finder for reward probe {OperationId}.", active.Request.OperationId);
            CompleteProbe(
                active,
                DadRouletteRewardProbeOutcome.Unknown,
                0,
                0,
                $"{summary} DAD could not close its owned Duty Finder window.",
                now);
        }
    }

    private void AdvanceProbeClose(ProbeSession active, DateTime now)
    {
        if (!IsDutyFinderVisible())
        {
            CompleteProbe(
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
            CompleteProbe(
                active,
                DadRouletteRewardProbeOutcome.Unknown,
                0,
                0,
                $"{active.Summary} DAD-owned Duty Finder close could not be verified.",
                now);
        }
    }

    private void CompleteProbe(
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
            "[dad] UI-hydrated Daily Roulette reward probe operation={OperationId} slot={SlotId} outcome={Outcome} rewards={Received}/{Maximum} openedByDad={OpenedByDad}: {Summary}",
            active.Request.OperationId,
            active.Request.SlotId,
            outcome,
            received,
            maximum,
            active.OpenedByDad,
            summary);
    }

    private static DadRouletteRewardProbeResultDto BuildProbeResult(ProbeSession active, DateTime now)
        => active.TerminalResult?.Clone() ?? DadRouletteRewardProbeResultDto.FromRequest(
            active.Request,
            DadRouletteRewardProbeOutcome.Pending,
            string.IsNullOrWhiteSpace(active.Summary)
                ? "Roulette reward probe is pending."
                : active.Summary,
            now,
            dutyFinderOpenedByDad: active.OpenedByDad);

    private void AdvanceDiagnostic(DiagnosticSession active, DateTime now)
    {
        try
        {
            if (active.State == DiagnosticState.Closing)
            {
                AdvanceDiagnosticClose(active, now);
                return;
            }

            if (now >= active.DeadlineUtc)
            {
                FailDiagnostic(active, "Duty Roulette reward diagnostic timed out.", now);
                return;
            }

            if (!TryValidateDiagnosticIdentity(active.CharacterContentId, out var identityFailure))
            {
                FailDiagnostic(active, identityFailure, now);
                return;
            }

            if (IsContentsFinderQueueStateActive())
            {
                FailDiagnostic(active, "Duty Finder entered an active queue state during the diagnostic.", now);
                return;
            }

            switch (active.State)
            {
                case DiagnosticState.Initial:
                    AdvanceDiagnosticInitial(active, now);
                    break;
                case DiagnosticState.Opening:
                    AdvanceDiagnosticOpening(active, now);
                    break;
                case DiagnosticState.Freezing:
                    AdvanceDiagnosticFreeze(active, now);
                    break;
                case DiagnosticState.Inspecting:
                    AdvanceDiagnosticInspection(active, now);
                    break;
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[dad] Duty Roulette reward-state diagnostic failed safely.");
            FailDiagnostic(active, $"Duty Roulette diagnostic failed safely: {ex.Message}", now);
        }
    }

    private void AdvanceDiagnosticInitial(DiagnosticSession active, DateTime now)
    {
        var agent = AgentContentsFinder.Instance();
        var manager = RaptureAtkUnitManager.Instance();
        if (agent == null || manager == null)
        {
            FailDiagnostic(active, "Duty Finder agent or addon manager is unavailable.", now);
            return;
        }

        var addon = manager->GetAddonByName("ContentsFinder");
        if (addon != null && addon->IsVisible)
        {
            FailDiagnostic(
                active,
                "Duty Finder was already open; the all-roulette diagnostic preserved it without navigation.",
                now);
            return;
        }

        if (agent->IsAgentActive())
        {
            FailDiagnostic(
                active,
                "Duty Finder agent/addon visibility was contradictory; the diagnostic preserved it.",
                now);
            return;
        }

        var hud = AgentHUD.Instance();
        if (hud == null || !hud->IsMainCommandEnabled(33))
        {
            FailDiagnostic(active, "Duty Finder is closed but its main command is unavailable.", now);
            return;
        }

        active.OpenedByDad = true;
        active.OpenIssuedAtUtc = now;
        active.State = DiagnosticState.Opening;
        active.Summary = "Opening Duty Finder before freezing live Duty Roulette rows.";
        UpdatePendingDiagnosticStatus(active);
        agent->Show();
    }

    private void AdvanceDiagnosticOpening(DiagnosticSession active, DateTime now)
    {
        if (!TryGetVisibleDutyFinder(out var agent, out _, out _))
        {
            if (now - active.OpenIssuedAtUtc >= OpenTimeout)
                FailDiagnostic(active, "Duty Finder did not become visible after DAD opened it.", now);
            return;
        }

        active.FreezeGate.Reset();
        active.HydrationIssuedAtUtc = now;
        active.State = DiagnosticState.Freezing;
        active.Summary = "Hydrating the live Duty Roulette tab before freezing its rows.";
        UpdatePendingDiagnosticStatus(active);
        agent->OpenRouletteDuty(checked((byte)DadRouletteCatalogProjection.MainScenarioRouletteId));
    }

    private void AdvanceDiagnosticFreeze(DiagnosticSession active, DateTime now)
    {
        if (now - active.HydrationIssuedAtUtc >= SelectionTimeout)
        {
            FailDiagnostic(active, "Live Duty Roulette rows did not stabilize before the diagnostic timeout.", now);
            return;
        }

        if (!TryGetVisibleDutyFinder(out var agent, out var addon, out var failure))
        {
            active.Summary = failure;
            UpdatePendingDiagnosticStatus(active);
            return;
        }

        if (!DadDutyFinderLiveEntryScanner.TryCapture(agent, addon, out var snapshot, out failure))
        {
            active.Summary = failure;
            UpdatePendingDiagnosticStatus(active);
            return;
        }

        var freezeStatus = active.FreezeGate.Observe(snapshot, out var frozen, out var reason);
        if (freezeStatus == DadRouletteRewardDiagnosticFreezeStatus.Invalid)
        {
            FailDiagnostic(active, reason, now);
            return;
        }

        if (freezeStatus == DadRouletteRewardDiagnosticFreezeStatus.Waiting || frozen == null)
        {
            active.Summary = reason;
            UpdatePendingDiagnosticStatus(active);
            return;
        }

        active.FrozenRows = frozen;
        active.Progress = new DadRouletteRewardDiagnosticProgress(frozen.Rows.Count);
        active.CurrentInspection = CreateDiagnosticInspection(active, now);
        active.State = DiagnosticState.Inspecting;
        active.Summary = $"Inspecting live Duty Roulette row 1/{frozen.Rows.Count}.";
        UpdatePendingDiagnosticStatus(active);
    }

    private void AdvanceDiagnosticInspection(DiagnosticSession active, DateTime now)
    {
        if (active.FrozenRows == null || active.Progress == null || active.CurrentInspection == null)
        {
            FailDiagnostic(active, "The frozen Duty Roulette diagnostic state is incomplete.", now);
            return;
        }

        AdvanceRewardInspection(active.CurrentInspection, active.FrozenRows, now);
        active.Summary =
            $"Pending row {active.Progress.NextRowIndex + 1}/{active.Progress.TotalRows}: {active.CurrentInspection.Summary}";
        UpdatePendingDiagnosticStatus(active);
        if (active.CurrentInspection.Result == null)
            return;

        var inspectionResult = active.CurrentInspection.Result;
        var rowResult = new DadRouletteRewardDiagnosticRowResult(
            active.CurrentInspection.RouletteId,
            active.CurrentInspection.RouletteName,
            inspectionResult.FirstRawIsComplete,
            inspectionResult.SecondRawIsComplete,
            inspectionResult.Outcome,
            inspectionResult.Outcome == DadRouletteRewardProbeOutcome.Unknown
                ? inspectionResult.Summary
                : string.Empty);
        active.Progress.Add(rowResult);
        log.Information(DadRouletteRewardDiagnosticFormatting.BuildRowLog(rowResult));

        if (active.Progress.HasNext)
        {
            active.CurrentInspection = CreateDiagnosticInspection(active, now);
            active.Summary =
                $"Inspecting live Duty Roulette row {active.Progress.NextRowIndex + 1}/{active.Progress.TotalRows}.";
            UpdatePendingDiagnosticStatus(active);
            return;
        }

        BeginDiagnosticFinish(
            active,
            DadRouletteRewardDiagnosticRunState.Completed,
            "All frozen live Duty Roulette rows reached a row-specific result.",
            now);
    }

    private RewardInspection CreateDiagnosticInspection(DiagnosticSession active, DateTime now)
    {
        var frozen = active.FrozenRows ??
                     throw new InvalidOperationException("Diagnostic rows are not frozen.");
        var progress = active.Progress ??
                       throw new InvalidOperationException("Diagnostic progress is unavailable.");
        var row = frozen.Rows[progress.NextRowIndex];
        return new RewardInspection(
            active.CharacterContentId,
            row.RouletteId,
            row.LocalizedName,
            canNavigate: true,
            now);
    }

    private void FailDiagnostic(DiagnosticSession active, string reason, DateTime now)
    {
        RecordRemainingDiagnosticFailures(active, reason);
        BeginDiagnosticFinish(
            active,
            DadRouletteRewardDiagnosticRunState.Failed,
            reason,
            now);
    }

    private void RecordRemainingDiagnosticFailures(DiagnosticSession active, string reason)
    {
        if (active.FrozenRows == null || active.Progress == null)
            return;

        while (active.Progress.HasNext)
        {
            var row = active.FrozenRows.Rows[active.Progress.NextRowIndex];
            var result = new DadRouletteRewardDiagnosticRowResult(
                row.RouletteId,
                row.LocalizedName,
                null,
                null,
                DadRouletteRewardProbeOutcome.Unknown,
                reason);
            active.Progress.Add(result);
            log.Information(DadRouletteRewardDiagnosticFormatting.BuildRowLog(result));
        }
    }

    private void BeginDiagnosticFinish(
        DiagnosticSession active,
        DadRouletteRewardDiagnosticRunState finalState,
        string summary,
        DateTime now)
    {
        if (active.State is DiagnosticState.Closing or DiagnosticState.Terminal)
            return;

        active.PendingFinalState = finalState;
        active.Summary = summary;
        if (!DadRouletteRewardProbeUiOwnershipRules.ShouldClose(active.OpenedByDad))
        {
            CompleteDiagnostic(active, finalState, summary, now);
            return;
        }

        active.State = DiagnosticState.Closing;
        active.CloseIssuedAtUtc = now;
        latestDiagnosticStatus = BuildPendingDiagnosticStatus(active, "Pending: closing DAD-owned Duty Finder.");
        try
        {
            var agent = AgentContentsFinder.Instance();
            if (agent == null)
            {
                CompleteDiagnostic(
                    active,
                    DadRouletteRewardDiagnosticRunState.Failed,
                    $"{summary} DAD could not resolve its owned Duty Finder agent for close.",
                    now);
                return;
            }
            agent->Hide();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[dad] Failed to close DAD-owned Duty Finder after the roulette diagnostic.");
            CompleteDiagnostic(
                active,
                DadRouletteRewardDiagnosticRunState.Failed,
                $"{summary} DAD could not close its owned Duty Finder window.",
                now);
        }
    }

    private void AdvanceDiagnosticClose(DiagnosticSession active, DateTime now)
    {
        if (!IsDutyFinderVisible())
        {
            CompleteDiagnostic(active, active.PendingFinalState, active.Summary, now);
            return;
        }

        if (now - active.CloseIssuedAtUtc >= CloseTimeout)
        {
            CompleteDiagnostic(
                active,
                DadRouletteRewardDiagnosticRunState.Failed,
                $"{active.Summary} DAD-owned Duty Finder close could not be verified.",
                now);
        }
    }

    private void CompleteDiagnostic(
        DiagnosticSession active,
        DadRouletteRewardDiagnosticRunState finalState,
        string summary,
        DateTime now)
    {
        active.State = DiagnosticState.Terminal;
        if (active.Progress != null)
        {
            var totals = active.Progress.BuildStatus(
                finalState,
                active.StartedAtUtc,
                now);
            var finalSummary = finalState == DadRouletteRewardDiagnosticRunState.Completed
                ? totals.Summary
                : $"{totals.Summary} {summary}".Trim();
            latestDiagnosticStatus = totals with { Summary = finalSummary };
        }
        else
        {
            latestDiagnosticStatus = new DadRouletteRewardDiagnosticStatus(
                finalState,
                $"{finalState}: {summary}",
                active.StartedAtUtc,
                now);
        }

        log.Information(
            "[dad] Duty Roulette reward-state diagnostic totals status={Status} total={Total} inspected={Inspected} received={Received} unclaimed={Unclaimed} failed={Failed}: {Summary}",
            latestDiagnosticStatus.State,
            latestDiagnosticStatus.TotalRows,
            latestDiagnosticStatus.InspectedRows,
            latestDiagnosticStatus.ReceivedRows,
            latestDiagnosticStatus.UnclaimedRows,
            latestDiagnosticStatus.FailedRows,
            latestDiagnosticStatus.Summary);
    }

    private void UpdatePendingDiagnosticStatus(DiagnosticSession active)
        => latestDiagnosticStatus = BuildPendingDiagnosticStatus(active, $"Pending: {active.Summary}");

    private static DadRouletteRewardDiagnosticStatus BuildPendingDiagnosticStatus(
        DiagnosticSession active,
        string summary)
    {
        if (active.Progress != null)
        {
            return active.Progress.BuildStatus(
                DadRouletteRewardDiagnosticRunState.Pending,
                active.StartedAtUtc,
                null,
                summary);
        }

        return new DadRouletteRewardDiagnosticStatus(
            DadRouletteRewardDiagnosticRunState.Pending,
            summary,
            active.StartedAtUtc);
    }

    private void AdvanceRewardInspection(
        RewardInspection active,
        DadRouletteRewardDiagnosticFrozenRows? frozenRows,
        DateTime now)
    {
        if (active.Result != null)
            return;

        if (now >= active.DeadlineUtc)
        {
            FailRewardInspection(active, "Exact roulette inspection timed out.");
            return;
        }

        if (!TryGetVisibleDutyFinder(out var agent, out var addon, out var failure))
        {
            FailRewardInspection(active, failure);
            return;
        }

        if (active.State == RewardInspectionState.Hydrating)
        {
            if (!active.CanNavigate)
            {
                active.State = RewardInspectionState.Observing;
                active.Summary = "Reading a pre-existing exact roulette selection without navigation.";
                return;
            }

            active.MappingGate.Reset();
            active.RewardGate.Reset();
            active.State = RewardInspectionState.Mapping;
            active.Summary = $"Hydrating exact Duty Roulette #{active.RouletteId}.";
            agent->OpenRouletteDuty(checked((byte)active.RouletteId));
            return;
        }

        if (!DadDutyFinderLiveEntryScanner.TryCapture(agent, addon, out var snapshot, out failure))
        {
            active.Summary = failure;
            return;
        }

        var mapping = active.MappingGate.Observe(
            snapshot,
            new DadDutyFinderLiveTarget(
                DadDutyFinderLiveContentType.Roulette,
                active.RouletteId));
        if (!mapping.IsReady)
        {
            if (mapping.Status is DadDutyFinderMappingStatus.Absent or
                DadDutyFinderMappingStatus.Disabled or
                DadDutyFinderMappingStatus.Ambiguous or
                DadDutyFinderMappingStatus.Mismatch)
            {
                FailRewardInspection(active, mapping.Reason);
            }
            else
            {
                active.Summary = mapping.Reason;
            }
            return;
        }

        if (frozenRows != null &&
            !DadRouletteRewardDiagnosticLiveRowRules.MatchesFrozen(
                snapshot,
                frozenRows,
                out var frozenFailure))
        {
            FailRewardInspection(active, frozenFailure);
            return;
        }

        var resolved = mapping.Entry!;
        if (resolved.SelectionToken.CharacterContentId != active.CharacterContentId ||
            resolved.SelectionToken.Target != new DadDutyFinderLiveTarget(
                DadDutyFinderLiveContentType.Roulette,
                active.RouletteId))
        {
            FailRewardInspection(
                active,
                "Stable Duty Finder mapping contradicted the exact character or roulette identity.");
            return;
        }

        if (active.State == RewardInspectionState.Mapping)
        {
            if (!active.CanNavigate)
            {
                active.State = RewardInspectionState.Observing;
                return;
            }

            active.State = RewardInspectionState.Observing;
            active.RewardGate.Reset();
            active.Summary =
                $"Selected exact Duty Roulette #{active.RouletteId}; waiting for stable reward truth.";
            FireAddonIntCallback(addon, 3, resolved.UiRow.CallbackOrdinal);
            return;
        }

        var selectedType = ConvertContentType(agent->SelectedDuty.ContentType);
        if (!DadRouletteRewardExactSelectionRules.CanReadNativeRewardState(
                mapping,
                active.CharacterContentId,
                active.RouletteId,
                agent->HasRouletteSelected,
                selectedType,
                agent->SelectedDuty.Id))
        {
            if (agent->HasRouletteSelected)
            {
                FailRewardInspection(
                    active,
                    "Duty Finder selected a different roulette during exact reward inspection.");
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
            FailRewardInspection(active, "InstanceContent reward state is unavailable.");
            return;
        }

        var rawIsComplete =
            instanceContent->IsRouletteComplete(checked((byte)active.RouletteId));
        var received = rawIsComplete ? 1 : 0;
        var observation = new DadRouletteRewardObservation(
            Plugin.PlayerState.ContentId,
            active.RouletteId,
            resolved.SelectionToken.ListFingerprint,
            ExactRouletteSelected: true,
            received,
            MaxRewardCount: 1,
            now);
        var status = active.RewardGate.Observe(
            observation,
            active.CharacterContentId,
            active.RouletteId,
            out var observationReason);
        active.FirstRawCandidate = rawIsComplete;
        active.Summary = observationReason;
        if (status == DadRouletteRewardObservationStatus.Invalid)
        {
            FailRewardInspection(active, observationReason);
        }
        else if (status == DadRouletteRewardObservationStatus.Received)
        {
            active.Result = new RewardInspectionResult(
                DadRouletteRewardProbeOutcome.Received,
                rawIsComplete,
                rawIsComplete,
                "Two stable UI-hydrated native reads report the exact roulette reward already received.");
            active.State = RewardInspectionState.Terminal;
        }
        else if (status == DadRouletteRewardObservationStatus.NotReceived)
        {
            active.Result = new RewardInspectionResult(
                DadRouletteRewardProbeOutcome.NotReceived,
                rawIsComplete,
                rawIsComplete,
                "Two stable UI-hydrated native reads report the exact roulette reward unclaimed.");
            active.State = RewardInspectionState.Terminal;
        }
    }

    private static void FailRewardInspection(RewardInspection active, string failure)
    {
        active.Summary = failure;
        active.State = RewardInspectionState.Terminal;
        active.Result = new RewardInspectionResult(
            DadRouletteRewardProbeOutcome.Unknown,
            active.FirstRawCandidate,
            null,
            failure);
    }

    private bool TryValidateLocalIdentity(
        DadRouletteRewardProbeRequestDto request,
        out string failure)
    {
        var live = presenceService.BuildLiveSafetySnapshot();
        if (!string.Equals(
                live.WorkerSessionId.Value,
                request.RouteWorkerSessionId.Value,
                StringComparison.OrdinalIgnoreCase) ||
            !DadRosterIdentity.SameAccount(live.ManagedAccountKey, request.AccountKey) ||
            !string.Equals(
                live.ActiveCharacterKey.Value,
                request.CharacterKey.Value,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                live.Character.CharacterKey,
                request.CharacterKey.Value,
                StringComparison.OrdinalIgnoreCase) ||
            live.Character.ContentId != request.CharacterContentId ||
            !live.IsAvailable ||
            !live.WorldReadyStable)
        {
            failure =
                "The local route is not world-ready on the exact requested account, character, and Content ID.";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    private bool TryValidateDiagnosticIdentity(
        ulong expectedCharacterContentId,
        out string failure)
    {
        var live = presenceService.BuildLiveSafetySnapshot();
        if (expectedCharacterContentId == 0 ||
            Plugin.PlayerState.ContentId != expectedCharacterContentId ||
            live.Character.ContentId != expectedCharacterContentId ||
            !live.IsAvailable ||
            !live.WorldReadyStable)
        {
            failure =
                "The current character identity or world-ready state drifted during the Duty Roulette diagnostic.";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    private static bool TryGetVisibleDutyFinder(
        out AgentContentsFinder* agent,
        out AtkUnitBase* addon,
        out string failure)
    {
        agent = AgentContentsFinder.Instance();
        var manager = RaptureAtkUnitManager.Instance();
        addon = manager == null ? null : manager->GetAddonByName("ContentsFinder");
        if (agent == null || addon == null || !addon->IsVisible)
        {
            failure = "Duty Finder is not visibly available.";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    private static bool IsDutyFinderVisible()
    {
        var manager = RaptureAtkUnitManager.Instance();
        var addon = manager == null ? null : manager->GetAddonByName("ContentsFinder");
        return addon != null && addon->IsVisible;
    }

    private static bool IsExactSelectedRoulette(
        AgentContentsFinder* agent,
        uint rouletteId)
        => agent != null &&
           DadRouletteSelectionProof.IsExact(
               agent->HasRouletteSelected,
               agent->SelectedDuty.ContentType == ContentsType.Roulette,
               agent->SelectedDuty.Id,
               rouletteId);

    private static bool IsContentsFinderQueueStateActive()
    {
        var contentsFinder = ContentsFinder.Instance();
        return contentsFinder != null &&
               contentsFinder->QueueInfo.QueueState is
                   ContentsFinderQueueState.Pending or
                   ContentsFinderQueueState.Queued or
                   ContentsFinderQueueState.Ready or
                   ContentsFinderQueueState.Accepted;
    }

    private static DadDutyFinderLiveContentType ConvertContentType(ContentsType contentType)
        => contentType switch
        {
            ContentsType.Roulette => DadDutyFinderLiveContentType.Roulette,
            ContentsType.Regular => DadDutyFinderLiveContentType.Regular,
            _ => DadDutyFinderLiveContentType.None,
        };

    private static void FireAddonIntCallback(
        AtkUnitBase* addon,
        int first,
        int second)
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
        if (session is { OpenedByDad: true, State: not ProbeState.Terminal } ||
            diagnosticSession is { OpenedByDad: true, State: not DiagnosticState.Terminal })
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
        diagnosticSession = null;
    }

    private enum ProbeState
    {
        Initial,
        Opening,
        Inspecting,
        Closing,
        Terminal,
    }

    private enum DiagnosticState
    {
        Initial,
        Opening,
        Freezing,
        Inspecting,
        Closing,
        Terminal,
    }

    private enum RewardInspectionState
    {
        Hydrating,
        Mapping,
        Observing,
        Terminal,
    }

    private sealed class ProbeSession
    {
        public ProbeSession(
            DadRouletteRewardProbeRequestDto request,
            DateTime now)
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
        public DateTime CloseIssuedAtUtc { get; set; }
        public RewardInspection? Inspection { get; set; }
        public DadRouletteRewardProbeOutcome PendingOutcome { get; set; } =
            DadRouletteRewardProbeOutcome.Unknown;
        public int PendingReceived { get; set; }
        public int PendingMaximum { get; set; }
        public string Summary { get; set; } =
            "Waiting to inspect exact Daily Roulette reward truth.";
        public DadRouletteRewardProbeResultDto? TerminalResult { get; set; }
    }

    private sealed class DiagnosticSession
    {
        public DiagnosticSession(
            ulong characterContentId,
            DateTime now)
        {
            CharacterContentId = characterContentId;
            StartedAtUtc = now;
            DeadlineUtc = now + DiagnosticTimeout;
        }

        public ulong CharacterContentId { get; }
        public DateTime StartedAtUtc { get; }
        public DateTime DeadlineUtc { get; }
        public DiagnosticState State { get; set; }
        public bool OpenedByDad { get; set; }
        public DateTime OpenIssuedAtUtc { get; set; }
        public DateTime HydrationIssuedAtUtc { get; set; }
        public DateTime CloseIssuedAtUtc { get; set; }
        public DadRouletteRewardDiagnosticRunState PendingFinalState { get; set; } =
            DadRouletteRewardDiagnosticRunState.Failed;
        public string Summary { get; set; } =
            "Waiting to open Duty Finder for the roulette diagnostic.";
        public DadRouletteRewardDiagnosticFreezeGate FreezeGate { get; } = new();
        public DadRouletteRewardDiagnosticFrozenRows? FrozenRows { get; set; }
        public DadRouletteRewardDiagnosticProgress? Progress { get; set; }
        public RewardInspection? CurrentInspection { get; set; }
    }

    private sealed class RewardInspection
    {
        public RewardInspection(
            ulong characterContentId,
            uint rouletteId,
            string rouletteName,
            bool canNavigate,
            DateTime now)
        {
            CharacterContentId = characterContentId;
            RouletteId = rouletteId;
            RouletteName = rouletteName;
            CanNavigate = canNavigate;
            DeadlineUtc = now + SelectionTimeout;
            State = canNavigate
                ? RewardInspectionState.Hydrating
                : RewardInspectionState.Observing;
        }

        public ulong CharacterContentId { get; }
        public uint RouletteId { get; }
        public string RouletteName { get; }
        public bool CanNavigate { get; }
        public DateTime DeadlineUtc { get; }
        public RewardInspectionState State { get; set; }
        public string Summary { get; set; } =
            "Waiting for exact UI-hydrated roulette reward truth.";
        public bool? FirstRawCandidate { get; set; }
        public DadDutyFinderStableMappingGate MappingGate { get; } = new();
        public DadRouletteRewardObservationGate RewardGate { get; } = new();
        public RewardInspectionResult? Result { get; set; }
    }

    private sealed record RewardInspectionResult(
        DadRouletteRewardProbeOutcome Outcome,
        bool? FirstRawIsComplete,
        bool? SecondRawIsComplete,
        string Summary);
}
