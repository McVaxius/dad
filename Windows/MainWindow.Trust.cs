using AethertekUI;
using Dalamud.Bindings.ImGui;
using dad.Models;
using dad.Services;

namespace dad.Windows;

public sealed partial class MainWindow
{
    private readonly DadPlannerGroup?[] trustDrafts = new DadPlannerGroup?[2];
    private readonly DateTime?[] trustDraftVersions = new DateTime?[2];
    private readonly bool[] trustDraftChanged = new bool[2];
    private string trustRosterCharacter = string.Empty;
    private DadAccountKey trustRosterAccount = new(string.Empty);
    private DadTrustNpcId trustRosterNpc;
    private string trustEditorStatus = string.Empty;

    private void DrawTrustWorkspace(DadVisibleRunState runState)
    {
        var snapshot = plugin.GetPlannerUiSnapshot(runState);
        var characters = snapshot.CuratedPool.Characters.Where(character => !string.IsNullOrWhiteSpace(character.AccountId) &&
            (plugin.Configuration.RunAsServerDad || string.Equals(character.AccountId, plugin.Configuration.ClientAccountId, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var jobs = plugin.GetLevelingJobCatalog().Where(static job => DadLevelingModeCompiler.IsEligibleForRole(job, DadPartyRole.Any)).ToArray();
        var duties = plugin.PresetProviderService.SearchPlannerDutyOptions(DadPlannerActivityMode.Trust, string.Empty, 1000);
        DadUi.Heading("Trust", "Balance companions, or save an exact Trust party.");
        if (characters.Length == 0)
        {
            UiGui.TextDisabled("Choose an own-account character in Crew before creating a Trust preset.");
            return;
        }
        if (!ImGui.BeginTabBar("dad-trust-pages", ImGuiTabBarFlags.FittingPolicyScroll)) return;
        if (UiGui.BeginTabItem("NPC leveling"))
        {
            DrawTrustEditor(0, characters, jobs, duties, runState);
            ImGui.EndTabItem();
        }
        if (UiGui.BeginTabItem("Trust presets"))
        {
            DrawTrustEditor(1, characters, jobs, duties, runState);
            ImGui.EndTabItem();
        }
        if (UiGui.BeginTabItem("NPC roster"))
        {
            var character = characters.FirstOrDefault(row => DadRosterIdentity.SameAccount(new(row.AccountId), trustRosterAccount) &&
                    string.Equals(row.CharacterKey, trustRosterCharacter, StringComparison.OrdinalIgnoreCase))
                ?? characters.FirstOrDefault(static row => row.Source == DadCharacterSource.LocalRuntime) ?? characters[0];
            if (DrawTrustCharacterPicker(character, characters, out var selected)) character = selected;
            trustRosterCharacter = character.CharacterKey;
            trustRosterAccount = new(character.AccountId);
            DrawTrustRefresh(character, runState);
            DrawTrustRoster(character.TrustProgress, duties, ref trustRosterNpc);
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
        if (trustEditorStatus.Length > 0) UiGui.TextDisabled(trustEditorStatus);
    }

    private void DrawTrustEditor(int page, IReadOnlyList<DadAcquiredCharacter> characters,
        IReadOnlyList<DadLevelingJobDescriptor> jobs, IReadOnlyList<DadPlannerDutyOption> duties, DadVisibleRunState runState)
    {
        var leveling = page == 0;
        var locked = IsPlannerLocked(runState) || plugin.SchedulerService.CurrentState.IsActive;
        ImGui.BeginDisabled(locked);
        var draft = trustDrafts[page];
        if (draft == null) draft = ResetTrustDraft(page, null, characters);
        var saved = plugin.Configuration.PlannerGroups.Where(group => group.ActivityMode == DadPlannerActivityMode.Trust &&
            !group.IsTemplate && !group.AutoPartyFormationOnly && group.Slots.Count == 1 && !group.Slots[0].IsSubstitute &&
            DadPlannerSlotRules.IsLeaderSlot(group.Slots[0].SlotId) &&
            (leveling ? group.LevelingMode?.Trust != null : group.LevelingMode?.Enabled != true) &&
            (plugin.Configuration.RunAsServerDad || DadLocalDutySupportRules.CanRunPreset(group, plugin.Configuration.ClientAccountId))).ToArray();
        if (UiGui.BeginCombo("Preset##trust-preset", draft.DisplayName))
        {
            foreach (var group in saved)
                if (UiGui.Selectable(group.DisplayName + "##" + group.GroupId, group.GroupId == draft.GroupId))
                    draft = ResetTrustDraft(page, group, characters);
            UiGui.EndCombo();
        }
        DadUi.SameLineIfFits(DadUi.ButtonWidth("New"));
        if (DadUi.Button("New##trust-new")) draft = ResetTrustDraft(page, null, characters);
        DadUi.SameLineIfFits(DadUi.ButtonWidth("Duplicate"));
        if (DadUi.Button("Duplicate##trust-duplicate"))
        {
            draft = DadSchedulerGroupCloneRules.CloneWithSlots(draft, draft.Slots);
            draft.GroupId = Guid.NewGuid().ToString("N");
            draft.DisplayName = UiText.F("{0} Copy", draft.DisplayName);
            draft.ScheduleEnabled = false;
            draft.NextEligibleTimeUtc = null;
            foreach (var association in DadShoppingAssociationRules.PlanAssignments(draft))
            { association.AssociationId = Guid.NewGuid().ToString("N"); association.ResetCompletionState(); }
            trustDrafts[page] = draft;
            trustDraftVersions[page] = null;
            trustDraftChanged[page] = true;
        }
        var name = draft.DisplayName;
        if (UiGui.InputText("Name##trust-name", ref name, 160)) { draft.DisplayName = name; trustDraftChanged[page] = true; }
        var character = FindTrustCharacter(draft, characters);
        if (DrawTrustCharacterPicker(character, characters, out var selected))
        {
            character = selected;
            draft.Slots = [TrustSlot(selected)];
            if (draft.TrustParty != null) draft.TrustParty.PlayerJobId = selected.CurrentJobId ?? 0;
            trustDraftChanged[page] = true;
        }
        DadUi.Badge("Own account", DadUiTone.Neutral);
        if (character == null)
        {
            UiGui.TextDisabled("Choose an own-account character in Crew before creating a Trust preset.");
            ImGui.EndDisabled();
            return;
        }
        DrawTrustRefresh(character, runState);
        var selection = DrawTrustPanels(leveling, draft, character, jobs, duties, out var changed);
        trustDraftChanged[page] |= changed;
        var canSave = !string.IsNullOrWhiteSpace(draft.DisplayName) && (leveling || selection.Status == DadLevelingCompilationStatus.Ready);
        ImGui.BeginDisabled(!canSave);
        if (DadUi.Button(leveling ? "Save setup##trust-save" : "Save preset##trust-save", DadUiTone.Accent)) SaveTrustDraft(page);
        ImGui.EndDisabled();
        var isSaved = !trustDraftChanged[page] && plugin.Configuration.PlannerGroups.Any(group => group.GroupId == draft.GroupId);
        ImGui.BeginDisabled(!isSaved || selection.Status != DadLevelingCompilationStatus.Ready || !plugin.SchedulerService.CanRunPresetHere(draft));
        DadUi.SameLineIfFits(DadUi.ButtonWidth(leveling ? "Start leveling" : "Run preset"));
        if (DadUi.Button(leveling ? "Start leveling##trust-run" : "Run preset##trust-run", DadUiTone.Accent))
        {
            var result = plugin.StartSchedulerPresetFromJson(DadIpcJson.Serialize(new DadSchedulerStartRequest { GroupId = draft.GroupId }));
            trustEditorStatus = DadIpcJson.Deserialize<DadSchedulerPresetState>(result)?.Summary ?? string.Empty;
        }
        DadUi.SameLineIfFits(DadUi.ButtonWidth("Schedule"));
        if (DadUi.Button("Schedule##trust-schedule"))
        {
            plugin.SelectPlannerGroup(draft.GroupId);
            pendingMainTab = DadMainWindowTab.Presets;
            pendingPresetsTab = DadPresetsWindowTab.Scheduler;
        }
        ImGui.EndDisabled();
        UiGui.TextDisabled(trustDraftChanged[page] ? "Save your changes before starting or scheduling." : "Saving does not start a run.");
        ImGui.EndDisabled();
    }

    private DadPlannerGroup ResetTrustDraft(int page, DadPlannerGroup? saved, IReadOnlyList<DadAcquiredCharacter> characters)
    {
        var character = characters.FirstOrDefault(static row => row.Source == DadCharacterSource.LocalRuntime) ?? characters[0];
        var draft = saved == null ? new DadPlannerGroup
        {
            DisplayName = page == 0 ? UiText.T("NPC leveling") : UiText.T("Trust presets"),
            ActivityMode = DadPlannerActivityMode.Trust, DutyExpectedPartySize = 1,
            Slots = [TrustSlot(character)], StopPolicy = new() { Mode = DadPlannerStopMode.AfterRuns, AfterRuns = 1 },
        } : DadSchedulerGroupCloneRules.CloneWithSlots(saved, saved.Slots);
        if (page == 0)
        {
            draft.LevelingMode.Enabled = true;
            draft.LevelingMode.Trust ??= new() { AllowedJobs = character.CurrentJobId is > 0 ? [character.CurrentJobId.Value] : [] };
            draft.TrustParty = null;
        }
        else
        {
            draft.LevelingMode.Enabled = false;
            draft.TrustParty ??= new() { PlayerJobId = character.CurrentJobId ?? 0, Npcs = [DadTrustNpcId.Unknown, DadTrustNpcId.Unknown, DadTrustNpcId.Unknown] };
        }
        trustDrafts[page] = draft;
        trustDraftVersions[page] = saved?.UpdatedAtUtc;
        trustDraftChanged[page] = saved == null || page == 1 && saved.TrustParty == null;
        trustEditorStatus = string.Empty;
        return draft;
    }

    private void SaveTrustDraft(int page)
    {
        var draft = trustDrafts[page]!;
        var index = plugin.Configuration.PlannerGroups.FindIndex(group => group.GroupId == draft.GroupId);
        if (index >= 0 && plugin.Configuration.PlannerGroups[index].UpdatedAtUtc != trustDraftVersions[page])
        {
            trustEditorStatus = "This preset changed. Select it again before saving.";
            return;
        }
        var saved = DadSchedulerGroupCloneRules.CloneWithSlots(draft, draft.Slots);
        if (index < 0) plugin.Configuration.PlannerGroups.Add(saved);
        else plugin.Configuration.PlannerGroups[index] = saved;
        plugin.TouchPlannerGroup(saved);
        trustDraftVersions[page] = saved.UpdatedAtUtc;
        trustDraftChanged[page] = false;
        trustEditorStatus = "Saved";
    }

    private static DadPlannerGroupSlot TrustSlot(DadAcquiredCharacter character) => new()
    {
        SlotId = DadPlannerSlotRules.LeaderSlotId, RequiredAccountKey = new(character.AccountId),
        RequiredCharacterKey = new(character.CharacterKey), RequiredRole = DadPartyRole.Any, AllowSubstitution = false,
    };

    private static DadAcquiredCharacter? FindTrustCharacter(DadPlannerGroup draft, IReadOnlyList<DadAcquiredCharacter> characters)
    {
        var slots = draft.Slots.Where(static row => !row.IsSubstitute && DadPlannerSlotRules.IsLeaderSlot(row.SlotId)).ToArray();
        var slot = slots.Length == 1 ? slots[0] : null;
        var matches = slot == null ? [] : characters.Where(character =>
            DadRosterIdentity.SameAccount(slot.RequiredAccountKey, new(character.AccountId)) &&
            string.Equals(slot.RequiredCharacterKey.Value, character.CharacterKey, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool DrawTrustCharacterPicker(DadAcquiredCharacter? character, IReadOnlyList<DadAcquiredCharacter> characters, out DadAcquiredCharacter selected)
    {
        selected = character ?? characters[0];
        var changed = false;
        if (UiGui.BeginCombo("Character##trust-character", character?.CharacterKey ?? UiText.T("Choose")))
        {
            foreach (var row in characters)
                if (UiGui.Selectable(row.CharacterKey + "##" + row.AccountId, ReferenceEquals(row, character))) { selected = row; changed = true; }
            UiGui.EndCombo();
        }
        return changed;
    }

    private void DrawTrustRefresh(DadAcquiredCharacter character, DadVisibleRunState runState)
    {
        var local = plugin.CharacterIntelligenceService.CurrentPool.Characters.FirstOrDefault(static row => row.Source == DadCharacterSource.LocalRuntime);
        var canRefresh = local != null && character.ContentId != 0 && local.ContentId == character.ContentId &&
            DadRosterIdentity.SameAccount(new(local.AccountId), new(character.AccountId)) && !IsPlannerLocked(runState);
        ImGui.BeginDisabled(!canRefresh || plugin.NpcDutyQueueService.IsTrustRosterRefreshActive);
        if (DadUi.Button("Refresh Trust levels##trust-refresh"))
            plugin.NpcDutyQueueService.RequestTrustRosterRefresh(local!);
        ImGui.EndDisabled();
        if (plugin.NpcDutyQueueService.IsTrustRosterRefreshActive) UiGui.TextDisabled("Refreshing Trust levels...");
        if (!canRefresh) UiGui.TextDisabled("Refresh levels on the client running this character.");
    }

    internal static DadTrustPartySelection DrawTrustPanels(bool leveling, DadPlannerGroup draft, DadAcquiredCharacter character,
        IReadOnlyList<DadLevelingJobDescriptor> jobs, IReadOnlyList<DadPlannerDutyOption> duties, out bool changed)
    {
        changed = false;
        var wide = ImGui.GetContentRegionAvail().X >= 760 * MaterialTheme.Metrics.Scale;
        var table = wide && ImGui.BeginTable("dad-trust-setup-preview", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings);
        if (table)
        {
            ImGui.TableSetupColumn("Setup", ImGuiTableColumnFlags.WidthStretch, 1.65f);
            ImGui.TableSetupColumn("Preview", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableNextRow(); ImGui.TableNextColumn();
        }
        if (DadUi.BeginCard("dad-trust-setup"))
        {
            changed = leveling ? DrawTrustLevelingSetup(draft.LevelingMode.Trust!, character, jobs) : DrawTrustFixedSetup(draft, character, jobs, duties);
            DadUi.EndCard();
        }
        var selection = leveling
            ? DadTrustPartySelector.SelectLeveling(draft.LevelingMode.Trust!, character, jobs, duties)
            : DadTrustPartySelector.SelectFixed(draft.TrustParty!, draft.DutyContentFinderConditionId, character, jobs, duties);
        if (table) ImGui.TableNextColumn();
        if (DadUi.BeginCard("dad-trust-preview"))
        {
            DrawTrustPreview(selection, character, jobs, leveling);
            DadUi.EndCard();
        }
        if (table) ImGui.EndTable();
        return selection;
    }

    internal static bool DrawTrustLevelingSetup(DadTrustLevelingOptions options, DadAcquiredCharacter character, IReadOnlyList<DadLevelingJobDescriptor> jobs)
    {
        DadUi.Section("NPC leveling");
        var npcTarget = options.NpcTargetLevel;
        var changed = UiGui.InputInt("NPC target level", ref npcTarget, 1);
        if (changed) options.NpcTargetLevel = Math.Clamp(npcTarget, 1, 999);
        UiGui.TextDisabled("Companions stop at their own level cap.");
        var all = options.AllAvailableNpcs;
        if (UiGui.Checkbox("All available companions", ref all)) { options.AllAvailableNpcs = all; changed = true; }
        if (!all)
        {
            var candidates = character.TrustProgress?.Npcs ?? [];
            if (UiGui.BeginCombo("Companions", UiText.F("{0} selected", options.SelectedNpcs.Count)))
            {
                foreach (var npc in candidates.Where(static npc => npc.Unlocked == true))
                {
                    var selected = options.SelectedNpcs.Contains(npc.NpcId);
                    if (UiGui.Selectable(TrustNpcName(npc) + "##" + npc.NpcId, selected, ImGuiSelectableFlags.DontClosePopups))
                    { if (selected) options.SelectedNpcs.Remove(npc.NpcId); else options.SelectedNpcs.Add(npc.NpcId); changed = true; }
                }
                UiGui.EndCombo();
            }
        }
        var levelJobs = options.LevelPlayerJobs;
        if (UiGui.Checkbox("Level my jobs too", ref levelJobs)) { options.LevelPlayerJobs = levelJobs; changed = true; }
        if (levelJobs)
        {
            var target = options.PlayerTargetLevel;
            if (UiGui.InputInt("Player-job target level", ref target, 1)) { options.PlayerTargetLevel = Math.Clamp(target, 1, 999); changed = true; }
        }
        DadUi.Section("Jobs DAD may use");
        var roles = new[] { DadPartyRole.Tank, DadPartyRole.Healer, DadPartyRole.Melee, DadPartyRole.PhysicalRanged, DadPartyRole.Caster };
        using var jobRows = MaterialTable.PushTightRows();
        if (ImGui.BeginTable("dad-trust-job-roles", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings))
        {
            var labelWidth = roles.Max(role => MaterialText.Measure(UiText.T(TrustRoleLabel(role))).X) + ImGui.GetStyle().ItemSpacing.X;
            ImGui.TableSetupColumn("Role", ImGuiTableColumnFlags.WidthFixed, labelWidth);
            ImGui.TableSetupColumn("Jobs", ImGuiTableColumnFlags.WidthStretch);
            foreach (var role in roles)
            {
                ImGui.TableNextRow(); ImGui.TableNextColumn(); ImGui.AlignTextToFramePadding(); UiGui.TextUnformatted(TrustRoleLabel(role));
                ImGui.TableNextColumn();
                var available = jobs.Where(job => job.Role == role && character.JobLevels.GetValueOrDefault(job.JobId) > 0).ToArray();
                var selected = available.Where(job => options.AllowedJobs.Contains(job.JobId)).Select(job => $"{job.Abbreviation} {character.JobLevels[job.JobId]}").ToArray();
                using var cells = UiGui.PushCellFields();
                ImGui.SetNextItemWidth(-1);
                if (UiGui.BeginCombo(TrustRoleLabel(role) + "##trust-jobs", selected.Length == 0 ? UiText.T("Choose") : string.Join(", ", selected)))
                {
                    foreach (var job in available)
                    {
                        var enabled = options.AllowedJobs.Contains(job.JobId);
                        if (UiGui.Selectable($"{job.Abbreviation} {character.JobLevels[job.JobId]}##{job.JobId}", enabled, ImGuiSelectableFlags.DontClosePopups))
                        { if (enabled) options.AllowedJobs.Remove(job.JobId); else options.AllowedJobs.Add(job.JobId); changed = true; }
                    }
                    UiGui.EndCombo();
                }
            }
            ImGui.EndTable();
        }
        UiGui.TextDisabled("Jobs already at target can still help level companions.");
        return changed;
    }

    internal static bool DrawTrustFixedSetup(DadPlannerGroup draft, DadAcquiredCharacter character,
        IReadOnlyList<DadLevelingJobDescriptor> jobs, IReadOnlyList<DadPlannerDutyOption> duties)
    {
        DadUi.Section("Your Trust party");
        var party = draft.TrustParty!;
        var changed = false;
        var job = jobs.FirstOrDefault(job => job.JobId == party.PlayerJobId);
        if (UiGui.BeginCombo("Your job", job?.Abbreviation ?? UiText.T("Choose")))
        {
            foreach (var option in jobs.Where(job => character.JobLevels.GetValueOrDefault(job.JobId) > 0))
                if (UiGui.Selectable($"{option.Abbreviation} {character.JobLevels[option.JobId]}##{option.JobId}", option.JobId == party.PlayerJobId))
                { party.PlayerJobId = option.JobId; changed = true; }
            UiGui.EndCombo();
        }
        while (party.Npcs.Count < 3) party.Npcs.Add(DadTrustNpcId.Unknown);
        for (var index = 0; index < 3; index++)
        {
            ImGui.PushID(index);
            var selected = character.TrustProgress?.Npcs.FirstOrDefault(npc => npc.NpcId == party.Npcs[index]);
            if (UiGui.BeginCombo(UiText.F("Companion {0}", index + 1), selected == null ? UiText.T("Choose") : TrustNpcName(selected)))
            {
                foreach (var npc in (character.TrustProgress?.Npcs ?? []).Where(static npc => npc.Unlocked == true))
                {
                    ImGui.BeginDisabled(party.Npcs.Where((_, slot) => slot != index).Contains(npc.NpcId));
                    if (UiGui.Selectable(TrustNpcName(npc) + "##" + npc.NpcId, party.Npcs[index] == npc.NpcId))
                    { party.Npcs[index] = npc.NpcId; changed = true; }
                    ImGui.EndDisabled();
                }
                UiGui.EndCombo();
            }
            ImGui.PopID();
        }
        DadUi.Section("Dungeon", "Only dungeons available to this whole party are listed.");
        var compatible = duties.Where(duty => DadTrustPartySelector.SelectFixed(party, duty.ContentFinderConditionId, character, jobs, duties).Status == DadLevelingCompilationStatus.Ready).ToArray();
        var current = compatible.FirstOrDefault(duty => duty.ContentFinderConditionId == draft.DutyContentFinderConditionId);
        if (UiGui.BeginCombo("Dungeon##trust-duty", current?.DutyDisplayName ?? UiText.T("Choose")))
        {
            foreach (var duty in compatible)
                if (UiGui.Selectable(duty.DutyDisplayName + "##" + duty.ContentFinderConditionId, duty.ContentFinderConditionId == draft.DutyContentFinderConditionId))
                { draft.DutyContentFinderConditionId = duty.ContentFinderConditionId; draft.DutyDisplayName = duty.DutyDisplayName; changed = true; }
            UiGui.EndCombo();
        }
        var runs = draft.StopPolicy.AfterRuns;
        if (draft.StopPolicy.Mode != DadPlannerStopMode.AfterRuns) { draft.StopPolicy.Mode = DadPlannerStopMode.AfterRuns; changed = true; }
        if (UiGui.InputInt("Runs", ref runs, 1)) { draft.StopPolicy.Mode = DadPlannerStopMode.AfterRuns; draft.StopPolicy.AfterRuns = Math.Clamp(runs, 1, 9999); changed = true; }
        if (party.Npcs.Contains(DadTrustNpcId.Ryne)) UiGui.TextDisabled("Ryne joins Shadowbringers duties only.");
        if (draft.Slots.Count == 1 && (draft.Slots[0].RequiredJobId != party.PlayerJobId || draft.Slots[0].RequiredRole != DadPartyRole.Any))
        { draft.Slots[0].RequiredJobId = party.PlayerJobId; draft.Slots[0].RequiredRole = DadPartyRole.Any; changed = true; }
        return changed;
    }

    internal static void DrawTrustPreview(DadTrustPartySelection selection, DadAcquiredCharacter character,
        IReadOnlyList<DadLevelingJobDescriptor> jobs, bool leveling)
    {
        DadUi.Section(leveling ? "Next run" : "Preset preview");
        DadUi.Badge(selection.Status == DadLevelingCompilationStatus.Ready ? "Ready" : selection.Status == DadLevelingCompilationStatus.Complete ? "Complete" : "Needs attention",
            selection.Status == DadLevelingCompilationStatus.Ready ? DadUiTone.Success : DadUiTone.Neutral);
        if (selection.Status == DadLevelingCompilationStatus.Complete)
        { UiGui.TextDisabled("All selected Trust leveling goals are reached."); return; }
        if (selection.Status != DadLevelingCompilationStatus.Ready || selection.Duty == null)
        { UiGui.TextDisabled("No compatible dungeon. Check party roles, levels and unlocks."); return; }
        UiGui.TextWrapped(selection.Duty.DutyDisplayName);
        UiGui.TextUnformatted(UiText.F("Level {0}", selection.Duty.JobLevelRequired));
        ImGui.Separator();
        var job = jobs.FirstOrDefault(job => job.JobId == selection.PlayerJobId);
        UiGui.TextUnformatted(UiText.F("You: {0} {1}", job?.Abbreviation ?? string.Empty, selection.PlayerLevel));
        DadUi.Badge(job == null ? "Unknown" : TrustRoleLabel(job.Role));
        foreach (var npc in selection.Npcs)
        {
            var observed = character.TrustProgress?.Npcs.FirstOrDefault(row => row.NpcId == npc.NpcId);
            UiGui.TextUnformatted($"{(observed == null ? npc.NpcId.ToString() : TrustNpcName(observed))} {npc.Level}");
            DadUi.Badge(npc.Role == DadTrustRoles.Dps ? "DPS" : npc.Role.ToString());
        }
        UiGui.TextDisabled(leveling ? "Party, job and dungeon update after each run." : "Run this group exactly as saved.");
    }

    internal static void DrawTrustRoster(DadTrustProgress? progress, IReadOnlyList<DadPlannerDutyOption> duties, ref DadTrustNpcId selectedNpc)
    {
        DadUi.Section("NPC roster", "Levels belong to this character. Refresh them before planning a party.");
        if (progress == null) { UiGui.TextDisabled("Refresh Trust levels"); return; }
        if (!ImGui.BeginTable("dad-trust-roster", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings)) return;
        foreach (var label in new[] { "Companion", "Role", "Level", "Level cap", "Last updated" }) ImGui.TableSetupColumn(UiText.T(label));
        ImGui.TableHeadersRow();
        foreach (var npc in progress.Npcs)
        {
            ImGui.TableNextRow(); ImGui.TableNextColumn();
            if (UiGui.Selectable(TrustNpcName(npc) + "##" + npc.NpcId, selectedNpc == npc.NpcId, ImGuiSelectableFlags.SpanAllColumns)) selectedNpc = npc.NpcId;
            var roles = progress.Duties.SelectMany(static duty => duty.Members ?? []).Where(member => member.NpcId == npc.NpcId)
                .Aggregate(DadTrustRoles.None, static (roles, member) => roles | member.AvailableRoles);
            ImGui.TableNextColumn(); UiGui.TextUnformatted(roles == DadTrustRoles.All ? "All-rounder" : roles == DadTrustRoles.Dps ? "DPS" : roles.ToString());
            ImGui.TableNextColumn(); UiGui.TextUnformatted(npc.Unlocked == false ? "Locked" : npc.Level?.ToString(UiText.Current.Culture) ?? "?");
            ImGui.TableNextColumn(); UiGui.TextUnformatted(npc.LevelCap?.ToString(UiText.Current.Culture) ?? "?");
            ImGui.TableNextColumn(); UiGui.TextUnformatted(npc.ObservedAtUtc.ToLocalTime().ToString("g", UiText.Current.Culture));
        }
        ImGui.EndTable();
        var selectedId = selectedNpc;
        var companion = progress.Npcs.FirstOrDefault(npc => npc.NpcId == selectedId);
        if (companion == null) { UiGui.TextDisabled("Select a companion to see compatible dungeons."); return; }
        DadUi.Section("Compatible dungeons");
        UiGui.TextUnformatted(TrustNpcName(companion));
        foreach (var duty in duties.Where(duty => companion.Unlocked == true && companion.Level >= duty.JobLevelRequired &&
            progress.Duties.Any(reading => reading.ContentFinderConditionId == duty.ContentFinderConditionId && reading.Unlocked == true &&
                reading.Members?.Any(member => member.NpcId == companion.NpcId) == true)))
            UiGui.BulletText(duty.DutyDisplayName);
    }

    private static string TrustNpcName(DadTrustNpcObservation npc)
        => string.IsNullOrWhiteSpace(npc.DisplayName) ? npc.NpcId switch
        { DadTrustNpcId.Yshtola => "Y'shtola", DadTrustNpcId.GrahaTia => "G'raha Tia", _ => npc.NpcId.ToString() } : npc.DisplayName;

    private static string TrustRoleLabel(DadPartyRole role) => role switch
    { DadPartyRole.PhysicalRanged => "Ranged", DadPartyRole.Caster => "Caster", DadPartyRole.Melee => "Melee", _ => role.ToString() };
}
