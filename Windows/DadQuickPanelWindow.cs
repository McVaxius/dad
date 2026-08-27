using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using dad.Models;

namespace dad.Windows;

public sealed class DadQuickPanelWindow : Window, IDisposable
{
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
        : base("DAD Quick Commands###DadQuickCommands", ImGuiWindowFlags.None)
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = MinimumWindowSize,
            MaximumSize = new Vector2(900f, 900f),
        };
        Size = new Vector2(520f, 420f);
        SizeCondition = ImGuiCond.FirstUseEver;
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

    public override void Draw()
    {
        ApplyPendingPositionChange();
        DadUi.Heading("Quick Commands", "Send one registered slash command through DAD's authenticated coordinator route.");

        if (!plugin.Configuration.RunAsServerDad)
        {
            DrawClientReceiverGate();
            return;
        }

        var targets = GetConnectedClientTargets();
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("##dad-quick-command", ref command, 256);
        var validation = ValidateCommand(command, out var submittedCommand);
        if (!string.IsNullOrWhiteSpace(validation))
            ImGui.TextDisabled(validation);
        else
            ImGui.TextDisabled("Commands resolve through each Client DAD's registered command manager; chat injection is not used.");

        ImGui.Spacing();
        ImGui.BeginDisabled(targets.Count == 0 || !string.IsNullOrWhiteSpace(validation));
        if (DadUi.Button($"Send to all ({targets.Count})", DadUiTone.Accent, new Vector2(-1f, 30f)))
            SendToTargets(targets, submittedCommand);
        ImGui.EndDisabled();

        DadUi.Section("Connected Client DADs", "Only currently routable remote Client DAD sessions are listed.");
        if (targets.Count == 0)
        {
            ImGui.TextDisabled("No connected Client DADs.");
        }
        else
        {
            foreach (var target in targets)
            {
                var label = FormatTarget(target);
                ImGui.TextWrapped(label);
                ImGui.SameLine();
                ImGui.BeginDisabled(!string.IsNullOrWhiteSpace(validation));
                if (ImGui.SmallButton($"Send##dad-quick-send-{target.WorkerSessionId.Value}"))
                    SendToTargets([target], submittedCommand);
                ImGui.EndDisabled();
            }
        }

        DadUi.Section("Session status");
        DadUi.KeyValue("Dispatch", $"{queuedCount} queued | {acceptedCount} accepted | {rejectedCount} rejected", 84f);
        ImGui.TextWrapped(lastStatus);
    }

    private void DrawClientReceiverGate()
    {
        DadUi.Section("This Client DAD", "Coordinator commands are rejected unless this existing opt-in is enabled.");
        var allow = plugin.Configuration.AllowRemoteCommandExecution;
        if (ImGui.Checkbox("Allow authenticated Coordinator registered commands", ref allow))
        {
            plugin.Configuration.AllowRemoteCommandExecution = allow;
            plugin.Configuration.Save();
        }

        ImGui.TextWrapped("This existing gate covers quick-panel and configured character-load commands. Only one-line registered slash commands are accepted; DAD disabled or Local-only mode still rejects remote mutation.");
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
            ? "(no loaded character)"
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
