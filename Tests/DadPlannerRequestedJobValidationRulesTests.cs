using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadPlannerRequestedJobValidationRulesTests
{
    [Fact]
    public void AnyCurrentJobDoesNotRequireXadbData()
    {
        var slot = Slot(requiredJobId: null);

        var failure = DadPlannerRequestedJobValidationRules.Validate(slot, []);

        Assert.Equal(DadPlannerRequestedJobValidationFailure.None, failure);
    }

    [Fact]
    public void RequestedJobMayDifferFromCurrentJobWhenXadbOwnsIt()
    {
        var slot = Slot(requiredJobId: 21);
        var character = Character(currentJobId: 24, xadbReady: true, (21, 90), (24, 100));

        var failure = DadPlannerRequestedJobValidationRules.Validate(slot, [character]);

        Assert.Equal(DadPlannerRequestedJobValidationFailure.None, failure);
    }

    [Fact]
    public void ExactCharacterMayUseItsConfiguredAccountAlias()
    {
        var slot = Slot(requiredJobId: 21);
        slot.RequiredAccountKey = new DadAccountKey("main-account");
        var character = Character(currentJobId: 24, xadbReady: true, (21, 90));
        character.AccountAlias = "main-account";

        var failure = DadPlannerRequestedJobValidationRules.Validate(slot, [character]);

        Assert.Equal(DadPlannerRequestedJobValidationFailure.None, failure);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(8u)]
    [InlineData(18u)]
    [InlineData(43u)]
    public void NonCombatJobCannotBeRequested(uint requiredJobId)
    {
        var failure = DadPlannerRequestedJobValidationRules.Validate(
            Slot(requiredJobId),
            [Character(currentJobId: requiredJobId, xadbReady: true, (requiredJobId, 100))]);

        Assert.Equal(DadPlannerRequestedJobValidationFailure.InvalidCombatJob, failure);
    }

    [Fact]
    public void RequestedJobRequiresCurrentXadbData()
    {
        var failure = DadPlannerRequestedJobValidationRules.Validate(
            Slot(requiredJobId: 21),
            [Character(currentJobId: 21, xadbReady: false, (21, 100))]);

        Assert.Equal(DadPlannerRequestedJobValidationFailure.XadbUnavailable, failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequestedJobMustHavePositiveXadbLevel(bool includeZeroLevel)
    {
        var jobs = includeZeroLevel
            ? new[] { (jobId: 21u, level: 0) }
            : Array.Empty<(uint jobId, int level)>();
        var failure = DadPlannerRequestedJobValidationRules.Validate(
            Slot(requiredJobId: 21),
            [Character(currentJobId: 24, xadbReady: true, jobs)]);

        Assert.Equal(DadPlannerRequestedJobValidationFailure.JobUnavailable, failure);
    }

    [Theory]
    [InlineData("other-account", "Venat@Excalibur", 1234ul)]
    [InlineData("account-1", "Azem@Excalibur", 1234ul)]
    [InlineData("account-1", "Venat@Excalibur", 9999ul)]
    public void WrongExactIdentityCannotSupplyRequestedJob(
        string accountId,
        string characterKey,
        ulong contentId)
    {
        var character = Character(currentJobId: 21, xadbReady: true, (21, 100));
        character.AccountId = accountId;
        character.CharacterKey = characterKey;
        character.ContentId = contentId;

        var failure = DadPlannerRequestedJobValidationRules.Validate(
            Slot(requiredJobId: 21),
            [character]);

        Assert.Equal(DadPlannerRequestedJobValidationFailure.ExactCharacterUnavailable, failure);
    }

    private static DadPresetCharacterSlot Slot(uint? requiredJobId)
        => new()
        {
            SlotId = "Slot1",
            RequiredAccountKey = new DadAccountKey("account-1"),
            RequiredCharacterKey = new DadCharacterKey("Venat@Excalibur"),
            CharacterKey = "Venat@Excalibur",
            ContentId = 1234,
            RequiredJobId = requiredJobId,
        };

    private static DadAcquiredCharacter Character(
        uint? currentJobId,
        bool xadbReady,
        params (uint jobId, int level)[] jobs)
        => new()
        {
            AccountId = "account-1",
            CharacterKey = "Venat@Excalibur",
            ContentId = 1234,
            CurrentJobId = currentJobId,
            XadbReady = xadbReady,
            JobLevels = jobs.ToDictionary(static job => job.jobId, static job => job.level),
        };
}
