using System.Security.Cryptography;
using dad.Models;

namespace dad.Services;

public static class DadAlliancePartyFinderRules
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(15),
    ];

    public const int MinimumAllianceSize = 1;
    public const int MaximumAllianceSize = 8;
    public const int MinimumTotalSize = 3;
    public const int MaximumTotalSize = 24;

    public static DadAlliancePresetValidation ValidateSavedRows(
        IEnumerable<DadPlannerGroupSlot>? rows)
    {
        var primary = DadPlannerSlotRules.GetPrimaryRows(rows);
        return ValidateAssignments(
            primary.Select(slot => (slot.SlotId, slot.AllianceAssignment, HasIdentity: true)),
            hostCharacterKey: null);
    }

    public static DadAlliancePresetValidation ValidateEffectiveSlots(
        IEnumerable<DadPresetCharacterSlot>? slots,
        DadCharacterKey? hostCharacterKey = null)
    {
        var effective = (slots ?? [])
            .Where(static slot => slot != null)
            .ToList();
        return ValidateAssignments(
            effective.Select(slot => (
                slot.SlotId,
                slot.AllianceAssignment,
                HasIdentity: !string.IsNullOrWhiteSpace(slot.CharacterKey) &&
                             slot.ContentId.GetValueOrDefault() != 0)),
            hostCharacterKey,
            effective);
    }

    public static bool CanAssign(
        IEnumerable<DadPlannerGroupSlot>? rows,
        string slotId,
        DadAllianceAssignment assignment)
    {
        if (assignment == DadAllianceAssignment.None)
            return true;
        if (!IsConcreteAssignment(assignment))
            return false;

        var normalizedSlotId = DadPlannerSlotRules.NormalizeStrictSlotId(slotId);
        return DadPlannerSlotRules.GetPrimaryRows(rows)
            .Count(slot =>
                !string.Equals(slot.SlotId, normalizedSlotId, StringComparison.OrdinalIgnoreCase) &&
                slot.AllianceAssignment == assignment) < MaximumAllianceSize;
    }

    public static bool IsConcreteAssignment(DadAllianceAssignment assignment)
        => assignment is DadAllianceAssignment.A
            or DadAllianceAssignment.B
            or DadAllianceAssignment.C;

    public static int GeneratePasscode(Func<int, int, int>? randomInt32 = null)
        => (randomInt32 ?? RandomNumberGenerator.GetInt32)(1000, 10000);

    public static TimeSpan GetRetryDelay(int completedAttempts)
        => RetryDelays[Math.Clamp(completedAttempts, 0, RetryDelays.Length - 1)];

    public static int GetJoinAllianceButtonIndex(DadAllianceAssignment assignment)
        => assignment switch
        {
            DadAllianceAssignment.A => 0,
            DadAllianceAssignment.B => 1,
            DadAllianceAssignment.C => 2,
            _ => -1,
        };

    public static DadAllianceAssignment FromCrossRealmGroupIndex(int groupIndex)
        => groupIndex switch
        {
            0 => DadAllianceAssignment.A,
            1 => DadAllianceAssignment.B,
            2 => DadAllianceAssignment.C,
            _ => DadAllianceAssignment.None,
        };

    public static bool IsExactListingMatch(
        string? observedLeaderName,
        string? observedLeaderWorld,
        string? expectedLeaderName,
        string? expectedLeaderWorld)
        => string.Equals(
               NormalizeIdentity(observedLeaderName),
               NormalizeIdentity(expectedLeaderName),
               StringComparison.OrdinalIgnoreCase) &&
           string.Equals(
               NormalizeIdentity(observedLeaderWorld),
               NormalizeIdentity(expectedLeaderWorld),
               StringComparison.OrdinalIgnoreCase);

    public static string ValidateInstruction(DadAllianceRecruitmentInstructionDto? instruction)
    {
        if (instruction == null)
            return "Alliance recruitment instruction is missing.";
        if (instruction.SchemaVersion != DadAllianceRecruitmentInstructionDto.CurrentSchemaVersion)
            return $"Alliance recruitment instruction schema {instruction.SchemaVersion} is unsupported.";
        if (!Guid.TryParseExact(instruction.RecruitmentId, "N", out _))
            return "Alliance recruitment ID is invalid.";
        if (instruction.CoordinatorWorkerSessionId.IsEmpty)
            return "Coordinator worker identity is missing.";
        if (string.IsNullOrWhiteSpace(instruction.CoordinatorIdentity))
            return "Coordinator identity is missing.";
        if (instruction.TargetWorkerSessionId.IsEmpty)
            return "Target worker identity is missing.";
        if (instruction.TargetCharacterKey.IsEmpty ||
            string.IsNullOrWhiteSpace(instruction.TargetCharacterName) ||
            string.IsNullOrWhiteSpace(instruction.TargetCharacterWorld) ||
            instruction.TargetContentId == 0)
        {
            return "Exact target character identity is incomplete.";
        }
        if (!string.Equals(
                instruction.TargetCharacterKey.Value.Trim(),
                $"{instruction.TargetCharacterName.Trim()}@{instruction.TargetCharacterWorld.Trim()}",
                StringComparison.OrdinalIgnoreCase))
        {
            return "Exact target character name/world contradicts its character key.";
        }
        if (string.IsNullOrWhiteSpace(instruction.LeaderName) ||
            string.IsNullOrWhiteSpace(instruction.LeaderWorld))
        {
            return "Exact Party Finder leader identity is incomplete.";
        }
        if (!IsConcreteAssignment(instruction.AssignedAlliance))
            return "A concrete A/B/C assignment is required.";
        if (instruction.Passcode is < 1000 or > 9999)
            return "The Party Finder passcode must be exactly four digits.";
        if (instruction.Attempt < 0 || instruction.StopGeneration < 0)
            return "Attempt and Stop generation must be non-negative.";
        return string.Empty;
    }

    public static string BuildDedupeKey(string? recruitmentId, DadCharacterKey targetCharacterKey)
        => $"{(recruitmentId ?? string.Empty).Trim()}|{targetCharacterKey.Value.Trim()}";

    private static DadAlliancePresetValidation ValidateAssignments(
        IEnumerable<(string SlotId, DadAllianceAssignment Assignment, bool HasIdentity)> source,
        DadCharacterKey? hostCharacterKey,
        IReadOnlyList<DadPresetCharacterSlot>? effectiveSlots = null)
    {
        var rows = source.ToList();
        var blockers = new List<string>();
        var invalidAssignments = rows
            .Where(static row => !Enum.IsDefined(row.Assignment))
            .Select(static row => row.SlotId)
            .ToList();
        if (invalidAssignments.Count > 0)
            blockers.Add($"Invalid alliance value on {string.Join(", ", invalidAssignments)}.");

        var unassigned = rows
            .Where(static row => row.Assignment == DadAllianceAssignment.None)
            .Select(static row => row.SlotId)
            .ToList();
        if (unassigned.Count > 0)
            blockers.Add($"Assign A, B, or C to {string.Join(", ", unassigned)}.");

        var unresolved = rows
            .Where(static row => !row.HasIdentity)
            .Select(static row => row.SlotId)
            .ToList();
        if (unresolved.Count > 0)
            blockers.Add($"Resolve an online exact character for {string.Join(", ", unresolved)}.");

        var a = rows.Count(static row => row.Assignment == DadAllianceAssignment.A);
        var b = rows.Count(static row => row.Assignment == DadAllianceAssignment.B);
        var c = rows.Count(static row => row.Assignment == DadAllianceAssignment.C);
        ValidateCount("A", a, blockers);
        ValidateCount("B", b, blockers);
        ValidateCount("C", c, blockers);

        var total = a + b + c;
        if (total is < MinimumTotalSize or > MaximumTotalSize)
            blockers.Add($"Alliance recruitment requires {MinimumTotalSize}-{MaximumTotalSize} effective characters; found {total}.");

        if (effectiveSlots != null)
        {
            var duplicateCharacters = effectiveSlots
                .Where(static slot => !string.IsNullOrWhiteSpace(slot.CharacterKey))
                .GroupBy(static slot => slot.CharacterKey.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(static group => group.Count() > 1)
                .Select(static group => group.Key)
                .ToList();
            if (duplicateCharacters.Count > 0)
                blockers.Add("One character cannot occupy multiple alliance slots.");

            if (hostCharacterKey is { IsEmpty: false } host)
            {
                var hostSlots = effectiveSlots
                    .Where(slot => string.Equals(
                        slot.CharacterKey?.Trim(),
                        host.Value.Trim(),
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (hostSlots.Count != 1)
                    blockers.Add("The current PF creator must resolve to exactly one effective preset slot.");
                else if (hostSlots[0].AllianceAssignment != DadAllianceAssignment.A)
                    blockers.Add("The current PF creator must be assigned to Alliance A.");
            }
        }

        var summary = blockers.Count == 0
            ? $"Ready: A {a}/8, B {b}/8, C {c}/8 ({total} total)."
            : $"Blocked: A {a}/8, B {b}/8, C {c}/8 ({total} total). {string.Join(" ", blockers)}";
        return new DadAlliancePresetValidation
        {
            AllianceACount = a,
            AllianceBCount = b,
            AllianceCCount = c,
            Blockers = blockers,
            Summary = summary,
        };
    }

    private static void ValidateCount(string alliance, int count, ICollection<string> blockers)
    {
        if (count < MinimumAllianceSize)
            blockers.Add($"Alliance {alliance} requires at least one character.");
        else if (count > MaximumAllianceSize)
            blockers.Add($"Alliance {alliance} cannot exceed eight characters.");
    }

    private static string NormalizeIdentity(string? value)
        => value?.Trim() ?? string.Empty;
}

public sealed class DadAllianceDeliveryDedupe
{
    private readonly object gate = new();
    private readonly Dictionary<string, long> acceptedGenerations = new(StringComparer.OrdinalIgnoreCase);

    public bool TryAccept(
        string recruitmentId,
        DadCharacterKey targetCharacterKey,
        long stopGeneration)
    {
        var key = DadAlliancePartyFinderRules.BuildDedupeKey(recruitmentId, targetCharacterKey);
        lock (gate)
        {
            if (acceptedGenerations.TryGetValue(key, out var accepted) && accepted >= stopGeneration)
                return false;
            acceptedGenerations[key] = stopGeneration;
            if (acceptedGenerations.Count > 4096)
                acceptedGenerations.Remove(acceptedGenerations.Keys.First());
            return true;
        }
    }

    public void Reset(string recruitmentId, DadCharacterKey targetCharacterKey)
    {
        var key = DadAlliancePartyFinderRules.BuildDedupeKey(recruitmentId, targetCharacterKey);
        lock (gate)
            acceptedGenerations.Remove(key);
    }
}
