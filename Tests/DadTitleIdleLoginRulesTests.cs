using dad.Models;
using Xunit;

namespace dad.Tests;

public sealed class DadTitleIdleLoginRulesTests
{
    [Theory]
    [InlineData(DadVermaxionReadinessKind.Idle)]
    [InlineData(DadVermaxionReadinessKind.NotLoaded)]
    public void FreshIdleOrAbsentVermaxionAllowsExactTitleLogin(DadVermaxionReadinessKind kind)
    {
        var snapshot = ValidTitle(kind);

        Assert.True(DadTitleIdleLoginRules.CanProveAutoRetainerOwnership(snapshot));
        Assert.True(DadTitleIdleLoginRules.CanContinueOwnedAttempt(snapshot));
    }

    [Theory]
    [InlineData(DadVermaxionReadinessKind.Busy)]
    [InlineData(DadVermaxionReadinessKind.Unavailable)]
    public void LoadedBusyOrUnreadableVermaxionStillBlocksTitleLogin(DadVermaxionReadinessKind kind)
    {
        var snapshot = ValidTitle(kind);

        Assert.False(DadTitleIdleLoginRules.CanContinueOwnedAttempt(snapshot));
        Assert.Contains(kind.ToString(), DadTitleIdleLoginRules.BuildWaitSummary(snapshot));
    }

    [Theory]
    [InlineData(DadVermaxionReadinessKind.Idle)]
    [InlineData(DadVermaxionReadinessKind.NotLoaded)]
    public void StaleVermaxionEvidenceStillBlocksTitleLogin(DadVermaxionReadinessKind kind)
    {
        var snapshot = ValidTitle(kind);
        snapshot.VermaxionStatusEvidenceFresh = false;

        Assert.False(DadTitleIdleLoginRules.CanContinueOwnedAttempt(snapshot));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void NoVermaxionDoesNotBypassOtherTitleSafetyGates(int failure)
    {
        var snapshot = ValidTitle(DadVermaxionReadinessKind.NotLoaded);
        switch (failure)
        {
            case 0: snapshot.AccountMatches = false; break;
            case 1: snapshot.ClientRouteConnected = false; break;
            case 2: snapshot.AutoRetainerBusy = true; break;
            case 3: snapshot.AutoRetainerSuppressed = true; break;
            case 4: snapshot.LifestreamCanAutoLogin = false; break;
            case 5: snapshot.ExternalAutomationHeld = true; break;
            case 6: snapshot.TitleSurface = DadTitleSurface.CharacterSelect; break;
            case 7: snapshot.TitleSurfaceEvidenceFresh = false; break;
        }

        Assert.False(DadTitleIdleLoginRules.CanContinueOwnedAttempt(snapshot));
    }

    [Theory]
    [InlineData(DadVermaxionReadinessKind.Idle, true)]
    [InlineData(DadVermaxionReadinessKind.NotLoaded, true)]
    [InlineData(DadVermaxionReadinessKind.Busy, false)]
    [InlineData(DadVermaxionReadinessKind.Unavailable, false)]
    public void MovieDismissalUsesTheSameOptionalVermaxionGate(DadVermaxionReadinessKind kind, bool expected)
    {
        var snapshot = ValidTitle(kind);
        snapshot.TitleSurface = DadTitleSurface.TitleMovie;
        snapshot.TitleMovieExactReady = true;

        Assert.Equal(expected, DadTitleIdleLoginRules.CanDismissExactTitleMovie(snapshot));
    }

    private static DadWakeTakeoverTargetSnapshot ValidTitle(DadVermaxionReadinessKind kind)
        => new()
        {
            ClientRouteConnected = true,
            AccountMatches = true,
            CharacterKnownToAccount = true,
            TitleSurface = DadTitleSurface.TitleMenu,
            TitleSurfaceEvidenceFresh = true,
            TitleClientLoggedOut = true,
            TitleNoActiveConditionFlags = true,
            TitleMenuReady = true,
            AutoRetainerAvailable = true,
            SuppressionReadable = true,
            VermaxionStatusEvidenceFresh = true,
            VermaxionStatusKind = kind,
            LifestreamAvailable = true,
            LifestreamCanAutoLogin = true,
        };
}
