using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using dad.Models;

namespace dad.Windows;

public sealed class DadDependenciesWindow : Window, IDisposable
{
    private static readonly Vector2 MinimumWindowSize = new(520f, 420f);
    private readonly Plugin plugin;
    private Vector2? pendingPosition;
    private bool resetPositionConditionNextDraw;

    public DadDependenciesWindow(Plugin plugin)
        : base("DAD Dependencies###DadDependencies", ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;
        RespectCloseHotkey = false;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = MinimumWindowSize,
            MaximumSize = new Vector2(900f, 1000f),
        };
        Size = new Vector2(620f, 620f);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose()
    {
    }

    public void Sync()
        => IsOpen = DadDependencyWindowRules.ShouldBeOpen(
            plugin.Configuration.PluginEnabled,
            plugin.DependencyService.Snapshot);

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

    public override void OnClose()
    {
        IsOpen = DadDependencyWindowRules.ResolveCloseAttempt(
            plugin.Configuration.PluginEnabled,
            plugin.DependencyService.Snapshot);
    }

    public override void Draw()
    {
        ApplyPendingPositionChange();
        var snapshot = plugin.DependencyService.Snapshot;

        DadUi.Heading("Required plugins", "DAD waits here until every required plugin is installed, current, and loaded.");
        ImGui.Spacing();
        DadUi.Badge("New work paused", DadUiTone.Warning);
        ImGui.SameLine();
        ImGui.TextWrapped("Active work keeps running. DAD never cancels a run because dependency truth changes.");
        ImGui.Spacing();

        foreach (var entry in snapshot.Entries)
        {
            DadUi.Section(entry.DisplayName);
            var tone = entry.State switch
            {
                DadDependencyState.Ready => DadUiTone.Success,
                DadDependencyState.Checking => DadUiTone.Neutral,
                _ => DadUiTone.Warning,
            };
            DadUi.Badge(FormatState(entry.State), tone);
            ImGui.SameLine();
            ImGui.TextWrapped(entry.OperatorSummary);

            if (entry.State is DadDependencyState.Missing or DadDependencyState.InstalledNotLoaded or DadDependencyState.UpdateRequired)
                DrawInstallerActions(entry);
        }

        ImGui.Spacing();
        if (DadUi.Button("Disable DAD", DadUiTone.Danger, new Vector2(-1f, 34f)))
            plugin.SetPluginEnabled(false);
    }

    private static string FormatState(DadDependencyState state)
        => state switch
        {
            DadDependencyState.Ready => "Ready",
            DadDependencyState.Missing => "Missing",
            DadDependencyState.InstalledNotLoaded => "Installed, not loaded",
            DadDependencyState.UpdateRequired => "Update required",
            _ => "Checking",
        };

    private static void DrawInstallerActions(DadDependencyEntry entry)
    {
        var options = DadDependencyInstallerRules.ResolveOptions(entry);
        for (var index = 0; index < options.Count; index++)
        {
            var option = options[index];
            var kind = option.UpdatesOnly
                ? PluginInstallerOpenKind.UpdateablePlugins
                : option.InstalledOnly
                    ? PluginInstallerOpenKind.InstalledPlugins
                    : PluginInstallerOpenKind.AllPlugins;
            if (index > 0)
                ImGui.SameLine();
            if (DadUi.Button($"{option.Label}##{entry.RequirementId}-{index}"))
                Plugin.PluginInterface.OpenPluginInstallerTo(kind, option.SearchText);
        }
    }

    private void QueuePosition(Vector2 position)
        => pendingPosition = position;

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
