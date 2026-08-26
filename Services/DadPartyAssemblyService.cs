using dad.Models;

namespace dad.Services;

public enum DadPartyMembershipDisposition
{
    Ready,
    Wait,
    Reject,
}

public readonly record struct DadPartyMembershipDecision(
    DadPartyMembershipDisposition Disposition,
    string Summary);

public sealed record DadExactPartyTeardownAggregate(
    bool Complete,
    bool Success,
    IReadOnlyList<string> FailedSlots,
    string Summary);

public static class DadExactPartyTeardownRules
{
    public static IReadOnlyList<DadAssemblyInstructionDto> GetDispatchableInstructions(
        IReadOnlyList<DadAssemblyInstructionDto> instructions,
        IReadOnlyDictionary<string, DadRunStepResultDto> terminalResults)
    {
        var leader = instructions.FirstOrDefault(static instruction =>
            instruction.InstructionKind == DadAssemblyInstructionKind.DisbandParty);
        if (leader == null)
            return [];

        if (!terminalResults.ContainsKey(leader.SlotId))
            return [leader];

        return instructions
            .Where(static instruction =>
                instruction.InstructionKind == DadAssemblyInstructionKind.LeaveParty)
            .Where(instruction => !terminalResults.ContainsKey(instruction.SlotId))
            .ToList();
    }

    public static DadExactPartyTeardownAggregate Aggregate(
        IReadOnlyList<DadAssemblyInstructionDto> instructions,
        IReadOnlyDictionary<string, DadRunStepResultDto> terminalResults)
    {
        if (instructions.Count == 0 || terminalResults.Count < instructions.Count)
        {
            var pending = instructions
                .Where(instruction => !terminalResults.ContainsKey(instruction.SlotId))
                .Select(static instruction => instruction.SlotId)
                .ToList();
            return new DadExactPartyTeardownAggregate(
                Complete: false,
                Success: false,
                FailedSlots: [],
                Summary: $"Waiting for terminal teardown result(s): {string.Join(", ", pending)}.");
        }

        var failed = instructions
            .Where(instruction =>
                !terminalResults.TryGetValue(instruction.SlotId, out var result) ||
                !result.Success)
            .Select(static instruction => instruction.SlotId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return failed.Count == 0
            ? new DadExactPartyTeardownAggregate(
                Complete: true,
                Success: true,
                FailedSlots: [],
                Summary: $"Exact-roster teardown completed on all {instructions.Count} slot(s).")
            : new DadExactPartyTeardownAggregate(
                Complete: true,
                Success: false,
                FailedSlots: failed,
                Summary: $"Exact-roster teardown partially failed on {string.Join(", ", failed)}.");
    }
}

internal static class DadPartySnapshotSourceRules
{
    public static IReadOnlyList<DadPartyMemberSnapshot> Read(
        bool crossRealmPartyActive,
        Func<IReadOnlyList<DadPartyMemberSnapshot>> readPartyList,
        Func<IReadOnlyList<DadPartyMemberSnapshot>> readCrossRealmParty)
        => crossRealmPartyActive ? readCrossRealmParty() : readPartyList();
}

public sealed class DadPartyAssemblyService
{
    public List<DadAssemblyInstructionDto> BuildInstructions(DadRunPlan plan, IReadOnlyList<DadParticipantSnapshot> participants, out string blocker)
        => BuildInstructionsCore(
            plan,
            participants,
            manifest: null,
            participants.FirstOrDefault(static participant => participant.IsAuthority)?.WorkerSessionId
            ?? new DadWorkerSessionId(string.Empty),
            runtimeInviteTargets: null,
            out blocker);

    internal List<DadAssemblyInstructionDto> BuildInstructions(
        DadRunPlan plan,
        IReadOnlyList<DadParticipantSnapshot> participants,
        DadRunSlotManifest manifest,
        DadWorkerSessionId authorityWorkerSessionId,
        out string blocker)
        => BuildInstructions(
            plan,
            participants,
            manifest,
            authorityWorkerSessionId,
            runtimeInviteTargets: null,
            out blocker);

    internal List<DadAssemblyInstructionDto> BuildInstructions(
        DadRunPlan plan,
        IReadOnlyList<DadParticipantSnapshot> participants,
        DadRunSlotManifest manifest,
        DadWorkerSessionId authorityWorkerSessionId,
        IReadOnlyDictionary<string, DadNativePartyInviteTarget>? runtimeInviteTargets,
        out string blocker)
        => BuildInstructionsCore(
            plan,
            participants,
            manifest,
            authorityWorkerSessionId,
            runtimeInviteTargets,
            out blocker);

