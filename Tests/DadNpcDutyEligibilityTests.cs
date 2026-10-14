using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

// Review M21: GetBlocker is the last gate before committing a character to an NPC duty (5 branches).
public sealed class DadNpcDutyEligibilityTests
{
    private const uint ArcanistJobId = 26;  // combat
    private const uint WhiteMageJobId = 24; // combat
    private const uint FisherJobId = 18;    // non-combat

    [Theory]
    [InlineData(36u)]
    [InlineData(43u)]
    public void LimitedJobsCannotUseNpcDuties(uint jobId)
    {
        Assert.Contains("limited job", DadNpcDutyEligibility.GetBlocker(Character(jobId, 90), "Synthetic duty", 4, 0));
    }

    private static DadAcquiredCharacter Character(uint? jobId, int? level, (uint job, int lvl)[]? jobLevels = null)
    {
        var character = new DadAcquiredCharacter
        {
            CharacterKey = "Aaa@World",
            CurrentJobId = jobId,
            CurrentLevel = level,
            CurrentJobAbbrev = jobId == WhiteMageJobId ? "WHM" : jobId == FisherJobId ? "FSH" : string.Empty,
        };

        foreach (var (job, lvl) in jobLevels ?? [])
            character.JobLevels[job] = lvl;

        return character;
    }

    [Fact]
    public void CombatJobAtRequiredLevelIsEligible()
    {
        var blocker = DadNpcDutyEligibility.GetBlocker(Character(WhiteMageJobId, 90), "Sastasha", 4, 50);
        Assert.Null(blocker);
    }

    [Fact]
    public void CombatJobBelowRequiredLevelIsBlocked()
    {
        var blocker = DadNpcDutyEligibility.GetBlocker(Character(WhiteMageJobId, 40), "Sastasha", 4, 50);
        Assert.NotNull(blocker);
        Assert.Contains("level 50", blocker);
    }

    [Fact]
    public void ZeroLevelRequirementIsAlwaysEligibleForCombatJob()
    {
        var blocker = DadNpcDutyEligibility.GetBlocker(Character(WhiteMageJobId, 1), "Sastasha", 4, 0);
        Assert.Null(blocker);
    }

    [Fact]
    public void NonCombatJobIsBlocked()
    {
        var blocker = DadNpcDutyEligibility.GetBlocker(Character(FisherJobId, 90), "Sastasha", 4, 50);
        Assert.NotNull(blocker);
        Assert.Contains("combat job", blocker);
    }

    [Fact]
    public void NoCurrentJobDataIsBlocked()
    {
        var blocker = DadNpcDutyEligibility.GetBlocker(Character(null, null), "Sastasha", 4, 50);
        Assert.NotNull(blocker);
        Assert.Contains("no current job data", blocker);
    }

    [Fact]
    public void InfersSoleCombatJobFromJobLevels()
    {
        // No CurrentJobId; a combat job (WHM) and a non-combat job (FSH) in JobLevels — infers WHM.
        var blocker = DadNpcDutyEligibility.GetBlocker(
            Character(null, null, [(WhiteMageJobId, 90), (FisherJobId, 50)]),
            "Sastasha",
            4,
            50);
        Assert.Null(blocker);
    }

    [Fact]
    public void RequiredJobUsesXadbLedgerWhenCurrentJobIsUnknown()
    {
        var blocker = DadNpcDutyEligibility.GetBlocker(
            Character(null, null, [(ArcanistJobId, 16), (WhiteMageJobId, 90)]),
            "Sastasha",
            4,
            15,
            ArcanistJobId);

        Assert.Null(blocker);
    }

    [Fact]
    public void RequiredJobLevelDoesNotBorrowCurrentJobLevel()
    {
        var blocker = DadNpcDutyEligibility.GetBlocker(
            Character(WhiteMageJobId, 90, [(WhiteMageJobId, 90)]),
            "Sastasha",
            4,
            15,
            ArcanistJobId);

        Assert.NotNull(blocker);
        Assert.Contains("no level data for required job #26", blocker);
    }

    [Fact]
    public void RequiredJobBelowDutyLevelIsBlocked()
    {
        var blocker = DadNpcDutyEligibility.GetBlocker(
            Character(WhiteMageJobId, 90, [(ArcanistJobId, 14), (WhiteMageJobId, 90)]),
            "Sastasha",
            4,
            15,
            ArcanistJobId);

        Assert.NotNull(blocker);
        Assert.Contains("level 14 on required job #26", blocker);
        Assert.Contains("requires level 15", blocker);
    }
}
