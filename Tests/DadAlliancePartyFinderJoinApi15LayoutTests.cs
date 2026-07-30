using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Xunit;

namespace dad.Tests;

public sealed class DadAlliancePartyFinderJoinApi15LayoutTests
{
    [Fact]
    public void JoinSearchAgentOffsetsMatchApi15()
    {
        AssertOffset<AgentLookingForGroup>(
            nameof(AgentLookingForGroup.LastViewedListing),
            11424);
        AssertOffset<AgentLookingForGroup>(
            nameof(AgentLookingForGroup.NumberOfListingsDisplayed),
            14002);
        AssertOffset<AgentLookingForGroup>(
            nameof(AgentLookingForGroup.SearchAreaTab),
            14009);
        AssertOffset<AgentLookingForGroup>(
            nameof(AgentLookingForGroup.CategoryTab),
            14011);
    }

    [Fact]
    public void JoinListingRendererOffsetsMatchApi15()
    {
        AssertOffset<AddonLookingForGroup>(
            nameof(AddonLookingForGroup.StandardViewList),
            25432);
        AssertOffset<AddonLookingForGroup>(
            nameof(AddonLookingForGroup.CompactViewList),
            25440);
        AssertOffset<AtkComponentList>(
            nameof(AtkComponentList.ItemRendererList),
            240);
        AssertOffset<AtkComponentList>(
            nameof(AtkComponentList.AllocatedItemRendererListLength),
            248);
        AssertOffset<AtkComponentList>(
            nameof(AtkComponentList.ListLength),
            288);
        AssertOffset<AtkComponentList.ListItem>(
            nameof(AtkComponentList.ListItem.AtkComponentListItemRenderer),
            8);
        AssertOffset<AtkComponentListItemRenderer>(
            nameof(AtkComponentListItemRenderer.ListItemIndex),
            388);
    }

    [Fact]
    public void JoinDetailOffsetsAndFlagsMatchApi15()
    {
        AssertOffset<AgentLookingForGroup.Detailed>(
            nameof(AgentLookingForGroup.Detailed.DutyId),
            40);
        AssertOffset<AgentLookingForGroup.Detailed>(
            nameof(AgentLookingForGroup.Detailed.HomeWorld),
            82);
        AssertOffset<AgentLookingForGroup.Detailed>(
            nameof(AgentLookingForGroup.Detailed.JoinConditionFlags),
            91);
        AssertOffset<AgentLookingForGroup.Detailed>(
            nameof(AgentLookingForGroup.Detailed.IsAlliance),
            93);
        AssertOffset<AgentLookingForGroup.Detailed>(
            nameof(AgentLookingForGroup.Detailed.NumberOfParties),
            94);

        Assert.Equal(
            2,
            (byte)AgentLookingForGroup.JoinCondition.Private);
        Assert.Equal(
            4,
            (byte)AgentLookingForGroup.JoinCondition.AllianceRaid);
    }

    private static void AssertOffset<T>(string field, int expected)
        where T : struct
        => Assert.Equal(
            expected,
            Marshal.OffsetOf<T>(field).ToInt32());
}
