using dad.Models;

namespace dad.Services;

public static class DadTrustPartySelector
{
    public static DadTrustPartySelection SelectFixed(DadTrustFixedParty party, uint contentFinderConditionId,
        DadAcquiredCharacter character, IEnumerable<DadLevelingJobDescriptor> jobCatalog,
        IEnumerable<DadPlannerDutyOption> dutyCatalog, DateTime? minimumObservedAtUtc = null, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DadClock.UtcNow;
        if (now.Kind != DateTimeKind.Utc || minimumObservedAtUtc is { Kind: not DateTimeKind.Utc })
            return Block("Trust observation times are unavailable.");
        if (party.Npcs is not { Count: 3 } || party.Npcs.Distinct().Count() != 3)
            return Block("A Trust preset requires exactly three different companions.");
        var jobs = jobCatalog.Where(job => job.JobId == party.PlayerJobId).ToArray();
        if (jobs.Length != 1 || !DadLevelingModeCompiler.IsEligibleForRole(jobs[0], DadPartyRole.Any) ||
            !character.JobLevels.TryGetValue(party.PlayerJobId, out var level) || level <= 0)
            return Block("The fixed player job is locked, unsupported, or missing its level reading.");
        var progress = DadTrustProgressRules.ForOwner(character.TrustProgress, new(character.AccountId),
            new(character.CharacterKey), character.ContentId);
        if (progress == null) return Block("Refresh this character's Trust roster before choosing a party.");
        var duties = dutyCatalog.Where(duty => duty.ContentFinderConditionId == contentFinderConditionId && duty.SupportsTrust).ToArray();
        var reading = progress.Duties.SingleOrDefault(duty => duty.ContentFinderConditionId == contentFinderConditionId);
        if (duties.Length != 1 || duties[0].JobLevelRequired <= 0 || reading?.Unlocked != true || reading.Members == null ||
            !ReadIsCurrent(reading.ObservedAtUtc, minimumObservedAtUtc, now))
            return Block("Choose an unlocked Trust dungeon with a current party-availability reading.");
        var duty = duties[0];
        if (level < duty.JobLevelRequired) return Block("The fixed player job is below the dungeon's required level.");
        var npcs = new List<DadTrustNpcObservation>();
        var members = new List<DadTrustDutyMember>();
        foreach (var id in party.Npcs)
        {
            var npc = progress.Npcs.SingleOrDefault(npc => npc.NpcId == id);
            var member = reading.Members.SingleOrDefault(member => member.NpcId == id);
            if (npc?.Unlocked != true || npc.Level is not > 0 || npc.Level < duty.JobLevelRequired ||
                member == null || !ReadIsCurrent(npc.ObservedAtUtc, minimumObservedAtUtc, now))
                return Block("A selected companion cannot enter this dungeon, or needs a current level/unlock reading.");
            npcs.Add(npc);
            members.Add(member);
        }
        if (!TryAssignRoles(jobs[0].Role, members, out var roles)) return Block("The fixed Trust party needs one tank, one healer and two damage dealers, including your job.");
        return new()
        {
            Status = DadLevelingCompilationStatus.Ready, Duty = duty, PlayerJobId = party.PlayerJobId, PlayerLevel = level,
            Npcs = npcs.Select((npc, index) => new DadTrustSelectedNpc(npc.NpcId, roles[index], npc.Level!.Value, false)).ToList(),
            Summary = "Fixed player job, three companions and compatible dungeon.",
        };
    }

