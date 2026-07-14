using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using dad.Models;

namespace dad.Windows;

public sealed class DadMiniStatusWindow : Window, IDisposable
{
    private static readonly Vector2 MinimumWindowSize = new(440f, 320f);
    private static readonly TimeSpan ConfirmationWindow = TimeSpan.FromSeconds(5);
    private readonly Plugin plugin;
    private Vector2? pendingPosition;
    private bool resetPositionConditionNextDraw;
    private string pendingAction = string.Empty;
    private DateTime pendingActionExpiresUtc = DateTime.MinValue;

    public DadMiniStatusWindow(Plugin plugin)
        : base("DAD Mini Status###DadMiniStatus", ImGuiWindowFlags.None)
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = MinimumWindowSize,
            MaximumSize = new Vector2(1100f, 1200f),
        };
        Size = new Vector2(560f, 540f);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose()
    {
    }

    public void ResetToOrigin() => QueuePosition(new Vector2(1f, 1f));

    public void QueueRandomVisibleJump()
    {
        var viewport = ImGui.GetMainViewport();
        var maxX = MathF.Max(viewport.WorkPos.X + 1f, viewport.WorkPos.X + viewport.WorkSize.X - MinimumWindowSize.X - 24f);
        var maxY = MathF.Max(viewport.WorkPos.Y + 1f, viewport.WorkPos.Y + viewport.WorkSize.Y - MinimumWindowSize.Y - 24f);
        QueuePosition(new Vector2(
            viewport.WorkPos.X + 1f + (float)Random.Shared.NextDouble() * MathF.Max(1f, maxX - viewport.WorkPos.X - 1f),
            viewport.WorkPos.Y + 1f + (float)Random.Shared.NextDouble() * MathF.Max(1f, maxY - viewport.WorkPos.Y - 1f)));
    }

    public override void Draw()
    {
        ApplyPendingPositionChange();
        var snapshot = plugin.BuildMiniStatusSnapshot();
        DrawHeader(snapshot);
        DrawNavigationControls();
        DrawEmergencyStop();
        DrawRun(snapshot);
        DrawInboundTakeover(snapshot.LocalTakeover);
        DrawScheduler(snapshot);
        DrawFailures(snapshot);
        DrawStopAllStatus(snapshot.LastStopAll);
        DrawWorkers(snapshot);
    }

    private static void DrawInboundTakeover(DadWakeTakeoverResultDto? takeover)
    {
        var flags = takeover?.Status == DadWakeTakeoverStatus.Blocked
            ? ImGuiTreeNodeFlags.DefaultOpen
            : ImGuiTreeNodeFlags.None;
        if (takeover == null || !ImGui.CollapsingHeader("Inbound wake order", flags))
            return;

        var created = takeover.VermaxionReservationCreatedAtUtc ?? takeover.Snapshot.LastHeartbeatUtc;
        var age = created == default ? "unknown" : FormatDuration(DateTime.UtcNow - created);
        DrawStateText(
            $"Target {takeover.CharacterKey} | operation {takeover.OperationToken}",
            takeover.Status == DadWakeTakeoverStatus.Blocked ? MiniState.Bad :
            takeover.Phase >= DadWakeTakeoverPhase.Prepared ? MiniState.Good : MiniState.Warning);
        ImGui.TextWrapped($"Reservation {takeover.VermaxionReservationState} | VERMAXION {Text(takeover.ExternalAutomationActivity)}/{Text(takeover.ExternalAutomationState)}");
        if (takeover.VermaxionReservationState == DadVermaxionReservationState.Unavailable &&
            takeover.Phase is >= DadWakeTakeoverPhase.Prepared and <= DadWakeTakeoverPhase.Ready &&
            string.Equals(takeover.ExternalAutomationActivity, "CompatibilityHandoff", StringComparison.OrdinalIgnoreCase))
        {
            ImGui.TextWrapped("Compatibility handoff: VERMAXION idle / AR idle");
        }
        ImGui.TextWrapped($"AutoRetainer {(takeover.AutoRetainerBusy ? "busy" : "idle")} | Multi Mode {(takeover.MultiModeEnabled ? "on" : "off")} | takeover {takeover.Stage}/{takeover.Phase}");
        ImGui.TextWrapped($"Order age {age} | logical order: no expiry");
        if (!string.IsNullOrWhiteSpace(takeover.VermaxionReservationSummary))
            ImGui.TextWrapped(takeover.VermaxionReservationSummary);
    }

    private void DrawHeader(DadMiniStatusSnapshot snapshot)
    {
        DadUi.Heading("DAD Monitor", "Current activity, connection health, and safe stop controls.");
        var authorityTone = snapshot.Authority.Kind == DadAuthorityViewKind.RemoteStale || !string.IsNullOrWhiteSpace(snapshot.TransportError)
            ? DadUiTone.Warning
            : snapshot.Authority.HasRemoteAuthority || snapshot.IsCoordinator
                ? DadUiTone.Success
                : DadUiTone.Neutral;
        DadUi.Badge($"{snapshot.RoleText} | {snapshot.Authority.StateText}", authorityTone);
        ImGui.SameLine();
        DadUi.Badge($"{snapshot.ConnectedWorkerCount} client(s)", snapshot.ConnectedWorkerCount > 0 || snapshot.IsCoordinator ? DadUiTone.Info : DadUiTone.Neutral);
        DadUi.KeyValue("Route", snapshot.Authority.OwnershipText, 92f);
        DadUi.KeyValue("Connection", snapshot.TransportStatus, 92f);
        if (!snapshot.IsCoordinator && !plugin.TransportService.CurrentTransport.AuthorityRoutable)
        {
            var transport = plugin.TransportService.CurrentTransport;
            var retry = transport.NextReconnectUtc.HasValue
                ? $"next attempt in {Math.Max(0, (transport.NextReconnectUtc.Value - DateTime.UtcNow).TotalSeconds):0}s"
                : "attempt in progress";
            DrawStateText($"Coordinator reconnect {transport.ReconnectAttempt}: {retry}. Reconnect remains active until DAD is disabled.", MiniState.Warning);
        }
        if (!string.IsNullOrWhiteSpace(snapshot.TransportError))
            DrawStateText($"Transport error: {snapshot.TransportError}", MiniState.Bad);
    }

    private void DrawNavigationControls()
    {
        ImGui.Spacing();
        if (DadUi.Button("Open full DAD", DadUiTone.Accent))
            plugin.OpenMainUi();
        ImGui.SameLine();
        if (DadUi.Button("Generate issue report"))
            plugin.GenerateIssueReport();
    }

    private void DrawEmergencyStop()
    {
        DadUi.Section("Emergency stop", "Stops the current run, schedule, and connected DAD work after confirmation.");
        var stopLabel = IsPending("stop-all") ? "Confirm Stop all" : "Stop all";
        if (DadUi.Button(stopLabel, DadUiTone.Danger, new Vector2(-1f, 32f)))
        {
            Guarded("stop-all", () =>
            {
                var status = plugin.RequestStopAll();
                plugin.PrintStatus($"Stop-all {status.OperationId}: {status.Summary}");
            });
        }
        if (IsPending("stop-all"))
            DrawStateText("Click Confirm Stop all within five seconds.", MiniState.Warning);
    }

    private void DrawRun(DadMiniStatusSnapshot snapshot)
    {
        DadUi.Section("Current activity");
        var run = snapshot.VisibleRun;
        var module = run.CurrentExecutorStatus.ModuleId != DadModuleId.None ? run.CurrentExecutorStatus.ModuleId : run.ModuleId;
        DadUi.Badge($"{run.Status} | {DadOperatorPhaseText.GetPhaseLabel(run)} | {module}", ToneForRun(run));
        DadUi.KeyValue("Task", $"{Text(run.ActiveTaskName)} | {Text(run.ActiveTaskStatus)}", 92f);
        DadUi.KeyValue("Progress", $"{Math.Max(0, run.ActiveTaskIndex)}/{Math.Max(run.TotalTaskCount, run.RequestedTaskCount)} | {run.CompletedTaskCount} completed", 92f);
        var runStarted = run.CurrentExecutorStatus.StartedAtUtc;
        DadUi.KeyValue("Elapsed", runStarted.HasValue ? FormatDuration(DateTime.UtcNow - runStarted.Value) : "not reported by active run", 92f);
        DadUi.KeyValue("Summary", Text(run.Summary), 92f);
        if (!string.IsNullOrWhiteSpace(run.BlockedReason))
            DrawStateText($"Blocker: {run.BlockedReason}", MiniState.Bad);
        foreach (var warning in run.Warnings)
            DrawStateText($"Warning: {warning}", MiniState.Warning);

        if (Plugin.IsBusy(run))
        {
            var label = IsPending("cancel-run") ? "Confirm cancel active run" : "Cancel active run";
            if (DadUi.Button(label, DadUiTone.Warning))
                Guarded("cancel-run", plugin.CancelActiveRunFromMini);
        }
    }

    private void DrawScheduler(DadMiniStatusSnapshot snapshot)
    {
        var schedule = snapshot.Schedule.ActiveRun;
        var state = snapshot.SchedulerQueue.ActiveState;
        var pendingCount = snapshot.SchedulerQueue.PendingJobs.Count;
        var hasActivity = schedule.IsActive || state.IsActive || pendingCount > 0;
        var flags = schedule.IsActive || state.IsActive ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
        var schedulerHeaderLabel = hasActivity ? $"Schedule & queue ({pendingCount} waiting)" : "Schedule & queue";
        if (!ImGui.CollapsingHeader(schedulerHeaderLabel, flags))
            return;

        DadUi.KeyValue("Schedule", $"{Text(schedule.ScheduleName)} | {schedule.Status}/{schedule.Phase} | owner {Text(schedule.RequestedBy)}");
        DadUi.KeyValue("Active job", $"{Text(state.JobId)} | preset {Text(state.PresetName)} | owner {Text(snapshot.SchedulerQueue.ActiveQueueOwner)}");
        DrawStateText($"Phase: {state.Phase} | {Text(state.Summary)}", StateForScheduler(state.Phase));
        if (state.IsActive)
            DadUi.KeyValue("Elapsed", FormatDuration(DateTime.UtcNow - state.StartedAtUtc));
        if (!string.IsNullOrWhiteSpace(state.BlockedReason))
            DrawStateText($"Blocker: {state.BlockedReason}", MiniState.Bad);

        if (schedule.IsActive)
        {
            var cancelLabel = IsPending("cancel-schedule") ? "Confirm cancel schedule" : "Cancel active schedule";
            if (DadUi.Button(cancelLabel, DadUiTone.Warning))
                Guarded("cancel-schedule", () => plugin.CancelActiveScheduleFromMini());
            if (state.IsActive && !string.IsNullOrWhiteSpace(state.JobId))
                ImGui.SameLine();
        }
        if (state.IsActive && !string.IsNullOrWhiteSpace(state.JobId))
        {
            var cancelLabel = IsPending("cancel-scheduler") ? "Confirm cancel scheduler job" : "Cancel active scheduler job";
            if (DadUi.Button(cancelLabel, DadUiTone.Warning))
                Guarded("cancel-scheduler", () => plugin.CancelSchedulerJobFromMini(state.JobId));
        }

        if (state.Slots.Count > 0 && ImGui.TreeNode("Slots"))
        {
            foreach (var slot in state.Slots)
            {
                var remaining = DadWakeStageTimeoutPolicy.GetRemaining(
                    slot,
                    DateTime.UtcNow,
                    plugin.Configuration.VermaxionHoldTimeoutSeconds,
                    plugin.Configuration.AutoRetainerBusyTimeoutSeconds,
                    plugin.Configuration.ParticipantReadyTimeoutSeconds);
                DrawStateText(
                    $"{slot.SlotId}: target {slot.RequiredCharacterKey} | worker {slot.MatchedWorkerSessionId} | active {slot.ActiveCharacterKey}",
                    slot.Ready ? MiniState.Good : string.IsNullOrWhiteSpace(slot.BlockedReason) ? MiniState.Warning : MiniState.Bad);
                var timeout = slot.WakePolicy == DadSchedulerWakePolicy.LaunchIfOffline
                    ? "no expiry"
                    : slot.TimeoutStage == DadWakeTimeoutStage.None
                    ? "none"
                    : $"{slot.TimeoutStage} {FormatDuration(remaining)} remaining";
                var takeover = slot.TakeoverStage == DadWakeTakeoverStage.Ready && !slot.Ready
                    ? "heartbeat revalidation failed"
                    : $"{slot.TakeoverStage}/{slot.TakeoverPhase}";
                ImGui.TextWrapped($"  client {(slot.ClientConnected ? "connected" : "offline")} | character {(slot.CorrectCharacter ? "correct" : "mismatch/waiting")} | takeover {takeover} | ready {slot.Ready} | timeout {timeout}");
                var nextCheck = slot.NextTakeoverStatusCheckUtc.HasValue
                    ? $"in {Math.Max(0, (slot.NextTakeoverStatusCheckUtc.Value - DateTime.UtcNow).TotalSeconds):0}s"
                    : "now";
                var reservationAge = slot.VermaxionReservationUpdatedAtUtc.HasValue
                    ? FormatDuration(DateTime.UtcNow - slot.VermaxionReservationUpdatedAtUtc.Value) + " ago"
                    : "unknown";
                ImGui.TextWrapped($"  reservation {slot.VermaxionReservationState} ({reservationAge}) | VERMAXION {Text(slot.ExternalAutomationActivity)}/{Text(slot.ExternalAutomationState)} | AR {(slot.AutoRetainerBusy ? "busy" : "idle")}, Multi Mode {(slot.MultiModeEnabled ? "on" : "off")} | next status check {nextCheck}");
                if (!string.IsNullOrWhiteSpace(slot.BlockedReason))
                    DrawStateText($"  Blocker: {slot.BlockedReason}", MiniState.Bad);
            }
            ImGui.TreePop();
        }

        if (pendingCount > 0 && ImGui.TreeNode($"Pending jobs ({pendingCount})"))
        {
            for (var index = 0; index < snapshot.SchedulerQueue.PendingJobs.Count; index++)
            {
                var job = snapshot.SchedulerQueue.PendingJobs[index];
                var eligibility = !job.Enabled ? "disabled" : job.NextEligibleTimeUtc > DateTime.UtcNow ? $"eligible {job.NextEligibleTimeUtc:u}" : "eligible now";
                ImGui.TextWrapped($"{index + 1}. {job.PresetName} | owner {Text(job.RequestedBy)} | priority {job.Priority} | {eligibility} | {Text(job.StatusSummary)}");
                var key = $"cancel-job:{job.JobId}";
                var label = IsPending(key) ? $"Confirm cancel##{job.JobId}" : $"Cancel##{job.JobId}";
                if (ImGui.SmallButton(label))
                    Guarded(key, () => plugin.CancelSchedulerJobFromMini(job.JobId));
            }
            ImGui.TreePop();
        }
    }

    private void DrawWorkers(DadMiniStatusSnapshot snapshot)
    {
        if (!ImGui.CollapsingHeader($"Client details ({snapshot.ConnectedParticipants.Count + 1})"))
            return;
        var worker = snapshot.LocalWorker;
        DrawStateText($"Local execution: {worker.State} | {worker.Role} | {worker.ModuleId} | {Text(worker.Summary)}",
            worker.State is DadWorkerExecutionState.Failed or DadWorkerExecutionState.TimedOut ? MiniState.Bad :
            worker.State is DadWorkerExecutionState.Running or DadWorkerExecutionState.Starting ? MiniState.Good : MiniState.Neutral);
        var local = snapshot.LocalParticipant;
        ImGui.TextWrapped($"Local heartbeat/eligibility: {local.State} | {(local.IsEligibleForRun ? "eligible / connected" : "waiting")} | post-AR {local.PostArReady} | {Text(local.StatusText)}");
        foreach (var participant in snapshot.ConnectedParticipants)
        {
            var age = DateTime.UtcNow - participant.LastHeartbeatUtc;
            DrawStateText($"{participant.WorkerSessionId}: {participant.State} | {(participant.IsEligibleForRun ? "eligible / connected" : "waiting")} | heartbeat {FormatDuration(age)} ago | {Text(participant.StatusText)}",
                age > TimeSpan.FromSeconds(10) ? MiniState.Warning : MiniState.Good);
        }
    }

    private static void DrawFailures(DadMiniStatusSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.RecentFailure))
            return;
        DadUi.Section("Most recent terminal failure");
        DrawStateText(snapshot.RecentFailure, MiniState.Bad);
    }

    private static void DrawStopAllStatus(DadStopAllStatus? status)
    {
        var flags = status is { IsFinal: false } || status?.Partial == true
            ? ImGuiTreeNodeFlags.DefaultOpen
            : ImGuiTreeNodeFlags.None;
        if (status == null || !ImGui.CollapsingHeader("Latest Stop-all acknowledgement", flags))
            return;
        DrawStateText($"Operation {status.OperationId} | {(status.IsFinal ? "final" : "pending")} | {(status.Partial ? "partial" : "complete")}",
            status.Partial ? MiniState.Warning : status.IsFinal ? MiniState.Good : MiniState.Neutral);
        ImGui.TextWrapped(status.Summary);
        ImGui.TextWrapped($"Local: {status.LocalResult.State} | {Text(status.LocalResult.Summary)}");
        foreach (var worker in status.Workers)
            DrawStateText($"{worker.WorkerSessionId}: {worker.State} | {Text(worker.Summary)}", StateForStop(worker.State));
    }

    private void Guarded(string action, Action execute)
    {
        var now = DateTime.UtcNow;
        if (string.Equals(pendingAction, action, StringComparison.Ordinal) && now <= pendingActionExpiresUtc)
        {
            pendingAction = string.Empty;
            pendingActionExpiresUtc = DateTime.MinValue;
            execute();
            return;
        }
        pendingAction = action;
        pendingActionExpiresUtc = now + ConfirmationWindow;
    }

    private bool IsPending(string action)
    {
        if (DateTime.UtcNow > pendingActionExpiresUtc)
            pendingAction = string.Empty;
        return string.Equals(pendingAction, action, StringComparison.Ordinal);
    }

    private void QueuePosition(Vector2 position)
    {
        pendingPosition = position;
        IsOpen = true;
    }

    private void ApplyPendingPositionChange()
    {
        if (pendingPosition.HasValue)
        {
            Position = pendingPosition.Value;
            PositionCondition = ImGuiCond.Always;
            pendingPosition = null;
            resetPositionConditionNextDraw = true;
        }
        else if (resetPositionConditionNextDraw)
        {
            PositionCondition = ImGuiCond.FirstUseEver;
            resetPositionConditionNextDraw = false;
        }
    }

    private static DadUiTone ToneForRun(DadRunResult run)
        => run.Status is DadRunStatus.Failed or DadRunStatus.PartialFailure or DadRunStatus.TimedOut or DadRunStatus.Rejected
            ? DadUiTone.Danger
            : Plugin.IsBusy(run) ? DadUiTone.Success : DadUiTone.Neutral;

    private static MiniState StateForScheduler(DadSchedulerPresetPhase phase)
        => phase is DadSchedulerPresetPhase.Blocked or DadSchedulerPresetPhase.TimedOut ? MiniState.Bad
            : phase is DadSchedulerPresetPhase.Resolving or DadSchedulerPresetPhase.LaunchingClients or DadSchedulerPresetPhase.WaitingForHeartbeat or DadSchedulerPresetPhase.LoadingCharacters or DadSchedulerPresetPhase.ReadyToStart or DadSchedulerPresetPhase.StartingPlanner
                ? MiniState.Good
                : MiniState.Neutral;

    private static MiniState StateForStop(DadStopAllWorkerState state)
        => state == DadStopAllWorkerState.Acknowledged ? MiniState.Good
            : state == DadStopAllWorkerState.Expected ? MiniState.Warning
            : MiniState.Bad;

    private static void DrawStateText(string text, MiniState state)
    {
        var color = state switch
        {
            MiniState.Good => DadUi.ToneColor(DadUiTone.Success),
            MiniState.Warning => DadUi.ToneColor(DadUiTone.Warning),
            MiniState.Bad => DadUi.ToneColor(DadUiTone.Danger),
            _ => ImGui.GetStyle().Colors[(int)ImGuiCol.Text],
        };
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    private static string Text(string? value) => string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    private static string FormatDuration(TimeSpan value) => value.TotalHours >= 1 ? $"{value.TotalHours:0.0}h" : value.TotalMinutes >= 1 ? $"{value.TotalMinutes:0.0}m" : $"{Math.Max(0, value.TotalSeconds):0}s";

    private enum MiniState
    {
        Neutral,
        Good,
        Warning,
        Bad,
    }
}
