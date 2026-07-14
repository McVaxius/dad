namespace dad.Models;

public static class DadPlannerSlotRules
{
    public const int MinSlotNumber = 1;
    public const int MaxSlotNumber = 56;
    public const string LeaderSlotId = "Slot1";

    public static string FormatSlotId(int slotNumber)
        => $"Slot{Math.Clamp(slotNumber, MinSlotNumber, MaxSlotNumber)}";

    public static bool IsLeaderSlot(string? slotId)
        => string.Equals(NormalizeStrictSlotId(slotId), LeaderSlotId, StringComparison.OrdinalIgnoreCase);

    public static bool TryParseStrictSlotNumber(string? slotId, out int slotNumber)
    {
        slotNumber = 0;
        var trimmed = slotId?.Trim() ?? string.Empty;
        if (!trimmed.StartsWith("Slot", StringComparison.OrdinalIgnoreCase))
            return false;

        var digits = trimmed[4..];
        if (digits.Length == 0 || !digits.All(char.IsDigit))
            return false;

        if (!int.TryParse(digits, out var parsed) || parsed is < MinSlotNumber or > MaxSlotNumber)
            return false;

        slotNumber = parsed;
        return true;
    }

    public static string NormalizeStrictSlotId(string? slotId)
        => TryParseStrictSlotNumber(slotId, out var slotNumber)
            ? FormatSlotId(slotNumber)
            : string.Empty;

    public static List<DadPlannerGroupSlot> NormalizeGroupSlots(IEnumerable<DadPlannerGroupSlot>? source)
    {
        var groups = new List<SlotGroup>();
        var groupsByLegacySlotNumber = new Dictionary<int, SlotGroup>();
        var primaryLegacySlotNumbers = new HashSet<int>();
        var pendingSubstitutesByLegacySlotNumber = new Dictionary<int, List<PendingSlot>>();
        var nextLegacySlotNumber = MinSlotNumber;
        var originalIndex = 0;

        foreach (var sourceSlot in source ?? [])
        {
            if (sourceSlot == null)
                continue;

            var legacySlotNumber = ResolveLegacySlotNumber(sourceSlot.SlotId, primaryLegacySlotNumbers, ref nextLegacySlotNumber);
            if (legacySlotNumber is < MinSlotNumber or > MaxSlotNumber)
            {
                originalIndex++;
                continue;
            }

            if (groupsByLegacySlotNumber.TryGetValue(legacySlotNumber, out var existingGroup))
            {
                existingGroup.Substitutes.Add(new PendingSlot(sourceSlot, originalIndex++));
                continue;
            }

            if (sourceSlot.IsSubstitute)
            {
                if (!pendingSubstitutesByLegacySlotNumber.TryGetValue(legacySlotNumber, out var pendingSubstitutes))
                {
                    pendingSubstitutes = [];
                    pendingSubstitutesByLegacySlotNumber[legacySlotNumber] = pendingSubstitutes;
                }

                pendingSubstitutes.Add(new PendingSlot(sourceSlot, originalIndex++));
                continue;
            }

            primaryLegacySlotNumbers.Add(legacySlotNumber);
            var group = new SlotGroup(legacySlotNumber, new PendingSlot(sourceSlot, originalIndex++));
            if (pendingSubstitutesByLegacySlotNumber.Remove(legacySlotNumber, out var pending))
                group.Substitutes.AddRange(pending);
            groups.Add(group);
            groupsByLegacySlotNumber[legacySlotNumber] = group;
        }

        var orderedGroups = groups
            .OrderBy(static group => group.LegacySlotNumber)
            .ThenBy(static group => group.Primary.OriginalIndex)
            .Take(MaxSlotNumber)
            .ToList();

        var normalized = new List<DadPlannerGroupSlot>();
        for (var groupIndex = 0; groupIndex < orderedGroups.Count; groupIndex++)
        {
            var slotId = FormatSlotId(groupIndex + 1);
            var group = orderedGroups[groupIndex];
            normalized.Add(CloneSlotForNormalization(group.Primary.Slot, slotId, isSubstitute: false));
            normalized.AddRange(group.Substitutes
                .OrderBy(static pending => pending.OriginalIndex)
                .Select(pending => CloneSlotForNormalization(pending.Slot, slotId, isSubstitute: true)));
        }

        return normalized;
    }

    public static List<DadPlannerGroupSlot> TakePrimarySlotsWithSubstitutes(IEnumerable<DadPlannerGroupSlot>? source, int primarySlotCap)
    {
        var cap = Math.Clamp(primarySlotCap, MinSlotNumber, MaxSlotNumber);
        return NormalizeGroupSlots(source)
            .Where(slot => TryParseStrictSlotNumber(slot.SlotId, out var slotNumber) && slotNumber <= cap)
            .ToList();
    }