    private static List<DadAssemblyInstructionDto> BuildInstructionsCore(
        DadRunPlan plan,
        IReadOnlyList<DadParticipantSnapshot> participants,
        DadRunSlotManifest? manifest,
        DadWorkerSessionId authorityWorkerSessionId,
        IReadOnlyDictionary<string, DadNativePartyInviteTarget>? runtimeInviteTargets,
        out string blocker)
    {
        blocker = string.Empty;
        var instructions = new List<DadAssemblyInstructionDto>();

        if (participants.Count < plan.RequiredParticipantCount)
        {
            blocker = $"Need {plan.RequiredParticipantCount} participant(s), have {participants.Count}.";
            return instructions;
        }

        if (plan.Orchestration.QueueAuthority != DadQueueAuthority.Leader)
        {
            blocker = $"Party assembly requires leader queue authority; request has {plan.Orchestration.QueueAuthority}.";
            return instructions;
        }

        var ordered = manifest == null
            ? OrderParticipantsForParty(plan, participants)
            : OrderFrozenParticipants(manifest, participants, out blocker);
        if (!string.IsNullOrWhiteSpace(blocker))
            return [];
        var slotOneIsRegisteredIsland = manifest?.Slots.SingleOrDefault(static slot =>
            string.Equals(slot.SlotId, DadPlannerSlotRules.LeaderSlotId, StringComparison.OrdinalIgnoreCase))
            ?.RouteKind == DadRunSlotRouteKind.RegisteredIsland;
        if (!slotOneIsRegisteredIsland &&
            !string.IsNullOrWhiteSpace(plan.LeaderCharacterKey) &&
            ordered.Count > 0 &&
            !string.Equals(ordered[0].ActiveCharacterKey, plan.LeaderCharacterKey, StringComparison.OrdinalIgnoreCase))
        {
            blocker = $"Configured leader {plan.LeaderCharacterKey} is not online on Slot1.";
            return [];
        }

        var frozenInviter = new DadExpectedPartyInviter();
        var inviteTargets = new List<DadNativePartyInviteTarget>();
        if (manifest != null &&
            !TryBuildFrozenPartyAuthority(
                plan,
                manifest,
                ordered,
                runtimeInviteTargets,
                out frozenInviter,
                out inviteTargets,
                out blocker))
        {
            return [];
        }

        for (var index = 0; index < ordered.Count; index++)
        {
            var participant = ordered[index];
            if (string.IsNullOrWhiteSpace(participant.RegisteredIslandId) && !participant.PostArReady)
            {
                blocker = $"{participant.Character.CharacterKey} is not post-AR ready.";
                return [];
            }

            instructions.Add(new DadAssemblyInstructionDto
            {
                RunId = plan.Request.RequestId,
                AuthorityWorkerSessionId = authorityWorkerSessionId,
                ModuleId = plan.CompositeModuleId,
                SlotId = string.IsNullOrWhiteSpace(participant.AssignedSlotId)
                    ? DadPlannerSlotRules.FormatSlotId(index + 1)
                    : participant.AssignedSlotId,
                RequiredCharacterKey = participant.Character.CharacterKey,
                InstructionKind = index == 0 ? DadAssemblyInstructionKind.FormParty : DadAssemblyInstructionKind.JoinParty,
                FrozenInviter = frozenInviter.Clone(),
                InviteTargets = inviteTargets.Select(static target => target.Clone()).ToList(),
                Summary = index == 0
                    ? $"Slot1 leader confirmed on {participant.Character.CharacterKey}; form Dad party."
                    : $"Join Slot1's Dad party on {participant.Character.CharacterKey}.",
            });
        }

        return instructions;
    }

    internal List<DadAssemblyInstructionDto> BuildTeardownInstructions(
        DadRunPlan plan,
        IReadOnlyList<DadParticipantSnapshot> participants,
        DadRunSlotManifest manifest,
        DadWorkerSessionId authorityWorkerSessionId,
        out string blocker)
        => BuildTeardownInstructions(
            plan,
            participants,
            manifest,
            authorityWorkerSessionId,
            runtimeInviteTargets: null,
            out blocker);

