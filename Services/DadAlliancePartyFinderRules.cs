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
            or DadAllianceAssignment.C
            or DadAllianceAssignment.D
            or DadAllianceAssignment.E
            or DadAllianceAssignment.F
            or DadAllianceAssignment.G;

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
            DadAllianceAssignment.D => 3,
            DadAllianceAssignment.E => 4,
            DadAllianceAssignment.F => 5,
            DadAllianceAssignment.G => 6,
            _ => -1,
        };

    public static DadAllianceAssignment FromCrossRealmGroupIndex(int groupIndex)
        => groupIndex switch
        {
            0 => DadAllianceAssignment.A,
            1 => DadAllianceAssignment.B,
            2 => DadAllianceAssignment.C,
            3 => DadAllianceAssignment.D,
            4 => DadAllianceAssignment.E,
            5 => DadAllianceAssignment.F,
            6 => DadAllianceAssignment.G,
            _ => DadAllianceAssignment.None,
        };

    public static bool CanStop(DadAlliancePartyFinderStatus? status)
        => status != null &&
           (status.OwnsRecruitment ||
            (!string.IsNullOrWhiteSpace(status.CreateStage) &&
             status.State is DadAllianceRecruitmentState.Validating
                 or DadAllianceRecruitmentState.CreatingListing
                 or DadAllianceRecruitmentState.WaitingUnsafe
                 or DadAllianceRecruitmentState.RetryWaiting
                 or DadAllianceRecruitmentState.Blocked));

    public static bool CanGrabDads(DadAlliancePartyFinderStatus? status)
        => status != null &&
           status.State == DadAllianceRecruitmentState.ListingOpen &&
           status.OwnsRecruitment;

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
            return "A concrete A-G assignment is required.";
        if (instruction.CreateListingAsHost &&
            instruction.AssignedAlliance != DadAllianceAssignment.A)
        {
            return "Remote Slot1 PF host must be assigned to Alliance A.";
        }
        if (instruction.Passcode is < 1000 or > 9999)
            return "The Party Finder passcode must be exactly four digits.";
        if (instruction.Attempt < 0 || instruction.StopGeneration < 0)
            return "Attempt and Stop generation must be non-negative.";
        return string.Empty;
    }

    public static string BuildDedupeKey(string? recruitmentId, DadCharacterKey targetCharacterKey)
        => $"{(recruitmentId ?? string.Empty).Trim()}|{targetCharacterKey.Value.Trim()}";

    public static bool TryValidateAsyncResult(
        long dispatchedGeneration,
        long currentGeneration,
        DadAllianceRecruitmentInstructionDto instruction,
        DadAllianceRecruitmentResultDto? result,
        DadWorkerSessionId expectedCoordinatorWorkerSessionId,
        out string blocker)
    {
        blocker = string.Empty;
        if (dispatchedGeneration <= 0 || dispatchedGeneration != currentGeneration)
            blocker = "The Alliance PF completion belongs to an expired operation generation.";
        else if (result == null)
            blocker = "The Alliance PF completion returned no result.";
        else if (!Same(instruction.RecruitmentId, result.RecruitmentId) ||
                 !Same(instruction.CoordinatorWorkerSessionId.Value, expectedCoordinatorWorkerSessionId.Value) ||
                 !Same(instruction.TargetWorkerSessionId.Value, result.WorkerSessionId.Value) ||
                 !Same(instruction.TargetCharacterKey.Value, result.TargetCharacterKey.Value) ||
                 !string.Equals(instruction.TargetCharacterName.Trim(), result.TargetCharacterName.Trim(), StringComparison.Ordinal) ||
                 !string.Equals(instruction.TargetCharacterWorld.Trim(), result.TargetCharacterWorld.Trim(), StringComparison.Ordinal) ||
                 instruction.TargetContentId == 0 ||
                 instruction.TargetContentId != result.TargetContentId ||
                 instruction.AssignedAlliance != result.ExpectedAlliance ||
                 instruction.Attempt != result.Attempt ||
                 instruction.StopGeneration != result.StopGeneration)
        {
            blocker = "The Alliance PF completion contradicts its recruitment, source/target worker, character/content, alliance, attempt, or stop generation.";
        }

        return blocker.Length == 0;
    }

    public static bool TryValidateAsyncCancellationResult(
        long dispatchedGeneration,
        long currentGeneration,
        DadAllianceRecruitmentCancellationDto cancellation,
        DadAllianceRecruitmentInstructionDto instruction,
        DadAllianceRecruitmentResultDto? result,
        out string blocker)
    {
        blocker = string.Empty;
        if (dispatchedGeneration <= 0 || dispatchedGeneration != currentGeneration)
            blocker = "The Alliance PF cancellation completion belongs to an expired operation generation.";
        else if (result == null)
            blocker = "The Alliance PF cancellation returned no result.";
        else if (!Same(cancellation.RecruitmentId, instruction.RecruitmentId) ||
                 !Same(cancellation.RecruitmentId, result.RecruitmentId) ||
                 !Same(cancellation.CoordinatorWorkerSessionId.Value, instruction.CoordinatorWorkerSessionId.Value) ||
                 !Same(cancellation.TargetWorkerSessionId.Value, instruction.TargetWorkerSessionId.Value) ||
                 !Same(cancellation.TargetWorkerSessionId.Value, result.WorkerSessionId.Value) ||
                 !Same(cancellation.TargetCharacterKey.Value, instruction.TargetCharacterKey.Value) ||
                 !Same(cancellation.TargetCharacterKey.Value, result.TargetCharacterKey.Value) ||
                 instruction.TargetContentId == 0 ||
                 instruction.TargetContentId != result.TargetContentId ||
                 instruction.AssignedAlliance != result.ExpectedAlliance ||
                 instruction.Attempt != result.Attempt ||
                 cancellation.StopGeneration != result.StopGeneration)
        {
            blocker = "The Alliance PF cancellation completion contradicts its recruitment, source/target worker, character/content, alliance, attempt, or stop generation.";
        }

        return blocker.Length == 0;
    }

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
            blockers.Add($"Assign A, B, C, D, E, F, or G to {string.Join(", ", unassigned)}.");

        var unresolved = rows
            .Where(static row => !row.HasIdentity)
            .Select(static row => row.SlotId)
            .ToList();
        if (unresolved.Count > 0)
            blockers.Add($"Resolve an online exact character for {string.Join(", ", unresolved)}.");

        var a = rows.Count(static row => row.Assignment == DadAllianceAssignment.A);
        var b = rows.Count(static row => row.Assignment == DadAllianceAssignment.B);
        var c = rows.Count(static row => row.Assignment == DadAllianceAssignment.C);
        var d = rows.Count(static row => row.Assignment == DadAllianceAssignment.D);
        var e = rows.Count(static row => row.Assignment == DadAllianceAssignment.E);
        var f = rows.Count(static row => row.Assignment == DadAllianceAssignment.F);
        var g = rows.Count(static row => row.Assignment == DadAllianceAssignment.G);
        ValidateCount("A", a, blockers);
        ValidateCount("B", b, blockers);
        ValidateCount("C", c, blockers);
        ValidateCount("D", d, blockers, required: false);
        ValidateCount("E", e, blockers, required: false);
        ValidateCount("F", f, blockers, required: false);
        ValidateCount("G", g, blockers, required: false);

        var total = a + b + c + d + e + f + g;
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
            ? $"Ready: A {a}/8, B {b}/8, C {c}/8, D {d}/8, E {e}/8, F {f}/8, G {g}/8 ({total} total)."
            : $"Blocked: A {a}/8, B {b}/8, C {c}/8, D {d}/8, E {e}/8, F {f}/8, G {g}/8 ({total} total). {string.Join(" ", blockers)}";
        return new DadAlliancePresetValidation
        {
            AllianceACount = a,
            AllianceBCount = b,
            AllianceCCount = c,
            AllianceDCount = d,
            AllianceECount = e,
            AllianceFCount = f,
            AllianceGCount = g,
            Blockers = blockers,
            Summary = summary,
        };
    }

    private static void ValidateCount(
        string alliance,
        int count,
        ICollection<string> blockers,
        bool required = true)
    {
        if (required && count < MinimumAllianceSize)
            blockers.Add($"Alliance {alliance} requires at least one character.");
        else if (count > MaximumAllianceSize)
            blockers.Add($"Alliance {alliance} cannot exceed eight characters.");
    }

    private static string NormalizeIdentity(string? value)
        => value?.Trim() ?? string.Empty;

    private static bool Same(string? left, string? right)
        => string.Equals(
            left?.Trim(),
            right?.Trim(),
            StringComparison.OrdinalIgnoreCase);
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

