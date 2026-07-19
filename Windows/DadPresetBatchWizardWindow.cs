using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using dad.Models;

namespace dad.Windows;

public sealed class DadPresetBatchWizardWindow : Window
{
    private readonly Plugin plugin;
    private DadAccountRosterCatalog catalog = new();
    private DadPresetBatchDraft draft = new();
    private DadPresetBatchPreview? preview;
    private int stepIndex;
    private bool initialized;
    private string status = "Nothing is written until Preview is valid and Apply is pressed.";
    private string newPoolName = "Data-center pool";
    private int newPoolCrewCount = 1;
    private readonly HashSet<uint> newPoolDataCenters = [];

    public DadPresetBatchWizardWindow(Plugin plugin)
        : base("DAD Batch Preset Wizard###DadPresetBatchWizard", ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;
        RespectCloseHotkey = false;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(780f, 600f),
            MaximumSize = new Vector2(1500f, 1400f),
        };
        Size = new Vector2(980f, 820f);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void OnOpen()
    {
        catalog = plugin.RosterCatalogService.CurrentCatalog;
        if (initialized)
            return;
        initialized = true;
        ResetDraft();
    }

    public void ResetToOrigin()
    {
        Position = new Vector2(1f, 1f);
        PositionCondition = ImGuiCond.Always;
    }

    public override void Draw()
    {
        DadUi.Heading("BATCH PRESET WIZARD", "Zip rotating accounts across named DC pools, reuse exact anchors, then append ordinary Plans and Schedules only after review.");
        ImGui.TextDisabled("Session draft only | append-only Apply | exact session-only Undo | 512 Plan/entry limit");
        ImGui.SameLine();
        if (ImGui.SmallButton("Reset draft"))
            ResetDraft();

        DrawStepRail();
        ImGui.Separator();
        switch (stepIndex)
        {
            case 0:
                DrawAccountsStep();
                break;
            case 1:
                DrawPoolsStep();
                break;
            case 2:
                DrawTemplatesStep();
                break;
            default:
                DrawPreviewStep();
                break;
        }

        ImGui.Separator();
        ImGui.TextWrapped(status);
        DrawNavigation();
    }

    private void DrawStepRail()
    {
        var labels = new[] { "1 Accounts", "2 Pools + anchors", "3 Templates", "4 Preview + Apply" };
        for (var index = 0; index < labels.Length; index++)
        {
            if (index > 0)
                ImGui.SameLine();
            if (ImGui.SmallButton($"{labels[index]}##dad-batch-step-{index}"))
                stepIndex = index;
        }
    }

