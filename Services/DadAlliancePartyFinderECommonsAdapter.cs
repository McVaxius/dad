using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using ECommons.Automation.UIInput;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace dad.Services;

/// <summary>
/// Framework-thread UI adapter. It intentionally uses only ECommons stateless
/// addon wrappers and UI-input helpers; ECommons' global service layer is never
/// initialized.
/// </summary>
internal sealed unsafe class DadAlliancePartyFinderECommonsAdapter :
    IDadAlliancePartyFinderCreateUi,
    IDisposable
{
    private readonly ICommandManager commandManager;
    private readonly IDataManager dataManager;
    private readonly IToastGui toastGui;
    private int errorToastSequence;
    private string errorToast = string.Empty;
    private int targetDutyDropDownIndex = -1;
    private bool disposed;

    public DadAlliancePartyFinderECommonsAdapter(
        ICommandManager commandManager,
        IDataManager dataManager,
        IToastGui toastGui)
    {
        this.commandManager = commandManager;
        this.dataManager = dataManager;
        this.toastGui = toastGui;
        toastGui.ErrorToast += OnErrorToast;
    }

    public DadAlliancePfCreateSnapshot Read(int passcode)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        targetDutyDropDownIndex = -1;

        var agent = AgentLookingForGroup.Instance();
        var main = GetAddon<AddonLookingForGroup>("LookingForGroup");
        var condition = GetAddon<AddonLookingForGroupCondition>("LookingForGroupCondition");
        var mainVisible = main != null && main->AddonLookingForGroupBase.AtkUnitBase.IsVisible;
        var conditionVisible = condition != null && condition->AtkUnitBase.IsVisible;
        var mainMaster = mainVisible ? new AddonMaster.LookingForGroup(main) : null;
        var conditionMaster = conditionVisible ? new AddonMaster.LookingForGroupCondition(condition) : null;
        var mainReady = mainMaster?.IsAddonReady == true;
        var conditionReady = conditionMaster?.IsAddonReady == true;
        var hardBlocker = string.Empty;

        if (mainReady && mainMaster!.RecruitMembersButton == null)
            hardBlocker = "The fully loaded Party Finder window is missing Recruit Members.";
        if (conditionReady && !HasRequiredConditionControls(condition))
            hardBlocker = "The fully loaded Party Finder conditions window is missing required alliance controls.";

        var targetDutyIds = dataManager.GetExcelSheet<ContentFinderCondition>()
            .Where(static row => string.Equals(
                row.Name.ToString().Trim(),
                DadAlliancePartyFinderNativeGateway.FormationDutyName,
                StringComparison.OrdinalIgnoreCase))
            .Select(static row => checked((ushort)row.RowId))
            .ToArray();
        var targetDutyId = targetDutyIds.Length == 1 ? targetDutyIds[0] : (ushort)0;
        var dropDownMatches = 0;
        var dutyEntryEnabled = false;
        if (conditionReady &&
            condition->DutyDropDown != null &&
            condition->DutyDropDown->List != null)
        {
            var list = condition->DutyDropDown->List;
            var count = Math.Max(0, list->GetItemCount());
            for (var index = 0; index < count; index++)
            {
                var labelPointer = list->GetItemLabel(index);
                var label = labelPointer.Value == null
                    ? string.Empty
                    : MemoryHelper.ReadSeStringNullTerminated((nint)labelPointer.Value).TextValue.Trim();
                if (!string.Equals(
                        label,
                        DadAlliancePartyFinderNativeGateway.FormationDutyName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                dropDownMatches++;
                targetDutyDropDownIndex = index;
                dutyEntryEnabled = !list->GetItemDisabledState(index);
            }
        }

        var stored = agent == null ? null : &agent->StoredRecruitmentInfo;
        var selectedCategory = stored == null ? 0u : (uint)stored->SelectedCategory;
        var selectedDutyId = stored == null ? (ushort)0 : stored->SelectedDutyId;
        var allianceSelected = conditionReady &&
                               IsChecked(condition->RecruitmentType, 1);
        var storedExact = StoredSettingsExact(stored, passcode, targetDutyId);
        var ownListingId = agent == null ? 0u : agent->OwnListingId;
        var storedContradictory = ownListingId != 0 && !storedExact;
        var readiness = BuildReadiness(
            agent != null,
            mainVisible,
            mainReady,
            conditionVisible,
            conditionReady,
            mainMaster?.RecruitMembersButton != null && IsUsable(mainMaster.RecruitMembersButton),
            conditionReady &&
            condition->DutyDropDown != null &&
            condition->DutyDropDown->List != null);

        return new DadAlliancePfCreateSnapshot
        {
            AgentAvailable = agent != null,
            MainVisible = mainVisible,
            MainReady = mainReady,
            MainRecruitUsable = mainReady &&
                                mainMaster!.RecruitMembersButton != null &&
                                IsUsable(mainMaster.RecruitMembersButton),
            ConditionVisible = conditionVisible,
            ConditionReady = conditionReady,
            AllianceSelected = allianceSelected,
            SelectedCategory = selectedCategory,
            TargetDutyId = targetDutyId,
            TargetDutySheetMatches = targetDutyIds.Length,
            DutyListLoaded = conditionReady &&
                             condition->DutyDropDown != null &&
                             condition->DutyDropDown->List != null,
            TargetDutyDropDownMatches = dropDownMatches,
            TargetDutyEntryEnabled = dutyEntryEnabled,
            SelectedDutyId = selectedDutyId,
            AllianceASelected = conditionReady && IsChecked(condition->AllianceSelection, 0),
            PrivateRecruitment = conditionReady && condition->FormPrivatePartyCheckbox->IsChecked,
            Passcode = conditionReady ? condition->PasswordNumericInput->Value : 0,
            CrossWorldRecruitment = conditionReady && !condition->LimitToWorldServerCheckbox->IsChecked,
            OnePlayerPerJob = conditionReady && condition->OnePlayerPerJobCheckbox->IsChecked,
            EmptyComment = conditionReady &&
                           string.IsNullOrEmpty(condition->CommentTextInput->AtkComponentInputBase.RawString.ToString()),
            UnrestrictedJobs = conditionReady && condition->RemoveRoleRestrictionsCheckBox->IsChecked,
            NumberOfGroups = stored == null ? 0 : stored->NumberOfGroups,
            SlotsPerGroup = stored == null ? 0 : stored->NumberOfSlotsInMainParty,
            StoredSettingsExact = storedExact,
            StoredSettingsContradictory = storedContradictory,
            OwnListingId = ownListingId,
            ErrorToastSequence = errorToastSequence,
            ErrorToast = errorToast,
            HardBlocker = hardBlocker,
            Readiness = readiness,
        };
    }

    public DadAlliancePfCreateActionResult Perform(
        DadAlliancePfCreateAction action,
        int passcode)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return action switch
        {
            DadAlliancePfCreateAction.CloseStaleWindows => CloseStaleWindows(),
            DadAlliancePfCreateAction.OpenMainWindow => Command(
                "/pfinder",
                "Requested a fresh Party Finder window."),
            DadAlliancePfCreateAction.OpenConditions => OpenConditions(),
            DadAlliancePfCreateAction.SelectAlliance => SelectAlliance(),
            DadAlliancePfCreateAction.SelectRaids => SelectRaids(),
            DadAlliancePfCreateAction.SelectDuty => SelectDuty(),
            DadAlliancePfCreateAction.ConfigureNextSetting => ConfigureNextSetting(passcode),
            DadAlliancePfCreateAction.Submit => Submit(),
            _ => new DadAlliancePfCreateActionResult(false, $"Unsupported Party Finder action {action}."),
        };
    }

    public void ResetErrors()
    {
        errorToastSequence = 0;
        errorToast = string.Empty;
    }

    public void StopCreate()
    {
        var condition = GetAddon<AddonLookingForGroupCondition>("LookingForGroupCondition");
        if (condition == null)
            return;
        var master = new AddonMaster.LookingForGroupCondition(condition);
        if (master.IsVisible && master.IsAddonReady)
            master.Cancel();
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        toastGui.ErrorToast -= OnErrorToast;
    }

    private DadAlliancePfCreateActionResult CloseStaleWindows()
    {
        var condition = GetAddon<AddonLookingForGroupCondition>("LookingForGroupCondition");
        if (condition != null)
        {
            var conditionMaster = new AddonMaster.LookingForGroupCondition(condition);
            if (conditionMaster.IsVisible)
            {
                if (!conditionMaster.IsAddonReady)
                    return new DadAlliancePfCreateActionResult(false, "Waiting for stale Party Finder conditions to become closable.");
                return conditionMaster.Cancel()
                    ? new DadAlliancePfCreateActionResult(true, "Closed stale Party Finder recruitment conditions.")
                    : new DadAlliancePfCreateActionResult(false, "Stale Party Finder conditions are not closable yet.");
            }
        }

        var main = GetAddon<AddonLookingForGroup>("LookingForGroup");
        if (main != null && main->AddonLookingForGroupBase.AtkUnitBase.IsVisible)
            return Command("/pfinder", "Closed the stale Party Finder window.");

        return new DadAlliancePfCreateActionResult(true, "Party Finder windows are already closed.");
    }

    private DadAlliancePfCreateActionResult OpenConditions()
    {
        var main = GetAddon<AddonLookingForGroup>("LookingForGroup");
        if (main == null)
            return new DadAlliancePfCreateActionResult(false, "Party Finder is not visible.");
        var master = new AddonMaster.LookingForGroup(main);
        return master.IsAddonReady && master.RecruitMembersOrDetails()
            ? new DadAlliancePfCreateActionResult(true, "Clicked ECommons Recruit Members.")
            : new DadAlliancePfCreateActionResult(false, "Recruit Members is not usable.");
    }

    private DadAlliancePfCreateActionResult SelectAlliance()
    {
        var condition = GetAddon<AddonLookingForGroupCondition>("LookingForGroupCondition");
        if (condition == null)
            return new DadAlliancePfCreateActionResult(false, "Party Finder conditions are not visible.");
        var master = new AddonMaster.LookingForGroupCondition(condition);
        return master.IsAddonReady && master.Alliance()
            ? new DadAlliancePfCreateActionResult(true, "Selected Alliance recruitment through ECommons.")
            : new DadAlliancePfCreateActionResult(false, "Alliance recruitment is not selectable.");
    }

    private DadAlliancePfCreateActionResult SelectRaids()
    {
        var condition = GetAddon<AddonLookingForGroupCondition>("LookingForGroupCondition");
        if (condition == null)
            return new DadAlliancePfCreateActionResult(false, "Party Finder conditions are not visible.");
        var master = new AddonMaster.LookingForGroupCondition(condition);
        if (!master.IsAddonReady)
            return new DadAlliancePfCreateActionResult(false, "Party Finder conditions are not ready.");
        master.SelectDutyCategory(DadAlliancePartyFinderCreateFlow.RaidsCategoryBitIndex);
        return new DadAlliancePfCreateActionResult(
            true,
            $"Selected the Raids category through ECommons bit index {DadAlliancePartyFinderCreateFlow.RaidsCategoryBitIndex}.");
    }

    private DadAlliancePfCreateActionResult SelectDuty()
    {
        var condition = GetAddon<AddonLookingForGroupCondition>("LookingForGroupCondition");
        if (condition == null || condition->DutyDropDown == null || targetDutyDropDownIndex < 0)
            return new DadAlliancePfCreateActionResult(false, "The exact Labyrinth dropdown entry is unavailable.");
        condition->DutyDropDown->SelectItem(targetDutyDropDownIndex);
        return new DadAlliancePfCreateActionResult(
            true,
            $"Selected the enabled Labyrinth duty dropdown entry {targetDutyDropDownIndex}.");
    }

    private DadAlliancePfCreateActionResult ConfigureNextSetting(int passcode)
    {
        var condition = GetAddon<AddonLookingForGroupCondition>("LookingForGroupCondition");
        if (condition == null || !HasRequiredConditionControls(condition))
            return new DadAlliancePfCreateActionResult(false, "Party Finder alliance settings are unavailable.");
        var addon = &condition->AtkUnitBase;

        if (!IsChecked(condition->AllianceSelection, 0))
            return ClickRadioAsButton(condition->AllianceSelection[0].Value, addon, "Selected Alliance A.");
        if (!condition->FormPrivatePartyCheckbox->IsChecked)
            return ClickCheckbox(condition->FormPrivatePartyCheckbox, addon, "Enabled private recruitment.");
        if (condition->PasswordNumericInput->Value != passcode)
        {
            condition->PasswordNumericInput->InnerSetValue(passcode, true, false);
            return new DadAlliancePfCreateActionResult(true, "Entered the exact four-digit PF passcode.");
        }
        if (condition->LimitToWorldServerCheckbox->IsChecked)
            return ClickCheckbox(condition->LimitToWorldServerCheckbox, addon, "Enabled cross-world recruitment.");
        if (condition->OnePlayerPerJobCheckbox->IsChecked)
            return ClickCheckbox(condition->OnePlayerPerJobCheckbox, addon, "Disabled one-player-per-job restrictions.");
        if (!string.IsNullOrEmpty(condition->CommentTextInput->AtkComponentInputBase.RawString.ToString()))
        {
            condition->CommentTextInput->SetText(string.Empty);
            return new DadAlliancePfCreateActionResult(true, "Cleared the Party Finder comment.");
        }
        if (!condition->RemoveRoleRestrictionsCheckBox->IsChecked)
            return ClickCheckbox(condition->RemoveRoleRestrictionsCheckBox, addon, "Enabled unrestricted jobs for every alliance slot.");

        return new DadAlliancePfCreateActionResult(
            false,
            "Visible controls are exact, but the Party Finder agent has not retained three groups with eight slots each.",
            "Party Finder settings acknowledgement is contradictory.");
    }

    private DadAlliancePfCreateActionResult Submit()
    {
        var condition = GetAddon<AddonLookingForGroupCondition>("LookingForGroupCondition");
        if (condition == null)
            return new DadAlliancePfCreateActionResult(false, "Party Finder conditions are not visible.");
        var master = new AddonMaster.LookingForGroupCondition(condition);
        return master.IsAddonReady && master.Recruit()
            ? new DadAlliancePfCreateActionResult(true, "Submitted recruitment through ECommons.")
            : new DadAlliancePfCreateActionResult(false, "Recruit is not usable.");
    }

    private DadAlliancePfCreateActionResult Command(string command, string success)
        => commandManager.ProcessCommand(command)
            ? new DadAlliancePfCreateActionResult(true, success)
            : new DadAlliancePfCreateActionResult(false, $"The {command} command was rejected.");

    private static DadAlliancePfCreateActionResult ClickRadioAsButton(
        AtkComponentRadioButton* radio,
        AtkUnitBase* addon,
        string summary)
    {
        var button = (AtkComponentButton*)radio;
        if (button == null || !IsUsable(button))
            return new DadAlliancePfCreateActionResult(false, summary, "Alliance radio button is not usable.");
        (*button).ClickAddonButton(addon);
        return new DadAlliancePfCreateActionResult(true, summary);
    }

    private static DadAlliancePfCreateActionResult ClickCheckbox(
        AtkComponentCheckBox* checkbox,
        AtkUnitBase* addon,
        string summary)
    {
        if (checkbox == null ||
            !checkbox->IsEnabled ||
            checkbox->AtkResNode == null ||
            !checkbox->AtkResNode->IsVisible())
            return new DadAlliancePfCreateActionResult(false, summary, "Party Finder checkbox is not usable.");
        (*checkbox).ClickCheckBox(addon);
        return new DadAlliancePfCreateActionResult(true, summary);
    }

    private static bool StoredSettingsExact(
        AgentLookingForGroup.RecruitmentSub* stored,
        int passcode,
        ushort targetDutyId)
        => stored != null &&
           targetDutyId != 0 &&
           (uint)stored->SelectedCategory == DadAlliancePartyFinderCreateFlow.RaidsCategoryMask &&
           stored->SelectedDutyId == targetDutyId &&
           stored->Password == passcode &&
           stored->NumberOfGroups == 3 &&
           stored->NumberOfSlotsInMainParty == 8 &&
           stored->LimitRecruitingToWorld == 0 &&
           stored->OnePlayerPerJob == 0 &&
           string.IsNullOrEmpty(stored->CommentString);

    private static bool HasRequiredConditionControls(AddonLookingForGroupCondition* addon)
    {
        if (addon == null ||
            addon->DutyCategoryDropDown == null ||
            addon->DutyDropDown == null ||
            addon->CommentTextInput == null ||
            addon->PasswordNumericInput == null ||
            addon->FormPrivatePartyCheckbox == null ||
            addon->LimitToWorldServerCheckbox == null ||
            addon->OnePlayerPerJobCheckbox == null ||
            addon->RecruitMembersButton == null ||
            addon->RemoveRoleRestrictionsCheckBox == null ||
            addon->RecruitmentType.Length <= 1 ||
            addon->RecruitmentType[1].Value == null ||
            addon->AllianceSelection.Length == 0 ||
            addon->AllianceSelection[0].Value == null ||
            addon->MemberRoleButtons.Length != 24)
        {
            return false;
        }

        foreach (var button in addon->MemberRoleButtons)
        {
            if (button.Value == null)
                return false;
        }

        return true;
    }

    private static bool IsChecked(
        Span<FFXIVClientStructs.Interop.Pointer<AtkComponentRadioButton>> buttons,
        int index)
        => index >= 0 &&
           index < buttons.Length &&
           buttons[index].Value != null &&
           buttons[index].Value->IsChecked;

    private static bool IsUsable(AtkComponentButton* button)
        => button != null &&
           button->IsEnabled &&
           button->AtkResNode != null &&
           button->AtkResNode->IsVisible();

    private static string BuildReadiness(
        bool agent,
        bool mainVisible,
        bool mainReady,
        bool conditionVisible,
        bool conditionReady,
        bool recruitUsable,
        bool dutyListLoaded)
        => $"agent={agent}; main-visible={mainVisible}; main-ready={mainReady}; " +
           $"recruit-usable={recruitUsable}; condition-visible={conditionVisible}; " +
           $"condition-ready={conditionReady}; duty-list-loaded={dutyListLoaded}";

    private void OnErrorToast(ref SeString message, ref bool isHandled)
    {
        errorToast = message.TextValue.Trim();
        errorToastSequence++;
    }

    private static T* GetAddon<T>(string name) where T : unmanaged
    {
        var manager = RaptureAtkUnitManager.Instance();
        return manager == null ? null : (T*)manager->GetAddonByName(name);
    }
}
