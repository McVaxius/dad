using AethertekUI.Dalamud;
using AethertekUI;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using dad.Models;

namespace dad.Windows;

public sealed class DadMiniStatusWindow : Window, IDisposable
{
    private readonly MaterialWindowMotion motion = new();
    private static readonly Vector2 MinimumWindowSize = new(440f, 320f);
    private static readonly TimeSpan ConfirmationWindow = TimeSpan.FromSeconds(5);
    private readonly Plugin plugin;
    private Vector2? pendingPosition;
    private bool resetPositionConditionNextDraw;
    private string pendingAction = string.Empty;
    private DateTime pendingActionExpiresUtc = DateTime.MinValue;

    public DadMiniStatusWindow(Plugin plugin)
        : base("DAD Mini Status###DadMiniStatus", ImGuiWindowFlags.HorizontalScrollbar)
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = MinimumWindowSize,
            MaximumSize = new Vector2(1100f, 1200f),
        };
        Size = new Vector2(460f, 650f);
        SizeCondition = ImGuiCond.FirstUseEver;
        TitleBarButtons.Add(new()
        {
            Icon = FontAwesomeIcon.WindowMaximize, Priority = 0, IconOffset = new(2, 1),
            Click = button => { if (button == ImGuiMouseButton.Left) plugin.OpenMainUi(); },
            ShowTooltip = () => UiGui.SetTooltip("Open full DAD"),
        });
        TitleBarButtons.Add(new()
        {
            Icon = FontAwesomeIcon.Cog, Priority = -10, IconOffset = new(2, 1),
            Click = button => { if (button == ImGuiMouseButton.Left) plugin.OpenConfigUi(); },
            ShowTooltip = () => UiGui.SetTooltip("Settings"),
        });
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

    public override void PreDraw()
    {
        UiGui.ReserveTitleSpace(this, UiText.T(WindowName.Split("##", 2)[0]), MinimumWindowSize.X);
        motion.Prepare(this, reducedMotion: false, roundedCorners: true);
    }

    public override void PostDraw()
    {
        motion.Restore(this);
        UiGui.PaintTitleWithImage(this, UiText.T(WindowName.Split("##", 2)[0]));
    }

    public override void Draw()
    {
        motion.DrawChrome();
        ApplyPendingPositionChange();
        var snapshot = plugin.BuildMiniStatusSnapshot();
        DrawHeader(snapshot);
        DrawAutoParty(snapshot.AutoParty);
        DrawEmergencyStop();
        DrawRun(snapshot);
        DrawScheduler(snapshot);
        DrawInboundTakeover(snapshot.LocalTakeover);
        DrawFailures(snapshot);
        DrawWorkers(snapshot);
        DrawStopAllStatus(snapshot.LastStopAll);
    }

    private static void DrawInboundTakeover(DadWakeTakeoverResultDto? takeover)
    {
        var flags = takeover?.Status == DadWakeTakeoverStatus.Blocked
            ? ImGuiTreeNodeFlags.DefaultOpen
            : ImGuiTreeNodeFlags.None;
        if (takeover == null || !UiGui.IconCollapsingHeader("Inbound wake order",MaterialIcon.Bell, flags))
            return;

        var created = takeover.VermaxionReservationCreatedAtUtc ?? takeover.Snapshot.LastHeartbeatUtc;
        var age = created == default ? UiText.T("unknown") : FormatDuration(DateTime.UtcNow - created);
        DrawStateText(
            $"Target {takeover.CharacterKey} | operation {takeover.OperationToken}",
            takeover.Status == DadWakeTakeoverStatus.Blocked ? MiniState.Bad :
            takeover.Phase >= DadWakeTakeoverPhase.Prepared ? MiniState.Good : MiniState.Warning);
        UiGui.TextWrapped(UiText.F("Reservation {0} | VERMAXION {1}/{2}", takeover.VermaxionReservationState, UiText.T(Text(takeover.ExternalAutomationActivity)), UiText.T(Text(takeover.ExternalAutomationState))));
        if (takeover.VermaxionReservationState == DadVermaxionReservationState.Unavailable &&
            takeover.Phase is >= DadWakeTakeoverPhase.Prepared and <= DadWakeTakeoverPhase.Ready &&
            string.Equals(takeover.ExternalAutomationActivity, "CompatibilityHandoff", StringComparison.OrdinalIgnoreCase))
        {
            UiGui.TextWrapped("Compatibility handoff: VERMAXION idle / AR idle");
        }
        UiGui.TextWrapped(UiText.F("AutoRetainer {0} | Multi Mode {1} | takeover {2}/{3}", UiText.T(takeover.AutoRetainerBusy ? "busy" : "idle"), UiText.T(takeover.MultiModeEnabled ? "on" : "off"), takeover.Stage, takeover.Phase));
        UiGui.TextWrapped($"Order age {age} | logical order: no expiry");
        if (!string.IsNullOrWhiteSpace(takeover.VermaxionReservationSummary))
            UiGui.TextWrapped(takeover.VermaxionReservationSummary);
    }

    private void DrawHeader(DadMiniStatusSnapshot snapshot)
    {
        var windowRootId = ImGui.GetID("");
        DadUi.IconHeading("DAD Monitor",string.Empty,MaterialIcon.Chart);
        DadUi.SameLineIfFits(DadUi.BadgeWidth(snapshot.RoleText));
        DadUi.IconBadge(snapshot.RoleText,snapshot.IsCoordinator?MaterialIcon.Crown:MaterialIcon.Person,DadUiTone.Accent);
        DadUi.SameLineIfFits(DadUi.BadgeWidth(snapshot.Authority.StateText));
        var authorityTone=snapshot.Authority.Kind==DadAuthorityViewKind.RemoteStale || !string.IsNullOrWhiteSpace(snapshot.TransportError)
            ?DadUiTone.Warning:snapshot.Authority.HasRemoteAuthority || snapshot.IsCoordinator?DadUiTone.Success:DadUiTone.Neutral;
        DadUi.Badge(snapshot.Authority.StateText,authorityTone);
        if (DadUi.BeginCard("dad-mini-connection"))
        {
            var connectionWidth=Math.Max(DadUi.CaptionWidth("Connection"),DadUi.BadgeWidth(snapshot.TransportStatus)*MaterialTheme.Metrics.Scale);
            var clientsWidth=Math.Max(DadUi.CaptionWidth("Clients"),MaterialText.Measure(UiText.F("{0} client(s)",snapshot.ConnectedWorkerCount)).X);
            var columns=ImGui.GetContentRegionAvail().X>=2*Math.Max(connectionWidth,clientsWidth)+ImGui.GetStyle().CellPadding.X*4?2:1;
            if (ImGui.BeginTable("dad-mini-connection-values",columns,ImGuiTableFlags.SizingStretchSame))
            {
                ImGui.TableNextColumn();DadUi.Caption("Connection");DadUi.Badge(snapshot.TransportStatus,authorityTone);
                ImGui.TableNextColumn();DadUi.Caption("Clients");UiGui.TextUnformatted(UiText.F("{0} client(s)",snapshot.ConnectedWorkerCount));
                ImGui.EndTable();
            }
            if (ImGui.IsItemHovered()) UiGui.SetTooltip(snapshot.Authority.OwnershipText);
            ImGuiP.PushOverrideID(windowRootId);
            DrawNavigationControls();
            ImGui.PopID();
            DadUi.EndCard();
        }
        if (!snapshot.IsCoordinator && !plugin.TransportService.CurrentTransport.AuthorityRoutable)
        {
            var transport = plugin.TransportService.CurrentTransport;
            var retry = transport.NextReconnectUtc.HasValue
                ? UiText.F("next attempt in {0:0}s", Math.Max(0, (transport.NextReconnectUtc.Value - DateTime.UtcNow).TotalSeconds))
                : UiText.T("attempt in progress");
            DrawStateText(UiText.F("Coordinator reconnect {0}: {1}. Reconnect remains active until DAD is disabled.", transport.ReconnectAttempt, retry), MiniState.Warning);
        }
        if (!string.IsNullOrWhiteSpace(snapshot.TransportError))
            DrawStateText(UiText.F("Transport error: {0}",UiText.T(snapshot.TransportError)), MiniState.Bad);
    }

    private void DrawNavigationControls()
    {
        ImGui.Spacing();
        if (DadUi.IconButton("Open full DAD",MaterialIcon.ExternalLink, DadUiTone.Accent))
            plugin.OpenMainUi();
        DadUi.SameLineIfFits(DadUi.IconButtonWidth("Generate issue report"));
        if (DadUi.IconButton("Generate issue report",MaterialIcon.Document))
            plugin.GenerateIssueReport();
    }

    private void DrawAutoParty(DadMiniAutoPartySnapshot autoParty)
    {
        var windowRootId = ImGui.GetID("");
        if (!DadUi.BeginCard("dad-mini-autoparty")) return;
        DadUi.IconHeading("AutoParty",string.Empty,MaterialIcon.Group);DadUi.SameLineIfFits(DadUi.BadgeWidth(autoParty.Enabled?"Enabled":"Disabled"));
        DadUi.Badge(autoParty.Enabled?"Enabled":"Disabled",autoParty.Enabled?DadUiTone.Success:DadUiTone.Neutral);
        (string Label,string Value)[] values=
        [
            ("Endpoint",autoParty.EndpointState),
            ("Pairing",UiText.F("{0} reciprocal | {1} online",autoParty.ActivePairingCount,autoParty.OnlinePairingCount)),
            ("Directory",autoParty.DirectoryState),
            ("Formation",autoParty.ExactFormationPhase.ToString()),
        ];
        var scale=MaterialTheme.Metrics.Scale;
        var widths=values.Select(value=>MathF.Ceiling(Math.Max(DadUi.CaptionWidth(value.Label),MaterialText.Measure(UiText.T(value.Value)).X)+2*scale)).ToArray();
        var pairedWidths=new[]{Math.Max(widths[0],widths[2]),Math.Max(widths[1],widths[3])};
        var availableWidth=ImGui.GetContentRegionAvail().X;
        var columns=availableWidth>=widths.Sum()+35*scale?4:availableWidth>=pairedWidths.Sum()+17*scale?2:1;
        using (var geometry=new MaterialStyleScope())
        {
            geometry.Style(ImGuiStyleVar.CellPadding,new Vector2(4,4)*scale);
            if (ImGui.BeginTable("dad-mini-autoparty-values",columns,ImGuiTableFlags.SizingStretchProp|ImGuiTableFlags.BordersInnerV))
            {
                var columnWidths=columns==4?widths:columns==2?pairedWidths:new[]{Math.Max(1,availableWidth-8*scale)};
                for (var column=0;column<columns;column++)
                    ImGui.TableSetupColumn("##value-"+column,ImGuiTableColumnFlags.WidthStretch,columnWidths[column]);
                foreach (var value in values)
                {
                    ImGui.TableNextColumn();DadUi.Caption(value.Label);UiGui.TextWrapped(value.Value);
                }
                ImGui.EndTable();
            }
        }
        if (!string.IsNullOrWhiteSpace(autoParty.ExactFormationSummary)) UiGui.TextWrapped(autoParty.ExactFormationSummary);
        if (!string.IsNullOrWhiteSpace(autoParty.FirstBlocker)) DrawStateText(UiText.F("Blocker: {0}",UiText.T(autoParty.FirstBlocker)),MiniState.Warning);
        ImGuiP.PushOverrideID(windowRootId);
        if (DadUi.IconButton("Open AutoParty",MaterialIcon.ExternalLink)) plugin.OpenAutoPartyUi();
        DadUi.SameLineIfFits(DadUi.IconButtonWidth(autoParty.DirectoryRefreshInProgress?"Refreshing…":"Refresh paired directory"));
        ImGui.BeginDisabled(!autoParty.DirectoryRefreshEligible);
        if (DadUi.IconButton(autoParty.DirectoryRefreshInProgress?"Refreshing…":"Refresh paired directory",MaterialIcon.Refresh)) plugin.TryStartPairedDirectoryRefresh();
        ImGui.EndDisabled();
        if (!autoParty.DirectoryRefreshEligible && autoParty.DirectoryRefreshCooldownRemaining>TimeSpan.Zero && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            UiGui.SetTooltip(UiText.F("{0:0}s cooldown",Math.Ceiling(autoParty.DirectoryRefreshCooldownRemaining.TotalSeconds)));
        var disbandLabel=IsPending("autoparty-disband")?"Confirm AutoParty disband":"Guarded AutoParty disband";
        DadUi.SameLineIfFits(DadUi.IconButtonWidth(disbandLabel));
        ImGui.BeginDisabled(!autoParty.CanGuardedDisband);
        if (DadUi.IconButton(disbandLabel,MaterialIcon.Group,DadUiTone.Warning)) Guarded("autoparty-disband",()=>plugin.RequestAutoPartyFormationDisband());
        ImGui.EndDisabled();
        if (!autoParty.CanGuardedDisband && !string.IsNullOrWhiteSpace(autoParty.GuardedDisbandBlocker) && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            UiGui.SetTooltip(autoParty.GuardedDisbandBlocker);
        if (IsPending("autoparty-disband")) DrawStateText("Click Confirm AutoParty disband within five seconds.",MiniState.Warning);
        ImGui.PopID();
        DadUi.EndCard();
    }

    private void DrawEmergencyStop()
    {
        ImGui.Spacing();
        var stopLabel = IsPending("stop-all") ? "Confirm Stop all" : "Stop all";
        if (DadUi.IconButton(stopLabel,MaterialIcon.Stop, DadUiTone.Danger, new Vector2(-1f, 32f)))
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
        var windowRootId = ImGui.GetID("");
        if (!DadUi.BeginCard("dad-mini-activity")) return;
        var run = snapshot.DisplayRun;
        var module = run.CurrentExecutorStatus.ModuleId != DadModuleId.None ? run.CurrentExecutorStatus.ModuleId : run.ModuleId;
        var headerRight=ImGui.GetCursorScreenPos().X+ImGui.GetContentRegionAvail().X;
        DadUi.IconHeading("Current activity",string.Empty,MaterialIcon.List);
        using (UiText.Font(UiFontRole.Caption))
        {
            var statusWidth=MaterialText.Measure(UiText.T(run.Status.ToString())).X;
            if (ImGui.GetItemRectMax().X+ImGui.GetStyle().ItemSpacing.X+statusWidth<=headerRight)
            {
                ImGui.SameLine();
                ImGui.SetCursorScreenPos(new Vector2(headerRight-statusWidth,ImGui.GetCursorScreenPos().Y));
            }
            UiGui.TextColored(DadUi.ToneColor(ToneForRun(run)),run.Status.ToString());
            if (ImGui.IsItemHovered()) UiGui.SetTooltip(UiText.T(DadOperatorPhaseText.GetPhaseLabel(run))+" | "+UiText.T(module.ToString()));
        }
        var task=Text(run.ActiveTaskName);
        var progress=UiText.F("{0} / {1}",Math.Max(0,run.ActiveTaskIndex),Math.Max(run.TotalTaskCount,run.RequestedTaskCount));
        var started=run.CurrentExecutorStatus.StartedAtUtc;
        var elapsed=started.HasValue?FormatDuration(DateTime.UtcNow-started.Value):UiText.T("not reported by active run");
        var showCancel=Plugin.IsBusy(snapshot.VisibleRun);
        var cancelLabel=IsPending("cancel-run")?"Confirm cancel active run":"Cancel active run";
        var scale=MaterialTheme.Metrics.Scale;
        var cellPadding=2*ImGui.GetStyle().CellPadding.X;
        var taskWidth=Math.Max(DadUi.CaptionWidth("Task"),Math.Clamp(MaterialText.Measure(UiText.T(task)).X,90*scale,160*scale))+cellPadding;
        var progressWidth=Math.Max(DadUi.CaptionWidth("Progress"),MaterialText.Measure(progress).X)+cellPadding;
        var elapsedWidth=Math.Max(DadUi.CaptionWidth("Elapsed"),MaterialText.Measure(elapsed).X)+cellPadding;
        var valuesWidth=taskWidth+progressWidth+elapsedWidth;
        float cancelWidth;
        using (UiText.Font(UiFontRole.Small)) cancelWidth=UiGui.IconButtonWidth(cancelLabel)+cellPadding;
        var inlineCancel=showCancel && ImGui.GetContentRegionAvail().X>=valuesWidth+cancelWidth;
        var columns=inlineCancel?4:ImGui.GetContentRegionAvail().X>=valuesWidth?3:1;
        void CancelControl()
        {
            using var font=UiText.Font(UiFontRole.Small);
            ImGuiP.PushOverrideID(windowRootId);
            if (DadUi.IconButton(cancelLabel,MaterialIcon.Stop,DadUiTone.Warning))
                Guarded("cancel-run",plugin.CancelActiveRunFromMini);
            ImGui.PopID();
        }
        if (ImGui.BeginTable("dad-mini-task-progress",columns,ImGuiTableFlags.SizingStretchSame))
        {
            if (columns>1)
            {
                ImGui.TableSetupColumn("##task",ImGuiTableColumnFlags.WidthStretch,taskWidth);
                ImGui.TableSetupColumn("##progress",ImGuiTableColumnFlags.WidthFixed,progressWidth-cellPadding);
                ImGui.TableSetupColumn("##elapsed",ImGuiTableColumnFlags.WidthFixed,elapsedWidth-cellPadding);
                if (inlineCancel) ImGui.TableSetupColumn("##cancel",ImGuiTableColumnFlags.WidthFixed,cancelWidth-cellPadding);
            }
            ImGui.TableNextColumn();DadUi.Caption("Task");UiGui.TextWrapped(task);
            if (!string.IsNullOrWhiteSpace(run.ActiveTaskStatus)) UiGui.TextDisabled(run.ActiveTaskStatus);
            ImGui.TableNextColumn();DadUi.Caption("Progress");UiGui.TextUnformatted(progress);
            ImGui.TableNextColumn();DadUi.Caption("Elapsed");UiGui.TextWrapped(elapsed);
            if (inlineCancel)
            {
                ImGui.TableNextColumn();
                using (UiText.Font(UiFontRole.Caption)) ImGui.SetCursorPosY(ImGui.GetCursorPosY()+ImGui.GetTextLineHeight()+ImGui.GetStyle().ItemSpacing.Y);
                CancelControl();
            }
            ImGui.EndTable();
        }
        if (!string.IsNullOrWhiteSpace(run.Summary)) UiGui.TextWrapped(run.Summary);
        if (!string.IsNullOrWhiteSpace(run.BlockedReason))
            DrawStateText(UiText.F("Blocker: {0}", UiText.T(run.BlockedReason)), MiniState.Bad);
        foreach (var warning in run.Warnings)
            DrawStateText(UiText.F("Warning: {0}", UiText.T(warning)), MiniState.Warning);

        if (showCancel && !inlineCancel) CancelControl();
        DadUi.EndCard();
    }

    private void DrawScheduler(DadMiniStatusSnapshot snapshot)
    {
        var schedule = snapshot.Schedule.ActiveRun;
        var state = snapshot.SchedulerQueue.ActiveState;
        var pendingCount = snapshot.SchedulerQueue.PendingJobs.Count;
        var hasActivity = schedule.IsActive || state.IsActive || pendingCount > 0;
        var flags = schedule.IsActive || state.IsActive ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
        var schedulerHeaderLabel = hasActivity ? $"Schedule & queue ({pendingCount} waiting)" : "Schedule & queue";
        if (!UiGui.IconCollapsingHeader(schedulerHeaderLabel,MaterialIcon.Calendar, flags))
            return;

        DadUi.KeyValue("Schedule", UiText.F("{0} | {1}/{2} | owner {3}", Text(schedule.ScheduleName), schedule.Status, schedule.Phase, Text(schedule.RequestedBy)));
        DadUi.KeyValue("Active job", $"{Text(state.JobId)} | preset {Text(state.PresetName)} | owner {Text(snapshot.SchedulerQueue.ActiveQueueOwner)}");
        DrawStateText(UiText.F("Phase: {0} | {1}", state.Phase, UiText.T(Text(state.Summary))), StateForScheduler(state.Phase));
        if (state.IsActive)
            DadUi.KeyValue("Elapsed", FormatDuration(DateTime.UtcNow - state.StartedAtUtc));
        if (!string.IsNullOrWhiteSpace(state.BlockedReason))
            DrawStateText(UiText.F("Blocker: {0}", UiText.T(state.BlockedReason)), MiniState.Bad);

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

        if (state.Slots.Count > 0 && UiGui.TreeNode("Slots"))
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
                    ? UiText.T("no expiry")
                    : slot.TimeoutStage == DadWakeTimeoutStage.None
                    ? UiText.T("none")
                    : UiText.F("{0} {1} remaining", slot.TimeoutStage, FormatDuration(remaining));
                var takeover = slot.TakeoverStage == DadWakeTakeoverStage.Ready && !slot.Ready
                    ? UiText.T("heartbeat revalidation failed")
                    : UiText.T(slot.TakeoverStage.ToString()) + "/" + UiText.T(slot.TakeoverPhase.ToString());
                UiGui.TextWrapped(UiText.F("  client {0} | character {1} | takeover {2} | ready {3} | timeout {4}", UiText.T(slot.ClientConnected ? "connected" : "offline"), UiText.T(slot.CorrectCharacter ? "correct" : "mismatch/waiting"), takeover, UiText.T(slot.Ready ? "True" : "False"), timeout));
                var nextCheck = slot.NextTakeoverStatusCheckUtc.HasValue
                    ? UiText.F("in {0:0}s", Math.Max(0, (slot.NextTakeoverStatusCheckUtc.Value - DateTime.UtcNow).TotalSeconds))
                    : UiText.T("now");
                var reservationAge = slot.VermaxionReservationUpdatedAtUtc.HasValue
                    ? UiText.F("{0} ago", FormatDuration(DateTime.UtcNow - slot.VermaxionReservationUpdatedAtUtc.Value))
                    : UiText.T("unknown");
                UiGui.TextWrapped(UiText.F("  reservation {0} ({1}) | VERMAXION {2}/{3} | AR {4}, Multi Mode {5} | next status check {6}", slot.VermaxionReservationState, reservationAge, UiText.T(Text(slot.ExternalAutomationActivity)), UiText.T(Text(slot.ExternalAutomationState)), UiText.T(slot.AutoRetainerBusy ? "busy" : "idle"), UiText.T(slot.MultiModeEnabled ? "on" : "off"), nextCheck));
                if (!string.IsNullOrWhiteSpace(slot.BlockedReason))
                    DrawStateText(UiText.F("  Blocker: {0}", UiText.T(slot.BlockedReason)), MiniState.Bad);
            }
            ImGui.TreePop();
        }

        if (pendingCount > 0 && UiGui.TreeNode($"Pending jobs ({pendingCount})"))
        {
            for (var index = 0; index < snapshot.SchedulerQueue.PendingJobs.Count; index++)
            {
                var job = snapshot.SchedulerQueue.PendingJobs[index];
                var eligibility = !job.Enabled ? UiText.T("disabled") : job.NextEligibleTimeUtc > DateTime.UtcNow ? UiText.F("eligible {0:u}", job.NextEligibleTimeUtc) : UiText.T("eligible now");
                UiGui.TextWrapped(UiText.F("{0}. {1} | owner {2} | priority {3} | {4} | {5}", index + 1, job.PresetName, Text(job.RequestedBy), job.Priority, eligibility, UiText.T(Text(job.StatusSummary))));
                var key = $"cancel-job:{job.JobId}";
                var label = IsPending(key) ? $"Confirm cancel##{job.JobId}" : $"Cancel##{job.JobId}";
                if (UiGui.SmallButton(label))
                    Guarded(key, () => plugin.CancelSchedulerJobFromMini(job.JobId));
            }
            ImGui.TreePop();
        }
    }

    private void DrawWorkers(DadMiniStatusSnapshot snapshot)
    {
        if (!UiGui.IconCollapsingHeader($"Client details ({snapshot.ConnectedParticipants.Count + 1})",MaterialIcon.Person))
            return;
        var worker = snapshot.LocalWorker;
        DrawStateText(UiText.F("Local execution: {0} | {1} | {2} | {3}", worker.State, worker.Role, worker.ModuleId, UiText.T(Text(worker.Summary))),
            worker.State is DadWorkerExecutionState.Failed or DadWorkerExecutionState.TimedOut ? MiniState.Bad :
            worker.State is DadWorkerExecutionState.Running or DadWorkerExecutionState.Starting ? MiniState.Good : MiniState.Neutral);
        var local = snapshot.LocalParticipant;
        UiGui.TextWrapped(UiText.F("Local heartbeat/eligibility: {0} | {1} | post-AR {2} | {3}", local.State, UiText.T(local.IsEligibleForRun ? "eligible / connected" : "waiting"), UiText.T(local.PostArReady ? "True" : "False"), UiText.T(Text(local.StatusText))));
        foreach (var participant in snapshot.ConnectedParticipants)
        {
            var age = DateTime.UtcNow - participant.LastHeartbeatUtc;
            DrawStateText(UiText.F("{0}: {1} | {2} | heartbeat {3} ago | {4}", participant.WorkerSessionId, participant.State, UiText.T(participant.IsEligibleForRun ? "eligible / connected" : "waiting"), FormatDuration(age), UiText.T(Text(participant.StatusText))),
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
        if (status == null || !UiGui.IconCollapsingHeader("Latest Stop-all acknowledgement",MaterialIcon.Document, flags))
            return;
        DrawStateText(UiText.F("Operation {0} | {1} | {2}", status.OperationId, UiText.T(status.IsFinal ? "final" : "pending"), UiText.T(status.Partial ? "partial" : "complete")),
            status.Partial ? MiniState.Warning : status.IsFinal ? MiniState.Good : MiniState.Neutral);
        UiGui.TextWrapped(status.Summary);
        UiGui.TextWrapped(UiText.F("Local: {0} | {1}", status.LocalResult.State, UiText.T(Text(status.LocalResult.Summary))));
        foreach (var worker in status.Workers)
            DrawStateText(UiText.F("{0}: {1} | {2}", worker.WorkerSessionId, worker.State, UiText.T(Text(worker.Summary))), StateForStop(worker.State));
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
            : phase is DadSchedulerPresetPhase.Resolving or DadSchedulerPresetPhase.LaunchingClients or DadSchedulerPresetPhase.WaitingForHeartbeat or DadSchedulerPresetPhase.LoadingCharacters or DadSchedulerPresetPhase.WaitingForDependencies or DadSchedulerPresetPhase.ReadyToStart or DadSchedulerPresetPhase.StartingPlanner
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
        UiGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    private static string Text(string? value) => string.IsNullOrWhiteSpace(value) ? UiText.T("(none)") : value;
    private static string FormatDuration(TimeSpan value) => value.TotalHours >= 1 ? UiText.F("{0:0.0}h",value.TotalHours) : value.TotalMinutes >= 1 ? UiText.F("{0:0.0}m",value.TotalMinutes) : UiText.F("{0:0}s",Math.Max(0,value.TotalSeconds));

    private enum MiniState
    {
        Neutral,
        Good,
        Warning,
        Bad,
    }
}
