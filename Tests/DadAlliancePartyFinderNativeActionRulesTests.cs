using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadAlliancePartyFinderNativeActionRulesTests
{
    [Fact]
    public void AlliancePreparationUsesCurrentVerifiedReceiver()
    {
        AssertAgentEvent(
            DadAlliancePfNativeAction.SelectAlliance,
            0,
            new(DadAlliancePfNativeValueKind.Int, 35),
            new(DadAlliancePfNativeValueKind.UInt, 1),
            new(DadAlliancePfNativeValueKind.Undefined));
    }

    [Fact]
    public void RaidsPreparationUsesFlagBitIndexFive()
    {
        AssertAgentEvent(
            DadAlliancePfNativeAction.SelectRaids,
            0,
            new(DadAlliancePfNativeValueKind.Int, 12),
            new(DadAlliancePfNativeValueKind.UInt, 5),
            new(DadAlliancePfNativeValueKind.Undefined));
    }

    [Fact]
    public void DutyPreparationUsesResolvedDropdownIndex()
    {
        AssertAgentEvent(
            DadAlliancePfNativeAction.SelectDuty,
            17,
            new(DadAlliancePfNativeValueKind.Int, 13),
            new(DadAlliancePfNativeValueKind.UInt, 17),
            new(DadAlliancePfNativeValueKind.Undefined));
    }

    [Fact]
    public void DutyPreparationRejectsNegativeDropdownIndex()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DadAlliancePartyFinderNativeActionRules.GetAgentEventSpec(
                DadAlliancePfNativeAction.SelectDuty,
                -1));
    }

    private static void AssertAgentEvent(
        DadAlliancePfNativeAction action,
        int argument,
        params DadAlliancePfNativeValue[] expected)
    {
        var spec =
            DadAlliancePartyFinderNativeActionRules.GetAgentEventSpec(
                action,
                argument);

        Assert.Equal(
            DadAlliancePartyFinderNativeActionRules.LookingForGroupEventKind,
            spec.EventKind);
        Assert.Equal(expected, spec.Values);
    }
}