internal sealed class DadStableAllianceHydrationTracker
{
    private ulong contentId;
    private DadAllianceAssignment candidate;
    private DadAllianceAssignment stable;
    private int observationCount;
    private long lastObservationGeneration = -1;

    public DadAllianceAssignment Observe(
        ulong observedContentId,
        DadAllianceAssignment observedAlliance,
        long observationGeneration)
    {
        if (observedContentId == 0)
        {
            Reset();
            return DadAllianceAssignment.None;
        }

        if (observedContentId != contentId)
        {
            Reset();
            contentId = observedContentId;
        }

        if (observationGeneration == lastObservationGeneration)
            return stable;
        lastObservationGeneration = observationGeneration;

        if (observedAlliance == DadAllianceAssignment.None)
        {
            candidate = DadAllianceAssignment.None;
            stable = DadAllianceAssignment.None;
            observationCount = 0;
            return stable;
        }

        if (candidate != observedAlliance)
        {
            candidate = observedAlliance;
            stable = DadAllianceAssignment.None;
            observationCount = 1;
            return stable;
        }

        observationCount++;
        if (observationCount >= 2)
            stable = candidate;
        return stable;
    }

    public DadAllianceAssignment GetStable(ulong observedContentId)
        => observedContentId != 0 && observedContentId == contentId
            ? stable
            : DadAllianceAssignment.None;

