using dad.Models;

namespace dad.Services;

internal static class DadCrewFormationPlannerRules
{
    public static bool TryBuildPlan(
        DadRunRequest request,
        DadCharacterPool pool,
        Configuration configuration,
        DadAcquiredCharacter? activeCoordinatorCharacter,
        bool requireLiveReadiness,
        bool allowWakeableCoordinatorLeader,
        out DadRunPlan plan,
        out string rejectionReason)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(configuration);

        plan = new DadRunPlan();
        rejectionReason = string.Empty;
        var orchestration = request.Orchestration;
        var roster = orchestration.RequiredRosterCharacters ?? [];
        var expectedPartySize = orchestration.RosterIntent?.ExpectedPartySize ?? 0;

        if (expectedPartySize < 2)
            return Fail("Crew Formation requires at least two exact roster characters.", out rejectionReason);
        if (roster.Count != expectedPartySize)
        {
            return Fail(
                $"Crew Formation roster requires exactly {expectedPartySize} character(s), received {roster.Count}.",
                out rejectionReason);
        }

        for (var index = 0; index < roster.Count; index++)
        {
            var reference = roster[index];
            var slotId = DadPlannerSlotRules.FormatSlotId(index + 1);
            if (reference.AccountKey.IsEmpty || reference.CharacterKey.IsEmpty || reference.ContentId == 0)
            {
                return Fail(
                    $"{slotId} requires an exact managed account, character key, and non-zero Content ID for Crew Formation.",
                    out rejectionReason);
            }
        }

        if (!ValidateUniqueRoster(roster, out rejectionReason))
            return false;

        var resolved = new List<DadAcquiredCharacter>(roster.Count);
        for (var index = 0; index < roster.Count; index++)
        {
            var reference = roster[index];
            var slotId = DadPlannerSlotRules.FormatSlotId(index + 1);
            var matches = pool.Characters
                .Where(character => Matches(reference, character))
                .ToList();
            if (matches.Count != 1)
            {
                return Fail(
                    matches.Count == 0
                        ? $"{slotId} exact roster identity '{reference.CharacterKey}' on account '{reference.AccountKey}' is unresolved."
                        : $"{slotId} exact roster identity '{reference.CharacterKey}' on account '{reference.AccountKey}' is ambiguous.",
                    out rejectionReason);
            }

            var character = activeCoordinatorCharacter != null &&
                            Matches(reference, activeCoordinatorCharacter)
                ? activeCoordinatorCharacter
                : matches[0];
            if (!ValidateRequestedJob(slotId, reference, character, out rejectionReason))
                return false;
            if (character.Blockers.Any(IsLocalIsolationReason))
            {
                return Fail(
                    $"{slotId} character '{reference.CharacterKey}' is local-only/isolated and cannot accept Crew Formation work.",
                    out rejectionReason);
            }
            if (requireLiveReadiness && !IsConnectedForRuntime(character))
            {
                return Fail(
                    $"{slotId} character '{reference.CharacterKey}' is not live, ready, and post-AR ready.",
                    out rejectionReason);
            }

            resolved.Add(character);
        }

        var leaderCharacterKey = orchestration.PreferredLeaderCharacterKey.IsEmpty
            ? roster[0].CharacterKey.Value
            : orchestration.PreferredLeaderCharacterKey.Value;
        if (!string.Equals(leaderCharacterKey, roster[0].CharacterKey.Value, StringComparison.OrdinalIgnoreCase))
        {
            return Fail(
                $"Crew Formation Slot1 must be the exact leader '{roster[0].CharacterKey}', not '{leaderCharacterKey}'.",
                out rejectionReason);
        }

        if (orchestration.AuthorityMode != DadAuthorityMode.ServerDad)
            return Fail("Crew Formation requires ServerDad coordinator authority.", out rejectionReason);
        if (orchestration.LocalOnlyOverride || orchestration.TransportMode != DadTransportMode.ServerHub)
            return Fail("Crew Formation requires the authenticated Dad Coordinator hub transport.", out rejectionReason);
        if (orchestration.QueueAuthority != DadQueueAuthority.Leader)
        {
            return Fail(
                $"Crew Formation requires leader queue authority; request has {orchestration.QueueAuthority}.",
                out rejectionReason);
        }

        var leader = resolved[0];
        if (!DadFullPartyExecutionRules.TryValidatePlannedCoordinatorLeader(
                request,
                leader,
                new DadAccountKey(string.Empty),
                activeCoordinatorCharacter,
                requireExactLocalIdentity: requireLiveReadiness,
                allowWakeableCoordinatorLeader: !requireLiveReadiness && allowWakeableCoordinatorLeader,
                out rejectionReason))
        {
            return false;
        }

