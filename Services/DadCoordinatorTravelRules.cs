using dad.Models;

namespace dad.Services;

public static class DadCoordinatorTravelRules
{
    public static readonly TimeSpan MaxLocationProofAge = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FutureClockTolerance = TimeSpan.FromSeconds(2);

    public static bool TryFreezeTarget(
        string runId,
        DadParticipantSnapshot coordinator,
        DateTime nowUtc,
        out DadCoordinatorTravelTarget target,
        out string blocker)
    {
        target = new DadCoordinatorTravelTarget();
        blocker = string.Empty;
        var location = coordinator.CurrentLocation;
        if (string.IsNullOrWhiteSpace(runId) ||
            coordinator.WorkerSessionId.IsEmpty ||
            coordinator.ManagedAccountKey.IsEmpty ||
            coordinator.ActiveCharacterKey.IsEmpty ||
            coordinator.Character.ContentId == 0)
        {
            blocker = "Slot1 assembly target requires exact run, worker, account, character, and Content ID identity.";
            return false;
        }

        if (!coordinator.IsAvailable || !coordinator.WorldReadyStable)
        {
            blocker = "Slot1 assembly target requires fresh world-stable live Slot1 truth.";
            return false;
        }

        if (!IsFreshComplete(location, nowUtc))
        {
            blocker = "Slot1 current world, data center, and region proof is missing, incomplete, or stale.";
            return false;
        }

        target = new DadCoordinatorTravelTarget
        {
            RunId = runId.Trim(),
            CoordinatorWorkerSessionId = coordinator.WorkerSessionId,
            CoordinatorAccountKey = coordinator.ManagedAccountKey,
            CoordinatorCharacterKey = coordinator.ActiveCharacterKey,
            CoordinatorContentId = coordinator.Character.ContentId,
            WorldId = location!.WorldId,
            WorldName = location.WorldName.Trim(),
            DataCenterId = location.DataCenterId,
            DataCenterName = location.DataCenterName.Trim(),
            RegionId = location.RegionId,
            RegionName = location.RegionName.Trim(),
            CapturedAtUtc = NormalizeUtc(nowUtc),
        };
        return true;
    }

    public static DadCoordinatorTravelProofResult ValidateParticipants(
        DadCoordinatorTravelTarget? target,
        IReadOnlyList<DadParticipantSnapshot> participants,
        DateTime nowUtc)
    {
        if (target is not { IsComplete: true })
        {
            return Blocked(
                "Frozen Slot1 assembly target is missing or incomplete.",
                immutableTargetChanged: true);
        }

        var coordinatorMatches = participants
            .Where(participant => Same(participant.WorkerSessionId.Value, target.CoordinatorWorkerSessionId.Value))
            .ToList();
        if (coordinatorMatches.Count != 1)
        {
            return Blocked(
                $"Frozen Slot1 worker '{target.CoordinatorWorkerSessionId}' has {coordinatorMatches.Count} live location proof row(s).",
                immutableTargetChanged: coordinatorMatches.Count > 1);
        }

        var coordinator = coordinatorMatches[0];
        if (!DadRosterIdentity.SameAccount(coordinator.ManagedAccountKey, target.CoordinatorAccountKey) ||
            !Same(coordinator.ActiveCharacterKey.Value, target.CoordinatorCharacterKey.Value) ||
            coordinator.Character.ContentId != target.CoordinatorContentId)
        {
            return Blocked(
                "Frozen Slot1 identity changed after the assembly target was accepted.",
                immutableTargetChanged: true);
        }

        foreach (var participant in participants)
        {
            var location = participant.CurrentLocation;
            var slot = string.IsNullOrWhiteSpace(participant.AssignedSlotId)
                ? participant.WorkerSessionId.Value
                : participant.AssignedSlotId;
            if (!IsFreshComplete(location, nowUtc))
            {
                return Blocked(
                    $"{slot} is missing fresh current-world, data-center, and region proof.",
                    immutableTargetChanged: false);
            }

            if (location!.DataCenterId != target.DataCenterId ||
                !Same(location.DataCenterName, target.DataCenterName) ||
                location.RegionId != target.RegionId ||
                !Same(location.RegionName, target.RegionName))
            {
                return Blocked(
                    $"{slot} is currently on {location.DataCenterName} ({location.RegionName}); frozen Slot1 data center is {target.DataCenterName} ({target.RegionName}).",
                    immutableTargetChanged: false);
            }

            if (!Same(participant.WorkerSessionId.Value, target.CoordinatorWorkerSessionId.Value))
                continue;

            if (location.WorldId != target.WorldId || !Same(location.WorldName, target.WorldName))
            {
                return Blocked(
                    $"Slot1 current world changed from frozen assembly target {target.WorldName} to {location.WorldName}.",
                    immutableTargetChanged: true);
            }
        }

        return new DadCoordinatorTravelProofResult
        {
            Ready = true,
            Summary = $"All {participants.Count} participant(s) proved current presence on frozen Slot1 data center {target.DataCenterName}.",
        };
    }

