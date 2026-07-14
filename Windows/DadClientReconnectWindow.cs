using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace dad.Windows;

public sealed class DadClientReconnectWindow : Window, IDisposable
{
    private static readonly Vector2 MinimumWindowSize = new(440f, 260f);
    private readonly Plugin plugin;
    private Vector2? pendingPosition;
    private bool resetPositionConditionNextDraw;
    private DateTime disableConfirmationExpiresUtc = DateTime.MinValue;

    public DadClientReconnectWindow(Plugin plugin)
        : base("DAD Client###DadClientReconnect", ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = MinimumWindowSize,
            MaximumSize = new Vector2(720f, 520f),
        };
        Size = new Vector2(540f, 360f);
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
        var transport = plugin.TransportService.CurrentTransport;
        var endpoint = plugin.TransportService.GetPreferredAuthorityEndpoint();
        var retryText = transport.NextReconnectUtc.HasValue
            ? $"Next attempt in {Math.Max(0, (transport.NextReconnectUtc.Value - DateTime.UtcNow).TotalSeconds):0}s"
            : "Connection attempt in progress";

        DadUi.Heading("Waiting for the Coordinator", "DAD will keep retrying automatically while this Client remains enabled.");
        ImGui.Spacing();
        DadUi.Badge("Coordinator offline", DadUiTone.Warning);
        DadUi.KeyValue("Coordinator", Text(endpoint), 120f);
        DadUi.KeyValue("Reconnect", $"Attempt {Math.Max(1, transport.ReconnectAttempt)} | {retryText}", 120f);
        DadUi.KeyValue("Transport", Text(transport.ConnectionStatus), 120f);
        if (!string.IsNullOrWhiteSpace(transport.LastDisconnectReason))
            DadUi.KeyValue("Last disconnect", transport.LastDisconnectReason, 120f);
        if (transport.LastConnectedUtc.HasValue)
            DadUi.KeyValue("Last connected", transport.LastConnectedUtc.Value.ToLocalTime().ToString("G"), 120f);

        DadUi.Section("Actions");
        if (DadUi.Button("Open full DAD", DadUiTone.Accent))
            plugin.OpenMainUi();
        ImGui.SameLine();
        if (DadUi.Button("Open DAD Monitor"))
            plugin.OpenMiniStatusUi();

        var confirming = DateTime.UtcNow <= disableConfirmationExpiresUtc;
        ImGui.Spacing();
        if (DadUi.Button(
                confirming ? "Confirm disable DAD" : "Disable DAD and stop reconnecting",
                DadUiTone.Danger,
                new Vector2(-1f, 32f)))
        {
            if (confirming)
            {
                disableConfirmationExpiresUtc = DateTime.MinValue;
                plugin.DisableDadFromReconnectWindow();
            }
            else
            {
                disableConfirmationExpiresUtc = DateTime.UtcNow.AddSeconds(5);
            }
        }
        if (confirming)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, DadUi.ToneColor(DadUiTone.Warning));
            ImGui.TextWrapped("Click Confirm disable DAD within five seconds. Reconnect attempts stop only when DAD is disabled.");
            ImGui.PopStyleColor();
        }
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

    private static string Text(string? value)
        => string.IsNullOrWhiteSpace(value) ? "(none)" : value;
}
