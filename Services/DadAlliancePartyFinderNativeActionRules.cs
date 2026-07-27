namespace dad.Services;

internal enum DadAlliancePfNativeAction
{
    CloseConditions,
    OpenConditions,
    SelectAlliance,
    SelectRaids,
    SelectDuty,
    Submit,
    ShowOwnedRecruitment,
    OpenOwnedDetails,
    EndRecruitment,
    ConfirmEndRecruitment,
}

internal enum DadAlliancePfNativeValueKind
{
    Undefined,
    Int,
    UInt,
}

internal readonly record struct DadAlliancePfNativeValue(
    DadAlliancePfNativeValueKind Kind,
    long NumericValue = 0);

internal enum DadAlliancePfAddonCallbackReceiver
{
    LookingForGroupDetail,
}

internal readonly record struct DadAlliancePfAddonCallbackSpec(
    DadAlliancePfAddonCallbackReceiver Receiver,
    bool UpdateVisibility,
    IReadOnlyList<DadAlliancePfNativeValue> Values);

internal readonly record struct DadAlliancePfAgentEventSpec(
    ulong EventKind,
    IReadOnlyList<DadAlliancePfNativeValue> Values);

internal static class DadAlliancePartyFinderNativeActionRules
{
    public const ulong LookingForGroupEventKind = 3;

    public static DadAlliancePfAgentEventSpec GetAgentEventSpec(
        DadAlliancePfNativeAction action,
        int argument = 0)
        => action switch
        {
            DadAlliancePfNativeAction.SelectAlliance => new(
                LookingForGroupEventKind,
                [
                    new(DadAlliancePfNativeValueKind.Int, 35),
                    new(DadAlliancePfNativeValueKind.UInt, 1),
                    new(DadAlliancePfNativeValueKind.Undefined),
                ]),
            DadAlliancePfNativeAction.SelectRaids => new(
                LookingForGroupEventKind,
                [
                    new(DadAlliancePfNativeValueKind.Int, 12),
                    new(DadAlliancePfNativeValueKind.UInt, 5),
                    new(DadAlliancePfNativeValueKind.Undefined),
                ]),
            DadAlliancePfNativeAction.SelectDuty when argument >= 0 => new(
                LookingForGroupEventKind,
                [
                    new(DadAlliancePfNativeValueKind.Int, 13),
                    new(DadAlliancePfNativeValueKind.UInt, argument),
                    new(DadAlliancePfNativeValueKind.Undefined),
                ]),
            DadAlliancePfNativeAction.SelectDuty =>
                throw new ArgumentOutOfRangeException(
                    nameof(argument),
                    argument,
                    "The Party Finder duty dropdown index cannot be negative."),
            _ => throw new ArgumentOutOfRangeException(
                nameof(action),
                action,
                "The action does not use the current Party Finder agent-event contract."),
        };

    public static DadAlliancePfAddonCallbackSpec GetAddonCallbackSpec(
        DadAlliancePfNativeAction action)
        => action switch
        {
            DadAlliancePfNativeAction.EndRecruitment => new(
                DadAlliancePfAddonCallbackReceiver.LookingForGroupDetail,
                UpdateVisibility: false,
                [
                    new(DadAlliancePfNativeValueKind.Int, 11),
                ]),
            _ => throw new ArgumentOutOfRangeException(
                nameof(action),
                action,
                "Only recruitment cleanup uses a fixed Party Finder addon callback."),
        };

    public static string GetDispatchSummary(
        DadAlliancePfNativeAction action)
        => action switch
        {
            DadAlliancePfNativeAction.EndRecruitment =>
                "Dispatched LookingForGroupDetail.FireCallback updateVisibility=false " +
                "payload [Int 11] to request End Recruitment.",
            _ => throw new ArgumentOutOfRangeException(
                nameof(action),
                action,
                "The action does not have a fixed audited callback summary."),
        };
}

internal interface IDadAlliancePartyFinderNativeActions
{
    DadAlliancePfCreateActionResult Perform(
        DadAlliancePfNativeAction action,
        int argument = 0);
}

internal interface IDadAllianceRecruitmentObserver
{
    bool IsActiveRecruitment { get; }
}