    public static bool IsFreshComplete(DadWorldLocationObservation? location, DateTime nowUtc)
    {
        if (location is not { IsComplete: true })
            return false;

        var now = NormalizeUtc(nowUtc);
        var observed = NormalizeUtc(location.ObservedAtUtc);
        return observed <= now + FutureClockTolerance && now - observed <= MaxLocationProofAge;
    }

    private static DadCoordinatorTravelProofResult Blocked(string summary, bool immutableTargetChanged)
        => new()
        {
            Ready = false,
            ImmutableTargetChanged = immutableTargetChanged,
            Summary = summary,
        };

    private static bool Same(string? left, string? right)
        => string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
}

public sealed class DadClientTravelGate
{
    public const uint OceRegionId = 4;
    public const int OceVisitorCharacterCap = 40;
    public const int MaxChangeWorldAttempts = 3;
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MaxOceRosterProofAge = TimeSpan.FromSeconds(30);

    private string immutableSignature = string.Empty;
    private bool acceptedInvocation;
    private bool invocationPending;
    private bool terminalFailure;
    private int invocationCount;
    private DateTime nextAttemptUtc = DateTime.MinValue;
    private string terminalSummary = string.Empty;

    public int InvocationCount => invocationCount;

    public DadClientTravelDecision Evaluate(DadClientTravelContext context, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(context);
        var assignment = context.Assignment ?? new DadWakeRequestDto();
        var target = assignment.CoordinatorTravelTarget;
        if (target is not { IsComplete: true })
            return Reject("Slot1 assembly target is missing or incomplete.");
        if (!string.Equals(target.RunId, assignment.RunId, StringComparison.Ordinal))
        {
            terminalFailure = true;
            terminalSummary = "Slot1 assembly target contradicts the assignment run.";
            return Reject(terminalSummary);
        }

        var signature = BuildSignature(assignment, target);
        if (string.IsNullOrWhiteSpace(immutableSignature))
            immutableSignature = signature;
        else if (!string.Equals(immutableSignature, signature, StringComparison.Ordinal))
        {
            terminalFailure = true;
            terminalSummary = "Immutable Slot1 assembly target or exact assignment identity changed.";
        }

        if (terminalFailure)
            return Reject(terminalSummary);

        var participant = context.Participant ?? new DadParticipantSnapshot();
        if (!ExactAssignmentIdentityMatches(assignment, participant))
            return Wait("Waiting for the exact assigned account, character, Content ID, worker, run, and slot before data-center travel.");

        var current = participant.CurrentLocation;
        if (!DadCoordinatorTravelRules.IsFreshComplete(current, nowUtc))
            return Wait("Waiting for fresh current-world, data-center, and region proof before data-center travel.");

        if (current!.DataCenterId == target.DataCenterId &&
            Same(current.DataCenterName, target.DataCenterName) &&
            current.RegionId == target.RegionId &&
            Same(current.RegionName, target.RegionName))
        {
            return Ready($"Current data center matches frozen Slot1 data center {target.DataCenterName}.");
        }

        if (acceptedInvocation)
            return Wait($"Lifestream accepted travel to {target.WorldName}; waiting for fresh {target.DataCenterName} location proof.");

        if (!TryAuthorizeDestination(context, target, nowUtc, out var policyBlocker))
            return Reject(policyBlocker);

        var safety = context.Safety ?? new DadClientTravelSafetyEvidence();
        if (!participant.WorldReadyStable || !participant.PostArReady)
            return Wait("Waiting for world-stable post-AR readiness before invoking Lifestream.");
        if (!safety.VermaxionSafe || participant.ExternalAutomationHeld)
            return Wait("Waiting for VERMAXION to prove automation is idle before invoking Lifestream.");
        if (!safety.AutoRetainerAvailable || safety.AutoRetainerBusy || safety.AutoRetainerMultiModeEnabled)
            return Wait("Waiting for AutoRetainer to be available, idle, and out of Multi Mode before invoking Lifestream.");
        if (!safety.LifestreamAvailable || safety.LifestreamBusy)
            return Wait("Waiting for Lifestream to be available and idle before invoking world travel.");
        if (invocationPending)
            return Wait("A Lifestream.ChangeWorld result is pending; no duplicate invocation is permitted.");

        var now = NormalizeUtc(nowUtc);
        if (now < nextAttemptUtc)
            return Wait($"Waiting until {nextAttemptUtc:O} before retrying an explicit false Lifestream result.");
        if (invocationCount >= MaxChangeWorldAttempts)
            return Reject("Lifestream.ChangeWorld exhausted its three explicit-false attempts.");

        invocationPending = true;
        return new DadClientTravelDecision
        {
            Action = DadClientTravelAction.InvokeLifestream,
            AttemptNumber = invocationCount + 1,
            DestinationWorldName = target.WorldName,
            Summary = $"Invoke Lifestream.ChangeWorld('{target.WorldName}') attempt {invocationCount + 1}/{MaxChangeWorldAttempts}.",
        };
    }

