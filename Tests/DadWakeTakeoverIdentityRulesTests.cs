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
            Account(persistedAccountId, "Configured Character@Excalibur"));

        Assert.False(decision.AccountMatches && decision.CharacterKnownToAccount);
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
}
