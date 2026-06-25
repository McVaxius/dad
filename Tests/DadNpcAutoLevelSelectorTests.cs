using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadNpcAutoLevelSelectorTests
{
    [Fact]
    public void SelectHighestEligibleDutyFiltersByLaneAndCurrentLevel()
    {
        var character = new DadAcquiredCharacter
        {
            CharacterKey = "Runner@Alpha",
            CurrentJobId = 19,
            CurrentJobAbbrev = "PLD",
            CurrentLevel = 63,
            JobLevels = new Dictionary<uint, int> { [19] = 63 },
        };
        var duties = new[]
        {
            Duty(10, "Low Support", level: 50, sync: 60, support: true, trust: true),
            Duty(20, "Trust Only", level: 61, sync: 70, support: false, trust: true),
            Duty(30, "High Support", level: 61, sync: 65, support: true, trust: false),
            Duty(40, "Too High", level: 70, sync: 80, support: true, trust: true),
        };

        var dutySupport = DadNpcAutoLevelSelector.SelectHighestEligibleDuty(duties, character, DadNpcAutoLevelLane.DutySupport, out var supportBlocker);
        var trust = DadNpcAutoLevelSelector.SelectHighestEligibleDuty(duties, character, DadNpcAutoLevelLane.Trust, out var trustBlocker);

        Assert.NotNull(dutySupport);
        Assert.Equal((uint)30, dutySupport.ContentFinderConditionId);
        Assert.Equal(string.Empty, supportBlocker);
        Assert.NotNull(trust);
        Assert.Equal((uint)20, trust.ContentFinderConditionId);
        Assert.Equal(string.Empty, trustBlocker);
    }

    [Fact]
    public void SelectHighestEligibleDutyReturnsDeterministicBlockerWithoutCharacter()
    {
        var selected = DadNpcAutoLevelSelector.SelectHighestEligibleDuty(
            [Duty(10, "Low Support", level: 50, sync: 60, support: true, trust: false)],
            null,
            DadNpcAutoLevelLane.DutySupport,
            out var blocker);

        Assert.Null(selected);
        Assert.Contains("requires a local character snapshot", blocker, StringComparison.OrdinalIgnoreCase);
    }

    private static DadPlannerDutyOption Duty(uint rowId, string name, int level, int sync, bool support, bool trust)
        => new()
        {
            ContentFinderConditionId = rowId,
            DutyDisplayName = name,
            JobLevelRequired = level,
            JobLevelSync = sync,
            SupportsDutySupport = support,
            SupportsTrust = trust,
        };
}