    private void DrawAccountsStep()
    {
        DadUi.Heading("ACCOUNTS AND CHARACTERS", "Rotating lanes contribute one ordered character per crew. Anchor lanes contribute one configured character per pool.");
        ImGui.TextWrapped("Selecting Rotating initially selects every Active character on that account; expand the lane to remove characters. Lane order is slot order.");

        var accounts = GetAccountKeys();
        foreach (var account in accounts)
        {
            ImGui.PushID(account.Value);
            var rotatingIndex = draft.RotatingLanes.FindIndex(lane => DadRosterIdentity.SameAccount(lane.AccountKey, account));
            var anchorIndex = draft.AnchorLanes.FindIndex(lane => DadRosterIdentity.SameAccount(lane.AccountKey, account));
            var rotating = rotatingIndex >= 0;
            var anchor = anchorIndex >= 0;
            ImGui.TextUnformatted(account.Value);
            ImGui.SameLine();
            if (ImGui.Checkbox("Rotating", ref rotating))
            {
                if (rotating)
                {
                    draft.AnchorLanes.RemoveAll(lane => DadRosterIdentity.SameAccount(lane.AccountKey, account));
                    draft.RotatingLanes.Add(new DadPresetBatchRotatingLane
                    {
                        AccountKey = account,
                        Characters = GetCharacters(account).Select(DadRosterIdentity.From).ToList(),
                    });
                }
                else
                {
                    draft.RotatingLanes.RemoveAll(lane => DadRosterIdentity.SameAccount(lane.AccountKey, account));
                }
                InvalidatePreview();
                rotatingIndex = draft.RotatingLanes.FindIndex(lane => DadRosterIdentity.SameAccount(lane.AccountKey, account));
                anchor = false;
            }
            ImGui.SameLine();
            if (ImGui.Checkbox("Anchor", ref anchor))
            {
                if (anchor)
                {
                    draft.RotatingLanes.RemoveAll(lane => DadRosterIdentity.SameAccount(lane.AccountKey, account));
                    draft.AnchorLanes.Add(new DadPresetBatchAnchorLane { AccountKey = account });
                }
                else
                {
                    draft.AnchorLanes.RemoveAll(lane => DadRosterIdentity.SameAccount(lane.AccountKey, account));
                }
                InvalidatePreview();
                rotating = false;
            }

            rotatingIndex = draft.RotatingLanes.FindIndex(lane => DadRosterIdentity.SameAccount(lane.AccountKey, account));
            if (rotatingIndex >= 0)
            {
                ImGui.SameLine();
                DrawMoveButtons(draft.RotatingLanes, rotatingIndex, "rotating");
                rotatingIndex = draft.RotatingLanes.FindIndex(lane => DadRosterIdentity.SameAccount(lane.AccountKey, account));
                var lane = draft.RotatingLanes[rotatingIndex];
                ImGui.SameLine();
                ImGui.TextDisabled($"{lane.Characters.Count} selected");
                if (ImGui.TreeNode($"Characters##dad-batch-characters-{account.Value}"))
                {
                    foreach (var character in GetCharacters(account))
                    {
                        var reference = DadRosterIdentity.From(character);
                        var selected = lane.Characters.Any(candidate => DadRosterIdentity.Matches(character, candidate));
                        if (ImGui.Checkbox($"{character.CharacterKey.Value} [{character.DataCenterName}]##{DadRosterIdentity.BuildKey(character)}", ref selected))
                        {
                            if (selected)
                                lane.Characters.Add(reference);
                            else
                                lane.Characters.RemoveAll(candidate => DadRosterIdentity.Matches(character, candidate));
                            InvalidatePreview();
                        }
                    }
                    ImGui.TreePop();
                }
            }
            else if (anchor)
            {
                anchorIndex = draft.AnchorLanes.FindIndex(lane => DadRosterIdentity.SameAccount(lane.AccountKey, account));
                ImGui.SameLine();
                DrawMoveButtons(draft.AnchorLanes, anchorIndex, "anchor");
                ImGui.SameLine();
                ImGui.TextDisabled("choose one character per pool in step 2");
            }
            ImGui.PopID();
        }

        if (accounts.Count == 0)
            ImGui.TextDisabled("No exact account roster is available yet. Refresh the DAD roster first.");
    }

    private void DrawPoolsStep()
    {
        DadUi.Heading("NAMED DC POOLS AND ANCHORS", "Each selected DC can belong to only one pool. Every rotating lane must supply the requested count in every pool.");
        ImGui.SetNextItemWidth(260f);
        ImGui.InputText("New pool name", ref newPoolName, DadPresetBatchLimits.MaxTextLength);
        ImGui.SetNextItemWidth(140f);
        ImGui.InputInt("Crew count", ref newPoolCrewCount);
        DrawDataCenterChecklist(newPoolDataCenters, "new-pool");
        ImGui.BeginDisabled(draft.Pools.Count >= DadPresetBatchLimits.MaxPools);
        if (ImGui.Button("Add pool"))
        {
            draft.Pools.Add(new DadPresetBatchPool
            {
                PoolId = Guid.NewGuid().ToString("N"),
                DisplayName = newPoolName,
                DataCenterIds = newPoolDataCenters.Order().ToList(),
                CrewCount = Math.Max(1, newPoolCrewCount),
            });
            newPoolDataCenters.Clear();
            InvalidatePreview();
        }
        ImGui.EndDisabled();

        foreach (var pool in draft.Pools.ToList())
        {
            ImGui.PushID(pool.PoolId);
            if (ImGui.CollapsingHeader($"{pool.DisplayName} | {pool.CrewCount} crews | {pool.DataCenterIds.Count} DC(s)##pool", ImGuiTreeNodeFlags.DefaultOpen))
            {
                var name = pool.DisplayName;
                var count = pool.CrewCount;
                ImGui.SetNextItemWidth(260f);
                if (ImGui.InputText("Name", ref name, DadPresetBatchLimits.MaxTextLength))
                {
                    pool.DisplayName = name;
                    InvalidatePreview();
                }
                ImGui.SetNextItemWidth(140f);
                if (ImGui.InputInt("Crews", ref count))
                {
                    pool.CrewCount = Math.Max(1, count);
                    InvalidatePreview();
                }
                var selectedDataCenters = pool.DataCenterIds.ToHashSet();
                if (DrawDataCenterChecklist(selectedDataCenters, pool.PoolId))
                {
                    pool.DataCenterIds = selectedDataCenters.Order().ToList();
                    InvalidatePreview();
                }

                foreach (var anchor in draft.AnchorLanes)
                    DrawAnchorAssignment(anchor, pool);

                if (ImGui.SmallButton("Delete pool"))
                {
                    draft.Pools.Remove(pool);
                    foreach (var anchor in draft.AnchorLanes)
                        anchor.Assignments.RemoveAll(assignment => string.Equals(assignment.PoolId, pool.PoolId, StringComparison.OrdinalIgnoreCase));
                    InvalidatePreview();
                }
            }
            ImGui.PopID();
        }
    }