    public static DadTrustPartySelection SelectLeveling(DadTrustLevelingOptions options,
        DadAcquiredCharacter character, IEnumerable<DadLevelingJobDescriptor> jobCatalog,
        IEnumerable<DadPlannerDutyOption> dutyCatalog, DateTime? minimumObservedAtUtc = null, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DadClock.UtcNow;
        if (now.Kind != DateTimeKind.Utc || minimumObservedAtUtc is { Kind: not DateTimeKind.Utc })
            return Block("Trust observation times are unavailable.");
        if (options.NpcTargetLevel is < 1 or > 999 || options.LevelPlayerJobs && options.PlayerTargetLevel is < 1 or > 999)
            return Block("Trust target levels must be between 1 and 999.");
        var progress = DadTrustProgressRules.ForOwner(character.TrustProgress, new(character.AccountId),
            new(character.CharacterKey), character.ContentId);
        if (progress == null) return Block("Refresh this character's Trust roster before choosing a party.");
        if (options.AllAvailableNpcs && (progress.RosterObservedAtUtc is not { } rosterRead || !ReadIsCurrent(rosterRead, minimumObservedAtUtc, now)))
            return Block("Refresh the full Trust roster before balancing all available companions.");
        if (options.AllAvailableNpcs && progress.Npcs.Any(static npc => npc.Unlocked == null))
            return Block("A companion's availability is still unknown; refresh the Trust roster.");
        var npcs = progress.Npcs.ToDictionary(static npc => npc.NpcId);
        var targetIds = options.AllAvailableNpcs
            ? progress.Npcs.Where(static npc => npc.Unlocked == true).Select(static npc => npc.NpcId).ToHashSet()
            : (options.SelectedNpcs ?? []).ToHashSet();
        foreach (var id in targetIds)
        {
            if (!npcs.TryGetValue(id, out var npc) || npc.Unlocked != true || npc.Level is not > 0 ||
                !ReadIsCurrent(npc.ObservedAtUtc, minimumObservedAtUtc, now))
                return Block("A selected companion needs a current level and unlock reading.");
        }
        if (targetIds.Count == 0 && !options.LevelPlayerJobs)
            return Block("Select available companions to level, or enable player-job leveling.");
        var pendingNpcs = targetIds.Where(id => npcs[id].Level <
            Math.Min(options.NpcTargetLevel, npcs[id].LevelCap ?? options.NpcTargetLevel)).ToHashSet();

        var descriptors = jobCatalog.Where(static job => job != null).GroupBy(static job => job.JobId).ToArray();
        if (descriptors.Any(static group => group.Count() != 1)) return Block("The combat-job catalog is ambiguous.");
        var jobs = descriptors.ToDictionary(static group => group.Key, static group => group.Single());
        var permitted = (options.AllowedJobs ?? []).Distinct().ToArray();
        if (permitted.Length == 0) return Block("Choose at least one job DAD may use.");
        foreach (var id in permitted)
        {
            if (!jobs.TryGetValue(id, out var job) || !DadLevelingModeCompiler.IsEligibleForRole(job, DadPartyRole.Any) ||
                !character.JobLevels.TryGetValue(id, out var level) || level <= 0)
                return Block("A permitted player job is locked, unsupported, or missing its level reading.");
        }
        var pendingJobs = permitted.Where(id => options.LevelPlayerJobs && character.JobLevels[id] < options.PlayerTargetLevel).ToHashSet();
        if (pendingNpcs.Count == 0 && pendingJobs.Count == 0)
            return new() { Status = DadLevelingCompilationStatus.Complete, Summary = "All selected Trust leveling goals are reached." };

        var dutyReadings = progress.Duties.ToDictionary(static duty => duty.ContentFinderConditionId);
        var duties = dutyCatalog.Where(static duty => duty.SupportsTrust && duty.JobLevelRequired > 0).ToArray();
        if (duties.GroupBy(static duty => duty.ContentFinderConditionId).Any(static group => group.Count() != 1))
            return Block("The Trust dungeon catalog is ambiguous.");
        DadTrustPartySelection? best = null;
        int[]? bestGoals = null;
        foreach (var duty in duties)
        {
            if (!dutyReadings.TryGetValue(duty.ContentFinderConditionId, out var reading) || reading.Unlocked != true ||
                reading.Members == null || !ReadIsCurrent(reading.ObservedAtUtc, minimumObservedAtUtc, now)) continue;
            var members = reading.Members.Where(member => member.AvailableRoles != DadTrustRoles.None &&
                    npcs.TryGetValue(member.NpcId, out var npc) && npc.Unlocked == true && npc.Level >= duty.JobLevelRequired &&
                    ReadIsCurrent(npc.ObservedAtUtc, minimumObservedAtUtc, now))
                .OrderBy(static member => member.NpcId).ToArray();
            foreach (var jobId in permitted)
            {
                var playerLevel = character.JobLevels[jobId];
                if (playerLevel < duty.JobLevelRequired) continue;
                for (var a = 0; a < members.Length - 2; a++)
                for (var b = a + 1; b < members.Length - 1; b++)
                for (var c = b + 1; c < members.Length; c++)
                {
                    var party = new[] { members[a], members[b], members[c] };
                    if (!TryAssignRoles(jobs[jobId].Role, party, out var roles)) continue;
                    var goals = party.Where(member => pendingNpcs.Contains(member.NpcId)).Select(member => npcs[member.NpcId].Level!.Value).ToList();
                    if (pendingJobs.Contains(jobId)) goals.Add(playerLevel);
                    if (goals.Count == 0) continue;
                    var goalLevels = goals.Order().Concat(Enumerable.Repeat(int.MaxValue, 4 - goals.Count)).ToArray();
                    if (best != null && CompareGoals(goalLevels, bestGoals!) > 0) continue;
                    if (best != null && CompareGoals(goalLevels, bestGoals!) == 0 &&
                        !PreferDutyAndJob(duty, jobId, best, character.CurrentJobId)) continue;
                    best = new()
                    {
                        Status = DadLevelingCompilationStatus.Ready,
                        Duty = duty,
                        PlayerJobId = jobId,
                        PlayerLevel = playerLevel,
                        IsPlayerLevelingGoal = pendingJobs.Contains(jobId),
                        Npcs = party.Select((member, index) => new DadTrustSelectedNpc(member.NpcId, roles[index],
                            npcs[member.NpcId].Level!.Value, pendingNpcs.Contains(member.NpcId))).ToList(),
                        Summary = "Lowest eligible selected levels, with a compatible party and dungeon.",
                    };
                    bestGoals = goalLevels;
                }
            }
        }
        return best ?? Block("No unlocked Trust dungeon can level the unfinished selected goals with the permitted jobs and compatible companions.");
    }

