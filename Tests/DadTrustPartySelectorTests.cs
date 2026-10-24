using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadTrustPartySelectorTests
{
    private static readonly DateTime At = new(2026, 10, 23, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FixedPartyKeepsExactlyTheSavedJobAndNpcsAndFiltersIncompatibleDungeons()
    {
        var f = new Fixture();
        var party = new DadTrustFixedParty { PlayerJobId = 19,
            Npcs = [DadTrustNpcId.Alphinaud, DadTrustNpcId.Alisaie, DadTrustNpcId.Ryne] };
        var plan = f.Fixed(party, 1);
        Assert.Equal(DadLevelingCompilationStatus.Ready, plan.Status);
        Assert.Equal(party.Npcs, plan.Npcs.Select(npc => npc.NpcId));
        Assert.Equal(19u, plan.PlayerJobId);
        Assert.All(plan.Npcs, npc => Assert.False(npc.IsLevelingGoal));
        Assert.Equal(DadLevelingCompilationStatus.Blocked, f.Fixed(party, 2).Status);
        f.Progress.Npcs.Single(npc => npc.NpcId == DadTrustNpcId.Ryne).Level = 78;
        Assert.Equal(DadLevelingCompilationStatus.Blocked, f.Fixed(party, 1).Status);
    }

    [Theory]
    [InlineData("duplicate-npc")]
    [InlineData("missing-healer")]
    [InlineData("locked-npc")]
    [InlineData("unknown-job")]
    [InlineData("stale-party")]
    public void FixedPartyCannotReplaceSavedMembersToWorkAroundInvalidity(string failure)
    {
        var f = new Fixture();
        var party = new DadTrustFixedParty { PlayerJobId = 19,
            Npcs = [DadTrustNpcId.Alphinaud, DadTrustNpcId.Alisaie, DadTrustNpcId.Yshtola] };
        switch (failure)
        {
            case "duplicate-npc": party.Npcs[2] = DadTrustNpcId.Alisaie; break;
            case "missing-healer": party.Npcs[0] = DadTrustNpcId.Ryne; break;
            case "locked-npc": f.Progress.Npcs[0].Unlocked = false; break;
            case "unknown-job": party.PlayerJobId = 999; break;
            default: f.Progress.Duties[0].ObservedAtUtc = At.AddMinutes(-1); break;
        }
        Assert.Equal(DadLevelingCompilationStatus.Blocked, f.Fixed(party, 1, At).Status);
    }

    [Fact]
    public void CappedPlayerJobsSupportTheLowestEligibleNpcGroup()
    {
        var f = new Fixture();
        f.Options.AllowedJobs = [19, 24];

        var plan = f.Select();

        Assert.Equal(DadLevelingCompilationStatus.Ready, plan.Status);
        Assert.Equal(19u, plan.PlayerJobId);
        Assert.False(plan.IsPlayerLevelingGoal);
        Assert.Equal(new[] { DadTrustNpcId.Alphinaud, DadTrustNpcId.Alisaie, DadTrustNpcId.Yshtola }, plan.Npcs.Select(n => n.NpcId));
        Assert.Equal(2u, plan.Duty!.ContentFinderConditionId);
        Assert.All(plan.Npcs, npc => Assert.True(npc.IsLevelingGoal));
    }

    [Fact]
    public void OptionalPlayerLevelingBalancesTheSamePermittedJobPool()
    {
        var f = new Fixture();
        f.Options.LevelPlayerJobs = true;
        f.Options.AllowedJobs = [19, 20, 25];

        var plan = f.Select();

        Assert.Equal(20u, plan.PlayerJobId);
        Assert.True(plan.IsPlayerLevelingGoal);
        Assert.Contains(plan.Npcs, npc => npc.NpcId == DadTrustNpcId.Thancred && !npc.IsLevelingGoal);
        Assert.Contains(plan.Npcs, npc => npc.NpcId == DadTrustNpcId.Alphinaud && npc.Level == 87);
        Assert.Contains(plan.Npcs, npc => npc.NpcId == DadTrustNpcId.Yshtola && npc.Level == 87);
    }

    [Fact]
    public void SeparateTargetsRequireBothGoalSetsAndRespectNpcCaps()
    {
        var f = new Fixture();
        foreach (var npc in f.Progress.Npcs) npc.Level = npc.LevelCap;
        f.Options.LevelPlayerJobs = true;
        f.Options.NpcTargetLevel = 100;
        f.Options.PlayerTargetLevel = 90;
        f.Options.AllowedJobs = [20];
        Assert.Equal(DadLevelingCompilationStatus.Ready, f.Select().Status);
        f.Character.JobLevels[20] = 90;
        Assert.Equal(DadLevelingCompilationStatus.Complete, f.Select().Status);

        f.Progress.Npcs.Single(n => n.NpcId == DadTrustNpcId.Alphinaud).Level = 95;
        Assert.Equal(DadLevelingCompilationStatus.Ready, f.Select().Status);
        f.Options.NpcTargetLevel = 95;
        Assert.Equal(DadLevelingCompilationStatus.Complete, f.Select().Status);
    }

    [Fact]
    public void MemberSpecificDungeonAvailabilityConstrainsTheWholeParty()
    {
        var f = new Fixture();
        f.Options.AllAvailableNpcs = false;
        f.Options.SelectedNpcs = [DadTrustNpcId.Ryne];
        f.Progress.Npcs.Single(n => n.NpcId == DadTrustNpcId.Ryne).Level = 75;
        f.Duties[0].JobLevelRequired = 73;
        f.Options.AllowedJobs = [19];

        var plan = f.Select();

        Assert.Equal(1u, plan.Duty!.ContentFinderConditionId);
        Assert.Contains(plan.Npcs, n => n.NpcId == DadTrustNpcId.Ryne);
        Assert.DoesNotContain(f.Progress.Duties[1].Members!, n => n.NpcId == DadTrustNpcId.Ryne);
        f.Progress.Duties[0].Unlocked = false;
        Assert.Equal(DadLevelingCompilationStatus.Blocked, f.Select().Status);
    }

    [Fact]
    public void JobMultiselectNeverExpandsBeyondPermittedJobsAndCanKeepACappedCurrentJob()
    {
        var f = new Fixture();
        f.Options.AllowedJobs = [19, 37];
        f.Character.CurrentJobId = 37;
        var plan = f.Select();
        Assert.Equal(37u, plan.PlayerJobId);
        Assert.All(plan.Npcs, npc => Assert.True(npc.IsLevelingGoal));
        f.Options.AllowedJobs = [];
        Assert.Equal(DadLevelingCompilationStatus.Blocked, f.Select().Status);
    }

    [Fact]
    public void AllRounderCanFillATankRoleForTwoSelectedNpcGoals()
    {
        var f = new Fixture();
        f.Options.AllAvailableNpcs = false;
        f.Options.SelectedNpcs = [DadTrustNpcId.Alphinaud, DadTrustNpcId.Yshtola];
        f.Options.AllowedJobs = [20];
        foreach (var duty in f.Progress.Duties)
            duty.Members = duty.Members!.Where(m => m.NpcId is DadTrustNpcId.Alphinaud or DadTrustNpcId.Yshtola or DadTrustNpcId.GrahaTia).ToList();

        var plan = f.Select();

        Assert.Equal(DadLevelingCompilationStatus.Ready, plan.Status);
        Assert.Contains(plan.Npcs, npc => npc.NpcId == DadTrustNpcId.GrahaTia && npc.Role == DadTrustRoles.Tank && !npc.IsLevelingGoal);
        foreach (var duty in f.Progress.Duties) duty.Members!.RemoveAll(m => m.NpcId == DadTrustNpcId.GrahaTia);
        Assert.Equal(DadLevelingCompilationStatus.Blocked, f.Select().Status);
    }

    [Theory]
    [InlineData("missing-level")]
    [InlineData("unknown-unlock")]
    [InlineData("wrong-account")]
    [InlineData("future-reading")]
    [InlineData("partial-roster")]
    [InlineData("conflicting-reading")]
    public void UnknownOrUnownedGoalsCannotBeReportedComplete(string failure)
    {
        var f = new Fixture();
        foreach (var npc in f.Progress.Npcs) npc.Level = npc.LevelCap;
        switch (failure)
        {
            case "missing-level": f.Progress.Npcs[0].Level = null; break;
            case "unknown-unlock": f.Options.AllAvailableNpcs = false; f.Options.SelectedNpcs = [DadTrustNpcId.Alphinaud]; f.Progress.Npcs[0].Unlocked = null; break;
            case "wrong-account": f.Progress.AccountKey = new("another-fixture-account"); break;
            case "future-reading": f.Progress.Npcs[0].ObservedAtUtc = At.AddHours(1); break;
            case "conflicting-reading": f.Progress.Npcs.Add(f.Progress.Npcs[0] with { Level = 99 }); break;
            default: f.Progress.RosterObservedAtUtc = null; break;
        }
        Assert.Equal(DadLevelingCompilationStatus.Blocked, f.Select().Status);
    }

    [Fact]
    public void ARequiredPostRunRefreshRejectsPriorGoalAndDutyReadings()
    {
        var f = new Fixture();
        Assert.Equal(DadLevelingCompilationStatus.Blocked, f.Select(At.AddSeconds(1)).Status);
        f.Progress.RosterObservedAtUtc = At.AddSeconds(1);
        foreach (var npc in f.Progress.Npcs) npc.ObservedAtUtc = At.AddSeconds(1);
        Assert.Equal(DadLevelingCompilationStatus.Blocked, f.Select(At.AddSeconds(1)).Status);
        foreach (var duty in f.Progress.Duties) duty.ObservedAtUtc = At.AddSeconds(1);
        Assert.Equal(DadLevelingCompilationStatus.Ready, f.Select(At.AddSeconds(1)).Status);
    }

    [Fact]
    public void SelectionDoesNotRewriteSavedObservationsOrOptions()
    {
        var f = new Fixture();
        var before = f.Progress.Clone();
        var allowed = f.Options.AllowedJobs.ToArray();
        Assert.Equal(DadLevelingCompilationStatus.Ready, f.Select().Status);
        Assert.True(DadTrustProgressRules.Same(before, f.Progress));
        Assert.Equal(allowed, f.Options.AllowedJobs);
    }

    [Fact]
    public void ConflictingDutyCatalogEntriesDoNotSelectAnArbitraryLevelRequirement()
    {
        var f = new Fixture();
        f.Duties[2].ContentFinderConditionId = f.Duties[1].ContentFinderConditionId;
        Assert.Equal(DadLevelingCompilationStatus.Blocked, f.Select().Status);
    }

    [Fact]
    public void AHiddenPlayerTargetDoesNotAffectNpcOnlyLeveling()
    {
        var f = new Fixture();
        f.Options.PlayerTargetLevel = 0;
        Assert.Equal(DadLevelingCompilationStatus.Ready, f.Select().Status);
        f.Options.LevelPlayerJobs = true;
        Assert.Equal(DadLevelingCompilationStatus.Blocked, f.Select().Status);
    }

    [Fact]
    public void TrustCompilerFreezesExactPartyAndSelectedJobWithoutLegacyThresholds()
    {
        var f = new Fixture();
        f.Options.AllowedJobs = [24]; // switch away from the saved tank role
        var group = f.CompilerGroup();
        var sourceJson = DadIpcJson.Serialize(group);
        var compiled = f.Compile(group);
        Assert.Equal(DadLevelingCompilationStatus.Ready, compiled.Status);
        var child = compiled.ChildGroup!;
        Assert.Equal(24u, child.TrustParty!.PlayerJobId);
        Assert.Equal(3, child.TrustParty.Npcs.Count);
        Assert.Equal(24u, Assert.Single(child.Slots).RequiredJobId);
        Assert.Equal(DadPartyRole.Healer, child.Slots[0].RequiredRole);
        Assert.Equal(DadPlannerActivityMode.Trust, child.ActivityMode);
        Assert.False(child.LevelingMode.Enabled);
        Assert.Equal(1, child.StopPolicy.AfterRuns);
        Assert.True(child.RefreshTrustNpcLevels);
        child.TrustParty.Npcs.Clear();
        Assert.Equal(sourceJson, DadIpcJson.Serialize(group));
        Assert.Empty(group.LevelingMode.DutyThresholds);
    }

    [Fact]
    public void TrustCompilerCannotReusePreRunReadingsForNextChildOrCompletion()
    {
        var f = new Fixture();
        var group = f.CompilerGroup();
        Assert.Equal(DadLevelingCompilationStatus.Ready, f.Compile(group).Status);
        Assert.Equal(DadLevelingCompilationStatus.Blocked, f.Compile(group, At.AddSeconds(1)).Status);
        foreach (var npc in f.Progress.Npcs) npc.Level = npc.LevelCap;
        Assert.Equal(DadLevelingCompilationStatus.Complete, f.Compile(group).Status);
        Assert.Equal(DadLevelingCompilationStatus.Blocked, f.Compile(group, At.AddSeconds(1)).Status);
    }

    [Fact]
    public void TrustOptionsAndExactPartySurviveGroupCloneWithoutSharingMutableLists()
    {
        var f = new Fixture();
        var group = f.CompilerGroup();
        group.TrustParty = new() { PlayerJobId = 19, Npcs = [DadTrustNpcId.Alphinaud, DadTrustNpcId.Alisaie, DadTrustNpcId.Yshtola] };
        var clone = DadSchedulerGroupCloneRules.CloneWithSlots(group, group.Slots);
        clone.TrustParty!.Npcs.Clear();
        clone.LevelingMode.Trust!.AllowedJobs.Clear();
        Assert.Equal(3, group.TrustParty.Npcs.Count);
        Assert.Single(group.LevelingMode.Trust!.AllowedJobs);
        var roundTrip = DadIpcJson.Deserialize<DadPlannerGroup>(DadIpcJson.Serialize(group))!;
        Assert.Equal(group.TrustParty.Npcs, roundTrip.TrustParty!.Npcs);
        Assert.Equal(group.LevelingMode.Trust.NpcTargetLevel, roundTrip.LevelingMode.Trust!.NpcTargetLevel);
    }

    private sealed class Fixture
    {
        internal readonly DadTrustLevelingOptions Options = new() { AllowedJobs = [19] };
        internal readonly DadAcquiredCharacter Character = new()
        {
            AccountId = "fixture-account", CharacterKey = "Fixture One@Example", ContentId = 1001,
            CurrentJobId = 19, JobLevels = new() { [19] = 100, [24] = 100, [20] = 87, [25] = 93, [37] = 100 },
        };
        internal DadTrustProgress Progress => Character.TrustProgress!;
        internal readonly DadPlannerDutyOption[] Duties =
        [
            new() { ContentFinderConditionId = 1, DutyDisplayName = "Fixture early dungeon", JobLevelRequired = 79, SupportsTrust = true },
            new() { ContentFinderConditionId = 2, DutyDisplayName = "Fixture later dungeon", JobLevelRequired = 87, SupportsTrust = true },
            new() { ContentFinderConditionId = 3, DutyDisplayName = "Fixture highest dungeon", JobLevelRequired = 89, SupportsTrust = true },
        ];
        private readonly DadLevelingJobDescriptor[] jobs =
        [
            new() { JobId = 19, Role = DadPartyRole.Tank, IsFullCombatJob = true },
            new() { JobId = 37, Role = DadPartyRole.Tank, IsFullCombatJob = true },
            new() { JobId = 24, Role = DadPartyRole.Healer, IsFullCombatJob = true },
            new() { JobId = 20, Role = DadPartyRole.Melee, IsFullCombatJob = true },
            new() { JobId = 25, Role = DadPartyRole.Caster, IsFullCombatJob = true },
        ];

        internal Fixture()
        {
            Character.TrustProgress = new() { AccountKey = new(Character.AccountId), CharacterKey = new(Character.CharacterKey),
                ContentId = Character.ContentId, RosterObservedAtUtc = At };
            Add(DadTrustNpcId.Alphinaud, 87, DadTrustRoles.Healer);
            Add(DadTrustNpcId.Alisaie, 89, DadTrustRoles.Dps);
            Add(DadTrustNpcId.Thancred, 100, DadTrustRoles.Tank);
            Add(DadTrustNpcId.Urianger, 89, DadTrustRoles.Healer);
            Add(DadTrustNpcId.Yshtola, 87, DadTrustRoles.Dps);
            Add(DadTrustNpcId.Ryne, 80, DadTrustRoles.Dps, 80);
            Add(DadTrustNpcId.GrahaTia, 100, DadTrustRoles.All);
        }

        private void Add(DadTrustNpcId id, int level, DadTrustRoles roles, int cap = 100)
        {
            Progress.Npcs.Add(new() { NpcId = id, Level = level, LevelCap = cap, Unlocked = true, ObservedAtUtc = At });
            foreach (var duty in Duties)
            {
                var reading = Progress.Duties.FirstOrDefault(d => d.ContentFinderConditionId == duty.ContentFinderConditionId);
                if (reading == null) { reading = new() { ContentFinderConditionId = duty.ContentFinderConditionId, Unlocked = true, Members = [], ObservedAtUtc = At }; Progress.Duties.Add(reading); }
                if (id != DadTrustNpcId.Ryne || duty.ContentFinderConditionId == 1)
                    reading.Members!.Add(new() { NpcId = id, AvailableRoles = roles });
            }
        }

        internal DadTrustPartySelection Select(DateTime? minimum = null)
            => DadTrustPartySelector.SelectLeveling(Options, Character, jobs, Duties, minimum, At.AddMinutes(5));
        internal DadTrustPartySelection Fixed(DadTrustFixedParty party, uint duty, DateTime? minimum = null)
            => DadTrustPartySelector.SelectFixed(party, duty, Character, jobs, Duties, minimum, At.AddMinutes(5));

        internal DadPlannerGroup CompilerGroup()
        {
            Character.XadbReady = true;
            Character.SnapshotVersion = 1;
            Character.XadbSnapshotUtc = At;
            Character.SnapshotQuality = "complete";
            return new() { ActivityMode = DadPlannerActivityMode.Trust, LevelingMode = new() { Enabled = true, Trust = Options.Clone() },
                Slots = [new() { SlotId = "Slot1", RequiredAccountKey = new(Character.AccountId), RequiredCharacterKey = new(Character.CharacterKey), RequiredRole = DadPartyRole.Tank }] };
        }

        internal DadLevelingCompilation Compile(DadPlannerGroup group, DateTime? minimum = null)
            => DadLevelingModeCompiler.Compile(group, new() { Characters = [Character] }, jobs, Duties, minimumTrustObservedAtUtc: minimum);
    }
}