        if (orchestration.InviteAuthority is DadInviteAuthority.External or DadInviteAuthority.NotNeeded)
            return Fail("Crew Formation requires Slot1 as the executable inviter.", out rejectionReason);
        if (!orchestration.PreferredInviterCharacterKey.IsEmpty &&
            !string.Equals(
                orchestration.PreferredInviterCharacterKey.Value,
                leaderCharacterKey,
                StringComparison.OrdinalIgnoreCase))
        {
            return Fail(
                $"Crew Formation inviter '{orchestration.PreferredInviterCharacterKey}' must be exact Slot1 '{leaderCharacterKey}'.",
                out rejectionReason);
        }
        var inviterCharacterKey = leaderCharacterKey;

        var module = new DadPlannedModuleExecution
        {
            ModuleId = DadModuleId.None,
            DisplayName = "Crew Formation",
            OwnerLabel = "Dad Coordinator",
            ExpectedPartySize = expectedPartySize,
            RequiresPeers = true,
            Summary = $"Form exact {expectedPartySize}-character crew.",
        };
        plan = new DadRunPlan
        {
            Request = request,
            CompositeModuleId = DadModuleId.None,
            Orchestration = orchestration,
            Summary = module.Summary,
            RequiredParticipantCount = expectedPartySize,
            RequiresRemoteParticipants = true,
            LeaderCharacterKey = leaderCharacterKey,
            InviterCharacterKey = inviterCharacterKey,
            Modules = [module],
            PlannerWarnings = [],
        };
        return true;
    }

    private static bool ValidateUniqueRoster(
        IReadOnlyList<DadRosterCharacterRef> roster,
        out string rejectionReason)
    {
        var duplicateAccount = roster
            .GroupBy(static reference => Normalize(reference.AccountKey.Value), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateAccount != null)
            return Fail($"Managed account '{duplicateAccount.Key}' appears in multiple Crew Formation slots.", out rejectionReason);

        var duplicateCharacter = roster
            .GroupBy(static reference => Normalize(reference.CharacterKey.Value), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateCharacter != null)
            return Fail($"Character '{duplicateCharacter.Key}' appears in multiple Crew Formation slots.", out rejectionReason);

        var duplicateContentId = roster
            .GroupBy(static reference => reference.ContentId)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateContentId != null)
            return Fail($"Content ID {duplicateContentId.Key} appears in multiple Crew Formation slots.", out rejectionReason);

        rejectionReason = string.Empty;
        return true;
    }

    private static bool ValidateRequestedJob(
        string slotId,
        DadRosterCharacterRef reference,
        DadAcquiredCharacter character,
        out string rejectionReason)
    {
        if (!reference.RequiredJobId.HasValue)
        {
            rejectionReason = string.Empty;
            return true;
        }

        var jobId = reference.RequiredJobId.Value;
        if (!DadRosterCharacterMerge.IsCombatJob(jobId))
            return Fail($"{slotId} requested class/job {jobId} is not a supported combat job.", out rejectionReason);
        if (character.JobLevels == null ||
            !character.JobLevels.TryGetValue(jobId, out var level) ||
            level <= 0)
        {
            return Fail(
                $"{slotId} requested class/job {jobId}, but '{reference.CharacterKey}' has no positive learned-job ledger entry for it.",
                out rejectionReason);
        }

        rejectionReason = string.Empty;
        return true;
    }

    private static bool Matches(DadRosterCharacterRef reference, DadAcquiredCharacter character)
    {
        var account = DadRosterIdentity.ResolveAccountKey(character.AccountId, character.AccountAlias);
        return !account.IsEmpty &&
               DadRosterIdentity.SameAccount(reference.AccountKey, account) &&
               DadRosterIdentity.SameCharacter(
                   reference.CharacterKey,
                   reference.ContentId,
                   new DadCharacterKey(character.CharacterKey),
                   character.ContentId);
    }

    private static bool IsConnectedForRuntime(DadAcquiredCharacter character)
        => character.Source is DadCharacterSource.LocalRuntime or DadCharacterSource.PeerRuntime
           && character.Freshness is DadSnapshotFreshness.Live or DadSnapshotFreshness.Recent
           && character.Readiness == DadReadinessState.Ready;

    private static bool IsLocalIsolationReason(string blocker)
        => blocker.Contains("local-only", StringComparison.OrdinalIgnoreCase)
           || blocker.Contains("local only", StringComparison.OrdinalIgnoreCase)
           || blocker.Contains("isolated", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? value)
        => (value ?? string.Empty).Trim();

    private static bool Fail(string reason, out string rejectionReason)
    {
        rejectionReason = reason;
        return false;
    }
}
