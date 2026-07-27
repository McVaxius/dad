using Dalamud.Plugin.Services;
using ECommons.Automation.UIInput;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace dad.Services;

internal sealed class DadAllianceLocalRecruitmentObserver :
    IDadAllianceRecruitmentObserver
{
    private const uint RecruitingOnlineStatusId = 26;
    private readonly IObjectTable objectTable;

    public DadAllianceLocalRecruitmentObserver(IObjectTable objectTable)
    {
        this.objectTable = objectTable;
    }

    public bool IsActiveRecruitment
        => objectTable.LocalPlayer?.OnlineStatus.RowId ==
           RecruitingOnlineStatusId;
}

/// <summary>
/// Enum-restricted typed UI actions for editor entry, one-shot game-owned
/// Alliance/Raids/duty selector preparation, Submit, and recruitment-only
/// cleanup. The final preset overlay never synthesizes selector bytes.
/// </summary>
internal sealed unsafe class DadAlliancePartyFinderTypedNativeActions :
    IDadAlliancePartyFinderNativeActions
{
    public DadAlliancePfCreateActionResult Perform(
        DadAlliancePfNativeAction action,
        int argument = 0)
    {
        try
        {
            return action switch
            {
                DadAlliancePfNativeAction.CloseConditions =>
                    ClickConditionButton(
                        false,
                        "Closed Party Finder recruitment conditions.",
                        "Party Finder conditions are not closable yet."),
                DadAlliancePfNativeAction.OpenConditions =>
                    ClickMainButton(
                        "Opened recruitment conditions through the typed Recruit Members control.",
                        "Recruit Members is not usable."),
                DadAlliancePfNativeAction.SelectAlliance or
                DadAlliancePfNativeAction.SelectRaids or
                DadAlliancePfNativeAction.SelectDuty =>
                    SendLookingForGroupEvent(action, argument),
                DadAlliancePfNativeAction.Submit =>
                    ClickConditionButton(
                        true,
                        "Submitted recruitment through the typed Recruit control.",
                        "Recruit is not usable."),
                DadAlliancePfNativeAction.ShowOwnedRecruitment =>
                    ShowOwnedRecruitment(),
                DadAlliancePfNativeAction.OpenOwnedDetails =>
                    ClickMainButton(
                        "Opened owned recruitment details through the typed details control.",
                        "Owned recruitment details are not usable."),
                DadAlliancePfNativeAction.EndRecruitment =>
                    FireEndRecruitmentCallback(),
                DadAlliancePfNativeAction.ConfirmEndRecruitment =>
                    ConfirmEndRecruitment(),
                _ => new DadAlliancePfCreateActionResult(
                    false,
                    $"Unsupported Party Finder native action {action}."),
            };
        }
        catch (Exception exception)
        {
            return new DadAlliancePfCreateActionResult(
                false,
                $"{action} failed.",
                exception.Message);
        }
    }

    private static DadAlliancePfCreateActionResult ClickMainButton(
        string success,
        string unavailable)
    {
        var addon = GetAddon<AddonLookingForGroup>("LookingForGroup");
        if (addon == null ||
            !IsReady(&addon->AddonLookingForGroupBase.AtkUnitBase) ||
            !ClickButton(
                addon->RecruitMembersButton,
                &addon->AddonLookingForGroupBase.AtkUnitBase))
        {
            return new DadAlliancePfCreateActionResult(false, unavailable);
        }

        return new DadAlliancePfCreateActionResult(true, success);
    }

    private static DadAlliancePfCreateActionResult ClickConditionButton(
        bool recruit,
        string success,
        string unavailable)
    {
        var addon = GetAddon<AddonLookingForGroupCondition>(
            "LookingForGroupCondition");
        if (addon == null ||
            !IsReady(&addon->AtkUnitBase) ||
            !ClickButton(
                recruit
                    ? addon->RecruitMembersButton
                    : addon->CancelButton,
                &addon->AtkUnitBase))
        {
            return new DadAlliancePfCreateActionResult(false, unavailable);
        }

        return new DadAlliancePfCreateActionResult(true, success);
    }

    private static DadAlliancePfCreateActionResult
        SendLookingForGroupEvent(
            DadAlliancePfNativeAction action,
            int argument)
    {
        var addon = GetAddon<AddonLookingForGroupCondition>(
            "LookingForGroupCondition");
        if (addon == null || !IsReady(&addon->AtkUnitBase))
        {
            return new DadAlliancePfCreateActionResult(
                false,
                "Party Finder conditions are not ready.");
        }

        var agent = AgentLookingForGroup.Instance();
        if (agent == null)
        {
            return new DadAlliancePfCreateActionResult(
                false,
                "Party Finder agent is unavailable.");
        }

        var spec =
            DadAlliancePartyFinderNativeActionRules.GetAgentEventSpec(
                action,
                argument);
        var values = stackalloc AtkValue[spec.Values.Count];
        for (var index = 0; index < spec.Values.Count; index++)
        {
            var value = spec.Values[index];
            values[index] = default;
            values[index].Type = value.Kind switch
            {
                DadAlliancePfNativeValueKind.Int => AtkValueType.Int,
                DadAlliancePfNativeValueKind.UInt => AtkValueType.UInt,
                DadAlliancePfNativeValueKind.Undefined =>
                    AtkValueType.Undefined,
                _ => throw new ArgumentOutOfRangeException(nameof(spec)),
            };
            if (value.Kind == DadAlliancePfNativeValueKind.Int)
                values[index].Int = checked((int)value.NumericValue);
            else if (value.Kind == DadAlliancePfNativeValueKind.UInt)
                values[index].UInt = checked((uint)value.NumericValue);
        }

        var returnValue = stackalloc AtkValue[1];
        returnValue[0] = default;
        agent->ReceiveEvent(
            returnValue,
            values,
            checked((uint)spec.Values.Count),
            spec.EventKind);
        return new DadAlliancePfCreateActionResult(
            true,
            action switch
            {
                DadAlliancePfNativeAction.SelectAlliance =>
                    "Dispatched Alliance selector event [Int 35, UInt 1, Undefined] once.",
                DadAlliancePfNativeAction.SelectRaids =>
                    "Dispatched Raids selector event [Int 12, UInt 5, Undefined] once.",
                DadAlliancePfNativeAction.SelectDuty =>
                    $"Dispatched Labyrinth selector event [Int 13, UInt {argument}, Undefined] once.",
                _ => throw new ArgumentOutOfRangeException(
                    nameof(action),
                    action,
                    null),
            });
    }

    private static DadAlliancePfCreateActionResult ShowOwnedRecruitment()
    {
        var agent = AgentLookingForGroup.Instance();
        if (agent == null)
        {
            return new DadAlliancePfCreateActionResult(
                false,
                "Party Finder agent is unavailable.");
        }

        agent->Show();
        return new DadAlliancePfCreateActionResult(
            true,
            "Requested the owned Party Finder window.");
    }

    private static DadAlliancePfCreateActionResult
        FireEndRecruitmentCallback()
    {
        var addon = GetAddon<AddonLookingForGroupDetail>(
            "LookingForGroupDetail");
        if (addon == null || !IsReady(&addon->AtkUnitBase))
        {
            return new DadAlliancePfCreateActionResult(
                false,
                "Owned recruitment details are not ready.");
        }

        var spec =
            DadAlliancePartyFinderNativeActionRules.GetAddonCallbackSpec(
                DadAlliancePfNativeAction.EndRecruitment);
        var values = stackalloc AtkValue[spec.Values.Count];
        for (var index = 0; index < spec.Values.Count; index++)
        {
            values[index] = default;
            values[index].Type = AtkValueType.Int;
            values[index].Int =
                checked((int)spec.Values[index].NumericValue);
        }
        addon->AtkUnitBase.FireCallback(
            checked((uint)spec.Values.Count),
            values,
            spec.UpdateVisibility);
        return new DadAlliancePfCreateActionResult(
            true,
            DadAlliancePartyFinderNativeActionRules.GetDispatchSummary(
                DadAlliancePfNativeAction.EndRecruitment));
    }

    private static DadAlliancePfCreateActionResult
        ConfirmEndRecruitment()
    {
        var addon = GetAddon<AddonSelectYesno>("SelectYesno");
        if (addon == null || !IsReady(&addon->AtkUnitBase))
        {
            return new DadAlliancePfCreateActionResult(
                false,
                "The recruitment-only confirmation is not ready.");
        }

        var values = stackalloc AtkValue[1];
        values[0] = default;
        values[0].Type = AtkValueType.Int;
        values[0].Int = 0;
        addon->AtkUnitBase.FireCallback(1, values, true);
        return new DadAlliancePfCreateActionResult(
            true,
            "Confirmed recruitment-only closure.");
    }

    private static bool ClickButton(
        AtkComponentButton* button,
        AtkUnitBase* addon)
    {
        if (button == null ||
            addon == null ||
            !button->IsEnabled ||
            button->AtkResNode == null ||
            !button->AtkResNode->IsVisible())
        {
            return false;
        }

        (*button).ClickAddonButton(addon);
        return true;
    }

    private static bool IsReady(AtkUnitBase* addon)
        => addon != null && addon->IsVisible && addon->IsReady;

    private static T* GetAddon<T>(string name) where T : unmanaged
    {
        var manager = RaptureAtkUnitManager.Instance();
        return manager == null
            ? null
            : (T*)manager->GetAddonByName(name);
    }
}