    public void RecordInvocationResult(DadLifestreamChangeWorldResult result, DateTime nowUtc)
    {
        if (!invocationPending || terminalFailure || acceptedInvocation)
            return;

        invocationPending = false;
        invocationCount++;
        switch (result.Outcome)
        {
            case DadLifestreamChangeWorldOutcome.Accepted:
                acceptedInvocation = true;
                break;
            case DadLifestreamChangeWorldOutcome.ExplicitFalse when invocationCount < MaxChangeWorldAttempts:
                nextAttemptUtc = NormalizeUtc(nowUtc) + RetryInterval;
                break;
            case DadLifestreamChangeWorldOutcome.ExplicitFalse:
                terminalFailure = true;
                terminalSummary = "Lifestream.ChangeWorld returned explicit false three times; travel failed closed.";
                break;
            default:
                terminalFailure = true;
                terminalSummary = string.IsNullOrWhiteSpace(result.Summary)
                    ? "Lifestream.ChangeWorld result was uncertain; travel failed closed without retry."
                    : $"Lifestream.ChangeWorld result was uncertain; travel failed closed without retry: {result.Summary}";
                break;
        }
    }

    public void Reset()
    {
        immutableSignature = string.Empty;
        acceptedInvocation = false;
        invocationPending = false;
        terminalFailure = false;
        invocationCount = 0;
        nextAttemptUtc = DateTime.MinValue;
        terminalSummary = string.Empty;
    }

    private static bool TryAuthorizeDestination(
        DadClientTravelContext context,
        DadCoordinatorTravelTarget target,
        DateTime nowUtc,
        out string blocker)
    {
        blocker = string.Empty;
        if (context.HomeRegionId == 0 || string.IsNullOrWhiteSpace(context.HomeRegionName))
        {
            blocker = "Character home-region proof is missing; cross-data-center travel is not authorized.";
            return false;
        }

        if (target.RegionId == context.HomeRegionId)
            return true;

        if (target.RegionId != OceRegionId)
        {
            blocker = $"Cross-region travel from home region {context.HomeRegionName} to {target.RegionName} is not authorized.";
            return false;
        }

        return TryAuthorizeOceVisitor(
            context.Assignment,
            context.HomeRegionId,
            context.OceCapacityProof,
            nowUtc,
            out blocker);
    }