    internal List<DadAssemblyInstructionDto> BuildTeardownInstructions(
        DadRunPlan plan,
        IReadOnlyList<DadParticipantSnapshot> participants,
        DadRunSlotManifest manifest,
        DadWorkerSessionId authorityWorkerSessionId,
        IReadOnlyDictionary<string, DadNativePartyInviteTarget>? runtimeInviteTargets,
        out string blocker)
    {
        var instructions = BuildInstructions(
            plan,
            participants,
            manifest,
            authorityWorkerSessionId,
            runtimeInviteTargets,
            out blocker);
        if (!string.IsNullOrWhiteSpace(blocker) || instructions.Count == 0)
            return [];

        foreach (var instruction in instructions)
        {
            if (instruction.InstructionKind == DadAssemblyInstructionKind.FormParty)
            {
                instruction.InstructionKind = DadAssemblyInstructionKind.DisbandParty;
                instruction.Summary = "Slot1 is performing guarded Dad party teardown.";
            }
            else
            {
                instruction.InstructionKind = DadAssemblyInstructionKind.LeaveParty;
                instruction.Summary = $"{instruction.SlotId} is leaving the exact Dad party or proving it is already solo.";
            }
        }

        return instructions;
    }

    public bool VerifyPartyMembership(
        DadRunPlan plan,
        IReadOnlyList<DadParticipantSnapshot> participants,
        IReadOnlyList<DadPartyMemberSnapshot> partyMembers,
        out string blocker)
    {
        var decision = EvaluatePartyMembership(plan, participants, partyMembers);
        blocker = decision.Disposition == DadPartyMembershipDisposition.Ready
            ? string.Empty
            : decision.Summary;
        return decision.Disposition == DadPartyMembershipDisposition.Ready;
    }

    public DadPartyMembershipDecision EvaluatePartyMembership(
        DadRunPlan plan,
        IReadOnlyList<DadParticipantSnapshot> participants,
        IReadOnlyList<DadPartyMemberSnapshot> partyMembers)
        => EvaluatePartyMembership(plan, participants, partyMembers, runtimeInviteTargets: null);

    internal DadPartyMembershipDecision EvaluatePartyMembership(
        DadRunPlan plan,
        IReadOnlyList<DadParticipantSnapshot> participants,
        IReadOnlyList<DadPartyMemberSnapshot> partyMembers,
        IReadOnlyDictionary<string, DadNativePartyInviteTarget>? runtimeInviteTargets)
    {
        if (plan.RequiredParticipantCount <= 1)
            return new DadPartyMembershipDecision(DadPartyMembershipDisposition.Ready, "Single-participant run needs no PartyList proof.");

        var expectedContentIds = participants
            .Select(participant => participant.Character.ContentId != 0
                ? participant.Character.ContentId
                : runtimeInviteTargets != null &&
                  runtimeInviteTargets.TryGetValue(participant.AssignedSlotId, out var target)
                    ? target.ContentId
                    : 0)
            .Where(static contentId => contentId != 0)
            .ToHashSet();
        if (expectedContentIds.Count != plan.RequiredParticipantCount)
        {
            return new DadPartyMembershipDecision(
                DadPartyMembershipDisposition.Reject,
                $"Frozen party manifest has {expectedContentIds.Count}/{plan.RequiredParticipantCount} exact nonzero Content IDs.");
        }

        if (partyMembers.Count > plan.RequiredParticipantCount)
        {
            return new DadPartyMembershipDecision(
                DadPartyMembershipDisposition.Reject,
                $"PartyList contains {partyMembers.Count} members, exceeding frozen party size {plan.RequiredParticipantCount}.");
        }

        var unexpected = partyMembers
            .Where(static member => member.ContentId != 0)
            .Where(member => !expectedContentIds.Contains(member.ContentId))
            .Select(static member => $"{member.CharacterKey}#{member.ContentId}")
            .ToList();
        if (unexpected.Count > 0)
        {
            return new DadPartyMembershipDecision(
                DadPartyMembershipDisposition.Reject,
                $"PartyList contains unexpected frozen-member contradiction(s): {string.Join(", ", unexpected)}.");
        }

        var observedContentIds = partyMembers
            .Select(static member => member.ContentId)
            .Where(static contentId => contentId != 0)
            .ToHashSet();
        var missingIds = expectedContentIds.Except(observedContentIds).ToList();
        if (partyMembers.Count < plan.RequiredParticipantCount || missingIds.Count > 0)
        {
            return new DadPartyMembershipDecision(
                DadPartyMembershipDisposition.Wait,
                $"Waiting for exact PartyList Content IDs: {observedContentIds.Count}/{plan.RequiredParticipantCount} proven; missing {string.Join(",", missingIds)}.");
        }

        return new DadPartyMembershipDecision(
            DadPartyMembershipDisposition.Ready,
            $"PartyList proves exact frozen membership {observedContentIds.Count}/{plan.RequiredParticipantCount}.");
    }

