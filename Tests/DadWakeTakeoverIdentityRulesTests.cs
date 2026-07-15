using dad.Models;
using Xunit;

namespace dad.Tests;

public sealed class DadWakeTakeoverIdentityRulesTests
{
    [Theory]
    [InlineData("")]
    [InlineData("stale-runtime-account")]
    public void ConfiguredClientAccountMatchesDespiteEmptyOrStaleTransientIdentity(string transientAccountId)
    {
        var account = Account("dad-client-42", "Hard'carry Gray'parse@Excalibur");

        var decision = DadWakeTakeoverIdentityRules.Evaluate(
            "dad-client-42",
            new DadAccountKey("dad-client-42"),
            new DadCharacterKey("Hard'carry Gray'parse@Excalibur"),
            account,
            [],
            new DadAccountKey(transientAccountId));

        Assert.True(decision.AccountMatches);
        Assert.True(decision.CharacterKnownToAccount);
        Assert.False(decision.TransientAccountMatches);
    }

    [Fact]
    public void RequestedCharacterIsCheckedOnlyAgainstRequestedAccount()
    {
        var requestedAccount = Account("dad-client-42", "Configured Character@Excalibur");

        var decision = DadWakeTakeoverIdentityRules.Evaluate(
            "dad-client-42",
            new DadAccountKey("dad-client-42"),
            new DadCharacterKey("Other Character@Excalibur"),
            requestedAccount,
            [],
            new DadAccountKey("dad-client-42"));

        Assert.True(decision.AccountMatches);
        Assert.False(decision.CharacterKnownToAccount);
        Assert.True(decision.TransientAccountMatches);
    }

    [Theory]
    [InlineData("different-account", "dad-client-42", "Configured Character@Excalibur")]
    [InlineData("dad-client-42", "different-account", "Configured Character@Excalibur")]
    [InlineData("dad-client-42", "dad-client-42", "Missing Character@Excalibur")]
    public void GenuineAccountOrCharacterMismatchBlocksIdentity(
        string configuredClientAccountId,
        string persistedAccountId,
        string requestedCharacter)
    {
        var decision = DadWakeTakeoverIdentityRules.Evaluate(
            configuredClientAccountId,
            new DadAccountKey("dad-client-42"),
            new DadCharacterKey(requestedCharacter),
            Account(persistedAccountId, "Configured Character@Excalibur"),
            []);

        Assert.False(decision.AccountMatches && decision.CharacterKnownToAccount);
    }

    [Fact]
    public void XadbKnownCharacterWithoutCharacterProfileIsAcceptedForMatchingClientDad()
    {
        var decision = DadWakeTakeoverIdentityRules.Evaluate(
            "dad-client-42",
            new DadAccountKey("dad-client-42"),
            new DadCharacterKey("Xadb Character@Excalibur"),
            Account("dad-client-42"),
            [XadbCharacter("dad-client-42", "Xadb Character@Excalibur")]);

        Assert.True(decision.AccountMatches);
        Assert.True(decision.CharacterKnownToAccount);
    }

    [Fact]
    public void XadbKnownCharacterCannotOverrideWrongConfiguredClientDad()
    {
        var decision = DadWakeTakeoverIdentityRules.Evaluate(
            "different-client-dad",
            new DadAccountKey("dad-client-42"),
            new DadCharacterKey("Xadb Character@Excalibur"),
            Account("dad-client-42"),
            [XadbCharacter("dad-client-42", "Xadb Character@Excalibur")]);

        Assert.False(decision.AccountMatches);
        Assert.False(decision.CharacterKnownToAccount);
    }

    [Fact]
    public void XadbKnownCharacterUnderAnotherClientDadIsRejected()
    {
        var decision = DadWakeTakeoverIdentityRules.Evaluate(
            "dad-client-42",
            new DadAccountKey("dad-client-42"),
            new DadCharacterKey("Xadb Character@Excalibur"),
            Account("dad-client-42"),
            [XadbCharacter("dad-client-99", "Xadb Character@Excalibur")]);

        Assert.True(decision.AccountMatches);
        Assert.False(decision.CharacterKnownToAccount);
    }

    [Fact]
    public void CharacterAbsentFromProfilesAndXadbRosterIsRejectedWithoutMutation()
    {
        var account = Account("dad-client-42", "Configured Character@Excalibur");
        var xadbRoster = new List<DadRosterKnownCharacterRecord>
        {
            XadbCharacter("dad-client-42", "Different Xadb Character@Excalibur"),
        };
        var configuredCharactersBefore = account.Characters.Keys.ToList();
        var xadbRosterBefore = xadbRoster.Select(static character => character.Clone()).ToList();

        var decision = DadWakeTakeoverIdentityRules.Evaluate(
            "dad-client-42",
            new DadAccountKey("dad-client-42"),
            new DadCharacterKey("Missing Character@Excalibur"),
            account,
            xadbRoster);

        Assert.True(decision.AccountMatches);
        Assert.False(decision.CharacterKnownToAccount);
        Assert.Equal(configuredCharactersBefore, account.Characters.Keys);
        Assert.Single(xadbRoster);
        Assert.Equal(xadbRosterBefore[0].AccountKey, xadbRoster[0].AccountKey);
        Assert.Equal(xadbRosterBefore[0].CharacterKey, xadbRoster[0].CharacterKey);
        Assert.Equal(xadbRosterBefore[0].UpdatedAtUtc, xadbRoster[0].UpdatedAtUtc);
    }

    private static AccountConfig Account(string accountId, params string[] characterKeys)
        => new()
        {
            AccountId = accountId,
            Characters = characterKeys.ToDictionary(
                static characterKey => characterKey,
                static _ => new CharacterConfig(),
                StringComparer.OrdinalIgnoreCase),
        };

    private static DadRosterKnownCharacterRecord XadbCharacter(string accountId, string characterKey)
        => new()
        {
            AccountKey = new DadAccountKey(accountId),
            CharacterKey = characterKey,
            XadbReady = true,
        };
}
