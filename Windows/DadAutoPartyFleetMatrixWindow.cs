using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using dad.Models;
using dad.Services;

namespace dad.Windows;

public sealed class DadAutoPartyFleetMatrixWindow : Window
{
    private readonly Plugin plugin;
    private string tsvDraft = DadAutoPartyFleetTsv.Header + "\r\n";
    private string status = "Fleet/Crew Matrix is disabled. Preview is available; apply is not.";
    private DadAutoPartyFleetPreview? preview;
    private string blueprintName = "Fleet Duty";
    private string dutyName = string.Empty;
    private int dutyId;
    private int repeatCount = 1;
    private bool dailyReset;
    private bool dutyUnsynced;

    public DadAutoPartyFleetMatrixWindow(Plugin plugin)
        : base("DAD Fleet / Crew Matrix###DadAutoPartyFleetMatrix", ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;
        RespectCloseHotkey = false;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(720f, 560f),
            MaximumSize = new Vector2(1400f, 1200f),
        };
        Size = new Vector2(900f, 780f);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void OnOpen()
    {
        RefreshTsv();
        preview = plugin.AutoPartyFleetMatrixService.BuildPreview();
    }

    public override void Draw()
    {
        var matrix = plugin.Configuration.AutoPartyFleet;
        DadUi.Heading("Fleet / Crew Matrix", "Build deterministic DAD Plans and Schedules from bounded local inventory and ordered Crew Sets.");
        DadUi.Badge(matrix.Enabled ? "Matrix apply enabled" : "Matrix apply disabled", matrix.Enabled ? DadUiTone.Warning : DadUiTone.Neutral);
        ImGui.SameLine();
        ImGui.TextWrapped("Discord transport, pairing, and typed execution keep their separate disabled gates.");

        var enabled = matrix.Enabled;
        if (ImGui.Checkbox("Allow local Matrix apply", ref enabled))
        {
            var result = plugin.AutoPartyFleetMatrixService.SetEnabled(enabled);
            status = result.Summary;
        }
        ImGui.TextDisabled($"Revision {matrix.Revision} | {matrix.Rows.Count}/{DadAutoPartyFleetLimits.MaxFleetRows} rows | {matrix.CrewSets.Count}/{DadAutoPartyFleetLimits.MaxCrewSets} Crew Sets | {matrix.Blueprints.Count}/{DadAutoPartyFleetLimits.MaxBlueprints} blueprints");
        ImGui.Separator();

        if (ImGui.BeginTabBar("DadFleetTabs"))
        {
            if (ImGui.BeginTabItem("Matrix TSV"))
            {
                DrawTsvEditor();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Blueprints"))
            {
                DrawBlueprints();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Preview / Apply"))
            {
                DrawPreviewAndApply();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }

        ImGui.Separator();
        ImGui.TextWrapped(status);
    }

    private void DrawTsvEditor()
    {
        ImGui.TextWrapped("Exact 9-column portable TSV. DAD account/character bindings never export. Unsafe spreadsheet formula prefixes, duplicate IDs, control characters, partial Crew assignments, and oversized input are rejected.");
        ImGui.InputTextMultiline("##DadFleetTsv", ref tsvDraft, DadAutoPartyFleetLimits.MaxTsvBytes, new Vector2(-1f, 360f));
        if (ImGui.Button("Reload current export"))
            RefreshTsv();
        ImGui.SameLine();
        if (ImGui.Button("Merge current DAD roster"))
        {
            var result = plugin.AutoPartyFleetMatrixService.MergeLocalRoster(plugin.CharacterIntelligenceService.CurrentPool.Characters);
            status = result.Summary;
            if (result.Succeeded)
                RefreshTsv();
        }
        ImGui.SameLine();
        if (ImGui.Button("Validate only"))
        {
            var parsed = DadAutoPartyFleetTsv.Parse(tsvDraft);
            status = parsed.Summary;
        }
        ImGui.SameLine();
        if (ImGui.Button("Import Matrix draft"))
        {
            var result = plugin.AutoPartyFleetMatrixService.ImportTsv(tsvDraft);
            status = result.Summary;
            if (result.Succeeded)
            {
                RefreshTsv();
                preview = plugin.AutoPartyFleetMatrixService.BuildPreview();
            }
        }
    }

    private void DrawBlueprints()
    {
        ImGui.TextWrapped("A blueprint generates one canonical Plan per selected Crew Set and, optionally, one Schedule containing those Plans in stable Crew Set order.");
        ImGui.InputText("Blueprint name", ref blueprintName, DadAutoPartyFleetLimits.MaxTextLength);
        ImGui.InputText("Duty name", ref dutyName, DadAutoPartyFleetLimits.MaxTextLength);
        ImGui.InputInt("Content Finder condition ID", ref dutyId);
        ImGui.InputInt("Repeat count", ref repeatCount);
        ImGui.Checkbox("Daily-reset Schedule", ref dailyReset);
        ImGui.Checkbox("Unsynced duty", ref dutyUnsynced);
        if (ImGui.Button("Add blueprint for all Crew Sets"))
            AddBlueprint();

        ImGui.Separator();
        foreach (var blueprint in plugin.Configuration.AutoPartyFleet.Blueprints.ToList())
        {
            ImGui.PushID(blueprint.BlueprintId);
            DadUi.Section(blueprint.DisplayName);
            ImGui.TextWrapped($"{blueprint.DutyDisplayName} ({blueprint.DutyContentFinderConditionId}) | {blueprint.CrewSetIds.Count} Crew Set(s) | repeat {blueprint.RepeatCount} | {blueprint.ScheduleCadence}");
            if (ImGui.Button("Delete blueprint"))
            {
                var result = plugin.AutoPartyFleetMatrixService.RemoveBlueprint(blueprint.BlueprintId);
                status = result.Summary;
                preview = plugin.AutoPartyFleetMatrixService.BuildPreview();
            }
            ImGui.PopID();
        }
    }

    private void DrawPreviewAndApply()
    {
        if (ImGui.Button("Refresh non-mutating preview"))
        {
            preview = plugin.AutoPartyFleetMatrixService.BuildPreview();
            status = preview.Summary;
        }
        preview ??= plugin.AutoPartyFleetMatrixService.BuildPreview();
        ImGui.TextWrapped(preview.Summary);
        ImGui.TextDisabled($"Fingerprint: {preview.Fingerprint}");
        foreach (var issue in preview.Issues)
            ImGui.BulletText($"{issue.SafeCode}: {issue.Message}");
        foreach (var group in preview.PlannerGroups)
            ImGui.BulletText($"Plan: {group.DisplayName} | {group.Slots.Count} slots | queue authority {group.QueueAuthority}");
        foreach (var schedule in preview.Schedules)
            ImGui.BulletText($"Schedule: {schedule.DisplayName} | {schedule.Entries.Count} entries | {schedule.Cadence}");

        ImGui.BeginDisabled(!plugin.Configuration.AutoPartyFleet.Enabled || !preview.CanApply);
        if (DadUi.Button("Apply Plans + Schedules atomically", DadUiTone.Warning, new Vector2(-1f, 34f)))
        {
            var result = plugin.AutoPartyFleetMatrixService.Apply();
            status = result.Summary;
            preview = plugin.AutoPartyFleetMatrixService.BuildPreview();
        }
        ImGui.EndDisabled();

        var undo = plugin.Configuration.AutoPartyFleet.UndoSnapshot;
        ImGui.BeginDisabled(undo == null);
        if (ImGui.Button("Undo last Matrix apply exactly"))
        {
            var result = plugin.AutoPartyFleetMatrixService.Undo(undo?.UndoToken);
            status = result.Summary;
            preview = plugin.AutoPartyFleetMatrixService.BuildPreview();
        }
        ImGui.EndDisabled();
    }

    private void AddBlueprint()
    {
        var crewIds = plugin.Configuration.AutoPartyFleet.CrewSets
            .Select(static crew => crew.CrewSetId)
            .Where(static crewId => !string.IsNullOrWhiteSpace(crewId))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (crewIds.Count == 0 || dutyId <= 0 || string.IsNullOrWhiteSpace(blueprintName))
        {
            status = "Add at least one Crew Set and enter a blueprint name and positive condition ID.";
            return;
        }
        if (plugin.Configuration.AutoPartyFleet.Blueprints.Count >= DadAutoPartyFleetLimits.MaxBlueprints)
        {
            status = "The blueprint limit has been reached.";
            return;
        }

        var blueprint = new DadAutoPartyFleetBlueprint
        {
            BlueprintId = Guid.NewGuid().ToString("N"),
            DisplayName = blueprintName,
            CrewSetIds = crewIds,
            RunFamily = DadPlannerRunFamily.DutyFinder,
            ActivityMode = DadPlannerActivityMode.PremadeDuty,
            DutyContentFinderConditionId = checked((uint)dutyId),
            DutyDisplayName = dutyName,
            DutyUnsynced = dutyUnsynced,
            CreateSchedule = true,
            ScheduleCadence = dailyReset ? DadScheduleCadence.DailyReset : DadScheduleCadence.Manual,
            RepeatCount = repeatCount,
        }.Normalize();
        var result = plugin.AutoPartyFleetMatrixService.AddBlueprint(blueprint);
        status = result.Summary;
        preview = plugin.AutoPartyFleetMatrixService.BuildPreview();
    }

    private void RefreshTsv()
    {
        try
        {
            tsvDraft = plugin.AutoPartyFleetMatrixService.ExportTsv();
            status = "Loaded the current Matrix as safe TSV.";
        }
        catch (Exception exception)
        {
            status = $"Fleet TSV export failed safely: {exception.GetType().Name}.";
        }
    }
}
