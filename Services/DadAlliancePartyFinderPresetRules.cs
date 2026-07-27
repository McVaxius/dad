using System.Buffers.Binary;

namespace dad.Services;

internal static class DadAlliancePartyFinderApi15Layout
{
    public const int AgentLookingForGroupSize = 0x36D8;
    public const int AvgItemLvOffset = 0x14E4;
    public const int AvgItemLvEnabledOffset = 0x14E6;
    public const int StoredRecruitmentInfoOffset = 0x2828;
    public const int GroupTypeTabOffset = 0x36BC;

    public const int RecruitmentSubSize = 0x478;
    public const int SelectedCategoryOffset = 0x0C;
    public const int SelectedDutyIdOffset = 0x10;
    public const int OpaqueDutySelectorOffset = 0x12;
    public const int OpaqueDutySelectorLength = 0x06;
    public const int ObjectiveOffset = 0x18;
    public const int CompletionStatusOffset = 0x1A;
    public const int DutyFinderSettingFlagsOffset = 0x1B;
    public const int LootRuleOffset = 0x1C;
    public const int PasswordOffset = 0x22;
    public const int LanguageFlagsOffset = 0x24;
    public const int NumberOfSlotsInMainPartyOffset = 0x25;
    public const int LimitRecruitingToWorldOffset = 0x26;
    public const int OnePlayerPerJobOffset = 0x27;
    public const int NumberOfGroupsOffset = 0x28;
    public const int MemberContentIdsOffset = 0x30;
    public const int SlotFlagsOffset = 0x1B0;
    public const int CommentOffset = 0x330;
    public const int CommentLength = 192;
}

internal sealed record DadAlliancePartyFinderApi15PresetState
{
    public required byte[] RecruitmentSub { get; init; }
    public byte GroupTypeTab { get; init; }
    public byte AvgItemLvEnabled { get; init; }
    public ushort AvgItemLv { get; init; }
}

internal sealed record DadAlliancePartyFinderPresetDefinition
{
    public const int SlotCount = 48;
    public const byte AllianceGroupTypeTab = 1;
    public const uint RaidsCategoryMask = 0x20;
    public const ushort LabyrinthDutyId = 92;
    public const byte AllianceGroupCount = 3;
    public const byte SlotsPerAllianceGroup = 8;
    public const ulong AllJobsOpenSlotFlag = 0xFFFFFFFE;
}

internal static class DadAlliancePartyFinderPresetRules
{
    public static DadAlliancePartyFinderApi15PresetState Capture(
        ReadOnlySpan<byte> recruitmentSub,
        byte groupTypeTab,
        byte avgItemLvEnabled,
        ushort avgItemLv)
    {
        RequireExactSize(recruitmentSub);
        return new DadAlliancePartyFinderApi15PresetState
        {
            RecruitmentSub = recruitmentSub.ToArray(),
            GroupTypeTab = groupTypeTab,
            AvgItemLvEnabled = avgItemLvEnabled,
            AvgItemLv = avgItemLv,
        };
    }

    public static DadAlliancePartyFinderApi15PresetState Build(
        DadAlliancePartyFinderApi15PresetState prepared,
        int passcode)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(prepared.RecruitmentSub);
        RequireExactSize(prepared.RecruitmentSub);
        if (passcode is < 1000 or > 9999)
        {
            throw new ArgumentOutOfRangeException(
                nameof(passcode),
                passcode,
                "The Party Finder passcode must be exactly four digits.");
        }
        if (prepared.GroupTypeTab !=
            DadAlliancePartyFinderPresetDefinition.AllianceGroupTypeTab)
        {
            throw new InvalidOperationException(
                "The game-owned Party Finder state is not on the Alliance recruitment tab.");
        }

        var recruitmentSub = prepared.RecruitmentSub.ToArray();
        var selectedCategory = BinaryPrimitives.ReadUInt32LittleEndian(
            recruitmentSub.AsSpan(
                DadAlliancePartyFinderApi15Layout.SelectedCategoryOffset,
                sizeof(uint)));
        var selectedDutyId = BinaryPrimitives.ReadUInt16LittleEndian(
            recruitmentSub.AsSpan(
                DadAlliancePartyFinderApi15Layout.SelectedDutyIdOffset,
                sizeof(ushort)));
        if (selectedCategory !=
            DadAlliancePartyFinderPresetDefinition.RaidsCategoryMask ||
            selectedDutyId !=
            DadAlliancePartyFinderPresetDefinition.LabyrinthDutyId)
        {
            throw new InvalidOperationException(
                $"The game-owned Party Finder selector is not the exact Raids/Labyrinth state " +
                $"(category 0x{selectedCategory:X}, duty {selectedDutyId}).");
        }