    public static List<DadPlannerGroupSlot> GetPrimaryRows(IEnumerable<DadPlannerGroupSlot>? slots)
        => NormalizeGroupSlots(slots)
            .Where(static slot => !slot.IsSubstitute)
            .ToList();

    public static List<DadPlannerGroupSlot> GetRowsForSlot(IEnumerable<DadPlannerGroupSlot>? slots, string slotId)
    {
        var normalizedSlotId = NormalizeStrictSlotId(slotId);
        if (string.IsNullOrWhiteSpace(normalizedSlotId))
            return [];

        return NormalizeGroupSlots(slots)
            .Where(slot => string.Equals(slot.SlotId, normalizedSlotId, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public static int CountPrimarySlots(IEnumerable<DadPlannerGroupSlot>? slots)
        => GetPrimaryRows(slots).Count;

    public static int NextPrimarySlotNumber(IEnumerable<DadPlannerGroupSlot>? slots, int cap = MaxSlotNumber)
    {
        var clampedCap = Math.Clamp(cap, MinSlotNumber, MaxSlotNumber);
        var used = NormalizeGroupSlots(slots)
            .Where(static slot => !slot.IsSubstitute)
            .Select(static slot => TryParseStrictSlotNumber(slot.SlotId, out var slotNumber) ? slotNumber : 0)
            .Where(static slotNumber => slotNumber > 0)
            .ToHashSet();

        for (var slotNumber = MinSlotNumber; slotNumber <= clampedCap; slotNumber++)
        {
            if (!used.Contains(slotNumber))
                return slotNumber;
        }

        return 0;
    }

    public static int GetSlotSortKey(string? slotId)
        => TryParseStrictSlotNumber(slotId, out var slotNumber) ? slotNumber : int.MaxValue;

    private static int ResolveLegacySlotNumber(string? label, HashSet<int> primarySlots, ref int nextSequentialSlot)
    {
        if (TryParseLegacySlotNumber(label, out var parsed))
            return parsed;

        while (nextSequentialSlot <= MaxSlotNumber && primarySlots.Contains(nextSequentialSlot))
            nextSequentialSlot++;

        return nextSequentialSlot++;
    }

    private static bool TryParseLegacySlotNumber(string? label, out int slotNumber)
    {
        slotNumber = 0;
        var trimmed = label?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
            return false;

        if (string.Equals(trimmed, "Leader", StringComparison.OrdinalIgnoreCase))
        {
            slotNumber = MinSlotNumber;
            return true;
        }

        if (TryParseStrictSlotNumber(trimmed, out slotNumber))
            return true;

        if (!trimmed.StartsWith("Party", StringComparison.OrdinalIgnoreCase))
            return false;

        var digits = trimmed[5..].Trim();
        if (digits.Length == 0 || !digits.All(char.IsDigit))
            return false;

        if (!int.TryParse(digits, out var parsed) || parsed is < MinSlotNumber or > MaxSlotNumber)
            return false;

        slotNumber = parsed;
        return true;
    }

    private static DadPlannerGroupSlot CloneSlotForNormalization(
        DadPlannerGroupSlot source,
        string slotId,
        bool isSubstitute)
        => new()
        {
            SlotId = slotId,
            IsSubstitute = isSubstitute,
            RequiredRole = source.RequiredRole,
            RequiredAccountKey = source.RequiredAccountKey,
            RequiredCharacterKey = source.RequiredCharacterKey,
            RequiredJobId = source.RequiredJobId,
            AdsLootMode = source.AdsLootMode,
            LevelSeekTarget = source.LevelSeekTarget is > 0 ? source.LevelSeekTarget : null,
            WakePolicy = source.WakePolicy,
            LaunchProfileId = source.LaunchProfileId?.Trim() ?? string.Empty,
            CharacterLoadInstruction = source.CharacterLoadInstruction?.Clone() ?? new DadCharacterLoadInstruction(),
            AllowSubstitution = source.AllowSubstitution,
        };

    private sealed class SlotGroup(int legacySlotNumber, PendingSlot primary)
    {
        public int LegacySlotNumber { get; } = legacySlotNumber;
        public PendingSlot Primary { get; } = primary;
        public List<PendingSlot> Substitutes { get; } = [];
    }

    private readonly record struct PendingSlot(DadPlannerGroupSlot Slot, int OriginalIndex);
}
