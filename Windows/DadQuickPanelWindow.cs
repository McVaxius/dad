using AethertekUI.Dalamud;
using AethertekUI;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using dad.Models;

namespace dad.Windows;

public sealed class DadQuickPanelWindow : Window, IDisposable
{
    private readonly MaterialWindowMotion motion = new();
    private static readonly Vector2 MinimumWindowSize = new(430f, 250f);
    private readonly Plugin plugin;
    private Vector2? pendingPosition;
    private bool resetPositionConditionNextDraw;
    private string command = string.Empty;
    private string lastStatus = "Enter one registered slash command.";
    private int queuedCount;
    private int acceptedCount;
    private int rejectedCount;

    public DadQuickPanelWindow(Plugin plugin)
        : base("DAD Quick Commands###DadQuickCommands", ImGuiWindowFlags.HorizontalScrollbar)
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = MinimumWindowSize,
            MaximumSize = new Vector2(900f, 900f),
        };
        Size = new Vector2(460f, 330f);
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
        var minX = viewport.WorkPos.X + 1f;
        var minY = viewport.WorkPos.Y + 1f;
        var maxX = MathF.Max(minX, viewport.WorkPos.X + viewport.WorkSize.X - MinimumWindowSize.X - 24f);
        var maxY = MathF.Max(minY, viewport.WorkPos.Y + viewport.WorkSize.Y - MinimumWindowSize.Y - 24f);
        QueuePosition(new Vector2(
            minX + (float)Random.Shared.NextDouble() * MathF.Max(1f, maxX - minX),
            minY + (float)Random.Shared.NextDouble() * MathF.Max(1f, maxY - minY)));
    }

    public override void PreDraw() => motion.Prepare(this, reducedMotion: false, roundedCorners: true);

    public override void PostDraw() => motion.Restore(this);

    public override void Draw()
    {
        motion.DrawChrome();
        UiGui.TitleWithButtons(WindowName.Split("##",2)[0],null,this);
        ApplyPendingPositionChange();
        var windowRootId = ImGui.GetID("");
        DadUi.IconHeading("Quick Commands",string.Empty,MaterialIcon.Terminal);
        if (ImGui.IsItemHovered()) UiGui.SetTooltip("Send one registered slash command through DAD's authenticated coordinator route.");

        if (!plugin.Configuration.RunAsServerDad)
        {
            DrawClientReceiverGate();
            return;
        }

        var targets = GetConnectedClientTargets();
        var validation=ValidateCommand(command,out var submittedCommand);
        var sendLabel=UiText.F("Send to all ({0})",targets.Count);
        var sendWidth=UiGui.IconButtonWidth($"Send to all ({targets.Count})",sendLabel);
        var split=ImGui.GetContentRegionAvail().X>=sendWidth+190*MaterialTheme.Metrics.Scale;
        ImGui.SetNextItemWidth(split?ImGui.GetContentRegionAvail().X-sendWidth-ImGui.GetStyle().ItemSpacing.X:-1);
        UiGui.InputText("##dad-quick-command",ref command,256);
        if (ImGui.IsItemHovered() && !string.IsNullOrWhiteSpace(validation)) UiGui.SetTooltip(validation);
        validation=ValidateCommand(command,out submittedCommand);
        if (split) ImGui.SameLine();
        ImGui.BeginDisabled(targets.Count==0 || !string.IsNullOrWhiteSpace(validation));
        // Preserve the original dynamic English ID while measuring the translated button.
        if (DadUi.IconButton($"Send to all ({targets.Count})",MaterialIcon.Send,DadUiTone.Accent,new Vector2(split?sendWidth:-1,0),sendLabel)) SendToTargets(targets,submittedCommand);
        ImGui.EndDisabled();
        if (!string.IsNullOrWhiteSpace(validation)) UiGui.TextDisabled(validation);
        if (DadUi.BeginCard("dad-quick-targets"))
        {
            DadUi.Heading("Connected Client DADs",string.Empty);
            if (targets.Count==0) UiGui.TextDisabled("No connected Client DADs.");
            var targetWidth=targets.Select(target=>MaterialText.Measure(FormatTarget(target)).X).DefaultIfEmpty(0).Max()+2*ImGui.GetStyle().CellPadding.X;
            var actionWidth=UiGui.IconButtonWidth("Send")+2*ImGui.GetStyle().CellPadding.X;
            var targetHeight=targets.Count*(ImGui.GetFrameHeight()+2*ImGui.GetStyle().CellPadding.Y)+ImGui.GetStyle().ScrollbarSize;
            if (targets.Count>0 && ImGui.BeginTable("dad-quick-target-list",2,ImGuiTableFlags.SizingFixedFit|ImGuiTableFlags.RowBg|ImGuiTableFlags.ScrollX|ImGuiTableFlags.NoSavedSettings,new Vector2(0,targetHeight),Math.Max(ImGui.GetContentRegionAvail().X,targetWidth+actionWidth)))
            {
                ImGui.TableSetupColumn("Character",ImGuiTableColumnFlags.WidthFixed,targetWidth);
                ImGui.TableSetupColumn("Send",ImGuiTableColumnFlags.WidthFixed,actionWidth);
                foreach (var target in targets)
                {
                    ImGui.TableNextRow();ImGui.TableNextColumn();MaterialText.Text(FormatTarget(target));
                    ImGui.TableNextColumn();ImGui.BeginDisabled(!string.IsNullOrWhiteSpace(validation));
                    ImGuiP.PushOverrideID(windowRootId);
                    ImGui.PushStyleVar(ImGuiStyleVar.FramePadding,new Vector2(ImGui.GetStyle().FramePadding.X,0));
                    if (UiGui.IconButton($"Send##dad-quick-send-{target.WorkerSessionId.Value}",MaterialIcon.Send)) SendToTargets([target],submittedCommand);
                    ImGui.PopStyleVar();
                    ImGui.PopID();
                    ImGui.EndDisabled();
                }
                ImGui.EndTable();
            }
            DadUi.EndCard();
        }
        DadUi.Section("Session status");
        DadUi.KeyValue("Dispatch", $"{queuedCount} queued | {acceptedCount} accepted | {rejectedCount} rejected", 84f);
        UiGui.TextWrapped(lastStatus);
    }

    private void DrawClientReceiverGate()
    {
        DadUi.Section("This Client DAD", "Coordinator commands are rejected unless this existing opt-in is enabled.");
        var allow = plugin.Configuration.AllowRemoteCommandExecution;
        if (UiGui.Checkbox("Allow authenticated Coordinator registered commands", ref allow))
        {
            plugin.Configuration.AllowRemoteCommandExecution = allow;
            plugin.Configuration.Save();
        }

        UiGui.TextWrapped("This existing gate covers quick-panel and configured character-load commands. Only one-line registered slash commands are accepted; DAD disabled or Local-only mode still rejects remote mutation.");
        DadUi.Badge(
            allow ? "Receiver opted in" : "Receiver off",
            allow ? DadUiTone.Success : DadUiTone.Neutral);
    }

    private List<DadParticipantSnapshot> GetConnectedClientTargets()
        => plugin.TransportService.CurrentTransport.KnownParticipants
            .Where(participant =>
                !participant.IsLocalClient &&
                participant.WorkerRole == DadWorkerRole.ClientDad &&
                !participant.WorkerSessionId.IsEmpty &&
                plugin.TransportService.IsWorkerOnline(participant.WorkerSessionId))
            .DistinctBy(static participant => participant.WorkerSessionId.Value, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static participant => participant.ActiveCharacterKey.Value, StringComparer.OrdinalIgnoreCase)
            .Select(static participant => participant.Clone())
            .ToList();

    private void SendToTargets(IReadOnlyList<DadParticipantSnapshot> targets, string submittedCommand)
    {
        var queuedNow = 0;
        foreach (var target in targets)
        {
            var request = new DadCharacterLoadCommandDto
            {
                AccountKey = target.ManagedAccountKey,
                CharacterKey = target.ActiveCharacterKey,
                Command = submittedCommand,
                DryRun = false,
            };
            var queued = plugin.TransportService.SendCharacterLoadCommand(
                target,
                request,
                result =>
                {
                    if (result.Accepted)
                        acceptedCount++;
                    else
                        rejectedCount++;
                    lastStatus = result.Accepted
                        ? "Latest Client DAD response: command accepted."
                        : $"Latest Client DAD response: {result.Summary}";
                },
                failure =>
                {
                    rejectedCount++;
                    lastStatus = $"Latest Client DAD response: {failure}";
                });
            if (queued)
            {
                queuedCount++;
                queuedNow++;
            }
            else
            {
                rejectedCount++;
                lastStatus = "A command was not queued because its Client DAD route is no longer available.";
            }
        }

        if (queuedNow > 0)
            lastStatus = $"Queued a command for {queuedNow} Client DAD(s); awaiting one acknowledgement from each.";
    }

    private string FormatTarget(DadParticipantSnapshot participant)
    {
        var character = participant.ActiveCharacterKey.IsEmpty
            ? UiText.T("(no loaded character)")
            : plugin.KrangleService.FormatCharacterKey(participant.ActiveCharacterKey.Value);
        var account = plugin.KrangleService.FormatAccountLabel(
            participant.ManagedAccountAlias,
            participant.ManagedAccountKey.Value);
        return $"{character} | {account}";
    }

    private static string ValidateCommand(string value, out string submittedCommand)
    {
        submittedCommand = value?.Trim() ?? string.Empty;
        if (submittedCommand.Length == 0)
            return "Enter a slash command.";
        if (submittedCommand.Length > 256)
            return "Command must be at most 256 characters.";
        if (submittedCommand[0] != '/')
            return "Command must start with /.";
        if (submittedCommand.Contains('\r') || submittedCommand.Contains('\n'))
            return "Command must be one line.";
        return string.Empty;
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
}