        BinaryPrimitives.WriteUInt16LittleEndian(
            recruitmentSub.AsSpan(
                DadAlliancePartyFinderApi15Layout.PasswordOffset,
                sizeof(ushort)),
            checked((ushort)passcode));
        recruitmentSub[
            DadAlliancePartyFinderApi15Layout.NumberOfSlotsInMainPartyOffset] =
            DadAlliancePartyFinderPresetDefinition.SlotsPerAllianceGroup;
        recruitmentSub[
            DadAlliancePartyFinderApi15Layout.LimitRecruitingToWorldOffset] = 0;
        recruitmentSub[
            DadAlliancePartyFinderApi15Layout.OnePlayerPerJobOffset] = 0;
        recruitmentSub[
            DadAlliancePartyFinderApi15Layout.NumberOfGroupsOffset] =
            DadAlliancePartyFinderPresetDefinition.AllianceGroupCount;
        recruitmentSub.AsSpan(
            DadAlliancePartyFinderApi15Layout.CommentOffset,
            DadAlliancePartyFinderApi15Layout.CommentLength).Clear();

        // The local member and its current role flag stay in Alliance A slot zero.
        // Every other member is stale in the solo-only creation contract.
        recruitmentSub.AsSpan(
            DadAlliancePartyFinderApi15Layout.MemberContentIdsOffset +
            sizeof(ulong),
            (DadAlliancePartyFinderPresetDefinition.SlotCount - 1) *
            sizeof(ulong)).Clear();
        for (var index = 1; index < 24; index++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(
                recruitmentSub.AsSpan(
                    DadAlliancePartyFinderApi15Layout.SlotFlagsOffset +
                    (index * sizeof(ulong)),
                    sizeof(ulong)),
                DadAlliancePartyFinderPresetDefinition.AllJobsOpenSlotFlag);
        }
        recruitmentSub.AsSpan(
            DadAlliancePartyFinderApi15Layout.SlotFlagsOffset +
            (24 * sizeof(ulong)),
            24 * sizeof(ulong)).Clear();

        return prepared with { RecruitmentSub = recruitmentSub };
    }

    private static void RequireExactSize(ReadOnlySpan<byte> recruitmentSub)
    {
        if (recruitmentSub.Length !=
            DadAlliancePartyFinderApi15Layout.RecruitmentSubSize)
        {
            throw new ArgumentException(
                $"The API-15 Party Finder recruitment snapshot must contain exactly " +
                $"{DadAlliancePartyFinderApi15Layout.RecruitmentSubSize} bytes.",
                nameof(recruitmentSub));
        }
    }
}

internal readonly record struct DadAlliancePartyFinderPresetTransactionResult(
    bool Success,
    string Error);

internal static class DadAlliancePartyFinderPresetTransaction
{
    public static DadAlliancePartyFinderPresetTransactionResult Execute(
        Action apply,
        Action refresh,
        Action rollback)
    {
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentNullException.ThrowIfNull(refresh);
        ArgumentNullException.ThrowIfNull(rollback);

        try
        {
            apply();
            refresh();
            return new DadAlliancePartyFinderPresetTransactionResult(
                true,
                string.Empty);
        }
        catch (Exception exception)
        {
            try
            {
                rollback();
            }
            catch (Exception rollbackException)
            {
                return new DadAlliancePartyFinderPresetTransactionResult(
                    false,
                    $"{exception.Message} Rollback also failed: {rollbackException.Message}");
            }

            return new DadAlliancePartyFinderPresetTransactionResult(
                false,
                exception.Message);
        }
    }
}

internal interface IDadAlliancePartyFinderPresetLoader : IDisposable
{
    bool IsAvailable { get; }
    string UnavailableReason { get; }
    DadAlliancePfCreateActionResult Apply(int passcode);
}
