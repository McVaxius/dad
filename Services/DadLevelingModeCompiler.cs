using dad.Models;

namespace dad.Services;

/// <summary>
/// Compiles one immutable ordinary planner child from exact roster truth. It never mutates the saved plan.
/// </summary>
public static class DadLevelingModeCompiler
{
    public static DadLevelingCompilation Compile(
        DadPlannerGroup source,
        DadCharacterPool pool,
        IEnumerable<DadLevelingJobDescriptor> jobCatalog,
        IEnumerable<DadPlannerDutyOption> dutyCatalog,
        int iteration = 1,
        Func<string>? idFactory = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(jobCatalog);
        ArgumentNullException.ThrowIfNull(dutyCatalog);

        var result = new DadLevelingCompilation { Iteration = Math.Max(1, iteration) };
        var options = source.LevelingMode?.Clone() ?? new DadLevelingModeOptions();
        if (!options.Enabled)
            return Block(result, "Leveling Mode is disabled.");
        if (options.GoalLevel is < 1 or > 999)
            return Block(result, "Leveling Mode goal must be between level 1 and 999.");
        if (!Enum.IsDefined(options.JobOrder))
            return Block(result, "Leveling Mode job order is invalid.");

        if (!TryNormalizeLane(source.ActivityMode, out var childLane, out var runFamily, out var npcLane))
            return Block(result, $"Leveling Mode does not support the {source.ActivityMode} lane.");

        var primaryRows = DadPlannerSlotRules.GetPrimaryRows(source.Slots);
        if (npcLane)
            primaryRows = primaryRows.Where(static row => DadPlannerSlotRules.IsLeaderSlot(row.SlotId)).ToList();
        if (primaryRows.Count == 0)
            return Block(result, "Leveling Mode requires at least one fixed primary crew slot.");

        var descriptors = BuildDescriptorCatalog(jobCatalog, result.Blockers);
        var duties = BuildDutyCatalog(dutyCatalog, result.Blockers);
        ValidateThresholds(options.DutyThresholds, duties, childLane, npcLane, primaryRows.Count, result.Blockers);
        if (result.Blockers.Count > 0)
            return FinishBlocked(result);

        foreach (var row in primaryRows)
        {
            if (row.RequiredAccountKey.IsEmpty || row.RequiredCharacterKey.IsEmpty || row.SharedIdentity != null)
            {
                result.Blockers.Add($"{row.SlotId} requires an exact configured local account and character for Leveling Mode.");
                continue;
            }

            var matches = pool.Characters
                .Where(character => CharacterMatches(row, character))
                .ToList();
            if (matches.Count != 1)
            {
                result.Blockers.Add(matches.Count == 0
                    ? $"{row.SlotId} could not resolve exact character {row.RequiredCharacterKey.Value} on account {row.RequiredAccountKey.Value}."
                    : $"{row.SlotId} resolved multiple roster rows for {row.RequiredCharacterKey.Value}; exact roster truth is required.");
                continue;
            }

            var character = matches[0];
            if (!HasCompleteLedger(character, out var ledgerBlocker))
            {
                result.Blockers.Add($"{row.SlotId} {row.RequiredCharacterKey.Value}: {ledgerBlocker}");
                continue;
            }

            var eligible = character.JobLevels
                .Where(static pair => pair.Key > 0 && pair.Value > 0)
                .Where(pair => descriptors.TryGetValue(pair.Key, out var descriptor) &&
                               IsEligibleForRole(descriptor, row.RequiredRole))
                .Select(pair => new EligibleJob(descriptors[pair.Key], pair.Value))
                .OrderBy(static job => job.Descriptor.JobId)
                .ToList();
            if (eligible.Count == 0)
            {
                result.Blockers.Add($"{row.SlotId} {row.RequiredCharacterKey.Value} has no unlocked full combat jobs compatible with role {row.RequiredRole}.");
                continue;
            }

            var belowGoal = eligible.Where(job => job.Level < options.GoalLevel).ToList();
            var complete = belowGoal.Count == 0;
            EligibleJob selected;
            if (complete)
            {
                selected = eligible.FirstOrDefault(job => character.CurrentJobId == job.Descriptor.JobId)
                           ?? eligible.OrderByDescending(static job => job.Level)
                               .ThenBy(static job => job.Descriptor.JobId)
                               .First();
            }
            else
            {
                selected = options.JobOrder == DadLevelingJobOrder.HighestBelowGoal
                    ? belowGoal.OrderByDescending(static job => job.Level)
                        .ThenBy(static job => job.Descriptor.JobId)
                        .First()
                    : belowGoal.OrderBy(static job => job.Level)
                        .ThenBy(static job => job.Descriptor.JobId)
                        .First();
            }

            result.Slots.Add(new DadLevelingSlotSelection
            {
                SlotId = row.SlotId,
                AccountKey = row.RequiredAccountKey,
                CharacterKey = row.RequiredCharacterKey,
                ContentId = character.ContentId,
                Role = row.RequiredRole,
                JobId = selected.Descriptor.JobId,
                JobAbbreviation = selected.Descriptor.Abbreviation,
                JobLevel = selected.Level,
                SlotComplete = complete,
                IsFiller = complete,
                Summary = complete
                    ? $"{row.SlotId}: complete; filler {selected.Descriptor.Abbreviation} level {selected.Level}."
                    : $"{row.SlotId}: {selected.Descriptor.Abbreviation} level {selected.Level} -> goal {options.GoalLevel}.",
            });
        }

        if (result.Blockers.Count > 0)
            return FinishBlocked(result);
        if (result.Slots.Count != primaryRows.Count)
            return Block(result, "Leveling Mode could not produce an exact job selection for every active slot.");

        result.PartyMinimumLevel = result.Slots.Min(static slot => slot.JobLevel);
        var applicable = options.DutyThresholds
            .Where(threshold => threshold.MinimumLevel <= result.PartyMinimumLevel)
            .LastOrDefault();
        if (applicable == null)
            return Block(result, $"No Leveling Mode duty threshold applies to party minimum level {result.PartyMinimumLevel}; no fallback duty was selected.");

        result.SelectedDuty = duties[applicable.ContentFinderConditionId];
        if (result.Slots.All(static slot => slot.SlotComplete))
        {
            result.Status = DadLevelingCompilationStatus.Complete;
            result.Summary = $"Leveling Mode complete: every eligible job in all {result.Slots.Count} active slot(s) reached goal {options.GoalLevel}.";
            return result;
        }

        idFactory ??= static () => Guid.NewGuid().ToString("N");
        result.ChildJobId = idFactory()?.Trim() ?? string.Empty;
        result.ChildRequestId = idFactory()?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(result.ChildJobId) || string.IsNullOrWhiteSpace(result.ChildRequestId))
            return Block(result, "Leveling Mode child job and request identities must both be non-empty.");
        if (string.Equals(result.ChildJobId, result.ChildRequestId, StringComparison.OrdinalIgnoreCase))
            return Block(result, "Leveling Mode child job and request identities must be unique.");

