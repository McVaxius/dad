using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadMiniAutoPartyProjectionTests
{
    [Fact]
    public void ReadyPrivateFormationProjectsExactMiniStatusAndActions()
    {
        var configuration = ActiveConfiguration();
        var groupId = $"{DadAutoPartyFreeformRules.GroupIdPrefix}test";
        var snapshot = DadMiniAutoPartyProjection.Build(
            dadEnabled: true,
            configuration,
            new DadAutoPartyEndpointSnapshot(
                DadAutoPartyEndpointConnectionState.Ready,
                "dad-ready",
                DateTime.UtcNow,
                DateTime.UtcNow,
                0,
                0,
                0,
                1),
            new DadAutoPartyDirectorySnapshot(
                1,
                [new DadAutoPartyListing { SharingIslandId = "peer-island" }],
                new HashSet<string>(["peer-island"], StringComparer.Ordinal)),
            new DadCrewFormationStatus
            {
                SourceGroupId = groupId,
                EffectiveGroupId = groupId,
                Phase = DadCrewFormationPhase.RegularGroupReady,
                Summary = "Exact group ready.",
            },
            refreshInProgress: false,
            refreshCooldownRemaining: TimeSpan.Zero,
            DadPairedDirectoryRefreshResult.NotRun(),
            canGuardedDisband: true,
            guardedDisbandBlocker: string.Empty);

        Assert.True(snapshot.Enabled);
        Assert.True(snapshot.EndpointReady);
        Assert.Equal(1, snapshot.ActivePairingCount);
        Assert.Equal(1, snapshot.OnlinePairingCount);
        Assert.Equal(1, snapshot.PrivateDirectoryListingCount);
        Assert.True(snapshot.DirectoryRefreshEligible);
        Assert.True(snapshot.ExactFormationRecognized);
        Assert.Equal(nameof(DadCrewFormationPhase.RegularGroupReady), snapshot.ExactFormationPhase);
        Assert.True(snapshot.CanGuardedDisband);
        Assert.Empty(snapshot.FirstBlocker);
    }

    [Fact]
    public void BlockerOrderStartsAtEnablementAndRefreshHonorsNormalGuards()
    {
        var configuration = ActiveConfiguration();
        configuration.Enabled = false;

        var snapshot = DadMiniAutoPartyProjection.Build(
            dadEnabled: false,
            configuration,
            DadAutoPartyEndpointSnapshot.Disabled(),
            new DadAutoPartyDirectorySnapshot(1, [], new HashSet<string>()),
            new DadCrewFormationStatus(),
            refreshInProgress: true,
            refreshCooldownRemaining: TimeSpan.FromSeconds(12),
            DadPairedDirectoryRefreshResult.NotRun(),
            canGuardedDisband: false,
            guardedDisbandBlocker: "No exact formation.");

        Assert.Equal("Enable DAD.", snapshot.FirstBlocker);
        Assert.False(snapshot.DirectoryRefreshEligible);
        Assert.True(snapshot.DirectoryRefreshInProgress);
        Assert.Equal(TimeSpan.FromSeconds(12), snapshot.DirectoryRefreshCooldownRemaining);
    }

    [Fact]
    public void CompletedExactFormationRetainsGuideCompletionEvidence()
    {
        var configuration = ActiveConfiguration();
        var groupId = $"{DadAutoPartyFreeformRules.GroupIdPrefix}completed";
        var snapshot = DadMiniAutoPartyProjection.Build(
            true,
            configuration,
            new DadAutoPartyEndpointSnapshot(
                DadAutoPartyEndpointConnectionState.Ready,
                "dad-ready",
                DateTime.UtcNow,
                DateTime.UtcNow,
                0,
                0,
                0,
                1),
            new DadAutoPartyDirectorySnapshot(1, [], new HashSet<string>()),
            new DadCrewFormationStatus
            {
                SourceGroupId = groupId,
                EffectiveGroupId = groupId,
                Phase = DadCrewFormationPhase.Completed,
                Summary = "Guarded disband completed.",
            },
            false,
            TimeSpan.Zero,
            DadPairedDirectoryRefreshResult.NotRun(),
            false,
            "No active formation.");

        Assert.True(snapshot.ExactFormationRecognized);
        Assert.True(snapshot.GuardedDisbandComplete);
        Assert.Equal(nameof(DadCrewFormationPhase.Completed), snapshot.ExactFormationPhase);
    }

    [Fact]
    public void GuideAndMiniStatusReuseFullAutoPartyActions()
    {
        var guideFlow = ReadRepositorySource("Windows", "DadGuideFlow.cs");
        var guideWindow = ReadRepositorySource("Windows", "SetupWizardWindow.cs");
        var mini = ReadRepositorySource("Windows", "DadMiniStatusWindow.cs");

        Assert.Contains("DadGuideFlow.AutoParty", guideFlow, StringComparison.Ordinal);
        Assert.Contains("Complete reciprocal pairing", guideFlow, StringComparison.Ordinal);
        Assert.Contains("Create first exact formation", guideWindow, StringComparison.Ordinal);
        Assert.Contains("plugin.OpenAutoPartyUi()", guideWindow, StringComparison.Ordinal);
        Assert.Contains("plugin.TryStartPairedDirectoryRefresh()", mini, StringComparison.Ordinal);
        Assert.Contains("Guarded(\"autoparty-disband\"", mini, StringComparison.Ordinal);
        Assert.Contains("plugin.RequestAutoPartyFormationDisband()", mini, StringComparison.Ordinal);
        Assert.DoesNotContain("Register endpoint##", mini, StringComparison.Ordinal);
        Assert.DoesNotContain("Submit pairing##", mini, StringComparison.Ordinal);
    }

    private static DadAutoPartyConfiguration ActiveConfiguration()
    {
        var key = Convert.ToBase64String(new byte[32]);
        return new DadAutoPartyConfiguration
        {
            Enabled = true,
            RegistrationState = DadAutoPartyRegistrationState.Active,
            RegistrationId = Guid.NewGuid().ToString("D"),
            RouteId = "route",
            WebhookCredentialReference = "mailbox",
            UplinkEpochId = Guid.NewGuid().ToString("D"),
            DownlinkEpochId = Guid.NewGuid().ToString("D"),
            MailboxEpochGeneration = 1,
            RelayKeyGeneration = 1,
            RelaySigningPublicKey = key,
            RelayAgreementPublicKey = key,
            Pairings =
            [
                new DadAutoPartyPairing
                {
                    PairingId = Guid.NewGuid().ToString("D"),
                    IslandId = "peer-island",
                    PublicKeyFingerprint = "peer-fingerprint",
                    LocalFingerprint = "local-fingerprint",
                    TranscriptHash = "transcript",
                    SigningPublicKey = key,
                    AgreementPublicKey = key,
                    ExpiresAtUtc = DateTime.UtcNow.AddDays(1),
                    ConfirmedAtUtc = DateTime.UtcNow,
                },
            ],
        };
    }

    private static string ReadRepositorySource(params string[] pathParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "dad.csproj")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the DAD repository root.");
        return File.ReadAllText(Path.Combine([root, .. pathParts]));
    }
}
