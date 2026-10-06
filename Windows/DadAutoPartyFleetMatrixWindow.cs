using AethertekUI;
using AethertekUI.Dalamud;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using dad.Models;
using dad.Services;

namespace dad.Windows;

public sealed class DadAutoPartyFleetMatrixWindow : Window
{
    private readonly MaterialWindowMotion motion = new();
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

    public override void PreDraw() => motion.Prepare(this, reducedMotion: false, roundedCorners: true);

    public override void PostDraw() => motion.Restore(this);

    public override void Draw()
    {
        motion.DrawChrome();
        UiGui.Title(WindowName.Split("##",2)[0]);
        var matrix = plugin.Configuration.AutoPartyFleet;
        DadUi.Heading("Fleet / Crew Matrix", "Build deterministic DAD Plans and Schedules from bounded local inventory and ordered Crew Sets.");
        DadUi.Badge(matrix.Enabled ? "Matrix apply enabled" : "Matrix apply disabled", matrix.Enabled ? DadUiTone.Warning : DadUiTone.Neutral);
        ImGui.SameLine();
        UiGui.TextWrapped("Discord transport, pairing, and typed execution keep their separate disabled gates.");

        var enabled = matrix.Enabled;
        if (UiGui.Checkbox("Allow local Matrix apply", ref enabled))
        {
            var result = plugin.AutoPartyFleetMatrixService.SetEnabled(enabled);
            status = result.Summary;
        }
        UiGui.TextDisabled($"Revision {matrix.Revision} | {matrix.Rows.Count}/{DadAutoPartyFleetLimits.MaxFleetRows} rows | {matrix.CrewSets.Count}/{DadAutoPartyFleetLimits.MaxCrewSets} Crew Sets | {matrix.Blueprints.Count}/{DadAutoPartyFleetLimits.MaxBlueprints} blueprints");
        ImGui.Separator();

        using var tabLineHeight=MaterialText.PushLineHeight(new[]{"Matrix TSV","Blueprints","Preview / Apply"}.Select(UiText.T).ToArray());

        if (ImGui.BeginTabBar("DadFleetTabs"))
        {
            if (UiGui.BeginTabItem("Matrix TSV"))
            {
                DrawTsvEditor();
                ImGui.EndTabItem();
            }
            if (UiGui.BeginTabItem("Blueprints"))
            {
                DrawBlueprints();
                ImGui.EndTabItem();
            }
            if (UiGui.BeginTabItem("Preview / Apply"))
            {
                DrawPreviewAndApply();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }

        ImGui.Separator();
        UiGui.TextWrapped(status);
    }

    private void DrawTsvEditor()
    {
        UiGui.TextWrapped("Exact 9-column portable TSV. DAD account/character bindings never export. Unsafe spreadsheet formula prefixes, duplicate IDs, control characters, partial Crew assignments, and oversized input are rejected.");
        UiGui.InputTextMultiline("##DadFleetTsv", ref tsvDraft, DadAutoPartyFleetLimits.MaxTsvBytes, new Vector2(-1f, 360f));
        if (UiGui.Button("Reload current export"))
            RefreshTsv();
        ImGui.SameLine();
        if (UiGui.Button("Merge current DAD roster"))
        {
            var result = plugin.AutoPartyFleetMatrixService.MergeLocalRoster(plugin.CharacterIntelligenceService.CurrentPool.Characters);
            status = result.Summary;
            if (result.Succeeded)
                RefreshTsv();
        }
        ImGui.SameLine();
        if (UiGui.Button("Validate only"))
        {
            var parsed = DadAutoPartyFleetTsv.Parse(tsvDraft);
            status = parsed.Summary;
        }
        ImGui.SameLine();
        if (UiGui.Button("Import Matrix draft"))
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
        UiGui.TextWrapped("A blueprint generates one canonical Plan per selected Crew Set and, optionally, one Schedule containing those Plans in stable Crew Set order.");
        UiGui.InputText("Blueprint name", ref blueprintName, DadAutoPartyFleetLimits.MaxTextLength);
        UiGui.InputText("Duty name", ref dutyName, DadAutoPartyFleetLimits.MaxTextLength);
        UiGui.InputInt("Content Finder condition ID", ref dutyId);
        UiGui.InputInt("Repeat count", ref repeatCount);
        UiGui.Checkbox("Daily-reset Schedule", ref dailyReset);
        UiGui.Checkbox("Unsynced duty", ref dutyUnsynced);
        if (UiGui.Button("Add blueprint for all Crew Sets"))
            AddBlueprint();

        ImGui.Separator();
        foreach (var blueprint in plugin.Configuration.AutoPartyFleet.Blueprints.ToList())
        {
            ImGui.PushID(blueprint.BlueprintId);
            DadUi.Section(blueprint.DisplayName);
            UiGui.TextWrapped($"{blueprint.DutyDisplayName} ({blueprint.DutyContentFinderConditionId}) | {blueprint.CrewSetIds.Count} Crew Set(s) | repeat {blueprint.RepeatCount} | {blueprint.ScheduleCadence}");
            if (UiGui.Button("Delete blueprint"))
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
        if (UiGui.Button("Refresh non-mutating preview"))
        {
            preview = plugin.AutoPartyFleetMatrixService.BuildPreview();
            status = preview.Summary;
        }
        preview ??= plugin.AutoPartyFleetMatrixService.BuildPreview();
        UiGui.TextWrapped(preview.Summary);
        UiGui.TextDisabled($"Fingerprint: {preview.Fingerprint}");
        foreach (var issue in preview.Issues)
            UiGui.BulletText($"{issue.SafeCode}: {issue.Message}");
        foreach (var group in preview.PlannerGroups)
            UiGui.BulletText($"Plan: {group.DisplayName} | {group.Slots.Count} slots | queue authority {group.QueueAuthority}");
        foreach (var schedule in preview.Schedules)
            UiGui.BulletText($"Schedule: {schedule.DisplayName} | {schedule.Entries.Count} entries | {schedule.Cadence}");

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
        if (UiGui.Button("Undo last Matrix apply exactly"))
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