    public static bool TryAssignRoles(DadPartyRole playerRole, IReadOnlyList<DadTrustDutyMember> members,
        out DadTrustRoles[] roles)
    {
        roles = new DadTrustRoles[3];
        if (members.Count != 3 || members.Any(static member => member == null || member.NpcId == DadTrustNpcId.Unknown ||
                !Enum.IsDefined(member.NpcId) || (member.AvailableRoles & ~DadTrustRoles.All) != 0) ||
            members.Select(static member => member.NpcId).Distinct().Count() != 3) return false;
        var required = new Dictionary<DadTrustRoles, int> { [DadTrustRoles.Tank] = 1, [DadTrustRoles.Healer] = 1, [DadTrustRoles.Dps] = 2 };
        var player = playerRole switch
        {
            DadPartyRole.Tank => DadTrustRoles.Tank,
            DadPartyRole.Healer => DadTrustRoles.Healer,
            DadPartyRole.Dps or DadPartyRole.Melee or DadPartyRole.PhysicalRanged or DadPartyRole.Caster => DadTrustRoles.Dps,
            _ => DadTrustRoles.None,
        };
        if (player == DadTrustRoles.None) return false;
        required[player]--;
        var assigned = roles;
        return Assign(0);

        bool Assign(int index)
        {
            if (index == 3) return required.Values.All(static count => count == 0);
            foreach (var role in new[] { DadTrustRoles.Tank, DadTrustRoles.Healer, DadTrustRoles.Dps })
            {
                if (required[role] == 0 || (members[index].AvailableRoles & role) == 0) continue;
                required[role]--;
                assigned[index] = role;
                if (Assign(index + 1)) return true;
                required[role]++;
            }
            return false;
        }
    }

    private static bool PreferDutyAndJob(DadPlannerDutyOption duty, uint jobId, DadTrustPartySelection prior, uint? currentJobId)
    {
        if (duty.JobLevelRequired != prior.Duty!.JobLevelRequired) return duty.JobLevelRequired > prior.Duty.JobLevelRequired;
        if (duty.ContentFinderConditionId != prior.Duty.ContentFinderConditionId) return duty.ContentFinderConditionId < prior.Duty.ContentFinderConditionId;
        if ((jobId == currentJobId) != (prior.PlayerJobId == currentJobId)) return jobId == currentJobId;
        return jobId < prior.PlayerJobId;
    }

    private static int CompareGoals(IReadOnlyList<int> left, IReadOnlyList<int> right)
    {
        for (var i = 0; i < left.Count; i++)
            if (left[i] != right[i]) return left[i].CompareTo(right[i]);
        return 0;
    }

    private static bool ReadIsCurrent(DateTime reading, DateTime? minimum, DateTime now)
        => reading <= now && (minimum == null || reading >= minimum.Value);
    private static DadTrustPartySelection Block(string reason) => new() { Summary = reason };
}
