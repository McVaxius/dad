using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadAlliancePartyFinderJoinFlowTests
{
    private static readonly DadAlliancePfJoinTarget Target = new()
    {
        LeaderName = "Expected Leader",
        LeaderWorld = "Expected World",
        TargetContentId = 123,
        AssignedAlliance = DadAllianceAssignment.C,
        Passcode = 9752,
    };

    [Fact]
    public void FullJoinIsOrderedSingleDispatchAndAcknowledgementGated()
    {
        var fixture = new Fixture();

        AssertAction(fixture.Advance(), DadAlliancePfJoinAction.Show);
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            MainVisible = true,
            MainReady = true,
        };
        AssertEvent(fixture.Advance(), "window-acknowledged");
        AssertAction(
            fixture.Advance(),
            DadAlliancePfJoinAction.SelectPrivate);
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            SearchAreaTab = 2,
        };
        AssertEvent(fixture.Advance(), "private-tab-acknowledged");
        AssertAction(
            fixture.Advance(),
            DadAlliancePfJoinAction.SelectRaids);
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            CategoryTab = 5,
        };
        AssertEvent(fixture.Advance(), "raids-acknowledged");
        AssertAction(fixture.Advance(), DadAlliancePfJoinAction.Refresh);
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            NumberOfListings = 2,
        };
        AssertEvent(fixture.Advance(), "refresh-acknowledged");
        AssertAction(
            fixture.Advance(),
            DadAlliancePfJoinAction.OpenListing,
            listingIndex: 0);

        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            DetailVisible = true,
            DetailReady = true,
            DetailLeaderName = "Another Leader",
            DetailLeaderWorld = "Expected World",
            DetailDutyId = 92,
            DetailPrivate = true,
            DetailAlliance = true,
            DetailPartyCount = 3,
        };
        AssertEvent(fixture.Advance(), "listing-rejected");
        AssertAction(
            fixture.Advance(),
            DadAlliancePfJoinAction.CloseDetail);
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            DetailVisible = false,
            DetailReady = false,
        };
        AssertEvent(fixture.Advance(), "detail-close-acknowledged");
        AssertAction(
            fixture.Advance(),
            DadAlliancePfJoinAction.OpenListing,
            listingIndex: 1);

        fixture.Ui.Snapshot = ExactDetail(fixture.Ui.Snapshot);
        AssertEvent(fixture.Advance(), "listing-acknowledged");
        AssertAction(
            fixture.Advance(),
            DadAlliancePfJoinAction.SelectAlliance,
            alliance: DadAllianceAssignment.C);
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            YesNoVisible = true,
            YesNoReady = true,
            YesNoIdentity = "fresh-confirmation",
        };
        AssertEvent(fixture.Advance(), "yesno-acknowledged");
        AssertAction(
            fixture.Advance(),
            DadAlliancePfJoinAction.ConfirmYes);
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            YesNoVisible = false,
            YesNoReady = false,
            YesNoIdentity = string.Empty,
            PrivatePromptVisible = true,
            PrivatePromptReady = true,
        };
        AssertEvent(
            fixture.Advance(),
            "private-prompt-acknowledged");
        AssertAction(
            fixture.Advance(),
            DadAlliancePfJoinAction.SubmitPasscodeAndCloseDetail,
            passcode: Target.Passcode);
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            PrivatePromptVisible = false,
            PrivatePromptReady = false,
            DetailVisible = false,
            DetailReady = false,
            ObservedAlliance = DadAllianceAssignment.C,
        };
        AssertEvent(
            fixture.Advance(),
            "passcode-and-detail-close-acknowledged");
        var completed = fixture.Advance();

        Assert.Equal(
            DadAlliancePfJoinResultKind.Succeeded,
            completed.Kind);
        AssertEvent(completed, "subgroup-acknowledged");
        Assert.Equal(
            [
                DadAlliancePfJoinAction.Show,
                DadAlliancePfJoinAction.SelectPrivate,
                DadAlliancePfJoinAction.SelectRaids,
                DadAlliancePfJoinAction.Refresh,
                DadAlliancePfJoinAction.OpenListing,
                DadAlliancePfJoinAction.CloseDetail,
                DadAlliancePfJoinAction.OpenListing,
                DadAlliancePfJoinAction.SelectAlliance,
                DadAlliancePfJoinAction.ConfirmYes,
                DadAlliancePfJoinAction.SubmitPasscodeAndCloseDetail,
            ],
            fixture.Ui.Requests.Select(static request => request.Action));
        Assert.All(
            fixture.Ui.Requests
                .GroupBy(static request => request),
            static group => Assert.Single(group));
    }

    [Fact]
    public void VisibleWindowIsNeverShownOrToggledDuringRefreshRetries()
    {
        var fixture = new Fixture
        {
            Ui =
            {
                Snapshot = new DadAlliancePfJoinSnapshot
                {
                    MainVisible = true,
                    MainReady = true,
                    SearchAreaTab = 2,
                    CategoryTab = 5,
                },
            },
        };

        AssertEvent(fixture.Advance(), "window-acknowledged");
        AssertEvent(fixture.Advance(), "private-tab-acknowledged");
        AssertEvent(fixture.Advance(), "raids-acknowledged");
        AssertAction(fixture.Advance(), DadAlliancePfJoinAction.Refresh);
        fixture.Clock.Advance(
            DadAlliancePartyFinderJoinFlow.ObservationTimeout);
        var retry = fixture.Advance();
        fixture.Advance();
        fixture.Advance();
        fixture.Advance();
        AssertAction(
            fixture.Advance(),
            DadAlliancePfJoinAction.Refresh);

        Assert.Equal(DadAlliancePfJoinResultKind.Retry, retry.Kind);
        Assert.DoesNotContain(
            fixture.Ui.Requests,
            static request => request.Action == DadAlliancePfJoinAction.Show);
        Assert.Equal(
            2,
            fixture.Ui.Requests.Count(
            static request =>
                request.Action == DadAlliancePfJoinAction.Refresh));
    }

    [Fact]
    public void ListingCursorIsPreservedWhileDetailIsPending()
    {
        var fixture = ReadyToInspect(numberOfListings: 2);

        AssertAction(
            fixture.Advance(),
            DadAlliancePfJoinAction.OpenListing,
            listingIndex: 0);
        var firstWait = fixture.Advance();
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        var secondWait = fixture.Advance();

        Assert.Equal(DadAlliancePfJoinResultKind.Waiting, firstWait.Kind);
        Assert.Equal(0, firstWait.ListingIndex);
        Assert.Equal(DadAlliancePfJoinResultKind.Waiting, secondWait.Kind);
        Assert.Equal(0, secondWait.ListingIndex);
        Assert.Single(
            fixture.Ui.Requests,
            static request =>
                request.Action == DadAlliancePfJoinAction.OpenListing);
    }

    [Theory]
    [InlineData(DadAllianceAssignment.A, 12, "AllianceA")]
    [InlineData(DadAllianceAssignment.B, 13, "AllianceB")]
    [InlineData(DadAllianceAssignment.C, 14, "AllianceC")]
    public void AllianceCallbacksMatchHudObservations(
        DadAllianceAssignment alliance,
        int callbackId,
        string callbackText)
    {
        var callbacks = DadAlliancePartyFinderJoinCallbacks.Build(
            new DadAlliancePfJoinActionRequest(
                DadAlliancePfJoinAction.SelectAlliance,
                Alliance: alliance));

        var callback = Assert.Single(callbacks);
        Assert.Equal("LookingForGroup", callback.Addon);
        Assert.True(callback.UpdateState);
        Assert.Equal([callbackId, callbackText], callback.Values);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ListingCallbackGroupsMatchExactZeroBasedHudPairs(
        int listingIndex)
    {
        var open = DadAlliancePartyFinderJoinCallbacks.Build(
            new DadAlliancePfJoinActionRequest(
                DadAlliancePfJoinAction.OpenListing,
                ListingIndex: listingIndex));

        Assert.Collection(
            open,
            callback =>
            {
                Assert.Equal("LookingForGroup", callback.Addon);
                Assert.True(callback.UpdateState);
                Assert.Equal([13, listingIndex], callback.Values);
            },
            callback =>
            {
                Assert.Equal("LookingForGroup", callback.Addon);
                Assert.True(callback.UpdateState);
                Assert.Equal([11, listingIndex], callback.Values);
            });
    }

    [Fact]
    public void ExactHudCallbackPlanUsesPrivateRaidsAndGroupedPasscodeClose()
    {
        AssertCallback(
            DadAlliancePfJoinAction.SelectPrivate,
            "LookingForGroup",
            [20, 2]);
        AssertCallback(
            DadAlliancePfJoinAction.SelectRaids,
            "LookingForGroup",
            [21, 5]);
        AssertCallback(
            DadAlliancePfJoinAction.Refresh,
            "LookingForGroup",
            [17]);
        AssertCallback(
            DadAlliancePfJoinAction.ConfirmYes,
            "SelectYesno",
            [0]);
        var submit = DadAlliancePartyFinderJoinCallbacks.Build(
            new DadAlliancePfJoinActionRequest(
                DadAlliancePfJoinAction.SubmitPasscodeAndCloseDetail,
                Passcode: 9752));
        Assert.Collection(
            submit,
            callback =>
            {
                Assert.Equal(
                    "LookingForGroupPrivate",
                    callback.Addon);
                Assert.True(callback.UpdateState);
                Assert.Equal([0, 9752], callback.Values);
            },
            callback =>
            {
                Assert.Equal(
                    "LookingForGroupDetail",
                    callback.Addon);
                Assert.False(callback.UpdateState);
                Assert.Equal([-2], callback.Values);
            });
    }

    [Fact]
    public void MismatchedDutyPrivateAllianceOrPartyCountIsRejected()
    {
        var variants = new[]
        {
            ExactDetail(new DadAlliancePfJoinSnapshot()) with
            {
                DetailDutyId = 91,
            },
            ExactDetail(new DadAlliancePfJoinSnapshot()) with
            {
                DetailPrivate = false,
            },
            ExactDetail(new DadAlliancePfJoinSnapshot()) with
            {
                DetailAlliance = false,
            },
            ExactDetail(new DadAlliancePfJoinSnapshot()) with
            {
                DetailPartyCount = 2,
            },
        };

        foreach (var detail in variants)
        {
            var fixture = ReadyToInspect(numberOfListings: 1);
            AssertAction(
                fixture.Advance(),
                DadAlliancePfJoinAction.OpenListing,
                listingIndex: 0);
            fixture.Ui.Snapshot = detail;

            AssertEvent(fixture.Advance(), "listing-rejected");
        }
    }

    [Fact]
    public void ExactDetailIsRevalidatedImmediatelyBeforeAllianceDispatch()
    {
        var fixture = ReadyAtExactDetail();
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            DetailLeaderWorld = "Changed World",
        };

        var blockedDispatch = fixture.Advance();

        AssertEvent(blockedDispatch, "retry-close-required");
        Assert.DoesNotContain(
            fixture.Ui.Requests,
            static request =>
                request.Action == DadAlliancePfJoinAction.SelectAlliance);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void UnexpectedRecruitmentBlocksWithoutCallbacksOrRetryLoop(
        bool workerRecruiting,
        bool conditionVisible)
    {
        var fixture = new Fixture
        {
            Ui =
            {
                Snapshot = new DadAlliancePfJoinSnapshot
                {
                    WorkerRecruiting = workerRecruiting,
                    RecruitmentConditionVisible = conditionVisible,
                    RecruitmentConditionReady = conditionVisible,
                },
            },
        };

        var blocked = fixture.Advance();
        fixture.Ui.Snapshot = new DadAlliancePfJoinSnapshot();
        var stillBlocked = fixture.Advance();

        Assert.Equal(DadAlliancePfJoinResultKind.Blocked, blocked.Kind);
        Assert.Equal(DadAlliancePfJoinStage.Blocked, blocked.Stage);
        AssertEvent(blocked, "unexpected-recruitment-blocked");
        Assert.Equal(DadAlliancePfJoinResultKind.Blocked, stillBlocked.Kind);
        Assert.Empty(fixture.Ui.Requests);
        Assert.Equal(1, fixture.Ui.ReadCount);
    }

    [Fact]
    public void MissingFreshConfirmationRetriesWithoutClickingYes()
    {
        var fixture = ReadyAtExactDetail();
        AssertAction(
            fixture.Advance(),
            DadAlliancePfJoinAction.SelectAlliance,
            alliance: DadAllianceAssignment.C);
        fixture.Clock.Advance(
            DadAlliancePartyFinderJoinFlow.ObservationTimeout);

        var closeRequired = fixture.Advance();
        Assert.Equal(
            DadAlliancePfJoinResultKind.Progress,
            closeRequired.Kind);
        AssertEvent(closeRequired, "retry-close-required");
        Assert.DoesNotContain(
            fixture.Ui.Requests,
            static request =>
                request.Action == DadAlliancePfJoinAction.ConfirmYes);
    }

    [Fact]
    public void UnacknowledgedDetailCloseIsNotRedispatchedInTheSameCycle()
    {
        var fixture = ReadyToInspect(numberOfListings: 1);
        fixture.Advance();
        fixture.Ui.Snapshot = ExactDetail(fixture.Ui.Snapshot) with
        {
            DetailPrivate = false,
        };
        fixture.Advance();
        AssertAction(
            fixture.Advance(),
            DadAlliancePfJoinAction.CloseDetail);
        fixture.Clock.Advance(
            DadAlliancePartyFinderJoinFlow.ObservationTimeout);

        var retry = fixture.Advance();

        Assert.Equal(DadAlliancePfJoinResultKind.Retry, retry.Kind);
        Assert.Single(
            fixture.Ui.Requests,
            static request =>
                request.Action == DadAlliancePfJoinAction.CloseDetail);
    }

    [Fact]
    public void MissingAddonActionRetriesSafely()
    {
        var fixture = new Fixture
        {
            Ui =
            {
                Snapshot = new DadAlliancePfJoinSnapshot
                {
                    MainVisible = true,
                    MainReady = true,
                },
                FailureAction = DadAlliancePfJoinAction.SelectPrivate,
            },
        };

        fixture.Advance();
        var retry = fixture.Advance();

        Assert.Equal(DadAlliancePfJoinResultKind.Retry, retry.Kind);
        Assert.Equal(DadAlliancePfJoinStage.EnsureWindow, retry.Stage);
    }

    [Fact]
    public void CallbackCompletionAloneNeverSucceedsWithoutExactSubgroup()
    {
        var fixture = ReadyAtExactDetail();
        AssertAction(
            fixture.Advance(),
            DadAlliancePfJoinAction.SelectAlliance,
            alliance: DadAllianceAssignment.C);
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            YesNoVisible = true,
            YesNoReady = true,
            YesNoIdentity = "fresh",
        };
        fixture.Advance();
        fixture.Advance();
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            YesNoVisible = false,
            YesNoReady = false,
            YesNoIdentity = string.Empty,
            PrivatePromptVisible = true,
            PrivatePromptReady = true,
        };
        fixture.Advance();
        AssertAction(
            fixture.Advance(),
            DadAlliancePfJoinAction.SubmitPasscodeAndCloseDetail,
            passcode: Target.Passcode);
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            PrivatePromptVisible = false,
            PrivatePromptReady = false,
            DetailVisible = false,
            DetailReady = false,
        };
        fixture.Advance();

        var waiting = fixture.Advance();

        Assert.Equal(DadAlliancePfJoinResultKind.Waiting, waiting.Kind);
        Assert.Equal(
            DadAlliancePfJoinStage.VerifyAlliance,
            waiting.Stage);
    }

    [Fact]
    public void StopPreventsFurtherRetryOrCallbacks()
    {
        var fixture = new Fixture();
        fixture.Flow.Stop();

        var stopped = fixture.Advance();

        Assert.Equal(DadAlliancePfJoinResultKind.Stopped, stopped.Kind);
        Assert.Empty(fixture.Ui.Requests);
    }

    private static Fixture ReadyToInspect(int numberOfListings)
    {
        var fixture = new Fixture
        {
            Ui =
            {
                Snapshot = new DadAlliancePfJoinSnapshot
                {
                    MainVisible = true,
                    MainReady = true,
                    SearchAreaTab = 2,
                    CategoryTab = 5,
                },
            },
        };
        fixture.Advance();
        fixture.Advance();
        fixture.Advance();
        AssertAction(fixture.Advance(), DadAlliancePfJoinAction.Refresh);
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            NumberOfListings = numberOfListings,
        };
        AssertEvent(fixture.Advance(), "refresh-acknowledged");
        return fixture;
    }

    private static Fixture ReadyAtExactDetail()
    {
        var fixture = ReadyToInspect(numberOfListings: 1);
        AssertAction(
            fixture.Advance(),
            DadAlliancePfJoinAction.OpenListing,
            listingIndex: 0);
        fixture.Ui.Snapshot = ExactDetail(fixture.Ui.Snapshot);
        AssertEvent(fixture.Advance(), "listing-acknowledged");
        return fixture;
    }

    private static DadAlliancePfJoinSnapshot ExactDetail(
        DadAlliancePfJoinSnapshot snapshot)
        => snapshot with
        {
            DetailVisible = true,
            DetailReady = true,
            DetailLeaderName = Target.LeaderName,
            DetailLeaderWorld = Target.LeaderWorld,
            DetailDutyId = 92,
            DetailPrivate = true,
            DetailAlliance = true,
            DetailPartyCount = 3,
        };

    private static void AssertAction(
        DadAlliancePfJoinResult result,
        DadAlliancePfJoinAction expected,
        int listingIndex = -1,
        DadAllianceAssignment alliance = DadAllianceAssignment.None,
        int passcode = 0)
    {
        Assert.Equal(DadAlliancePfJoinResultKind.Progress, result.Kind);
        var expectedEvent = expected switch
        {
            DadAlliancePfJoinAction.Show => "show-dispatched",
            DadAlliancePfJoinAction.SelectPrivate =>
                "private-tab-dispatched",
            DadAlliancePfJoinAction.SelectRaids => "raids-dispatched",
            DadAlliancePfJoinAction.Refresh => "refresh-dispatched",
            DadAlliancePfJoinAction.OpenListing =>
                "listing-open-dispatched",
            DadAlliancePfJoinAction.CloseDetail =>
                result.Event,
            DadAlliancePfJoinAction.SelectAlliance =>
                "alliance-dispatched",
            DadAlliancePfJoinAction.ConfirmYes => "yes-dispatched",
            DadAlliancePfJoinAction.SubmitPasscodeAndCloseDetail =>
                "passcode-and-detail-close-dispatched",
            _ => result.Event,
        };
        Assert.Equal(expectedEvent, result.Event);
        if (listingIndex >= 0)
            Assert.Equal(listingIndex, result.ListingIndex);
        _ = alliance;
        _ = passcode;
    }

    private static void AssertEvent(
        DadAlliancePfJoinResult result,
        string expected)
        => Assert.Equal(expected, result.Event);

    private static void AssertCallback(
        DadAlliancePfJoinAction action,
        string addon,
        object[] values,
        bool updateState = true)
    {
        var callback = Assert.Single(
            DadAlliancePartyFinderJoinCallbacks.Build(
                new DadAlliancePfJoinActionRequest(action)));
        Assert.Equal(addon, callback.Addon);
        Assert.Equal(updateState, callback.UpdateState);
        Assert.Equal(values, callback.Values);
    }

    private sealed class Fixture
    {
        public Fixture()
        {
            Flow = new DadAlliancePartyFinderJoinFlow(
                Ui,
                () => Clock.UtcNow);
        }

        public FakeClock Clock { get; } = new();
        public FakeUi Ui { get; } = new();
        public DadAlliancePartyFinderJoinFlow Flow { get; }

        public DadAlliancePfJoinResult Advance()
            => Flow.Advance(Target);
    }

    private sealed class FakeClock
    {
        public DateTime UtcNow { get; private set; } =
            new(2026, 7, 27, 0, 0, 0, DateTimeKind.Utc);

        public void Advance(TimeSpan duration)
            => UtcNow += duration;
    }

    private sealed class FakeUi : IDadAlliancePartyFinderJoinUi
    {
        public DadAlliancePfJoinSnapshot Snapshot { get; set; } = new();
        public DadAlliancePfJoinAction? FailureAction { get; set; }
        public int ReadCount { get; private set; }
        public List<DadAlliancePfJoinActionRequest> Requests { get; } = [];

        public DadAlliancePfJoinSnapshot Read(DadAlliancePfJoinTarget target)
        {
            ReadCount++;
            return Snapshot;
        }

        public DadAlliancePfJoinActionResult Perform(
            DadAlliancePfJoinActionRequest request)
        {
            Requests.Add(request);
            return request.Action == FailureAction
                ? new DadAlliancePfJoinActionResult(
                    false,
                    $"{request.Action} unavailable.")
                : new DadAlliancePfJoinActionResult(
                    true,
                    $"{request.Action} sent.");
        }
    }
}
