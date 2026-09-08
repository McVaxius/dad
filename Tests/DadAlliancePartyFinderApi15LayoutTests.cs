extern alias DalamudApi;

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using dad.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Xunit;
using ConditionFlag =
    DalamudApi::Dalamud.Game.ClientState.Conditions.ConditionFlag;

namespace dad.Tests;

public sealed class DadAlliancePartyFinderApi15LayoutTests
{
    // Keep the CI archive baseline and the reviewed 15.0.3.2 development references paired.
    // https://github.com/goatcorp/Dalamud/tree/83042016d0e9996dc44c9f7fd96a8d33a5e586f2/lib/FFXIVClientStructs
    // The newer AgentLookingForGroup adds a method; the tested layouts and condition values are unchanged.
    private static readonly (Version DalamudVersion, string ClientStructsCommit)[] ReviewedApi15Builds =
    [
        (new Version(15, 0, 3, 0), "cc474ca90dce0824334544ad7ec7d769f3cb6ee5"),
        (new Version(15, 0, 3, 2), "50e46a849ce2b2ada83e8fe4209c50b6a34d7695"),
    ];

    [Fact]
    public void InstalledDalamudIdentityIsReviewedApi15Baseline()
        => Assert.Contains(
            ReviewedApi15Builds,
            build => build.DalamudVersion == typeof(ConditionFlag).Assembly.GetName().Version);

    [Fact]
    public void InstalledClientStructsIdentityIsPinned()
    {
        var baseline = Assert.Single(
            ReviewedApi15Builds,
            build => build.DalamudVersion == typeof(ConditionFlag).Assembly.GetName().Version);
        var assembly =
            typeof(AgentLookingForGroup).Assembly;
        var productVersion = FileVersionInfo.GetVersionInfo(
            assembly.Location).ProductVersion;

        Assert.NotNull(productVersion);
        Assert.Contains(
            baseline.ClientStructsCommit,
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

    [Fact]
    [SupportedOSPlatform("windows7.0")]
    public void PartyFinderConditionValuesArePinned()
    {
        Assert.Equal(66, (int)ConditionFlag.UsingPartyFinder);
        Assert.Equal(
            84,
            (int)ConditionFlag
                .ParticipatingInCrossWorldPartyOrAlliance);
    }

    private static void AssertOffset<T>(string field, int expected)
        where T : struct
        => Assert.Equal(
            expected,
            Marshal.OffsetOf<T>(field).ToInt32());
}