    private void DrawTemplatesStep()
    {
        DadUi.Heading("TEMPLATES AND SCHEDULES", "Each selected ordinary Plan is cloned once per generated crew. Source Plans remain unchanged.");
        var primaryCount = draft.RotatingLanes.Count + draft.AnchorLanes.Count;
        foreach (var group in plugin.Configuration.PlannerGroups)
        {
            var selected = draft.Templates.Any(template => string.Equals(template.PlannerGroupId, group.GroupId, StringComparison.OrdinalIgnoreCase));
            var primarySlots = DadPlannerSlotRules.CountPrimarySlots(group.Slots);
            ImGui.PushID(group.GroupId);
            if (ImGui.Checkbox($"{group.DisplayName} ({primarySlots} primary){(group.IsTemplate ? " [Template]" : string.Empty)}", ref selected))
            {
                if (selected)
                {
                    draft.Templates.Add(new DadPresetBatchTemplate
                    {
                        PlannerGroupId = group.GroupId,
                        ActivityLabel = group.DisplayName,
                        PlanNameFormat = "{Activity} {Pool} {Index:00}",
                        ScheduleName = $"{group.DisplayName} Batch",
                    });
                }
                else
                {
                    draft.Templates.RemoveAll(template => string.Equals(template.PlannerGroupId, group.GroupId, StringComparison.OrdinalIgnoreCase));
                }
                InvalidatePreview();
            }
            if (primarySlots != primaryCount)
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"needs {primaryCount}");
            }
            ImGui.PopID();
        }

        ImGui.Separator();
        foreach (var template in draft.Templates)
        {
            var source = plugin.Configuration.PlannerGroups.FirstOrDefault(group => string.Equals(
                group.GroupId,
                template.PlannerGroupId,
                StringComparison.OrdinalIgnoreCase));
            ImGui.PushID(template.PlannerGroupId);
            DadUi.Section(source?.DisplayName ?? template.PlannerGroupId);
            var activity = template.ActivityLabel;
            var format = template.PlanNameFormat;
            var schedule = template.ScheduleName;
            ImGui.SetNextItemWidth(260f);
            if (ImGui.InputText("Activity label", ref activity, DadPresetBatchLimits.MaxTextLength))
            {
                template.ActivityLabel = activity;
                InvalidatePreview();
            }
            ImGui.SetNextItemWidth(360f);
            if (ImGui.InputText("Plan name format", ref format, DadPresetBatchLimits.MaxTextLength))
            {
                template.PlanNameFormat = format;
                InvalidatePreview();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Tokens: {Activity}, {Pool}, {Index}, {Index:00}.");
            ImGui.SetNextItemWidth(300f);
            if (ImGui.InputText("Schedule name", ref schedule, DadPresetBatchLimits.MaxTextLength))
            {
                template.ScheduleName = schedule;
                InvalidatePreview();
            }
            var repeat = template.RepeatCount;
            ImGui.SetNextItemWidth(140f);
            if (ImGui.InputInt("Repeat each entry", ref repeat))
            {
                template.RepeatCount = Math.Clamp(repeat, DadScheduleRules.MinRepeatCount, DadScheduleRules.MaxRepeatCount);
                InvalidatePreview();
            }
            var daily = template.ScheduleCadence == DadScheduleCadence.DailyReset;
            if (ImGui.Checkbox("DailyReset Schedule", ref daily))
            {
                template.ScheduleCadence = daily ? DadScheduleCadence.DailyReset : DadScheduleCadence.Manual;
                if (!daily)
                    template.SetDailyRewardChecksForAllPrimary = false;
                InvalidatePreview();
            }
            ImGui.SameLine();
            ImGui.BeginDisabled(!daily);
            var allDaily = template.SetDailyRewardChecksForAllPrimary;
            if (ImGui.Checkbox("Set Daily on every primary row", ref allDaily))
            {
                template.SetDailyRewardChecksForAllPrimary = allDaily;
                InvalidatePreview();
            }
            ImGui.EndDisabled();
            ImGui.PopID();
        }

        ImGui.Separator();
        var combined = draft.CreateCombinedSchedule;
        if (ImGui.Checkbox("Create interleaved combined Schedule", ref combined))
        {
            draft.CreateCombinedSchedule = combined;
            InvalidatePreview();
        }
        if (combined)
        {
            var name = draft.CombinedScheduleName;
            ImGui.SetNextItemWidth(320f);
            if (ImGui.InputText("Combined name", ref name, DadPresetBatchLimits.MaxTextLength))
            {
                draft.CombinedScheduleName = name;
                InvalidatePreview();
            }
            var daily = draft.CombinedScheduleCadence == DadScheduleCadence.DailyReset;
            if (ImGui.Checkbox("Combined DailyReset", ref daily))
            {
                draft.CombinedScheduleCadence = daily ? DadScheduleCadence.DailyReset : DadScheduleCadence.Manual;
                InvalidatePreview();
            }
            ImGui.TextDisabled("Combined order: pool, crew number, then template order.");
        }
    }

    private void DrawPreviewStep()
    {
        DadUi.Heading("PREVIEW, APPLY, AND EXACT UNDO", "Preview is non-mutating. Apply refuses stale source state; Undo refuses any post-apply Plan or Schedule drift.");
        if (ImGui.Button("Refresh exact preview"))
        {
            catalog = plugin.RosterCatalogService.CurrentCatalog;
            preview = plugin.PresetBatchWizardService.BuildPreview(draft, catalog);
            status = preview.Summary;
        }
        preview ??= plugin.PresetBatchWizardService.BuildPreview(draft, catalog);
        ImGui.TextWrapped(preview.Summary);
        ImGui.TextDisabled($"Fingerprint {preview.Fingerprint}");
        foreach (var issue in preview.Issues)
        {
            var prefix = issue.IsBlocking ? "BLOCK" : "WARN";
            ImGui.BulletText($"{prefix} {issue.SafeCode}: {issue.Message}");
        }
        if (preview.UnusedCounts.Count > 0)
        {
            var unused = preview.UnusedCounts.Sum(static count => count.UnusedCount);
            ImGui.TextDisabled($"Unused selected rotating characters: {unused}");
        }
        foreach (var schedule in preview.Schedules)
            ImGui.BulletText($"Schedule: {schedule.DisplayName} | {schedule.Entries.Count} entries | {schedule.Cadence}");
        foreach (var plan in preview.PlannerGroups.Take(12))
            ImGui.BulletText($"Plan: {plan.DisplayName} | {DadPlannerSlotRules.CountPrimarySlots(plan.Slots)} primary rows");
        if (preview.PlannerGroups.Count > 12)
            ImGui.TextDisabled($"...and {preview.PlannerGroups.Count - 12} more Plans in the frozen preview.");

        var blocker = plugin.GetShareMutationBlocker();
        ImGui.BeginDisabled(!preview.CanApply || !string.IsNullOrWhiteSpace(blocker));
        if (DadUi.Button("Append Plans + Schedules atomically", DadUiTone.Warning, new Vector2(-1f, 36f)))
        {
            var result = plugin.PresetBatchWizardService.Apply(preview);
            status = result.Summary;
            if (result.Succeeded)
                preview = null;
        }
        ImGui.EndDisabled();
        if (!string.IsNullOrWhiteSpace(blocker))
            ImGui.TextDisabled(blocker);

        ImGui.BeginDisabled(!plugin.PresetBatchWizardService.CanUndo);
        if (ImGui.Button("Undo last batch Apply exactly"))
        {
            var result = plugin.PresetBatchWizardService.Undo(plugin.PresetBatchWizardService.UndoToken);
            status = result.Summary;
            if (result.Succeeded)
                preview = null;
        }
        ImGui.EndDisabled();
    }

    private bool DrawDataCenterChecklist(HashSet<uint> selected, string id)
    {
        var changed = false;
        ImGui.TextUnformatted("Data centers");
        foreach (var dataCenter in catalog.Characters
                     .Where(static character => character.Visibility == DadRosterVisibility.Active && character.DataCenterId.HasValue)
                     .GroupBy(static character => character.DataCenterId!.Value)
                     .Select(static group => new
                     {
                         Id = group.Key,
                         Name = group.Select(static character => character.DataCenterName)
                             .FirstOrDefault(static name => !string.IsNullOrWhiteSpace(name)) ?? $"DC {group.Key}",
                     })
                     .OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(static item => item.Id))
        {
            var value = selected.Contains(dataCenter.Id);
            if (ImGui.Checkbox($"{dataCenter.Name} ({dataCenter.Id})##{id}-{dataCenter.Id}", ref value))
            {
                if (value)
                    selected.Add(dataCenter.Id);
                else
                    selected.Remove(dataCenter.Id);
                changed = true;
            }
            ImGui.SameLine();
        }
        ImGui.NewLine();
        return changed;
    }

    private void DrawAnchorAssignment(DadPresetBatchAnchorLane anchor, DadPresetBatchPool pool)
    {
        var assignment = anchor.Assignments.FirstOrDefault(candidate => string.Equals(
            candidate.PoolId,
            pool.PoolId,
            StringComparison.OrdinalIgnoreCase));
        var selected = assignment == null
            ? null
            : GetCharacters(anchor.AccountKey).FirstOrDefault(character => DadRosterIdentity.Matches(character, assignment.Character));
        ImGui.SetNextItemWidth(420f);
        if (ImGui.BeginCombo($"Anchor {anchor.AccountKey.Value}##anchor-{anchor.AccountKey.Value}", selected?.CharacterKey.Value ?? "(select exact character)"))
        {
            foreach (var character in GetCharacters(anchor.AccountKey))
            {
                var isSelected = selected != null && DadRosterIdentity.SameRow(selected, character);
                if (ImGui.Selectable($"{character.CharacterKey.Value} [{character.DataCenterName}]", isSelected))
                {
                    if (assignment == null)
                    {
                        assignment = new DadPresetBatchAnchorAssignment { PoolId = pool.PoolId };
                        anchor.Assignments.Add(assignment);
                    }
                    assignment.Character = DadRosterIdentity.From(character);
                    InvalidatePreview();
                }
                if (isSelected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
    }

    private void DrawNavigation()
    {
        ImGui.BeginDisabled(stepIndex == 0);
        if (ImGui.Button("Back"))
            stepIndex--;
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(stepIndex >= 3);
        if (ImGui.Button("Next"))
            stepIndex++;
        ImGui.EndDisabled();
    }

    private void DrawMoveButtons<T>(List<T> values, int index, string id)
    {
        ImGui.BeginDisabled(index <= 0);
        if (ImGui.SmallButton($"Up##{id}-up"))
        {
            (values[index - 1], values[index]) = (values[index], values[index - 1]);
            InvalidatePreview();
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(index < 0 || index >= values.Count - 1);
        if (ImGui.SmallButton($"Down##{id}-down"))
        {
            (values[index + 1], values[index]) = (values[index], values[index + 1]);
            InvalidatePreview();
        }
        ImGui.EndDisabled();
    }

    private List<DadAccountKey> GetAccountKeys()
        => catalog.Characters
            .Where(static character => character.Visibility == DadRosterVisibility.Active && !character.AccountKey.IsEmpty)
            .Select(static character => character.AccountKey)
            .DistinctBy(static account => account.Value, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static account => account.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private List<DadRosterCharacter> GetCharacters(DadAccountKey account)
        => catalog.Characters
            .Where(character => character.Visibility == DadRosterVisibility.Active && DadRosterIdentity.SameAccount(character.AccountKey, account))
            .OrderBy(static character => character.DataCenterName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static character => character.WorldName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static character => character.CharacterName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static character => character.ContentId)
            .ToList();

    private void InvalidatePreview()
    {
        preview = null;
        status = "Draft changed. Refresh Preview before Apply.";
    }

    private void ResetDraft()
    {
        catalog = plugin.RosterCatalogService.CurrentCatalog;
        draft = new DadPresetBatchDraft();
        preview = null;
        stepIndex = 0;
        status = "Draft reset. Nothing was written.";
        newPoolName = "Data-center pool";
        newPoolCrewCount = 1;
        newPoolDataCenters.Clear();
    }
}
