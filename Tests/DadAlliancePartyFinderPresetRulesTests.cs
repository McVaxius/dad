using System.Buffers.Binary;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadAlliancePartyFinderPresetRulesTests
{
    [Fact]
    public void CaptureAndBuildRoundTripTheCompleteApi15State()
    {
        var preparedBytes = CreatePreparedRecruitment();
        var captured = DadAlliancePartyFinderPresetRules.Capture(
            preparedBytes,
            groupTypeTab: 1,
            avgItemLvEnabled: 1,
            avgItemLv: 735);
        preparedBytes.AsSpan().Fill(0xEE);

        var preset = DadAlliancePartyFinderPresetRules.Build(
            captured,
            9752);

        Assert.Equal(
            DadAlliancePartyFinderApi15Layout.RecruitmentSubSize,
            preset.RecruitmentSub.Length);
        Assert.Equal(1, preset.GroupTypeTab);
        Assert.Equal(1, preset.AvgItemLvEnabled);
        Assert.Equal(735, preset.AvgItemLv);
        Assert.Equal(
            DadAlliancePartyFinderPresetDefinition.RaidsCategoryMask,
            ReadUInt32(
                preset.RecruitmentSub,
                DadAlliancePartyFinderApi15Layout.SelectedCategoryOffset));
        Assert.Equal(
            DadAlliancePartyFinderPresetDefinition.LabyrinthDutyId,
            ReadUInt16(
                preset.RecruitmentSub,
                DadAlliancePartyFinderApi15Layout.SelectedDutyIdOffset));
        Assert.Equal(
            9752,
            ReadUInt16(
                preset.RecruitmentSub,
                DadAlliancePartyFinderApi15Layout.PasswordOffset));
        Assert.Equal(
            DadAlliancePartyFinderPresetDefinition.SlotsPerAllianceGroup,
            preset.RecruitmentSub[
                DadAlliancePartyFinderApi15Layout.NumberOfSlotsInMainPartyOffset]);
        Assert.Equal(
            DadAlliancePartyFinderPresetDefinition.AllianceGroupCount,
            preset.RecruitmentSub[
                DadAlliancePartyFinderApi15Layout.NumberOfGroupsOffset]);
    }

    [Fact]
    public void BuildPreservesOpaqueSelectorAndCurrentOptionValues()
    {
        var prepared = CreatePreparedRecruitment();
        var opaqueBefore = prepared.AsSpan(
            DadAlliancePartyFinderApi15Layout.OpaqueDutySelectorOffset,
            DadAlliancePartyFinderApi15Layout.OpaqueDutySelectorLength).ToArray();
        var objective = prepared[
            DadAlliancePartyFinderApi15Layout.ObjectiveOffset];
        var completion = prepared[
            DadAlliancePartyFinderApi15Layout.CompletionStatusOffset];
        var dutyFinderSettings = prepared[
            DadAlliancePartyFinderApi15Layout.DutyFinderSettingFlagsOffset];
        var lootRule = prepared[
            DadAlliancePartyFinderApi15Layout.LootRuleOffset];
        var languages = prepared[
            DadAlliancePartyFinderApi15Layout.LanguageFlagsOffset];
        var preset = DadAlliancePartyFinderPresetRules.Build(
            Capture(prepared),
            9752);

        Assert.Equal(
            opaqueBefore,
            preset.RecruitmentSub.AsSpan(
                DadAlliancePartyFinderApi15Layout.OpaqueDutySelectorOffset,
                DadAlliancePartyFinderApi15Layout.OpaqueDutySelectorLength).ToArray());
        Assert.Equal(
            objective,
            preset.RecruitmentSub[
                DadAlliancePartyFinderApi15Layout.ObjectiveOffset]);
        Assert.Equal(
            completion,
            preset.RecruitmentSub[
                DadAlliancePartyFinderApi15Layout.CompletionStatusOffset]);
        Assert.Equal(
            dutyFinderSettings,
            preset.RecruitmentSub[
                DadAlliancePartyFinderApi15Layout.DutyFinderSettingFlagsOffset]);
        Assert.Equal(
            lootRule,
            preset.RecruitmentSub[
                DadAlliancePartyFinderApi15Layout.LootRuleOffset]);
        Assert.Equal(
            languages,
            preset.RecruitmentSub[
                DadAlliancePartyFinderApi15Layout.LanguageFlagsOffset]);
    }

    [Fact]
    public void BuildChangesOnlyTheApprovedOverlayBytes()
    {
        var prepared = CreatePreparedRecruitment();
        var preset = DadAlliancePartyFinderPresetRules.Build(
            Capture(prepared),
            9752);

        for (var index = 0; index < prepared.Length; index++)
        {
            if (!IsApprovedOverlayByte(index))
            {
                Assert.Equal(
                    prepared[index],
                    preset.RecruitmentSub[index]);
            }
        }
    }

    [Fact]
    public void BuildPreservesSlotZeroAndClearsEveryStaleMember()
    {
        var prepared = CreatePreparedRecruitment();
        var localMember = ReadUInt64(
            prepared,
            DadAlliancePartyFinderApi15Layout.MemberContentIdsOffset);
        var localSlot = ReadUInt64(
            prepared,
            DadAlliancePartyFinderApi15Layout.SlotFlagsOffset);
        var preset = DadAlliancePartyFinderPresetRules.Build(
            Capture(prepared),
            9752);

        Assert.Equal(
            localMember,
            ReadUInt64(
                preset.RecruitmentSub,
                DadAlliancePartyFinderApi15Layout.MemberContentIdsOffset));
        Assert.Equal(
            localSlot,
            ReadUInt64(
                preset.RecruitmentSub,
                DadAlliancePartyFinderApi15Layout.SlotFlagsOffset));
        for (var index = 1;
             index < DadAlliancePartyFinderPresetDefinition.SlotCount;
             index++)
        {
            Assert.Equal(
                0ul,
                ReadUInt64(
                    preset.RecruitmentSub,
                    DadAlliancePartyFinderApi15Layout.MemberContentIdsOffset +
                    (index * sizeof(ulong))));
        }
    }

    [Fact]
    public void BuildOpensExactlyTwentyThreeSlotsAndClearsUnusedGroups()
    {
        var preset = DadAlliancePartyFinderPresetRules.Build(
            Capture(CreatePreparedRecruitment()),
            9752);

        for (var index = 1; index < 24; index++)
        {
            Assert.Equal(
                DadAlliancePartyFinderPresetDefinition.AllJobsOpenSlotFlag,
                ReadUInt64(
                    preset.RecruitmentSub,
                    DadAlliancePartyFinderApi15Layout.SlotFlagsOffset +
                    (index * sizeof(ulong))));
        }
        for (var index = 24; index < 48; index++)
        {
            Assert.Equal(
                0ul,
                ReadUInt64(
                    preset.RecruitmentSub,
                    DadAlliancePartyFinderApi15Layout.SlotFlagsOffset +
                    (index * sizeof(ulong))));
        }
        Assert.All(
            preset.RecruitmentSub.Skip(
                DadAlliancePartyFinderApi15Layout.CommentOffset).Take(
                DadAlliancePartyFinderApi15Layout.CommentLength),
            static value => Assert.Equal(0, value));
    }

    [Fact]
    public void BuildIsIdempotent()
    {
        var first = DadAlliancePartyFinderPresetRules.Build(
            Capture(CreatePreparedRecruitment()),
            9752);
        var second = DadAlliancePartyFinderPresetRules.Build(
            first,
            9752);

        Assert.Equal(first.GroupTypeTab, second.GroupTypeTab);
        Assert.Equal(first.AvgItemLvEnabled, second.AvgItemLvEnabled);
        Assert.Equal(first.AvgItemLv, second.AvgItemLv);
        Assert.Equal(first.RecruitmentSub, second.RecruitmentSub);
    }

    [Fact]
    public void PresetLoaderContractOwnsExplicitLifecycleDisposal()
    {
        Assert.True(
            typeof(IDisposable).IsAssignableFrom(
                typeof(IDadAlliancePartyFinderPresetLoader)));
    }

    [Theory]
    [InlineData(999)]
    [InlineData(10000)]
    public void BuildRejectsNonFourDigitPasscodes(int passcode)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DadAlliancePartyFinderPresetRules.Build(
                Capture(CreatePreparedRecruitment()),
                passcode));
    }

    [Fact]
    public void CaptureRequiresExactApi15Size()
    {
        Assert.Throws<ArgumentException>(() =>
            DadAlliancePartyFinderPresetRules.Capture(
                new byte[
                    DadAlliancePartyFinderApi15Layout.RecruitmentSubSize - 1],
                1,
                0,
                0));
        Assert.Throws<ArgumentException>(() =>
            DadAlliancePartyFinderPresetRules.Capture(
                new byte[
                    DadAlliancePartyFinderApi15Layout.RecruitmentSubSize + 1],
                1,
                0,
                0));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void BuildRequiresPreparedGameOwnedRaidsAndLabyrinth(
        bool categoryExact,
        bool dutyExact)
    {
        var bytes = CreatePreparedRecruitment();
        WriteUInt32(
            bytes,
            DadAlliancePartyFinderApi15Layout.SelectedCategoryOffset,
            categoryExact
                ? DadAlliancePartyFinderPresetDefinition.RaidsCategoryMask
                : 0);
        WriteUInt16(
            bytes,
            DadAlliancePartyFinderApi15Layout.SelectedDutyIdOffset,
            dutyExact
                ? DadAlliancePartyFinderPresetDefinition.LabyrinthDutyId
                : (ushort)1117);

        Assert.Throws<InvalidOperationException>(() =>
            DadAlliancePartyFinderPresetRules.Build(
                Capture(bytes),
                9752));
    }

    [Fact]
    public void BuildRequiresPreparedAllianceTab()
    {
        var captured = DadAlliancePartyFinderPresetRules.Capture(
            CreatePreparedRecruitment(),
            groupTypeTab: 0,
            avgItemLvEnabled: 1,
            avgItemLv: 735);

        Assert.Throws<InvalidOperationException>(() =>
            DadAlliancePartyFinderPresetRules.Build(
                captured,
                9752));
    }

    [Fact]
    public void SuccessfulTransactionAppliesAndRefreshesExactlyOnce()
    {
        var calls = new List<string>();

        var result = DadAlliancePartyFinderPresetTransaction.Execute(
            () => calls.Add("apply"),
            () => calls.Add("refresh"),
            () => calls.Add("rollback"));

        Assert.True(result.Success);
        Assert.Equal(["apply", "refresh"], calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FailedTransactionRestoresCompleteOriginalState(
        bool failDuringApply)
    {
        var original = Capture(CreatePreparedRecruitment()) with
        {
            AvgItemLvEnabled = 1,
            AvgItemLv = 735,
        };
        var preset = DadAlliancePartyFinderPresetRules.Build(
            original,
            9752);
        var state = original;

        var result = DadAlliancePartyFinderPresetTransaction.Execute(
            apply: () =>
            {
                state = preset;
                if (failDuringApply)
                    throw new InvalidOperationException("apply failed");
            },
            refresh: () =>
                throw new InvalidOperationException("refresh failed"),
            rollback: () => state = original);

        Assert.False(result.Success);
        Assert.Same(original, state);
        Assert.Equal(1, state.GroupTypeTab);
        Assert.Equal(1, state.AvgItemLvEnabled);
        Assert.Equal(735, state.AvgItemLv);
        Assert.Equal(original.RecruitmentSub, state.RecruitmentSub);
        Assert.Contains(
            failDuringApply ? "apply failed" : "refresh failed",
            result.Error);
    }

    [Fact]
    public void RollbackFailureRemainsVisible()
    {
        var result = DadAlliancePartyFinderPresetTransaction.Execute(
            apply: static () => { },
            refresh: static () =>
                throw new InvalidOperationException("refresh failed"),
            rollback: static () =>
                throw new InvalidOperationException("rollback failed"));

        Assert.False(result.Success);
        Assert.Contains("refresh failed", result.Error);
        Assert.Contains("Rollback also failed", result.Error);
        Assert.Contains("rollback failed", result.Error);
    }

    private static DadAlliancePartyFinderApi15PresetState Capture(
        byte[] bytes)
        => DadAlliancePartyFinderPresetRules.Capture(
            bytes,
            groupTypeTab: 1,
            avgItemLvEnabled: 1,
            avgItemLv: 735);

    private static byte[] CreatePreparedRecruitment()
    {
        var bytes = Enumerable.Range(
                0,
                DadAlliancePartyFinderApi15Layout.RecruitmentSubSize)
            .Select(static index => (byte)((index * 37) + 11))
            .ToArray();
        WriteUInt32(
            bytes,
            DadAlliancePartyFinderApi15Layout.SelectedCategoryOffset,
            DadAlliancePartyFinderPresetDefinition.RaidsCategoryMask);
        WriteUInt16(
            bytes,
            DadAlliancePartyFinderApi15Layout.SelectedDutyIdOffset,
            DadAlliancePartyFinderPresetDefinition.LabyrinthDutyId);
        for (var index = 0;
             index < DadAlliancePartyFinderPresetDefinition.SlotCount;
             index++)
        {
            WriteUInt64(
                bytes,
                DadAlliancePartyFinderApi15Layout.MemberContentIdsOffset +
                (index * sizeof(ulong)),
                (ulong)(1000 + index));
            WriteUInt64(
                bytes,
                DadAlliancePartyFinderApi15Layout.SlotFlagsOffset +
                (index * sizeof(ulong)),
                (ulong)(2000 + index));
        }
        return bytes;
    }

    private static bool IsApprovedOverlayByte(int index)
        => index is >= DadAlliancePartyFinderApi15Layout.PasswordOffset
               and < DadAlliancePartyFinderApi15Layout.PasswordOffset +
                     sizeof(ushort) ||
           index == DadAlliancePartyFinderApi15Layout
               .NumberOfSlotsInMainPartyOffset ||
           index == DadAlliancePartyFinderApi15Layout
               .LimitRecruitingToWorldOffset ||
           index == DadAlliancePartyFinderApi15Layout
               .OnePlayerPerJobOffset ||
           index == DadAlliancePartyFinderApi15Layout.NumberOfGroupsOffset ||
           index is >= DadAlliancePartyFinderApi15Layout
                          .MemberContentIdsOffset + sizeof(ulong)
               and < DadAlliancePartyFinderApi15Layout.SlotFlagsOffset ||
           index is >= DadAlliancePartyFinderApi15Layout.SlotFlagsOffset +
                          sizeof(ulong)
               and < DadAlliancePartyFinderApi15Layout.CommentOffset ||
           index is >= DadAlliancePartyFinderApi15Layout.CommentOffset
               and < DadAlliancePartyFinderApi15Layout.CommentOffset +
                     DadAlliancePartyFinderApi15Layout.CommentLength;

    private static ushort ReadUInt16(byte[] bytes, int offset)
        => BinaryPrimitives.ReadUInt16LittleEndian(
            bytes.AsSpan(offset, sizeof(ushort)));

    private static uint ReadUInt32(byte[] bytes, int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(offset, sizeof(uint)));

    private static ulong ReadUInt64(byte[] bytes, int offset)
        => BinaryPrimitives.ReadUInt64LittleEndian(
            bytes.AsSpan(offset, sizeof(ulong)));

    private static void WriteUInt16(
        byte[] bytes,
        int offset,
        ushort value)
        => BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(offset, sizeof(ushort)),
            value);

    private static void WriteUInt32(
        byte[] bytes,
        int offset,
        uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(offset, sizeof(uint)),
            value);

    private static void WriteUInt64(
        byte[] bytes,
        int offset,
        ulong value)
        => BinaryPrimitives.WriteUInt64LittleEndian(
            bytes.AsSpan(offset, sizeof(ulong)),
            value);
}
