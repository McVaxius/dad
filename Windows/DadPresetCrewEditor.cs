using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using dad.Models;
using dad.Services;
using Lumina.Excel.Sheets;

namespace dad.Windows;

/// <summary>
/// Shared UI-only editor for saved preset character rows. The expert Plan page
/// and guided first-preset flow supply their own persistence callback while
/// using exactly the same controls and field semantics.
/// </summary>
internal sealed class DadPresetCrewEditor
{
    private readonly Plugin plugin;
    private readonly Dictionary<uint, string> classJobAbbrevCache = new();

    private sealed record JobOption(uint JobId, string Abbreviation, int Level);

    public DadPresetCrewEditor(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Draw(
        DadPlannerUiSnapshot plannerSnapshot,
        DadPlannerGroup group,
        Action<DadPlannerGroup> changed,
        string idPrefix)
    {
        group.Slots = DadPlannerSlotRules.NormalizeGroupSlots(group.Slots);

        var style = ImGui.GetStyle();
        var slotWidth = FixedTextWidth("Slot56");
        var typeWidth = FixedTextWidth("Substitute");
        var jobWidth = FixedFrameWidth("WHM 100");
        var roleWidth = FixedFrameWidth("PhysicalRanged");
        var lootWidth = FixedFrameWidth("NoChange");
        var levelWidth = FixedFrameWidth("999");
        var wakeWidth = MathF.Max(FixedFrameWidth("Online"), MathF.Max(FixedFrameWidth("Launch"), FixedFrameWidth("Load*")));
        var actionWidth = ButtonWidth("+ Sub") + style.ItemSpacing.X + ButtonWidth("Remove");

        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(2f, 2f));
        var tableOpen = ImGui.BeginTable(
            $"{idPrefix}-crew-rows",
            11,
            ImGuiTableFlags.Borders |
            ImGuiTableFlags.RowBg |
            ImGuiTableFlags.Resizable |
            ImGuiTableFlags.SizingFixedFit |
            ImGuiTableFlags.NoSavedSettings);
        if (!tableOpen)
        {
            ImGui.PopStyleVar();
            return;
        }

        ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthFixed, slotWidth);
        ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, typeWidth);
        ImGui.TableSetupColumn("Account", ImGuiTableColumnFlags.WidthStretch, 1.05f);
        ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch, 1.25f);
        ImGui.TableSetupColumn("Role", ImGuiTableColumnFlags.WidthFixed, roleWidth);
        ImGui.TableSetupColumn("Job", ImGuiTableColumnFlags.WidthFixed, jobWidth);
        ImGui.TableSetupColumn("Loot", ImGuiTableColumnFlags.WidthFixed, lootWidth);
        ImGui.TableSetupColumn("Lv.", ImGuiTableColumnFlags.WidthFixed, levelWidth);
        ImGui.TableSetupColumn("Wake", ImGuiTableColumnFlags.WidthFixed, wakeWidth);
        ImGui.TableSetupColumn("Profile", ImGuiTableColumnFlags.WidthStretch, 0.9f);
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, actionWidth);
        DrawHeaders();

        for (var index = 0; index < group.Slots.Count; index++)
        {
            var slot = group.Slots[index];
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(slot.SlotId);
            if (DadPlannerSlotRules.IsLeaderSlot(slot.SlotId) && !slot.IsSubstitute && ImGui.IsItemHovered())
                ImGui.SetTooltip("Slot1 is the party leader and inviter for this preset.");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(slot.IsSubstitute ? "Substitute" : "Primary");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(slot.IsSubstitute
                    ? "This fallback is tried only when the primary row for the same slot cannot resolve."
                    : "Primary rows are resolved before substitutes for the same slot.");
            }

            ImGui.TableNextColumn();
            DrawAccount(plannerSnapshot.CuratedPool, plannerSnapshot.AccountOptions, group, slot, index, idPrefix, changed);

            ImGui.TableNextColumn();
            DrawCharacter(plannerSnapshot, group, slot, index, idPrefix, changed);

            ImGui.TableNextColumn();
            DrawRole(plannerSnapshot, group, slot, index, idPrefix, changed);

            ImGui.TableNextColumn();
            DrawJob(plannerSnapshot, group, slot, index, idPrefix, changed);

            ImGui.TableNextColumn();
            DrawLoot(group, slot, index, idPrefix, changed);

            ImGui.TableNextColumn();
            DrawLevelSeek(group, slot, index, idPrefix, changed);

            ImGui.TableNextColumn();
            DrawWake(group, slot, index, idPrefix, changed);

            ImGui.TableNextColumn();
            DrawLaunchProfile(plannerSnapshot.LaunchProfiles, group, slot, index, idPrefix, changed);

            ImGui.TableNextColumn();
            if (!slot.IsSubstitute)
            {
                if (ImGui.SmallButton($"+ Sub##{idPrefix}-sub-{index}"))
                {
                    group.Slots.Insert(FindSubstituteInsertIndex(group.Slots, index), new DadPlannerGroupSlot
                    {
                        SlotId = slot.SlotId,
                        IsSubstitute = true,
                        RequiredRole = slot.RequiredRole,
                        AdsLootMode = slot.AdsLootMode,
                        LevelSeekTarget = slot.LevelSeekTarget,
                        WakePolicy = slot.WakePolicy,
                        AllowSubstitution = false,
                    });
                    changed(group);
                    break;
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Add an explicit fallback on this same physical row group. Primary is tried first, then substitutes in order.");
                ImGui.SameLine();
            }

            if (ImGui.SmallButton($"Remove##{idPrefix}-remove-{index}"))
            {
                group.Slots.RemoveAt(index);
                changed(group);
                break;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(slot.IsSubstitute ? "Remove this substitute row." : "Remove this primary row and leave its existing substitutes as saved fallback rows.");
        }

        ImGui.EndTable();
        ImGui.PopStyleVar();
    }

    private static void DrawHeaders()
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        foreach (var header in new[] { "Slot", "Type", "Account", "Character", "Role", "Job", "Loot", "Lv.", "Wake", "Profile", "Actions" })
        {
            ImGui.TableNextColumn();
            ImGui.TableHeader(header);
            if (header == "Lv." && ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Level seek target. Leave blank to disable it. The scheduler skips a preset only when every targeted exact row has a known level at or above its target.");
            }
        }
    }

    private void DrawAccount(
        DadCharacterPool characterPool,
        IReadOnlyList<DadRosterAccountOption> accountOptions,
        DadPlannerGroup group,
        DadPlannerGroupSlot slot,
        int index,
        string idPrefix,
        Action<DadPlannerGroup> changed)
    {
        var selectedAccount = accountOptions.FirstOrDefault(option =>
            string.Equals(option.AccountKey.Value, slot.RequiredAccountKey.Value, StringComparison.OrdinalIgnoreCase));
        var preview = slot.RequiredAccountKey.IsEmpty
            ? slot.SharedIdentity == null
                ? "(missing)"
                : $"Shared {ShortSharedToken(slot.SharedIdentity.AccountToken)} - remap"
            : selectedAccount == null ? slot.RequiredAccountKey.Value : FormatAccountOption(selectedAccount);
        ImGui.SetNextItemWidth(-1f);
        var open = ImGui.BeginCombo($"##{idPrefix}-account-{index}", preview);
        var hovered = ImGui.IsItemHovered();
        if (open)
        {
            foreach (var option in accountOptions)
            {
                var selected = string.Equals(slot.RequiredAccountKey.Value, option.AccountKey.Value, StringComparison.OrdinalIgnoreCase);
                var label = $"{FormatAccountOption(option)} ({option.AssignedCharacterCount})";
                if (ImGui.Selectable(label, selected))
                {
                    var accountChanged = !string.Equals(
                        slot.RequiredAccountKey.Value,
                        option.AccountKey.Value,
                        StringComparison.OrdinalIgnoreCase);
                    var characterCleared = false;
                    slot.RequiredAccountKey = option.AccountKey;
                    if (!slot.RequiredCharacterKey.IsEmpty &&
                        !characterPool.Characters.Any(character =>
                            string.Equals(character.CharacterKey, slot.RequiredCharacterKey.Value, StringComparison.OrdinalIgnoreCase) &&
                            MatchesAccount(character, option.AccountKey)))
                    {
                        slot.RequiredCharacterKey = new DadCharacterKey(string.Empty);
                        characterCleared = true;
                    }

                    if (accountChanged || characterCleared)
                        slot.RequiredJobId = null;
                    DadSharedPlanRules.CompleteAccountOnlyRemap(group, slot);
                    changed(group);
                }
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        if (hovered)
            ImGui.SetTooltip(preview);
    }

    private void DrawCharacter(
        DadPlannerUiSnapshot plannerSnapshot,
        DadPlannerGroup group,
        DadPlannerGroupSlot slot,
        int index,
        string idPrefix,
        Action<DadPlannerGroup> changed)
    {
        var needsAccount = slot.RequiredAccountKey.IsEmpty;
        var preview = needsAccount
            ? slot.SharedIdentity == null
                ? "Select account first"
                : $"{FormatSharedCharacter(slot.SharedIdentity)} - map account first"
            : slot.RequiredCharacterKey.IsEmpty
                ? slot.SharedIdentity is { RequiresCharacter: true } placeholder
                    ? $"{FormatSharedCharacter(placeholder)} - remap"
                    : "Any character"
                : plugin.KrangleService.FormatCharacterKey(slot.RequiredCharacterKey.Value);
        ImGui.SetNextItemWidth(-1f);
        ImGui.BeginDisabled(needsAccount);
        var open = ImGui.BeginCombo($"##{idPrefix}-character-{index}", preview);
        var hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
        if (open)
        {
            if (ImGui.Selectable("Any character on account", slot.RequiredCharacterKey.IsEmpty))
            {
                var characterChanged = !slot.RequiredCharacterKey.IsEmpty;
                slot.RequiredCharacterKey = new DadCharacterKey(string.Empty);
                if (characterChanged)
                    slot.RequiredJobId = null;
                changed(group);
            }

            foreach (var character in plannerSnapshot.GetCharactersForAccount(slot.RequiredAccountKey))
            {
                var selected = string.Equals(slot.RequiredCharacterKey.Value, character.CharacterKey, StringComparison.OrdinalIgnoreCase) &&
                               MatchesAccount(character, slot.RequiredAccountKey);
                var source = plugin.PresetProviderService.GetCharacterSourceLabel(character.Source);
                var world = string.IsNullOrWhiteSpace(character.WorldName) ? string.Empty : $" | {character.WorldName}";
                var name = plugin.KrangleService.FormatCharacterKey(character.CharacterKey);
                if (ImGui.Selectable($"{name}{world} | {source}", selected))
                {
                    var characterChanged = !string.Equals(
                        slot.RequiredCharacterKey.Value,
                        character.CharacterKey,
                        StringComparison.OrdinalIgnoreCase);
                    slot.RequiredCharacterKey = new DadCharacterKey(character.CharacterKey);
                    if (characterChanged)
                        slot.RequiredJobId = null;
                    DadSharedPlanRules.CompleteCharacterRemap(group, slot);
                    changed(group);
                }
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        ImGui.EndDisabled();
        if (hovered)
            ImGui.SetTooltip(preview);
    }

    private void DrawJob(
        DadPlannerUiSnapshot plannerSnapshot,
        DadPlannerGroup group,
        DadPlannerGroupSlot slot,
        int index,
        string idPrefix,
        Action<DadPlannerGroup> changed)
    {
        var selectedCharacter = ResolveCharacter(plannerSnapshot, slot);
        var allOptions = BuildJobOptions(selectedCharacter);
        var options = FilterJobOptions(allOptions, slot.RequiredRole);
        var selectedJob = slot.RequiredJobId is > 0
            ? options.FirstOrDefault(option => option.JobId == slot.RequiredJobId)
            : null;
        var learnedSavedJob = slot.RequiredJobId is > 0
            ? allOptions.FirstOrDefault(option => option.JobId == slot.RequiredJobId)
            : null;
        var hasRequestedJob = slot.RequiredJobId is > 0;
        var invalidSavedJob = hasRequestedJob && selectedJob == null;
        var preview = !hasRequestedJob
            ? "Any"
            : selectedJob != null
                ? $"{selectedJob.Abbreviation} {selectedJob.Level.ToString(CultureInfo.InvariantCulture)}"
                : $"! {ResolveClassJobAbbrev(slot.RequiredJobId!.Value)}";
        var disabled = selectedCharacter == null && !hasRequestedJob;

        ImGui.SetNextItemWidth(-1f);
        ImGui.BeginDisabled(disabled);
        if (invalidSavedJob)
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.4f, 0.35f, 1f));
        var open = ImGui.BeginCombo($"##{idPrefix}-job-{index}", preview);
        var hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
        if (invalidSavedJob)
            ImGui.PopStyleColor();

        if (open)
        {
            var anySelected = !hasRequestedJob;
            if (ImGui.Selectable("Any (use current job)", anySelected))
            {
                slot.RequiredJobId = null;
                changed(group);
            }
            if (anySelected)
                ImGui.SetItemDefaultFocus();

            foreach (var option in options)
            {
                var selected = option.JobId == slot.RequiredJobId;
                if (ImGui.Selectable($"{option.Abbreviation} Lv {option.Level.ToString(CultureInfo.InvariantCulture)}", selected))
                {
                    slot.RequiredJobId = option.JobId;
                    changed(group);
                }
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        ImGui.EndDisabled();

        if (!hovered)
            return;
        if (invalidSavedJob)
        {
            var reason = selectedCharacter == null
                ? "Select an exact character before validating this saved job."
                : learnedSavedJob == null
                    ? "This job is not present in the exact character's learned-job ledger."
                    : $"{learnedSavedJob.Abbreviation} does not match the selected {FormatRole(slot.RequiredRole)} role.";
            ImGui.SetTooltip($"Invalid saved job #{slot.RequiredJobId!.Value.ToString(CultureInfo.InvariantCulture)}. {reason} Choose Any or a compatible learned job; DAD will not rewrite the saved value until you explicitly change Role or Job.");
        }
        else if (selectedCharacter == null)
        {
            ImGui.SetTooltip("Select an exact character before choosing a job. Any uses the character's current job.");
        }
        else if (selectedJob != null)
        {
            ImGui.SetTooltip($"{selectedJob.Abbreviation} at learned level {selectedJob.Level.ToString(CultureInfo.InvariantCulture)}. Choices come from the exact character's durable learned-job ledger.");
        }
        else
        {
            ImGui.SetTooltip("Any uses the selected character's current job. The dropdown retains full learned-job details.");
        }
    }

    private void DrawRole(
        DadPlannerUiSnapshot plannerSnapshot,
        DadPlannerGroup group,
        DadPlannerGroupSlot slot,
        int index,
        string idPrefix,
        Action<DadPlannerGroup> changed)
    {
        ImGui.SetNextItemWidth(-1f);
        if (!ImGui.BeginCombo($"##{idPrefix}-role-{index}", FormatRole(slot.RequiredRole)))
            return;

        foreach (var role in Enum.GetValues<DadPartyRole>())
        {
            var selected = role == slot.RequiredRole;
            if (ImGui.Selectable(FormatRole(role), selected) && !selected)
            {
                var selectedCharacter = ResolveCharacter(plannerSnapshot, slot);
                var compatibleJobs = FilterJobOptions(BuildJobOptions(selectedCharacter), role);
                var selectedJobStillMatches = slot.RequiredJobId is > 0 &&
                                              compatibleJobs.Any(option => option.JobId == slot.RequiredJobId.Value);
                slot.RequiredRole = role;
                if (!selectedJobStillMatches)
                {
                    slot.RequiredJobId = compatibleJobs
                        .OrderBy(static option => option.Level)
                        .ThenBy(static option => option.Abbreviation, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(static option => option.JobId)
                        .Select(static option => (uint?)option.JobId)
                        .FirstOrDefault();
                }
                changed(group);
            }
            if (selected)
                ImGui.SetItemDefaultFocus();
        }
        ImGui.EndCombo();
    }

    private static void DrawLoot(
        DadPlannerGroup group,
        DadPlannerGroupSlot slot,
        int index,
        string idPrefix,
        Action<DadPlannerGroup> changed)
    {
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo($"##{idPrefix}-loot-{index}", slot.AdsLootMode.ToString()))
        {
            foreach (var mode in Enum.GetValues<DadAdsLootMode>())
            {
                var selected = mode == slot.AdsLootMode;
                if (ImGui.Selectable(mode.ToString(), selected))
                {
                    slot.AdsLootMode = mode;
                    changed(group);
                }
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("NoChange preserves ADS lootMode. Need, Greed, and Pass are patched on the exact selected worker before queueing.");
    }

    private static void DrawLevelSeek(
        DadPlannerGroup group,
        DadPlannerGroupSlot slot,
        int index,
        string idPrefix,
        Action<DadPlannerGroup> changed)
    {
        var text = slot.LevelSeekTarget?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        var width = ImGui.CalcTextSize("999").X + (ImGui.GetStyle().FramePadding.X * 2f);
        ImGui.SetNextItemWidth(width);
        if (ImGui.InputText($"##{idPrefix}-level-{index}", ref text, 4))
        {
            var trimmed = text.Trim();
            if (trimmed.Length == 0)
            {
                slot.LevelSeekTarget = null;
                changed(group);
            }
            else if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var level) && level is >= 1 and <= 999)
            {
                slot.LevelSeekTarget = level;
                changed(group);
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Blank disables Level seek. A preset is skipped only when every targeted exact row has a known level at or above its target.");
    }

    private static void DrawWake(
        DadPlannerGroup group,
        DadPlannerGroupSlot slot,
        int index,
        string idPrefix,
        Action<DadPlannerGroup> changed)
    {
        ImGui.SetNextItemWidth(-1f);
        var open = ImGui.BeginCombo($"##{idPrefix}-wake-{index}", CompactWakeLabel(slot.WakePolicy));
        var hovered = ImGui.IsItemHovered();
        if (open)
        {
            foreach (var policy in Enum.GetValues<DadSchedulerWakePolicy>())
            {
                var selected = policy == slot.WakePolicy;
                var disabledStub = policy == DadSchedulerWakePolicy.LoadCharacterIfOnline && !selected;
                ImGui.BeginDisabled(disabledStub);
                if (ImGui.Selectable(FullWakeLabel(policy), selected))
                {
                    slot.WakePolicy = policy;
                    changed(group);
                }
                var optionHovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
                ImGui.EndDisabled();
                if (optionHovered)
                    ImGui.SetTooltip(WakeDescription(policy));
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        if (hovered)
            ImGui.SetTooltip(WakeDescription(slot.WakePolicy));
    }

    private static void DrawLaunchProfile(
        IReadOnlyList<DadLaunchProfile> profiles,
        DadPlannerGroup group,
        DadPlannerGroupSlot slot,
        int index,
        string idPrefix,
        Action<DadPlannerGroup> changed)
    {
        var selectedProfile = profiles.FirstOrDefault(profile =>
            string.Equals(profile.ProfileId, slot.LaunchProfileId, StringComparison.OrdinalIgnoreCase));
        var preview = selectedProfile?.DisplayName ?? "Auto";
        var fullPreview = selectedProfile == null || string.IsNullOrWhiteSpace(selectedProfile.AccountKey.Value)
            ? preview
            : $"{selectedProfile.DisplayName} | {selectedProfile.AccountKey}";
        ImGui.SetNextItemWidth(-1f);
        var open = ImGui.BeginCombo($"##{idPrefix}-profile-{index}", preview);
        var hovered = ImGui.IsItemHovered();
        if (open)
        {
            if (ImGui.Selectable("Auto", string.IsNullOrWhiteSpace(slot.LaunchProfileId)))
            {
                slot.LaunchProfileId = string.Empty;
                changed(group);
            }
            foreach (var profile in profiles)
            {
                var selected = string.Equals(profile.ProfileId, slot.LaunchProfileId, StringComparison.OrdinalIgnoreCase);
                var label = string.IsNullOrWhiteSpace(profile.AccountKey.Value)
                    ? profile.DisplayName
                    : $"{profile.DisplayName} | {profile.AccountKey}";
                if (ImGui.Selectable(label, selected))
                {
                    slot.LaunchProfileId = profile.ProfileId;
                    changed(group);
                }
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        if (hovered)
            ImGui.SetTooltip(fullPreview);
    }

    private DadAcquiredCharacter? ResolveCharacter(DadPlannerUiSnapshot plannerSnapshot, DadPlannerGroupSlot slot)
    {
        if (slot.RequiredAccountKey.IsEmpty || slot.RequiredCharacterKey.IsEmpty)
            return null;
        return plannerSnapshot.GetCharactersForAccount(slot.RequiredAccountKey)
            .FirstOrDefault(character =>
                string.Equals(character.CharacterKey, slot.RequiredCharacterKey.Value, StringComparison.OrdinalIgnoreCase) &&
                MatchesAccount(character, slot.RequiredAccountKey));
    }

    private List<JobOption> BuildJobOptions(DadAcquiredCharacter? character)
        => character == null
            ? []
            : character.JobLevels
                .Where(static pair => pair.Key != 0 && pair.Value > 0 && DadRosterCharacterMerge.IsCombatJob(pair.Key))
                .Select(pair => new JobOption(pair.Key, ResolveClassJobAbbrev(pair.Key), pair.Value))
                .OrderBy(static option => option.Abbreviation, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static option => option.JobId)
                .ToList();

    private static List<JobOption> FilterJobOptions(
        IEnumerable<JobOption> options,
        DadPartyRole requiredRole)
        => options
            .Where(option => JobMatchesRole(option.JobId, requiredRole))
            .ToList();

    private static bool JobMatchesRole(uint jobId, DadPartyRole requiredRole)
    {
        if (requiredRole == DadPartyRole.Any)
            return true;

        var family = jobId switch
        {
            1 or 3 or 19 or 21 or 32 or 37 => DadPartyRole.Tank,
            6 or 24 or 28 or 33 or 40 => DadPartyRole.Healer,
            2 or 4 or 20 or 22 or 29 or 30 or 34 or 39 or 41 => DadPartyRole.Melee,
            5 or 23 or 31 or 38 => DadPartyRole.PhysicalRanged,
            7 or 25 or 26 or 27 or 35 or 42 => DadPartyRole.Caster,
            36 => DadPartyRole.Limited,
            _ => DadPartyRole.Any,
        };

        return requiredRole == DadPartyRole.Dps
            ? family is DadPartyRole.Melee or DadPartyRole.PhysicalRanged or DadPartyRole.Caster
            : family == requiredRole;
    }

    private string ResolveClassJobAbbrev(uint jobId)
    {
        if (jobId == 0)
            return string.Empty;
        if (classJobAbbrevCache.TryGetValue(jobId, out var cached))
            return cached;

        var resolved = $"Job {jobId.ToString(CultureInfo.InvariantCulture)}";
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<ClassJob>();
            if (sheet != null && sheet.TryGetRow(jobId, out var classJob))
            {
                var abbreviation = classJob.Abbreviation.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(abbreviation))
                    resolved = abbreviation;
            }
        }
        catch
        {
            // A short numeric fallback keeps the editor usable while Lumina starts.
        }

        classJobAbbrevCache[jobId] = resolved;
        return resolved;
    }

    private static int FindSubstituteInsertIndex(IReadOnlyList<DadPlannerGroupSlot> slots, int primaryIndex)
    {
        var primarySlotId = slots[primaryIndex].SlotId;
        var insertIndex = primaryIndex + 1;
        while (insertIndex < slots.Count &&
               string.Equals(slots[insertIndex].SlotId, primarySlotId, StringComparison.OrdinalIgnoreCase) &&
               slots[insertIndex].IsSubstitute)
        {
            insertIndex++;
        }
        return insertIndex;
    }

    private static bool MatchesAccount(DadAcquiredCharacter character, DadAccountKey accountKey)
        => (!string.IsNullOrWhiteSpace(character.AccountId) &&
            string.Equals(character.AccountId, accountKey.Value, StringComparison.OrdinalIgnoreCase))
           || (!string.IsNullOrWhiteSpace(character.AccountAlias) &&
               string.Equals(character.AccountAlias, accountKey.Value, StringComparison.OrdinalIgnoreCase));

    private static string FormatAccountOption(DadRosterAccountOption option)
    {
        var accountKey = option.AccountKey.Value?.Trim() ?? string.Empty;
        var displayName = !string.IsNullOrWhiteSpace(option.AccountAlias)
            ? option.AccountAlias.Trim()
            : !string.IsNullOrWhiteSpace(option.DisplayName)
                ? option.DisplayName.Trim()
                : accountKey;
        var onlineSuffix = option.OwnerOnline ? string.Empty : " [offline]";
        if (string.IsNullOrWhiteSpace(displayName) || string.Equals(displayName, accountKey, StringComparison.OrdinalIgnoreCase))
            return (string.IsNullOrWhiteSpace(displayName) ? "(account)" : displayName) + onlineSuffix;
        return string.IsNullOrWhiteSpace(accountKey)
            ? displayName + onlineSuffix
            : $"{displayName} [{accountKey}]{onlineSuffix}";
    }

    private static string FormatRole(DadPartyRole role)
        => role == DadPartyRole.Dps ? "DPS" : role.ToString();

    private static string FormatSharedCharacter(DadSharedIdentityPlaceholder placeholder)
        => string.IsNullOrWhiteSpace(placeholder.CharacterLabel)
            ? "Shared character"
            : placeholder.CharacterLabel;

    private static string ShortSharedToken(string token)
    {
        var value = token?.Trim() ?? string.Empty;
        return value.Length <= 14 ? value : value[..14];
    }

    private static string CompactWakeLabel(DadSchedulerWakePolicy policy)
        => policy switch
        {
            DadSchedulerWakePolicy.LaunchIfOffline => "Launch",
            DadSchedulerWakePolicy.LoadCharacterIfOnline => "Load*",
            _ => "Online",
        };

    private static string FullWakeLabel(DadSchedulerWakePolicy policy)
        => policy switch
        {
            DadSchedulerWakePolicy.LaunchIfOffline => "Launch if offline",
            DadSchedulerWakePolicy.LoadCharacterIfOnline => "Load character if online (compatibility stub)",
            _ => "Already online",
        };

    private static string WakeDescription(DadSchedulerWakePolicy policy)
        => policy switch
        {
            DadSchedulerWakePolicy.LaunchIfOffline => "Launch: start the mapped client when it is offline, then wait for the exact character.",
            DadSchedulerWakePolicy.LoadCharacterIfOnline => "Load*: compatibility stub only. New selections are disabled and the scheduler sends no commands for this policy.",
            _ => "Online: require the participant to already be online; do not launch or relog it.",
        };

    private static float FixedTextWidth(string representative)
        => MathF.Ceiling(ImGui.CalcTextSize(representative).X + 4f);

    private static float FixedFrameWidth(string representative)
        => MathF.Ceiling(ImGui.CalcTextSize(representative).X + (ImGui.GetStyle().FramePadding.X * 2f) + 4f);

    private static float ButtonWidth(string text)
        => MathF.Ceiling(ImGui.CalcTextSize(text).X + (ImGui.GetStyle().FramePadding.X * 2f));
}