    private static List<DadParticipantSnapshot> OrderParticipantsForParty(
        DadRunPlan plan,
        IReadOnlyList<DadParticipantSnapshot> participants)
    {
        var leaderKey = plan.LeaderCharacterKey?.Trim() ?? string.Empty;
        return participants
            .OrderByDescending(participant => !string.IsNullOrWhiteSpace(leaderKey) &&
                                               string.Equals(participant.ActiveCharacterKey, leaderKey, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(static participant => participant.IsAuthority)
            .ThenByDescending(static participant => participant.IsLocalClient)
            .ThenBy(static participant => DadPlannerSlotRules.GetSlotSortKey(participant.AssignedSlotId))
            .ThenBy(static participant => participant.AssignedSlotId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static participant => participant.Character.CharacterKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<DadParticipantSnapshot> OrderFrozenParticipants(
        DadRunSlotManifest manifest,
        IReadOnlyList<DadParticipantSnapshot> participants,
        out string blocker)
    {
        blocker = string.Empty;
        var ordered = new List<DadParticipantSnapshot>(manifest.Slots.Count);
        foreach (var slot in manifest.Slots.OrderBy(static slot => DadPlannerSlotRules.GetSlotSortKey(slot.SlotId)))
        {
            var matches = participants.Where(participant =>
                    string.Equals(participant.AssignedSlotId, slot.SlotId, StringComparison.OrdinalIgnoreCase) &&
                    (slot.RouteKind == DadRunSlotRouteKind.RegisteredIsland
                        ? !string.IsNullOrWhiteSpace(participant.RegisteredIslandId) &&
                          string.Equals(
                              participant.RegisteredIslandId,
                              slot.IslandId,
                              StringComparison.Ordinal) &&
                          !participant.WorkerSessionId.IsEmpty
                        : string.Equals(
                              participant.WorkerSessionId.Value,
                              slot.WorkerSessionId.Value,
                              StringComparison.OrdinalIgnoreCase) &&
                          DadRosterIdentity.SameAccount(participant.ManagedAccountKey, slot.AccountKey) &&
                          DadRosterIdentity.SameCharacter(
                              participant.ActiveCharacterKey,
                              participant.Character.ContentId,
                              slot.CharacterKey,
                              slot.ContentId)))
                .ToList();
            if (matches.Count != 1)
            {
                blocker = slot.RouteKind == DadRunSlotRouteKind.RegisteredIsland
                    ? $"{slot.SlotId} must resolve to one exact active registered-island runtime row; found {matches.Count}."
                    : $"{slot.SlotId} must resolve to one exact frozen worker/account/character/Content-ID row; found {matches.Count}.";
                return [];
            }

            ordered.Add(matches[0]);
        }

        return ordered;
    }

    private static bool TryBuildFrozenPartyAuthority(
        DadRunPlan plan,
        DadRunSlotManifest manifest,
        IReadOnlyList<DadParticipantSnapshot> ordered,
        IReadOnlyDictionary<string, DadNativePartyInviteTarget>? runtimeInviteTargets,
        out DadExpectedPartyInviter inviter,
        out List<DadNativePartyInviteTarget> inviteTargets,
        out string blocker)
    {
        inviter = new DadExpectedPartyInviter();
        inviteTargets = [];
        blocker = string.Empty;
        var slotOne = manifest.Slots.SingleOrDefault(static slot =>
            slot.IsLeader &&
            slot.IsInviter &&
            string.Equals(slot.SlotId, DadPlannerSlotRules.LeaderSlotId, StringComparison.OrdinalIgnoreCase));
        var remoteSlotOne = slotOne?.RouteKind == DadRunSlotRouteKind.RegisteredIsland;
        if (slotOne == null ||
            manifest.Slots.Count(static slot => slot.IsLeader) != 1 ||
            manifest.Slots.Count(static slot => slot.IsInviter) != 1 ||
            !string.Equals(manifest.LeaderCharacterKey, manifest.InviterCharacterKey, StringComparison.OrdinalIgnoreCase) ||
            (remoteSlotOne
                ? !string.Equals(
                      manifest.LeaderCharacterKey,
                      DadRunSlotManifestRules.RegisteredIslandSlotOneAuthority,
                      StringComparison.OrdinalIgnoreCase) ||
                  !string.Equals(
                      plan.LeaderCharacterKey,
                      DadRunSlotManifestRules.RegisteredIslandSlotOneAuthority,
                      StringComparison.OrdinalIgnoreCase)
                : !string.Equals(manifest.LeaderCharacterKey, slotOne.CharacterKey.Value, StringComparison.OrdinalIgnoreCase) ||
                  !string.Equals(plan.LeaderCharacterKey, slotOne.CharacterKey.Value, StringComparison.OrdinalIgnoreCase)) ||
            !string.Equals(plan.InviterCharacterKey, plan.LeaderCharacterKey, StringComparison.OrdinalIgnoreCase))
        {
            blocker = "Party assembly requires one exact frozen Slot1 leader and inviter.";
            return false;
        }

        var leader = ordered.SingleOrDefault(participant =>
            string.Equals(participant.AssignedSlotId, slotOne.SlotId, StringComparison.OrdinalIgnoreCase));
        var remoteLeaderTarget = slotOne.RouteKind == DadRunSlotRouteKind.RegisteredIsland &&
                                 runtimeInviteTargets != null &&
                                 runtimeInviteTargets.TryGetValue(slotOne.SlotId, out var resolvedLeaderTarget)
            ? resolvedLeaderTarget
            : null;
        if (leader == null ||
            (remoteLeaderTarget == null &&
             (string.IsNullOrWhiteSpace(leader.Character.CharacterName) ||
              leader.Character.WorldId == 0 ||
              leader.Character.WorldId > ushort.MaxValue)))
        {
            blocker = "Frozen Slot1 inviter is missing its exact name or World ID.";
            return false;
        }

        inviter = new DadExpectedPartyInviter
        {
            RunId = plan.Request.RequestId,
            WorkerSessionId = remoteLeaderTarget?.WorkerSessionId ?? slotOne.WorkerSessionId,
            AccountKey = remoteLeaderTarget?.AccountKey ?? slotOne.AccountKey,
            CharacterKey = remoteLeaderTarget?.CharacterKey ?? slotOne.CharacterKey,
            ContentId = remoteLeaderTarget?.ContentId ?? slotOne.ContentId,
            CharacterName = remoteLeaderTarget?.CharacterName ?? leader.Character.CharacterName,
            WorldId = remoteLeaderTarget?.WorldId ?? (ushort)leader.Character.WorldId,
        };

        foreach (var slot in manifest.Slots.Where(static slot => !slot.IsLeader))
        {
            var participant = ordered.Single(candidate =>
                string.Equals(candidate.AssignedSlotId, slot.SlotId, StringComparison.OrdinalIgnoreCase));
            var remoteTarget = slot.RouteKind == DadRunSlotRouteKind.RegisteredIsland &&
                               runtimeInviteTargets != null &&
                               runtimeInviteTargets.TryGetValue(slot.SlotId, out var resolvedRemoteTarget)
                ? resolvedRemoteTarget
                : null;
            if (remoteTarget == null &&
                (string.IsNullOrWhiteSpace(participant.Character.CharacterName) ||
                 participant.Character.WorldId == 0 ||
                 participant.Character.WorldId > ushort.MaxValue))
            {
                blocker = $"{slot.SlotId} invite target is missing its exact name or World ID.";
                return false;
            }

            inviteTargets.Add(remoteTarget?.Clone() ?? new DadNativePartyInviteTarget
            {
                RunId = plan.Request.RequestId,
                ModuleId = plan.CompositeModuleId,
                SlotId = slot.SlotId,
                AccountKey = slot.AccountKey,
                CharacterKey = slot.CharacterKey,
                ContentId = slot.ContentId,
                CharacterName = participant.Character.CharacterName,
                WorldId = (ushort)participant.Character.WorldId,
                WorkerSessionId = slot.WorkerSessionId,
            });
        }

        return true;
    }

    public static bool IsParticipantInParty(
        DadParticipantSnapshot participant,
        IReadOnlyList<DadPartyMemberSnapshot> partyMembers)
        => IsParticipantInParty(participant, partyMembers, runtimeInviteTarget: null);

    internal static bool IsParticipantInParty(
        DadParticipantSnapshot participant,
        IReadOnlyList<DadPartyMemberSnapshot> partyMembers,
        DadNativePartyInviteTarget? runtimeInviteTarget)
    {
        var characterKey = participant.ActiveCharacterKey.Value;
        var contentId = participant.Character.ContentId != 0
            ? participant.Character.ContentId
            : runtimeInviteTarget?.ContentId ?? 0;
        if (contentId != 0)
            return partyMembers.Any(member => member.ContentId == contentId);

        return partyMembers.Any(member =>
            !member.CharacterKey.IsEmpty &&
            string.Equals(member.CharacterKey.Value, characterKey, StringComparison.OrdinalIgnoreCase));
    }

    public static bool ShouldDispatchJoinInstruction(
        DadParticipantSnapshot participant,
        IReadOnlyList<DadPartyMemberSnapshot> leaderPartyMembers)
        => !IsParticipantInParty(participant, leaderPartyMembers);
}
