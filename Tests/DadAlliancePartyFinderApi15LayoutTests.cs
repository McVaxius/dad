using System.Diagnostics;
using System.Runtime.InteropServices;
using dad.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Xunit;

namespace dad.Tests;

public sealed class DadAlliancePartyFinderApi15LayoutTests
{
    private const string ExpectedClientStructsCommit =
        "0ce3f0220901a7c9f16d3fec526558e7829ca3b3";

    [Fact]
    public void InstalledClientStructsIdentityIsPinned()
    {
        var assembly =
            typeof(AgentLookingForGroup).Assembly;
        var productVersion = FileVersionInfo.GetVersionInfo(
            assembly.Location).ProductVersion;

        Assert.NotNull(productVersion);
        Assert.Contains(
            ExpectedClientStructsCommit,
            productVersion,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AgentAndRecruitmentSizesAndOffsetsMatchApi15()
    {
        Assert.Equal(
            DadAlliancePartyFinderApi15Layout.AgentLookingForGroupSize,
            Marshal.SizeOf<AgentLookingForGroup>());
        Assert.Equal(
            DadAlliancePartyFinderApi15Layout.RecruitmentSubSize,
            Marshal.SizeOf<AgentLookingForGroup.RecruitmentSub>());
        AssertOffset<AgentLookingForGroup>(
            nameof(AgentLookingForGroup.AvgItemLv),
            DadAlliancePartyFinderApi15Layout.AvgItemLvOffset);
        AssertOffset<AgentLookingForGroup>(
            nameof(AgentLookingForGroup.AvgItemLvEnabled),
            DadAlliancePartyFinderApi15Layout.AvgItemLvEnabledOffset);
        AssertOffset<AgentLookingForGroup>(
            nameof(AgentLookingForGroup.StoredRecruitmentInfo),
            DadAlliancePartyFinderApi15Layout.StoredRecruitmentInfoOffset);
        AssertOffset<AgentLookingForGroup>(
            nameof(AgentLookingForGroup.GroupTypeTab),
            DadAlliancePartyFinderApi15Layout.GroupTypeTabOffset);

        AssertOffset<AgentLookingForGroup.RecruitmentSub>(
            nameof(AgentLookingForGroup.RecruitmentSub.SelectedCategory),
            DadAlliancePartyFinderApi15Layout.SelectedCategoryOffset);
        AssertOffset<AgentLookingForGroup.RecruitmentSub>(
            nameof(AgentLookingForGroup.RecruitmentSub.SelectedDutyId),
            DadAlliancePartyFinderApi15Layout.SelectedDutyIdOffset);
        AssertOffset<AgentLookingForGroup.RecruitmentSub>(
            nameof(AgentLookingForGroup.RecruitmentSub.Objective),
            DadAlliancePartyFinderApi15Layout.ObjectiveOffset);
        AssertOffset<AgentLookingForGroup.RecruitmentSub>(
            nameof(AgentLookingForGroup.RecruitmentSub.CompletionStatus),
            DadAlliancePartyFinderApi15Layout.CompletionStatusOffset);
        AssertOffset<AgentLookingForGroup.RecruitmentSub>(
            nameof(AgentLookingForGroup.RecruitmentSub.DutyFinderSettingFlags),
            DadAlliancePartyFinderApi15Layout.DutyFinderSettingFlagsOffset);
        AssertOffset<AgentLookingForGroup.RecruitmentSub>(
            nameof(AgentLookingForGroup.RecruitmentSub.LootRule),
            DadAlliancePartyFinderApi15Layout.LootRuleOffset);
        AssertOffset<AgentLookingForGroup.RecruitmentSub>(
            nameof(AgentLookingForGroup.RecruitmentSub.Password),
            DadAlliancePartyFinderApi15Layout.PasswordOffset);
        AssertOffset<AgentLookingForGroup.RecruitmentSub>(
            nameof(AgentLookingForGroup.RecruitmentSub.LanguageFlags),
            DadAlliancePartyFinderApi15Layout.LanguageFlagsOffset);
        AssertOffset<AgentLookingForGroup.RecruitmentSub>(
            nameof(AgentLookingForGroup.RecruitmentSub.NumberOfSlotsInMainParty),
            DadAlliancePartyFinderApi15Layout.NumberOfSlotsInMainPartyOffset);
        AssertOffset<AgentLookingForGroup.RecruitmentSub>(
            nameof(AgentLookingForGroup.RecruitmentSub.LimitRecruitingToWorld),
            DadAlliancePartyFinderApi15Layout.LimitRecruitingToWorldOffset);
        AssertOffset<AgentLookingForGroup.RecruitmentSub>(
            nameof(AgentLookingForGroup.RecruitmentSub.OnePlayerPerJob),
            DadAlliancePartyFinderApi15Layout.OnePlayerPerJobOffset);
        AssertOffset<AgentLookingForGroup.RecruitmentSub>(
            nameof(AgentLookingForGroup.RecruitmentSub.NumberOfGroups),
            DadAlliancePartyFinderApi15Layout.NumberOfGroupsOffset);
        AssertOffset<AgentLookingForGroup.RecruitmentSub>(
            "_memberContentIds",
            DadAlliancePartyFinderApi15Layout.MemberContentIdsOffset);
        AssertOffset<AgentLookingForGroup.RecruitmentSub>(
            "_slotFlags",
            DadAlliancePartyFinderApi15Layout.SlotFlagsOffset);
        AssertOffset<AgentLookingForGroup.RecruitmentSub>(
            "_comment",
            DadAlliancePartyFinderApi15Layout.CommentOffset);
    }

    [Fact]
    public void CategoryWidthAndApi15EnumValuesArePinned()
    {
        Assert.Equal(
            typeof(uint),
            Enum.GetUnderlyingType(
                typeof(AgentLookingForGroup.DutyCategory)));
        Assert.Equal(
            0x20u,
            (uint)AgentLookingForGroup.DutyCategory.Raids);

        Assert.Equal(1, (byte)AgentLookingForGroup.Objective.None);
        Assert.Equal(
            2,
            (byte)AgentLookingForGroup.Objective.DutyCompletion);
        Assert.Equal(4, (byte)AgentLookingForGroup.Objective.Practice);
        Assert.Equal(8, (byte)AgentLookingForGroup.Objective.Loot);

        Assert.Equal(
            1,
            (byte)AgentLookingForGroup.CompletionStatus.None);
        Assert.Equal(
            2,
            (byte)AgentLookingForGroup.CompletionStatus.DutyComplete);
        Assert.Equal(
            4,
            (byte)AgentLookingForGroup.CompletionStatus.DutyIncomplete);
        Assert.Equal(
            8,
            (byte)AgentLookingForGroup.CompletionStatus
                .DutyCompleteWeeklyUnclaimed);
    }

    private static void AssertOffset<T>(string field, int expected)
        where T : struct
        => Assert.Equal(
            expected,
            Marshal.OffsetOf<T>(field).ToInt32());
}
