using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace dad.Services;

/// <summary>
/// Framework-thread UI adapter. Current typed ClientStructs own the Party Finder
/// controls; ECommons is limited to stateless UI-input extensions and its global
/// service layer is never initialized.
/// </summary>
internal sealed unsafe class DadAlliancePartyFinderECommonsAdapter :
    IDadAlliancePartyFinderCreateUi,
    IDadAlliancePartyFinderCleanupUi,
    IDisposable
{
    private const ushort PasswordDisabled = 10000;
    private const ulong AllJobsOpenSlotFlag = 0xFFFFFFFE;
    private readonly DadAlliancePartyFinderCommandDispatcher commandDispatcher;
    private readonly IDadAlliancePartyFinderNativeActions nativeActions;
    private readonly IDadAlliancePartyFinderPresetLoader presetLoader;
    private readonly IDadAllianceRecruitmentObserver recruitmentObserver;
    private readonly IDataManager dataManager;
    private readonly IToastGui toastGui;
    private int errorToastSequence;
    private string errorToast = string.Empty;
    private int targetDutyDropDownIndex = -1;
    private bool disposed;

    public DadAlliancePartyFinderECommonsAdapter(
        IDadGameCommandExecutor gameCommandExecutor,
        IDadAlliancePartyFinderNativeActions nativeActions,
        IDadAlliancePartyFinderPresetLoader presetLoader,
        IDadAllianceRecruitmentObserver recruitmentObserver,
        IDataManager dataManager,
        IToastGui toastGui)
    {
        commandDispatcher = new DadAlliancePartyFinderCommandDispatcher(gameCommandExecutor);
        this.nativeActions = nativeActions;
        this.presetLoader = presetLoader;
        this.recruitmentObserver = recruitmentObserver;
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
        var mainReady = mainVisible && main->AddonLookingForGroupBase.AtkUnitBase.IsReady;
        var conditionReady = conditionVisible && condition->AtkUnitBase.IsReady;
        var conditionControlsReady = conditionReady && HasRequiredConditionControls(condition);
        var activeRecruitment = recruitmentObserver.IsActiveRecruitment;
        var participatingInCrossWorldPartyOrAlliance =
            recruitmentObserver.IsParticipatingInCrossWorldPartyOrAlliance;
        var hardBlocker = string.Empty;

        if (mainReady && main->RecruitMembersButton == null)
            hardBlocker = "The fully loaded Party Finder window is missing Recruit Members.";
        if (conditionReady && !conditionControlsReady)
            hardBlocker = "The fully loaded Party Finder conditions window is missing required alliance controls.";
        if (!presetLoader.IsAvailable)
            hardBlocker = presetLoader.UnavailableReason;

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
        var selectedDutyDropDownIndex = -1;
        if (conditionControlsReady &&
            condition->DutyDropDown != null &&
            condition->DutyDropDown->List != null)
        {
            var list = condition->DutyDropDown->List;
            selectedDutyDropDownIndex = list->SelectedItemIndex;
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
        var storedPrivateRecruitment =
            stored != null &&
            stored->Password != PasswordDisabled;
        var storedPasscode = stored == null ? 0 : stored->Password;
        var storedCrossWorldRecruitment =
            stored != null &&
            DadAlliancePartyFinderPresetRules.IsStoredCrossWorldRecruitment(
                stored->LimitRecruitingToWorld);
        var storedOnePlayerPerJob =
            stored != null &&
            stored->OnePlayerPerJob != 0;
        var storedEmptyComment =
            stored != null &&
            string.IsNullOrEmpty(stored->CommentString);
        var storedOpenSlotsUnrestricted = StoredOpenSlotsUnrestricted(stored);
        var storedStaleMembersCleared = StoredStaleMembersCleared(stored);
        var allianceSelected = conditionControlsReady &&
                               IsChecked(condition->RecruitmentType, 1);
        var storedExactBeforeSubmit = StoredSettingsExactBeforeSubmit(
            agent,
            stored,
            passcode,
            targetDutyId);
        var storedExact = StoredSettingsExact(
            agent,
            stored,
            passcode,
            targetDutyId);
        var ownerHandle = agent == null ? 0u : agent->OwnListingId;
        var storedContradictory =
            activeRecruitment &&
            !conditionVisible &&
            !storedExact;
        var readiness = BuildReadiness(
            agent != null,
            mainVisible,
            mainReady,
            conditionVisible,
            conditionReady,
            mainReady && IsUsable(main->RecruitMembersButton),
            conditionControlsReady &&
            condition->DutyDropDown != null &&
            condition->DutyDropDown->List != null,
            targetDutyDropDownIndex,
            selectedDutyDropDownIndex,
            selectedCategory,
            selectedDutyId);

        return new DadAlliancePfCreateSnapshot
        {
            AgentAvailable = agent != null,
            MainVisible = mainVisible,
            MainReady = mainReady,
            MainRecruitUsable = mainReady && IsUsable(main->RecruitMembersButton),
            ConditionVisible = conditionVisible,
            ConditionReady = conditionReady,
            PresetLoaderAvailable = presetLoader.IsAvailable,
            PresetLoaderBlocker = presetLoader.UnavailableReason,
            GroupTypeTab = agent == null ? (byte)0 : agent->GroupTypeTab,
            AllianceSelected = allianceSelected,
            SelectedCategory = selectedCategory,
            TargetDutyId = targetDutyId,
            TargetDutySheetMatches = targetDutyIds.Length,
            DutyListLoaded = conditionControlsReady &&
                             condition->DutyDropDown != null &&
                             condition->DutyDropDown->List != null,
            TargetDutyDropDownMatches = dropDownMatches,
            TargetDutyEntryEnabled = dutyEntryEnabled,
            TargetDutyDropDownIndex = targetDutyDropDownIndex,
            SelectedDutyDropDownIndex = selectedDutyDropDownIndex,
            SelectedDutyId = selectedDutyId,
            AllianceASelected = conditionControlsReady && IsChecked(condition->AllianceSelection, 0),
            PrivateRecruitment = conditionControlsReady && condition->FormPrivatePartyCheckbox->IsChecked,
            StoredPrivateRecruitment = storedPrivateRecruitment,
            Passcode = conditionControlsReady ? condition->PasswordNumericInput->Value : 0,
            StoredPasscode = storedPasscode,
            CrossWorldRecruitment = conditionControlsReady && !condition->LimitToWorldServerCheckbox->IsChecked,
            StoredCrossWorldRecruitment = storedCrossWorldRecruitment,
            OnePlayerPerJob = conditionControlsReady && condition->OnePlayerPerJobCheckbox->IsChecked,
            StoredOnePlayerPerJob = storedOnePlayerPerJob,
            EmptyComment = conditionControlsReady &&
                           string.IsNullOrEmpty(condition->CommentTextInput->AtkComponentInputBase.RawString.ToString()),
            StoredEmptyComment = storedEmptyComment,
            UnrestrictedJobs = conditionControlsReady && condition->RemoveRoleRestrictionsCheckBox->IsChecked,
            StoredOpenSlotsUnrestricted = storedOpenSlotsUnrestricted,
            StoredStaleMembersCleared = storedStaleMembersCleared,
            NumberOfGroups = stored == null ? 0 : stored->NumberOfGroups,
            SlotsPerGroup = stored == null ? 0 : stored->NumberOfSlotsInMainParty,
            StoredSettingsExactBeforeSubmit = storedExactBeforeSubmit,
            StoredSettingsExact = storedExact,
            StoredSettingsContradictory = storedContradictory,
            OwnerHandle = ownerHandle,
            ActiveRecruitment = activeRecruitment,
            ParticipatingInCrossWorldPartyOrAlliance =
                participatingInCrossWorldPartyOrAlliance,
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
            DadAlliancePfCreateAction.OpenMainWindow => commandDispatcher.TryExecute(
                "Requested a fresh Party Finder window."),
            DadAlliancePfCreateAction.OpenConditions =>
                nativeActions.Perform(DadAlliancePfNativeAction.OpenConditions),
            DadAlliancePfCreateAction.SelectAlliance =>
                nativeActions.Perform(DadAlliancePfNativeAction.SelectAlliance),
            DadAlliancePfCreateAction.ReloadCloseConditions =>
                nativeActions.Perform(DadAlliancePfNativeAction.CloseConditions),
            DadAlliancePfCreateAction.ReloadRestoreAllianceTab =>
                RestoreAllianceGroupTypeTab(),
            DadAlliancePfCreateAction.ReloadMainWindow =>
                commandDispatcher.TryExecute(
                    "Requested the Party Finder window for the Alliance editor reload."),
            DadAlliancePfCreateAction.ReloadOpenConditions =>
                nativeActions.Perform(DadAlliancePfNativeAction.OpenConditions),
            DadAlliancePfCreateAction.SelectRaids =>
                nativeActions.Perform(DadAlliancePfNativeAction.SelectRaids),
            DadAlliancePfCreateAction.SelectDuty =>
                SelectDuty(),
            DadAlliancePfCreateAction.ApplyPreset =>
                presetLoader.Apply(passcode),
            DadAlliancePfCreateAction.Submit =>
                nativeActions.Perform(DadAlliancePfNativeAction.Submit),
            _ => new DadAlliancePfCreateActionResult(false, $"Unsupported Party Finder action {action}."),
        };
    }

    public void ResetErrors()
    {
        errorToastSequence = 0;
        errorToast = string.Empty;
    }

    public void StopCreate()
        => nativeActions.Perform(DadAlliancePfNativeAction.CloseConditions);

    public DadAlliancePfCleanupSnapshot ReadCleanup()
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        var agent = AgentLookingForGroup.Instance();
        var main = GetAddon<AddonLookingForGroup>("LookingForGroup");
        var detail = GetAddon<AddonLookingForGroupDetail>("LookingForGroupDetail");
        var confirmation = GetAddon<AddonSelectYesno>("SelectYesno");
        var mainVisible = main != null && main->AddonLookingForGroupBase.AtkUnitBase.IsVisible;
        var mainReady = mainVisible && main->AddonLookingForGroupBase.AtkUnitBase.IsReady;
        var detailVisible = detail != null && detail->AtkUnitBase.IsVisible;
        var detailReady = detailVisible && detail->AtkUnitBase.IsReady;
        var confirmationVisible = confirmation != null && confirmation->AtkUnitBase.IsVisible;
        var confirmationReady = confirmationVisible && confirmation->AtkUnitBase.IsReady;
        var confirmationText = confirmationVisible && confirmation->PromptText != null
            ? confirmation->PromptText->NodeText.ToString().Trim()
            : string.Empty;
        var ownerHandle = agent == null ? 0u : agent->OwnListingId;
        var activeRecruitment = recruitmentObserver.IsActiveRecruitment;
        var detailsUsable = mainReady && IsUsable(main->RecruitMembersButton);
        var hardBlocker = mainReady && main->RecruitMembersButton == null
            ? "The owned Party Finder window is missing its typed details control."
            : string.Empty;

        return new DadAlliancePfCleanupSnapshot
        {
            AgentAvailable = agent != null,
            ActiveRecruitment = activeRecruitment,
            OwnerHandle = ownerHandle,
            MainVisible = mainVisible,
            MainReady = mainReady,
            DetailsControlUsable = detailsUsable,
            DetailVisible = detailVisible,
            DetailReady = detailReady,
            ConfirmationVisible = confirmationVisible,
            ConfirmationReady = confirmationReady,
            ConfirmationIdentity = confirmationVisible
                ? $"{(nint)confirmation:X}"
                : string.Empty,
            ConfirmationText = confirmationText,
            OtherReadyPromptVisible = IsReadyAddon("LookingForGroupPrivate"),
            HardBlocker = hardBlocker,
            Readiness =
                $"active-recruitment={activeRecruitment}; owner-handle={ownerHandle}; " +
                $"main-visible={mainVisible}; main-ready={mainReady}; details-usable={detailsUsable}; " +
                $"detail-visible={detailVisible}; detail-ready={detailReady}; " +
                $"confirmation-visible={confirmationVisible}",
        };
    }

    public DadAlliancePfCreateActionResult PerformCleanup(DadAlliancePfNativeAction action)
        => nativeActions.Perform(action);

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        toastGui.ErrorToast -= OnErrorToast;
        presetLoader.Dispose();
    }

    private DadAlliancePfCreateActionResult SelectDuty()
    {
        if (targetDutyDropDownIndex < 0)
        {
            return new DadAlliancePfCreateActionResult(
                false,
                "The exact enabled Labyrinth duty row is unavailable.");
        }

        return nativeActions.Perform(
            DadAlliancePfNativeAction.SelectDuty,
            targetDutyDropDownIndex);
    }

    private static DadAlliancePfCreateActionResult
        RestoreAllianceGroupTypeTab()
    {
        var agent = AgentLookingForGroup.Instance();
        if (agent == null)
        {
            return new DadAlliancePfCreateActionResult(
                false,
                "The Alliance tab was not restored after typed Cancel.",
                "Party Finder agent is unavailable.");
        }

        agent->GroupTypeTab =
            DadAlliancePartyFinderPresetDefinition.AllianceGroupTypeTab;
        return new DadAlliancePfCreateActionResult(
            true,
            "Restored Alliance tab 1 once after typed Cancel without resending Alliance or refreshing the preset.");
    }

    private DadAlliancePfCreateActionResult CloseStaleWindows()
    {
        var condition = GetAddon<AddonLookingForGroupCondition>("LookingForGroupCondition");
        if (condition != null)
        {
            var addon = &condition->AtkUnitBase;
            if (addon->IsVisible)
            {
                if (!addon->IsReady)
                    return new DadAlliancePfCreateActionResult(false, "Waiting for stale Party Finder conditions to become closable.");
                return nativeActions.Perform(DadAlliancePfNativeAction.CloseConditions);
            }
        }

        var main = GetAddon<AddonLookingForGroup>("LookingForGroup");
        if (main != null && main->AddonLookingForGroupBase.AtkUnitBase.IsVisible)
            return commandDispatcher.TryExecute("Closed the stale Party Finder window.");

        return new DadAlliancePfCreateActionResult(true, "Party Finder windows are already closed.");
    }

    private static bool StoredSettingsExact(
        AgentLookingForGroup* agent,
        AgentLookingForGroup.RecruitmentSub* stored,
        int passcode,
        ushort targetDutyId)
        => StoredSettingsExactCommon(
            agent,
            stored,
            passcode,
            targetDutyId);

    private static bool StoredSettingsExactBeforeSubmit(
        AgentLookingForGroup* agent,
        AgentLookingForGroup.RecruitmentSub* stored,
        int passcode,
        ushort targetDutyId)
        => StoredSettingsExactCommon(
               agent,
               stored,
               passcode,
               targetDutyId) &&
           StoredStaleMembersCleared(stored);

    private static bool StoredSettingsExactCommon(
        AgentLookingForGroup* agent,
        AgentLookingForGroup.RecruitmentSub* stored,
        int passcode,
        ushort targetDutyId)
        => agent != null &&
           agent->GroupTypeTab ==
               DadAlliancePartyFinderPresetDefinition.AllianceGroupTypeTab &&
           stored != null &&
           targetDutyId == DadAlliancePartyFinderCreateFlow.LabyrinthDutyId &&
           (uint)stored->SelectedCategory == DadAlliancePartyFinderCreateFlow.RaidsCategoryMask &&
           stored->SelectedDutyId == DadAlliancePartyFinderCreateFlow.LabyrinthDutyId &&
           stored->Password == passcode &&
           stored->NumberOfGroups == 3 &&
           stored->NumberOfSlotsInMainParty == 8 &&
           DadAlliancePartyFinderPresetRules.IsStoredCrossWorldRecruitment(
               stored->LimitRecruitingToWorld) &&
           stored->OnePlayerPerJob == 0 &&
           string.IsNullOrEmpty(stored->CommentString) &&
           StoredOpenSlotsUnrestricted(stored);

    private static bool StoredOpenSlotsUnrestricted(
        AgentLookingForGroup.RecruitmentSub* stored)
    {
        if (stored == null ||
            stored->NumberOfGroups != 3 ||
            stored->NumberOfSlotsInMainParty != 8)
        {
            return false;
        }

        for (var index = 1; index < 24; index++)
        {
            if (stored->SlotFlags[index] != AllJobsOpenSlotFlag)
                return false;
        }
        for (var index = 24; index < 48; index++)
        {
            if (stored->SlotFlags[index] != 0)
                return false;
        }

        return true;
    }

    private static bool StoredStaleMembersCleared(
        AgentLookingForGroup.RecruitmentSub* stored)
    {
        if (stored == null)
            return false;

        for (var index = 1; index < 48; index++)
        {
            if (stored->MemberContentIds[index] != 0)
                return false;
        }

        return true;
    }

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
        bool dutyListLoaded,
        int targetDutyIndex,
        int visibleDutyIndex,
        uint category,
        ushort storedDutyId)
        => $"agent={agent}; main-visible={mainVisible}; main-ready={mainReady}; " +
           $"recruit-usable={recruitUsable}; condition-visible={conditionVisible}; " +
           $"condition-ready={conditionReady}; duty-list-loaded={dutyListLoaded}; " +
           $"duty-target-index={targetDutyIndex}; duty-visible-index={visibleDutyIndex}; " +
           $"category=0x{category:X}; stored-duty={storedDutyId}";

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

    private static bool IsReadyAddon(string name)
    {
        var manager = RaptureAtkUnitManager.Instance();
        var addon = manager == null ? null : manager->GetAddonByName(name);
        return addon != null && addon->IsVisible && addon->IsReady;
    }
}