    private static bool TryAuthorizeOceVisitor(
        DadWakeRequestDto assignment,
        uint observedHomeRegionId,
        DadOceTravelCapacityProof? proof,
        DateTime nowUtc,
        out string blocker)
    {
        blocker = string.Empty;
        var requiredAccount = assignment.RequiredAccountKey;
        if (proof == null ||
            !DadRosterIdentity.SameAccount(proof.AccountKey, requiredAccount) ||
            !proof.IsFullRosterAvailable ||
            !proof.IsComplete ||
            proof.XadbContractVersion.GetValueOrDefault() < 6)
        {
            blocker = "OCE visitor travel requires complete contract-v6 full local XADB roster proof for the exact managed account.";
            return false;
        }

        var now = NormalizeUtc(nowUtc);
        var observed = NormalizeUtc(proof.ObservedAtUtc);
        if (observed > now + TimeSpan.FromSeconds(2) || now - observed > MaxOceRosterProofAge)
        {
            blocker = "OCE visitor roster capacity proof is stale or has an invalid observation time.";
            return false;
        }

        var exactRows = proof.Characters
            .Where(row => DadRosterIdentity.SameAccount(row.AccountKey, requiredAccount))
            .GroupBy(BuildRosterIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToList();
        if (exactRows.Any(static row => row.HomeRegionId == 0 || string.IsNullOrWhiteSpace(row.HomeRegionName)) ||
            proof.AttributedCharacterCount != exactRows.Count ||
            proof.AdvertisedCharacterCount != exactRows.Count)
        {
            blocker = "OCE visitor roster capacity proof is incomplete or contradicts the advertised exact-account roster count.";
            return false;
        }

        var assignedRows = exactRows.Where(row =>
                assignment.RequiredContentId != 0 && row.ContentId == assignment.RequiredContentId ||
                assignment.RequiredContentId == 0 && Same(row.CharacterKey.Value, assignment.RequiredCharacterKey.Value))
            .ToList();
        if (assignedRows.Count != 1 || assignedRows[0].HomeRegionId != observedHomeRegionId)
        {
            blocker = "OCE visitor roster proof does not contain one exact assigned character with matching home-region proof.";
            return false;
        }

        var oceHomeCount = exactRows.Count(static row => row.HomeRegionId == OceRegionId);
        if (oceHomeCount >= OceVisitorCharacterCap)
        {
            blocker = $"OCE visitor travel is blocked because the exact managed account has {oceHomeCount} unique OCE-home characters (cap {OceVisitorCharacterCap}).";
            return false;
        }

        return true;
    }

    private static bool ExactAssignmentIdentityMatches(
        DadWakeRequestDto assignment,
        DadParticipantSnapshot participant)
        => !string.IsNullOrWhiteSpace(assignment.RunId) &&
           string.Equals(participant.RunId, assignment.RunId, StringComparison.Ordinal) &&
           !participant.WorkerSessionId.IsEmpty &&
           DadRosterIdentity.SameAccount(participant.ManagedAccountKey, assignment.RequiredAccountKey) &&
           Same(participant.ActiveCharacterKey.Value, assignment.RequiredCharacterKey.Value) &&
           assignment.RequiredContentId != 0 &&
           participant.Character.ContentId == assignment.RequiredContentId &&
           Same(participant.AssignedSlotId, assignment.AssignedSlotId);

    private static string BuildSignature(DadWakeRequestDto assignment, DadCoordinatorTravelTarget target)
        => string.Join(
            "|",
            Normalize(assignment.RunId),
            Normalize(assignment.AuthorityWorkerSessionId.Value),
            Normalize(assignment.RequiredAccountKey.Value),
            Normalize(assignment.RequiredCharacterKey.Value),
            assignment.RequiredContentId,
            Normalize(assignment.AssignedSlotId),
            Normalize(target.RunId),
            Normalize(target.CoordinatorWorkerSessionId.Value),
            Normalize(target.CoordinatorAccountKey.Value),
            Normalize(target.CoordinatorCharacterKey.Value),
            target.CoordinatorContentId,
            target.WorldId,
            Normalize(target.WorldName),
            target.DataCenterId,
            Normalize(target.DataCenterName),
            target.RegionId,
            Normalize(target.RegionName),
            NormalizeUtc(target.CapturedAtUtc).Ticks);

    private static string BuildRosterIdentity(DadOceRosterCharacterProof row)
        => row.ContentId != 0
            ? $"cid:{row.ContentId}"
            : $"key:{Normalize(row.CharacterKey.Value)}";

    private static DadClientTravelDecision Ready(string summary)
        => new() { Action = DadClientTravelAction.Ready, Summary = summary };

    private static DadClientTravelDecision Wait(string summary)
        => new() { Action = DadClientTravelAction.Wait, Summary = summary };

    private static DadClientTravelDecision Reject(string summary)
        => new() { Action = DadClientTravelAction.Reject, Summary = summary };

    private static bool Same(string? left, string? right)
        => string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);

    private static string Normalize(string? value)
        => (value ?? string.Empty).Trim().ToUpperInvariant();

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
}
