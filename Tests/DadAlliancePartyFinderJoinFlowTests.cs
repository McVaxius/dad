extern alias DalamudApi;

using System.Runtime.Versioning;
using dad.Models;
using dad.Services;
using SeString = DalamudApi::Dalamud.Game.Text.SeStringHandling.SeString;
using SeStringBuilder =
    DalamudApi::Dalamud.Game.Text.SeStringHandling.SeStringBuilder;
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
            MatchingListingIndexes = [1],
        };
        AssertEvent(fixture.Advance(), "refresh-acknowledged");
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
            DadAlliancePfJoinAction.SubmitPasscode,
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
            "passcode-acknowledged-detail-closed");
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
                DadAlliancePfJoinAction.SelectAlliance,
                DadAlliancePfJoinAction.ConfirmYes,
                DadAlliancePfJoinAction.SubmitPasscode,
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
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void MatchingRendererRowDrivesTheRequestedListingIndex(
        int matchingIndex)
    {
        var fixture = ReadyToInspect(
            numberOfListings: 3,
            matchingListingIndexes: [matchingIndex]);

        AssertAction(
            fixture.Advance(),
            DadAlliancePfJoinAction.OpenListing,
            listingIndex: matchingIndex);
        Assert.DoesNotContain(
            fixture.Ui.Requests,
            request =>
                request.Action == DadAlliancePfJoinAction.OpenListing &&
                request.ListingIndex < matchingIndex);
    }

    [Fact]
    public void DelayedRowHydrationWaitsWithoutCallbackThenOpensExactRow()
    {
        var fixture = ReadyToInspect(
            numberOfListings: 3,
            matchingListingIndexes: []);

        var waiting = fixture.Advance();
        fixture.Clock.Advance(TimeSpan.FromSeconds(4));
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            MatchingListingIndexes = [2],
        };
        var opened = fixture.Advance();

        Assert.Equal(DadAlliancePfJoinResultKind.Waiting, waiting.Kind);
        Assert.DoesNotContain(
            fixture.Ui.Requests,
            static request =>
                request.Action == DadAlliancePfJoinAction.OpenListing &&
                request.ListingIndex != 2);
        AssertAction(
            opened,
            DadAlliancePfJoinAction.OpenListing,
            listingIndex: 2);
    }

    [Fact]
    public void MissingMatchingNameTimesOutWithoutListingCallback()
    {
        var fixture = ReadyToInspect(
            numberOfListings: 2,
            matchingListingIndexes: []);

        Assert.Equal(
            DadAlliancePfJoinResultKind.Waiting,
            fixture.Advance().Kind);
        fixture.Clock.Advance(
            DadAlliancePartyFinderJoinFlow.ObservationTimeout);
        var retry = fixture.Advance();

        Assert.Equal(DadAlliancePfJoinResultKind.Retry, retry.Kind);
        Assert.DoesNotContain(
            fixture.Ui.Requests,
            static request =>
                request.Action == DadAlliancePfJoinAction.OpenListing);
    }

    [Fact]
    public void DuplicateCoordinatorRowsAdvanceAfterWorldValidationFails()
    {
        var fixture = ReadyToInspect(
            numberOfListings: 3,
            matchingListingIndexes: [0, 2]);
        AssertAction(
            fixture.Advance(),
            DadAlliancePfJoinAction.OpenListing,
            listingIndex: 0);
        fixture.Ui.Snapshot = ExactDetail(fixture.Ui.Snapshot) with
        {
            DetailLeaderWorld = "Other World",
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
            listingIndex: 2);
    }

    [Theory]
    [InlineData(false, "Fuyu Tempest", 0)]
    [InlineData(false, "Vartak Adamantaia", 1)]
    [InlineData(true, "Fuyu Tempest", 0)]
    [InlineData(true, "Vartak Adamantaia", 1)]
    public void StandardAndCompactViewsResolveExactRecruiterNodeName(
        bool compact,
        string recruiterName,
        int expectedIndex)
    {
        var selected = ListingView(
            new DadAlliancePfListingRendererSnapshot(0, "Fuyu Tempest"),
            new DadAlliancePfListingRendererSnapshot(
                1,
                "Vartak Adamantaia"));
        var unavailable = new DadAlliancePfListingViewSnapshot();

        var indexes = DadAlliancePartyFinderListingRowResolver.Resolve(
            recruiterName,
            2,
            compact ? unavailable : selected,
            compact ? selected : unavailable);

        Assert.Equal([expectedIndex], indexes);
    }

    [Fact]
    [SupportedOSPlatform("windows7.0")]
    public void FormattedSeStringRecruiterResolvesFromPlainTextValue()
    {
        var encoded = new SeStringBuilder()
            .AddUiForeground(43)
            .AddText("Fuyu Tempest")
            .AddUiForegroundOff()
            .Encode();
        var recruiterName = SeString.Parse(encoded).TextValue;
        var view = ListingView(
            new DadAlliancePfListingRendererSnapshot(0, recruiterName));

        var indexes = DadAlliancePartyFinderListingRowResolver.Resolve(
            "Fuyu Tempest",
            1,
            view,
            new DadAlliancePfListingViewSnapshot());

        Assert.Equal([0], indexes);
    }

    [Fact]
    public void HiddenUnreadyAmbiguousAndUnavailableListsAreRejected()
    {
        var renderer = new DadAlliancePfListingRendererSnapshot(
            0,
            Target.LeaderName);
        var visibleReady = ListingView(renderer);
        var hidden = visibleReady with { Visible = false };
        var unready = visibleReady with { Ready = false };
        var unavailable = new DadAlliancePfListingViewSnapshot();

        Assert.Empty(DadAlliancePartyFinderListingRowResolver.Resolve(
            Target.LeaderName,
            1,
            hidden,
            unavailable));
        Assert.Empty(DadAlliancePartyFinderListingRowResolver.Resolve(
            Target.LeaderName,
            1,
            unready,
            unavailable));
        Assert.Empty(DadAlliancePartyFinderListingRowResolver.Resolve(
            Target.LeaderName,
            1,
            visibleReady,
            visibleReady));
        Assert.Empty(DadAlliancePartyFinderListingRowResolver.Resolve(
            Target.LeaderName,
            1,
            unavailable,
            unavailable));
    }

    [Fact]
    public void InvalidDuplicateAndOutOfRangeRendererIndexesAreBoundedAndSorted()
    {
        var view = ListingView(
            new DadAlliancePfListingRendererSnapshot(
                2,
                Target.LeaderName),
            new DadAlliancePfListingRendererSnapshot(
                -1,
                Target.LeaderName),
            new DadAlliancePfListingRendererSnapshot(
                1,
                Target.LeaderName),
            new DadAlliancePfListingRendererSnapshot(
                1,
                Target.LeaderName),
            new DadAlliancePfListingRendererSnapshot(
                3,
                Target.LeaderName));

        var indexes = DadAlliancePartyFinderListingRowResolver.Resolve(
            Target.LeaderName,
            3,
            view,
            new DadAlliancePfListingViewSnapshot());

        Assert.Equal([1, 2], indexes);
    }

    [Theory]
    [InlineData("expected leader")]
    [InlineData(" Expected Leader")]
    [InlineData("Expected Leader ")]
    [InlineData("Expected")]
    [InlineData("Expected Leader Extra")]
    [InlineData("Other Recruiter")]
    public void RecruiterNodeRequiresExactOrdinalCoordinatorName(
        string recruiterName)
    {
        var view = ListingView(
            new DadAlliancePfListingRendererSnapshot(
                0,
                recruiterName));

        var indexes = DadAlliancePartyFinderListingRowResolver.Resolve(
            Target.LeaderName,
            1,
            view,
            new DadAlliancePfListingViewSnapshot());

        Assert.Empty(indexes);
    }

    [Fact]
    public void MissingRecruiterNodeProducesNoMatch()
    {
        var view = ListingView(
            new DadAlliancePfListingRendererSnapshot(0, null));

        var indexes = DadAlliancePartyFinderListingRowResolver.Resolve(
            Target.LeaderName,
            1,
            view,
            new DadAlliancePfListingViewSnapshot());

        Assert.Empty(indexes);
    }

    [Theory]
    [InlineData(DadAllianceAssignment.A, 12, "Alliance A")]
    [InlineData(DadAllianceAssignment.B, 13, "Alliance B")]
    [InlineData(DadAllianceAssignment.C, 14, "Alliance C")]
    [InlineData(DadAllianceAssignment.D, 15, "Alliance D")]
    [InlineData(DadAllianceAssignment.E, 16, "Alliance E")]
    [InlineData(DadAllianceAssignment.F, 17, "Alliance F")]
    [InlineData(DadAllianceAssignment.G, 18, "Alliance G")]
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
        Assert.Equal("LookingForGroupDetail", callback.Addon);
        Assert.True(callback.UpdateState);
        Assert.Equal([callbackId, callbackText], callback.Values);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(8)]
    public void NoneAndOutOfRangeAllianceCallbacksAreRejected(int rawAssignment)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => DadAlliancePartyFinderJoinCallbacks.Build(
                new DadAlliancePfJoinActionRequest(
                    DadAlliancePfJoinAction.SelectAlliance,
                    Alliance: (DadAllianceAssignment)rawAssignment)));

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
    public void ExactHudCallbackPlanUsesPrivateRaidsPasscodeAndRawDetailClose()
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
                DadAlliancePfJoinAction.SubmitPasscode,
                Passcode: 9752));
        var passcode = Assert.Single(submit);
        Assert.Equal("LookingForGroupPrivate", passcode.Addon);
        Assert.True(passcode.UpdateState);
        Assert.Equal([0, 9752], passcode.Values);
        AssertCallback(
            DadAlliancePfJoinAction.CloseDetail,
            "LookingForGroupDetail",
            [-2],
            updateState: false);
    }

    [Fact]
    public void ListingAllianceAndPasscodePayloadsRemainExact()
    {
        var listing = DadAlliancePartyFinderJoinCallbacks.Build(
            new DadAlliancePfJoinActionRequest(
                DadAlliancePfJoinAction.OpenListing,
                ListingIndex: 1));
        Assert.Collection(
            listing,
            callback => Assert.Equal([13, 1], callback.Values),
            callback => Assert.Equal([11, 1], callback.Values));

        var allianceB = Assert.Single(
            DadAlliancePartyFinderJoinCallbacks.Build(
                new DadAlliancePfJoinActionRequest(
                    DadAlliancePfJoinAction.SelectAlliance,
                    Alliance: DadAllianceAssignment.B)));
        var allianceC = Assert.Single(
            DadAlliancePartyFinderJoinCallbacks.Build(
                new DadAlliancePfJoinActionRequest(
                    DadAlliancePfJoinAction.SelectAlliance,
                    Alliance: DadAllianceAssignment.C)));
        var passcode = Assert.Single(
            DadAlliancePartyFinderJoinCallbacks.Build(
                new DadAlliancePfJoinActionRequest(
                    DadAlliancePfJoinAction.SubmitPasscode,
                    Passcode: Target.Passcode)));

        Assert.Equal([13, "Alliance B"], allianceB.Values);
        Assert.Equal([14, "Alliance C"], allianceC.Values);
        Assert.Equal([0, Target.Passcode], passcode.Values);
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
    public void AllianceBRetainsYesNoRoute()
    {
        var target = Target with
        {
            AssignedAlliance = DadAllianceAssignment.B,
        };
        var fixture = ReadyAtExactDetail();

        AssertAction(
            fixture.Flow.Advance(target),
            DadAlliancePfJoinAction.SelectAlliance);
        Assert.Equal(
            DadAllianceAssignment.B,
            fixture.Ui.Requests[^1].Alliance);
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            YesNoVisible = true,
            YesNoReady = true,
            YesNoIdentity = "alliance-b-confirmation",
        };
        AssertEvent(
            fixture.Flow.Advance(target),
            "yesno-acknowledged");
        AssertAction(
            fixture.Flow.Advance(target),
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
            fixture.Flow.Advance(target),
            "private-prompt-acknowledged");
        AssertAction(
            fixture.Flow.Advance(target),
            DadAlliancePfJoinAction.SubmitPasscode);

        Assert.Single(
            fixture.Ui.Requests,
            static request =>
                request.Action == DadAlliancePfJoinAction.ConfirmYes);
        Assert.Single(
            fixture.Ui.Requests,
            static request =>
                request.Action == DadAlliancePfJoinAction.SubmitPasscode);
    }

    [Fact]
    public void AllianceCDirectPrivatePromptSkipsYesAndSubmitsOnce()
    {
        var fixture = ReadyAtExactDetail();
        AssertAction(
            fixture.Advance(),
            DadAlliancePfJoinAction.SelectAlliance,
            alliance: DadAllianceAssignment.C);
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            PrivatePromptVisible = true,
            PrivatePromptReady = true,
        };

        AssertEvent(
            fixture.Advance(),
            "private-prompt-acknowledged");
        AssertAction(
            fixture.Advance(),
            DadAlliancePfJoinAction.SubmitPasscode,
            passcode: Target.Passcode);

        Assert.DoesNotContain(
            fixture.Ui.Requests,
            static request =>
                request.Action == DadAlliancePfJoinAction.ConfirmYes);
        Assert.Single(
            fixture.Ui.Requests,
            static request =>
                request.Action == DadAlliancePfJoinAction.SubmitPasscode);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void PreExistingPromptPreventsAllianceDispatch(
        bool yesNoVisible,
        bool privatePromptVisible)
    {
        var fixture = ReadyAtExactDetail();
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            YesNoVisible = yesNoVisible,
            YesNoReady = yesNoVisible,
            YesNoIdentity = yesNoVisible ? "stale" : string.Empty,
            PrivatePromptVisible = privatePromptVisible,
            PrivatePromptReady = privatePromptVisible,
        };
        var requestCount = fixture.Ui.Requests.Count;

        var waiting = fixture.Advance();
        fixture.Clock.Advance(
            DadAlliancePartyFinderJoinFlow.ObservationTimeout);
        var retry = fixture.Advance();

        Assert.Equal(DadAlliancePfJoinResultKind.Waiting, waiting.Kind);
        Assert.NotEqual(DadAlliancePfJoinResultKind.Waiting, retry.Kind);
        Assert.Equal(requestCount, fixture.Ui.Requests.Count);
        Assert.DoesNotContain(
            fixture.Ui.Requests,
            static request =>
                request.Action == DadAlliancePfJoinAction.SelectAlliance);
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, true, true, true)]
    [InlineData(true, false, true, true)]
    public void UnreadyOrSimultaneousPostAlliancePromptsSendNothing(
        bool yesNoVisible,
        bool yesNoReady,
        bool privatePromptVisible,
        bool privatePromptReady)
    {
        var fixture = ReadyAtExactDetail();
        AssertAction(
            fixture.Advance(),
            DadAlliancePfJoinAction.SelectAlliance);
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            YesNoVisible = yesNoVisible,
            YesNoReady = yesNoReady,
            YesNoIdentity = yesNoVisible ? "candidate" : string.Empty,
            PrivatePromptVisible = privatePromptVisible,
            PrivatePromptReady = privatePromptReady,
        };
        var requestCount = fixture.Ui.Requests.Count;

        var waiting = fixture.Advance();
        fixture.Clock.Advance(
            DadAlliancePartyFinderJoinFlow.ObservationTimeout);
        var retry = fixture.Advance();

        Assert.Equal(DadAlliancePfJoinResultKind.Waiting, waiting.Kind);
        Assert.NotEqual(DadAlliancePfJoinResultKind.Waiting, retry.Kind);
        Assert.Equal(requestCount, fixture.Ui.Requests.Count);
        Assert.DoesNotContain(
            fixture.Ui.Requests,
            static request =>
                request.Action is DadAlliancePfJoinAction.ConfirmYes or
                    DadAlliancePfJoinAction.SubmitPasscode);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void AcknowledgedRouteRechecksForOneReadyPromptBeforeCallback(
        bool yesNoRoute,
        bool simultaneous)
    {
        var fixture = ReadyAtExactDetail();
        AssertAction(
            fixture.Advance(),
            DadAlliancePfJoinAction.SelectAlliance);

        if (yesNoRoute)
        {
            fixture.Ui.Snapshot = fixture.Ui.Snapshot with
            {
                YesNoVisible = true,
                YesNoReady = true,
                YesNoIdentity = "acknowledged",
            };
            AssertEvent(
                fixture.Advance(),
                "yesno-acknowledged");
            fixture.Ui.Snapshot = fixture.Ui.Snapshot with
            {
                YesNoReady = simultaneous,
                PrivatePromptVisible = simultaneous,
                PrivatePromptReady = simultaneous,
            };
        }
        else
        {
            fixture.Ui.Snapshot = fixture.Ui.Snapshot with
            {
                PrivatePromptVisible = true,
                PrivatePromptReady = true,
            };
            AssertEvent(
                fixture.Advance(),
                "private-prompt-acknowledged");
            fixture.Ui.Snapshot = fixture.Ui.Snapshot with
            {
                YesNoVisible = simultaneous,
                YesNoReady = simultaneous,
                YesNoIdentity = simultaneous
                    ? "late-confirmation"
                    : string.Empty,
                PrivatePromptReady = simultaneous,
            };
        }

        var requestCount = fixture.Ui.Requests.Count;
        var waiting = fixture.Advance();
        fixture.Clock.Advance(
            DadAlliancePartyFinderJoinFlow.ObservationTimeout);
        var retry = fixture.Advance();

        Assert.Equal(DadAlliancePfJoinResultKind.Waiting, waiting.Kind);
        Assert.NotEqual(DadAlliancePfJoinResultKind.Waiting, retry.Kind);
        Assert.Equal(requestCount, fixture.Ui.Requests.Count);
        Assert.DoesNotContain(
            fixture.Ui.Requests,
            static request =>
                request.Action is DadAlliancePfJoinAction.ConfirmYes or
                    DadAlliancePfJoinAction.SubmitPasscode);
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
        var fixture = ReadyAfterPasscodeDispatch();
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
    public void PrivatePromptMustDisappearOnLaterSnapshotBeforeDeferredClose()
    {
        var fixture = ReadyAfterPasscodeDispatch();

        var waiting = fixture.Advance();

        Assert.Equal(DadAlliancePfJoinResultKind.Waiting, waiting.Kind);
        Assert.Equal(
            DadAlliancePfJoinStage.WaitPasscodeAcknowledged,
            waiting.Stage);
        Assert.Single(
            fixture.Ui.Requests,
            static request =>
                request.Action == DadAlliancePfJoinAction.SubmitPasscode);
        Assert.DoesNotContain(
            fixture.Ui.Requests,
            static request =>
                request.Action == DadAlliancePfJoinAction.CloseDetail);

        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            PrivatePromptVisible = false,
            PrivatePromptReady = false,
        };
        var close = fixture.Advance();

        AssertAction(close, DadAlliancePfJoinAction.CloseDetail);
        AssertEvent(close, "joined-detail-close-dispatched");
    }

    [Fact]
    public void AutomaticallyClosedDetailSkipsDeferredClose()
    {
        var fixture = ReadyAfterPasscodeDispatch();
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            PrivatePromptVisible = false,
            PrivatePromptReady = false,
            DetailVisible = false,
            DetailReady = false,
        };

        var acknowledged = fixture.Advance();

        AssertEvent(
            acknowledged,
            "passcode-acknowledged-detail-closed");
        Assert.Equal(
            DadAlliancePfJoinStage.VerifyAlliance,
            acknowledged.Stage);
        Assert.DoesNotContain(
            fixture.Ui.Requests,
            static request =>
                request.Action == DadAlliancePfJoinAction.CloseDetail);
    }

    [Fact]
    public void ReadyRemainingDetailReceivesExactlyOneDeferredClose()
    {
        var fixture = ReadyAfterPasscodeDispatch();
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            PrivatePromptVisible = false,
            PrivatePromptReady = false,
        };
        AssertEvent(
            fixture.Advance(),
            "joined-detail-close-dispatched");

        var waiting = fixture.Advance();
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            DetailVisible = false,
            DetailReady = false,
        };
        var acknowledged = fixture.Advance();

        Assert.Equal(DadAlliancePfJoinResultKind.Waiting, waiting.Kind);
        AssertEvent(
            acknowledged,
            "joined-detail-close-acknowledged");
        Assert.Single(
            fixture.Ui.Requests,
            static request =>
                request.Action == DadAlliancePfJoinAction.CloseDetail);
    }

    [Fact]
    public void UnreadyRemainingDetailWaitsWithoutPrematureClose()
    {
        var fixture = ReadyAfterPasscodeDispatch();
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            PrivatePromptVisible = false,
            PrivatePromptReady = false,
            DetailReady = false,
        };

        var acknowledged = fixture.Advance();
        var waiting = fixture.Advance();

        AssertEvent(
            acknowledged,
            "passcode-acknowledged-detail-pending");
        Assert.Equal(
            DadAlliancePfJoinStage.CloseJoinedDetail,
            acknowledged.Stage);
        Assert.Equal(DadAlliancePfJoinResultKind.Waiting, waiting.Kind);
        Assert.DoesNotContain(
            fixture.Ui.Requests,
            static request =>
                request.Action == DadAlliancePfJoinAction.CloseDetail);

        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            DetailReady = true,
        };
        AssertEvent(
            fixture.Advance(),
            "joined-detail-close-dispatched");
    }

    [Fact]
    public void EarlySubgroupObservationCannotBypassAcknowledgementOrClose()
    {
        var fixture = ReadyAfterPasscodeDispatch();
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            ObservedAlliance = DadAllianceAssignment.C,
        };

        var promptWaiting = fixture.Advance();
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            PrivatePromptVisible = false,
            PrivatePromptReady = false,
        };
        var close = fixture.Advance();
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            DetailVisible = false,
            DetailReady = false,
        };
        var closeAcknowledged = fixture.Advance();
        var completed = fixture.Advance();

        Assert.Equal(
            DadAlliancePfJoinResultKind.Waiting,
            promptWaiting.Kind);
        AssertEvent(close, "joined-detail-close-dispatched");
        AssertEvent(
            closeAcknowledged,
            "joined-detail-close-acknowledged");
        Assert.Equal(
            DadAlliancePfJoinResultKind.Succeeded,
            completed.Kind);
    }

    [Fact]
    public void PasscodeAcknowledgementTimeoutDoesNotDuplicateSubmission()
    {
        var fixture = ReadyAfterPasscodeDispatch();
        fixture.Clock.Advance(
            DadAlliancePartyFinderJoinFlow.ObservationTimeout);

        var retry = fixture.Advance();

        Assert.Equal(DadAlliancePfJoinResultKind.Retry, retry.Kind);
        Assert.Single(
            fixture.Ui.Requests,
            static request =>
                request.Action == DadAlliancePfJoinAction.SubmitPasscode);
        Assert.DoesNotContain(
            fixture.Ui.Requests,
            static request =>
                request.Action == DadAlliancePfJoinAction.CloseDetail);
    }

    [Fact]
    public void DirectPasscodeTimeoutCannotDuplicateSubmission()
    {
        var fixture = ReadyAfterDirectPasscodeDispatch();
        fixture.Clock.Advance(
            DadAlliancePartyFinderJoinFlow.ObservationTimeout);

        var retry = fixture.Advance();
        var stalePromptRetry = fixture.Advance();

        Assert.Equal(DadAlliancePfJoinResultKind.Retry, retry.Kind);
        Assert.Equal(
            DadAlliancePfJoinResultKind.Retry,
            stalePromptRetry.Kind);
        Assert.DoesNotContain(
            fixture.Ui.Requests,
            static request =>
                request.Action == DadAlliancePfJoinAction.ConfirmYes);
        Assert.Single(
            fixture.Ui.Requests,
            static request =>
                request.Action == DadAlliancePfJoinAction.SubmitPasscode);
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

    private static Fixture ReadyToInspect(
        int numberOfListings,
        IReadOnlyList<int>? matchingListingIndexes = null)
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
            MatchingListingIndexes =
                matchingListingIndexes ??
                Enumerable.Range(0, numberOfListings).ToArray(),
        };
        AssertEvent(fixture.Advance(), "refresh-acknowledged");
        return fixture;
    }

    private static DadAlliancePfListingViewSnapshot ListingView(
        params DadAlliancePfListingRendererSnapshot[] renderers)
        => new()
        {
            Available = true,
            Visible = true,
            Ready = true,
            ListLength = 3,
            Renderers = renderers,
        };

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

    private static Fixture ReadyAfterPasscodeDispatch()
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
            DadAlliancePfJoinAction.SubmitPasscode,
            passcode: Target.Passcode);
        return fixture;
    }

    private static Fixture ReadyAfterDirectPasscodeDispatch()
    {
        var fixture = ReadyAtExactDetail();
        AssertAction(
            fixture.Advance(),
            DadAlliancePfJoinAction.SelectAlliance,
            alliance: DadAllianceAssignment.C);
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            PrivatePromptVisible = true,
            PrivatePromptReady = true,
        };
        AssertEvent(
            fixture.Advance(),
            "private-prompt-acknowledged");
        AssertAction(
            fixture.Advance(),
            DadAlliancePfJoinAction.SubmitPasscode,
            passcode: Target.Passcode);
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
            DadAlliancePfJoinAction.SubmitPasscode =>
                "passcode-dispatched",
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