    public void Reset()
    {
        contentId = 0;
        candidate = DadAllianceAssignment.None;
        stable = DadAllianceAssignment.None;
        observationCount = 0;
        lastObservationGeneration = -1;
    }
}

public enum DadAllianceRemoteHostLifecycleState
{
    LocalHost,
    CreatePending,
    ListingOpen,
    CleanupPending,
    CleanupComplete,
    Blocked,
}

public static class DadAllianceRemoteHostRules
{
    public const string StoppedSafeStatusCode = "dad-alliance-stopped";

    private static readonly TimeSpan[] AuditBackoff =
    [
        TimeSpan.FromMilliseconds(750),
        TimeSpan.FromMilliseconds(1500),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(6),
        TimeSpan.FromSeconds(10),
    ];

    public static readonly TimeSpan CleanupDeadline = TimeSpan.FromSeconds(60);

    public static DadAllianceRemoteHostLifecycleState Evaluate(
        bool remoteHost,
        bool ownershipPossible,
        bool cleanupRequested,
        DadAllianceRecruitmentResultDto? result)
    {
        if (!remoteHost)
            return DadAllianceRemoteHostLifecycleState.LocalHost;
        if (cleanupRequested)
        {
            if (!ownershipPossible || IsStoppedProof(result))
                return DadAllianceRemoteHostLifecycleState.CleanupComplete;
            return DadAllianceRemoteHostLifecycleState.CleanupPending;
        }

        if (result?.ResultKind == DadAllianceRecruitmentResultKind.Blocked)
            return DadAllianceRemoteHostLifecycleState.Blocked;
        if (result is
            {
                ResultKind: DadAllianceRecruitmentResultKind.Succeeded,
                State: DadAllianceRecruitmentState.ListingOpen,
                ObservedAlliance: DadAllianceAssignment.A,
            })
        {
            return DadAllianceRemoteHostLifecycleState.ListingOpen;
        }

        return DadAllianceRemoteHostLifecycleState.CreatePending;
    }

    public static TimeSpan GetAuditBackoff(int dispatchedAttempts)
        => AuditBackoff[Math.Clamp(dispatchedAttempts - 1, 0, AuditBackoff.Length - 1)];

    public static DateTime GetFixedCleanupDeadline(DateTime? existingDeadlineUtc, DateTime nowUtc)
        => existingDeadlineUtc ?? EnsureUtc(nowUtc) + CleanupDeadline;

    public static bool CleanupExpired(DateTime? deadlineUtc, DateTime nowUtc)
        => deadlineUtc.HasValue && EnsureUtc(nowUtc) >= EnsureUtc(deadlineUtc.Value);

    public static bool CanClearTerminalPartial(
        bool cleanupTerminalPartial,
        bool activeRecruitment)
        => cleanupTerminalPartial && !activeRecruitment;

    public static bool IsStoppedProof(DadAllianceRecruitmentResultDto? result)
        => result is
        {
            ResultKind: DadAllianceRecruitmentResultKind.Stopped,
            State: DadAllianceRecruitmentState.Stopped,
        };

    public static bool TryValidateTerminalCleanupSnapshot(
        DadAllianceRecruitmentInstructionDto instruction,
        long expectedStopGeneration,
        DadAlliancePfUiSnapshotDto? snapshot,
        out string blocker)
    {
        blocker = string.Empty;
        if (snapshot == null)
            blocker = "The remote Slot1 terminal cleanup audit returned no snapshot.";
        else if (!Same(instruction.RecruitmentId, snapshot.RecruitmentId) ||
                 !Same(instruction.TargetWorkerSessionId.Value, snapshot.WorkerSessionId.Value) ||
                 !Same(instruction.TargetCharacterKey.Value, snapshot.TargetCharacterKey.Value) ||
                 instruction.AssignedAlliance != snapshot.AssignedAlliance ||
                 instruction.Attempt != snapshot.Attempt ||
                 expectedStopGeneration < 0 ||
                 expectedStopGeneration != snapshot.StopGeneration ||
                 snapshot.State != DadAllianceRecruitmentState.Stopped ||
                 !string.Equals(
                     snapshot.SafeStatusCode,
                     StoppedSafeStatusCode,
                     StringComparison.Ordinal))
        {
            blocker = "The remote Slot1 terminal cleanup snapshot contradicts its recruitment, worker, character assignment, attempt, stop generation, stopped state, or safe status.";
        }

        return blocker.Length == 0;
    }

    public static bool HasActiveOperation(
        DadAlliancePartyFinderStatus status,
        bool cleanupRequested,
        bool cleanupTerminalPartial,
        bool remoteHostUnresolved)
        => !string.IsNullOrWhiteSpace(status.RecruitmentId) &&
           (status.OwnsRecruitment ||
            cleanupRequested ||
            cleanupTerminalPartial ||
            remoteHostUnresolved ||
            status.State is not DadAllianceRecruitmentState.Complete
                and not DadAllianceRecruitmentState.Stopped
                and not DadAllianceRecruitmentState.Blocked);

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();

    private static bool Same(string? left, string? right)
        => string.Equals(
            left?.Trim(),
            right?.Trim(),
            StringComparison.OrdinalIgnoreCase);
}