        result.ChildGroup = BuildFrozenChild(source, primaryRows, result.Slots, result.SelectedDuty, childLane, runFamily, npcLane);
        result.Status = DadLevelingCompilationStatus.Ready;
        result.Summary = $"Leveling child {result.Iteration}: {result.SelectedDuty.DutyDisplayName} at party minimum level {result.PartyMinimumLevel}; " +
                         string.Join(" ", result.Slots.Select(static slot => slot.Summary));
        return result;
    }

    public static bool TryNormalizeLane(
        DadPlannerActivityMode source,
        out DadPlannerActivityMode childLane,
        out DadPlannerRunFamily runFamily,
        out bool npcLane)
    {
        childLane = source switch
        {
            DadPlannerActivityMode.DutySupportLeveling => DadPlannerActivityMode.DutySupport,
            DadPlannerActivityMode.TrustLeveling => DadPlannerActivityMode.Trust,
            DadPlannerActivityMode.DutyPremade => DadPlannerActivityMode.PremadeDuty,
            _ => source,
        };
        npcLane = childLane is DadPlannerActivityMode.DutySupport or DadPlannerActivityMode.Trust;
        runFamily = npcLane ? DadPlannerRunFamily.LevelingNpc : DadPlannerRunFamily.DutyFinder;
        return npcLane || childLane == DadPlannerActivityMode.PremadeDuty;
    }

    public static bool IsEligibleForRole(DadLevelingJobDescriptor descriptor, DadPartyRole role)
    {
        if (!descriptor.IsFullCombatJob || descriptor.IsLimitedJob || descriptor.JobId == 0)
            return false;

        return role switch
        {
            DadPartyRole.Any => descriptor.Role is DadPartyRole.Tank or DadPartyRole.Healer or DadPartyRole.Dps
                or DadPartyRole.Melee or DadPartyRole.PhysicalRanged or DadPartyRole.Caster,
            DadPartyRole.Dps => descriptor.Role is DadPartyRole.Dps or DadPartyRole.Melee
                or DadPartyRole.PhysicalRanged or DadPartyRole.Caster,
            DadPartyRole.Limited => false,
            _ => descriptor.Role == role,
        };
    }

    private static Dictionary<uint, DadLevelingJobDescriptor> BuildDescriptorCatalog(
        IEnumerable<DadLevelingJobDescriptor> source,
        ICollection<string> blockers)
    {
        var catalog = new Dictionary<uint, DadLevelingJobDescriptor>();
        foreach (var descriptor in source.Where(static descriptor => descriptor != null))
        {
            if (descriptor.JobId == 0)
                continue;
            if (!catalog.TryAdd(descriptor.JobId, descriptor))
                blockers.Add($"Job catalog contains duplicate job ID {descriptor.JobId}.");
        }
        if (catalog.Count == 0)
            blockers.Add("Leveling Mode job catalog is unavailable.");
        return catalog;
    }

    private static Dictionary<uint, DadPlannerDutyOption> BuildDutyCatalog(
        IEnumerable<DadPlannerDutyOption> source,
        ICollection<string> blockers)
    {
        var catalog = new Dictionary<uint, DadPlannerDutyOption>();
        foreach (var duty in source.Where(static duty => duty != null && duty.ContentFinderConditionId > 0))
        {
            if (!catalog.TryAdd(duty.ContentFinderConditionId, duty))
                blockers.Add($"Duty catalog contains duplicate ContentFinderCondition ID {duty.ContentFinderConditionId}.");
        }
        return catalog;
    }

    private static void ValidateThresholds(
        IReadOnlyList<DadLevelingDutyThreshold>? thresholds,
        IReadOnlyDictionary<uint, DadPlannerDutyOption> duties,
        DadPlannerActivityMode childLane,
        bool npcLane,
        int partySize,
        ICollection<string> blockers)
    {
        if (thresholds == null || thresholds.Count == 0)
        {
            blockers.Add("Leveling Mode requires at least one ordered duty threshold.");
            return;
        }

        var previous = 0;
        for (var index = 0; index < thresholds.Count; index++)
        {
            var threshold = thresholds[index];
            if (threshold == null)
            {
                blockers.Add($"Duty threshold row {index + 1} is missing.");
                continue;
            }
            if (threshold.MinimumLevel is < 1 or > 999)
                blockers.Add($"Duty threshold row {index + 1} has invalid minimum level {threshold.MinimumLevel}.");
            if (index > 0 && threshold.MinimumLevel <= previous)
                blockers.Add($"Duty thresholds must be strictly increasing; row {index + 1} level {threshold.MinimumLevel} follows {previous}.");
            previous = threshold.MinimumLevel;

            if (threshold.ContentFinderConditionId == 0 ||
                !duties.TryGetValue(threshold.ContentFinderConditionId, out var duty))
            {
                blockers.Add($"Duty threshold row {index + 1} references unavailable duty {threshold.ContentFinderConditionId}.");
                continue;
            }
            if (threshold.MinimumLevel < duty.JobLevelRequired)
                blockers.Add($"Duty threshold {threshold.MinimumLevel} is below {duty.DutyDisplayName}'s required level {duty.JobLevelRequired}.");
            if (!SupportsLane(duty, childLane))
                blockers.Add($"Duty {duty.DutyDisplayName} is incompatible with the {childLane} lane.");
            if (!npcLane && Math.Max(1, duty.QueueSize) != partySize)
                blockers.Add($"Duty {duty.DutyDisplayName} requires {Math.Max(1, duty.QueueSize)} players, but the fixed Leveling Mode crew has {partySize} slot(s).");
        }
    }

    private static bool SupportsLane(DadPlannerDutyOption duty, DadPlannerActivityMode lane)
        => lane switch
        {
            DadPlannerActivityMode.DutySupport => duty.SupportsDutySupport,
            DadPlannerActivityMode.Trust => duty.SupportsTrust,
            DadPlannerActivityMode.PremadeDuty => true,
            _ => false,
        };

    private static bool CharacterMatches(DadPlannerGroupSlot row, DadAcquiredCharacter character)
    {
        if (!string.Equals(row.RequiredCharacterKey.Value?.Trim(), character.CharacterKey?.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        var account = DadRosterIdentity.ResolveAccountKey(character.AccountId, character.AccountAlias);
        return !account.IsEmpty && DadRosterIdentity.SameAccount(row.RequiredAccountKey, account);
    }

    private static bool HasCompleteLedger(DadAcquiredCharacter character, out string blocker)
    {
        if (!character.XadbReady || character.SnapshotVersion is null or <= 0 || !character.XadbSnapshotUtc.HasValue)
        {
            blocker = "an exact XADB job ledger is unavailable.";
            return false;
        }
        if (character.NeedsRosterUpdate)
        {
            blocker = "the roster ledger requires refresh.";
            return false;
        }
        if (character.JobLevels == null || character.JobLevels.Count == 0)
        {
            blocker = "the job ledger is empty.";
            return false;
        }
        var quality = character.SnapshotQuality?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(quality) ||
            quality.Contains("partial", StringComparison.OrdinalIgnoreCase) ||
            quality.Contains("unknown", StringComparison.OrdinalIgnoreCase) ||
            quality.Contains("empty", StringComparison.OrdinalIgnoreCase) ||
            quality.Contains("missing", StringComparison.OrdinalIgnoreCase) ||
            quality.Contains("invalid", StringComparison.OrdinalIgnoreCase))
        {
            blocker = $"job ledger quality '{(string.IsNullOrWhiteSpace(quality) ? "unknown" : quality)}' is not complete.";
            return false;
        }

        blocker = string.Empty;
        return true;
    }

    private static DadPlannerGroup BuildFrozenChild(
        DadPlannerGroup source,
        IReadOnlyList<DadPlannerGroupSlot> primaryRows,
        IReadOnlyList<DadLevelingSlotSelection> selections,
        DadPlannerDutyOption duty,
        DadPlannerActivityMode childLane,
        DadPlannerRunFamily runFamily,
        bool npcLane)
    {
        var child = DadSchedulerGroupCloneRules.CloneWithSlots(source, primaryRows);
        child.LevelingMode.Enabled = false;
        child.ActivityMode = childLane;
        child.RunFamily = runFamily;
        child.DutyContentFinderConditionId = duty.ContentFinderConditionId;
        child.DutyDisplayName = duty.DutyDisplayName;
        child.DutyUnsynced = false;
        child.DutyExpectedPartySize = npcLane ? 1 : selections.Count;
        child.StopPolicy = new DadRunStopPolicy
        {
            Mode = DadPlannerStopMode.AfterRuns,
            AfterRuns = 1,
            SafetyCap = 1,
        }.Normalize();
        child.SharedStopTargetIdentityToken = string.Empty;
        child.ScheduleEnabled = false;
        child.NextEligibleTimeUtc = null;
        child.Slots = child.Slots.Select(row =>
        {
            var selection = selections.Single(selection =>
                string.Equals(selection.SlotId, row.SlotId, StringComparison.OrdinalIgnoreCase));
            row.IsSubstitute = false;
            row.RequiredJobId = selection.JobId;
            row.LevelSeekTarget = null;
            row.SkipIfDailyRouletteRewardReceived = false;
            return row;
        }).ToList();
        return child;
    }

    private static DadLevelingCompilation Block(DadLevelingCompilation result, string blocker)
    {
        result.Blockers.Add(blocker);
        return FinishBlocked(result);
    }

    private static DadLevelingCompilation FinishBlocked(DadLevelingCompilation result)
    {
        result.Status = DadLevelingCompilationStatus.Blocked;
        result.ChildGroup = null;
        result.ChildJobId = string.Empty;
        result.ChildRequestId = string.Empty;
        result.Summary = string.Join(" ", result.Blockers.Distinct(StringComparer.OrdinalIgnoreCase));
        return result;
    }

    private sealed record EligibleJob(DadLevelingJobDescriptor Descriptor, int Level);
}
