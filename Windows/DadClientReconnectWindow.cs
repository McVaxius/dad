using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace dad.Windows;

public sealed class DadClientReconnectWindow : Window, IDisposable
{
    private static readonly Vector2 MinimumWindowSize = new(420f, 220f);
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
        Size = new Vector2(500f, 300f);
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

        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.78f, 0.28f, 1f));
        ImGui.TextWrapped("Dad Coordinator is offline");
        ImGui.PopStyleColor();
        ImGui.TextWrapped("This DAD Client will keep reconnecting until the Coordinator returns or DAD is disabled.");
        ImGui.Separator();
        ImGui.TextWrapped($"Coordinator target: {Text(endpoint)}");
        ImGui.TextWrapped($"Reconnect attempt: {Math.Max(1, transport.ReconnectAttempt)} | {retryText}");
        ImGui.TextWrapped($"Transport: {Text(transport.ConnectionStatus)}");
        if (!string.IsNullOrWhiteSpace(transport.LastDisconnectReason))
            ImGui.TextWrapped($"Last disconnect: {transport.LastDisconnectReason}");
        if (transport.LastConnectedUtc.HasValue)
            ImGui.TextWrapped($"Last connected: {transport.LastConnectedUtc.Value.ToLocalTime():G}");

        ImGui.Spacing();
        if (ImGui.Button("Open full DAD"))
            plugin.OpenMainUi();
        ImGui.SameLine();
        if (ImGui.Button("Open DAD Mini"))
            plugin.OpenMiniStatusUi();

        var confirming = DateTime.UtcNow <= disableConfirmationExpiresUtc;
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.55f, 0.12f, 0.12f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.75f, 0.18f, 0.18f, 1f));
        if (ImGui.Button(confirming ? "Confirm Disable DAD" : "Disable DAD", new Vector2(-1f, 30f)))
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
        ImGui.PopStyleColor(2);
        if (confirming)
            ImGui.TextWrapped("Click Confirm Disable DAD within five seconds. Reconnect attempts stop only when DAD is disabled.");
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
