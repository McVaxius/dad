using Xunit;
using dad.Models;

namespace dad.Tests;

public sealed class DadRouletteRewardDetailRulesTests
{
    [Fact]
    public void VisibleRewardUnclaimedFromOceScreenshotIsNotReceived()
    {
        var detail = Detail() with { MarkerText = "Reward Unclaimed" };
        Assert.True(DadRouletteRewardDetailRules.TryCreateObservation(detail, 123, 1, out var observation, out _));
        Assert.Equal(0, observation.ReceivedRewardCount);
    }

    [Theory]
    [InlineData("Reward")]
    [InlineData("Adventurer in Need (DPS)")]
    [InlineData("Reward already received later")]
    public void UnrecognizedVisibleTextCannotClaimReward(string text)
        => Assert.False(DadRouletteRewardDetailRules.TryCreateObservation(Detail() with { MarkerText = text }, 123, 1, out _, out _));

    [Fact]
    public void ProductionRewardDecisionReadsTheExactVisibleJournalDetailIndicator()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "dad.csproj")))
            root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root!.FullName, "Services", "DadRouletteRewardProbeService.cs"));
        Assert.Contains("RewardsReceivedResNode", source);
        Assert.Contains("RewardsReceivedTextNode", source);
        Assert.Contains("DadRouletteRewardDetailRules.TryCreateObservation", source);
        Assert.Contains("GetExcelSheet<Lumina.Excel.Sheets.Addon>()", source);
        Assert.Contains("DadRouletteRewardDetailRules.ReceivedLabelRowId", source);
        Assert.Contains("DadRouletteRewardDetailRules.UnclaimedLabelRowId", source);
        Assert.Equal(102588u, DadRouletteRewardDetailRules.ReceivedLabelRowId);
        Assert.Equal(16469u, DadRouletteRewardDetailRules.UnclaimedLabelRowId);
        Assert.DoesNotContain("var received = rawIsComplete ? 1 : 0;", source);
    }

    [Theory]
    [InlineData("Reward already received", DadRouletteRewardObservationStatus.Received)]
    [InlineData("Reward Unclaimed", DadRouletteRewardObservationStatus.NotReceived)]
    public void ExactVisibleStatusTextRatherThanVisibilityDeterminesDaily(string text,
        DadRouletteRewardObservationStatus expected)
    {
        var detail = Detail() with { MarkerText = text };
        Assert.True(DadRouletteRewardDetailRules.TryCreateObservation(detail, 123, 1, out var first, out _));
        var gate = new DadRouletteRewardObservationGate();
        Assert.Equal(DadRouletteRewardObservationStatus.Waiting, gate.Observe(first, 123, 1, out _));
        Assert.Equal(DadRouletteRewardObservationStatus.Waiting,
            gate.Observe(first with { CapturedAtUtc = first.CapturedAtUtc.AddMilliseconds(100) }, 123, 1, out _));
        Assert.Equal(expected,
            gate.Observe(first with { CapturedAtUtc = first.CapturedAtUtc.AddMilliseconds(300) }, 123, 1, out _));
    }

    [Theory]
    [InlineData("Duty Roulette: Leveling", "Reward already received", "Reward Unclaimed")]
    [InlineData("Zufallsinhalt: Stufensteigerung", "Vergütung bereits erhalten", "Belohnung nicht erhalten")]
    [InlineData("Mission aléatoire : gain de niveaux", "Récompense déjà obtenue", "Récompense non obtenue")]
    [InlineData("コンテンツルーレット：レベリング", "報酬受領済み", "報酬未受領")]
    public void TitleAndBothRewardStatesUseExactLocalizedGameLabels(string title, string received, string unclaimed)
    {
        var detail = Detail() with
        {
            ExpectedTitle = title, DisplayedTitle = "  " + title + "\n", MarkerText = received,
            ExpectedReceivedText = received, ExpectedUnclaimedText = unclaimed,
        };
        Assert.True(DadRouletteRewardDetailRules.TryCreateObservation(detail, 123, 1, out var result, out _));
        Assert.Equal(1, result.ReceivedRewardCount);
        Assert.True(DadRouletteRewardDetailRules.TryCreateObservation(detail with { MarkerText = unclaimed }, 123, 1, out result, out _));
        Assert.Equal(0, result.ReceivedRewardCount);
        Assert.False(DadRouletteRewardDetailRules.TryCreateObservation(detail with { MarkerText = "unrecognized localized label" }, 123, 1, out _, out _));
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
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    public void MissingUnreadyOrContradictoryDetailNeverClaimsAReward(int fault)
    {
        var detail = fault switch
        {
            0 => Detail() with { CharacterContentId = 456 },
            1 => Detail() with { RouletteId = 3 },
            2 => Detail() with { ListFingerprint = "" },
            3 => Detail() with { DetailIdentity = 0 },
            4 => Detail() with { DetailsReady = false },
            5 => Detail() with { ExpectedTitle = "" },
            6 => Detail() with { DisplayedTitle = "Duty Roulette: Main Scenario" },
            7 => Detail() with { MarkerNodesAvailable = false },
            8 => Detail() with { MarkerVisible = false },
            9 => Detail() with { MarkerTextVisible = false },
            10 => Detail() with { MarkerText = "" },
            11 => Detail() with { CapturedAtUtc = default },
            12 => Detail() with { ExpectedReceivedText = "" },
            13 => Detail() with { ExpectedUnclaimedText = "" },
            14 => Detail() with { ExpectedUnclaimedText = "Reward already received" },
            _ => Detail() with { MarkerVisible = false, MarkerTextVisible = false },
        };
        Assert.False(DadRouletteRewardDetailRules.TryCreateObservation(detail, 123, 1, out _, out var reason));
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void RecreatedDetailAddonRequiresFreshTwoObservationProof()
    {
        var detail = Detail();
        var gate = new DadRouletteRewardObservationGate();
        Assert.True(DadRouletteRewardDetailRules.TryCreateObservation(detail, 123, 1, out var first, out _));
        Assert.Equal(DadRouletteRewardObservationStatus.Waiting, gate.Observe(first, 123, 1, out _));
        Assert.True(DadRouletteRewardDetailRules.TryCreateObservation(detail with
        { DetailIdentity = 999, CapturedAtUtc = detail.CapturedAtUtc.AddSeconds(1) }, 123, 1, out var next, out _));
        Assert.Equal(DadRouletteRewardObservationStatus.Waiting, gate.Observe(next, 123, 1, out _));
        Assert.Equal(DadRouletteRewardObservationStatus.Received,
            gate.Observe(next with { CapturedAtUtc = next.CapturedAtUtc.AddSeconds(1) }, 123, 1, out _));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void BothCheckedClientsMustHaveReceivedBeforeSkipping(bool firstReceived, bool secondReceived)
    {
        var first = DadDailyRewardPreflightRules.Resolve(2, firstReceived ? 1 : 0, true, false,
            firstReceived ? DadRouletteRewardProbeOutcome.Received : DadRouletteRewardProbeOutcome.NotReceived);
        if (!firstReceived)
        {
            Assert.Equal(DadDailyRewardPreflightDisposition.RunNormally, first);
            return;
        }
        Assert.Equal(DadDailyRewardPreflightDisposition.ContinueToNextCheckedSlot, first);
        var second = DadDailyRewardPreflightRules.Resolve(2, 1 + (secondReceived ? 1 : 0), true, false,
            secondReceived ? DadRouletteRewardProbeOutcome.Received : DadRouletteRewardProbeOutcome.NotReceived);
        Assert.Equal(secondReceived ? DadDailyRewardPreflightDisposition.SkipEntry : DadDailyRewardPreflightDisposition.RunNormally, second);
    }

    private static DadRouletteRewardDetailSnapshot Detail()
        => new(123, 1, "exact-live-list", 42, true, "Duty Roulette: Leveling", "Duty Roulette: Leveling",
            true, true, true, "Reward already received", new DateTime(2026, 10, 19, 21, 0, 0, DateTimeKind.Utc),
            "Reward already received", "Reward Unclaimed");

    [Fact]
    public void ClaimStatusChangingBetweenObservationsCannotReusePreviousReceivedProof()
    {
        var gate = new DadRouletteRewardObservationGate();
        var detail = Detail();
        Assert.True(DadRouletteRewardDetailRules.TryCreateObservation(detail, 123, 1, out var received, out _));
        Assert.Equal(DadRouletteRewardObservationStatus.Waiting, gate.Observe(received, 123, 1, out _));
        Assert.True(DadRouletteRewardDetailRules.TryCreateObservation(detail with
        { MarkerText = "  Reward\nUnclaimed  ", CapturedAtUtc = detail.CapturedAtUtc.AddSeconds(1) }, 123, 1, out var unclaimed, out _));
        Assert.Equal(DadRouletteRewardObservationStatus.Waiting, gate.Observe(unclaimed, 123, 1, out _));
        Assert.Equal(DadRouletteRewardObservationStatus.NotReceived,
            gate.Observe(unclaimed with { CapturedAtUtc = unclaimed.CapturedAtUtc.AddSeconds(1) }, 123, 1, out _));
    }
}
