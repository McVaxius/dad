using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadAlliancePartyFinderCleanupFlowTests
{
    [Fact]
    public void OnlyRecruitmentCleanupRetainsFixedCallbackContract()
    {
        AssertAddonSpec(
            DadAlliancePfNativeAction.EndRecruitment,
            DadAlliancePfAddonCallbackReceiver.LookingForGroupDetail,
            updateVisibility: false,
            new DadAlliancePfNativeValue(DadAlliancePfNativeValueKind.Int, 11));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DadAlliancePartyFinderNativeActionRules.GetAddonCallbackSpec(
                DadAlliancePfNativeAction.Submit));
    }

    [Fact]
    public void CleanupDispatchSummaryNamesExactReceiverAndPayload()
    {
        var summary =
            DadAlliancePartyFinderNativeActionRules.GetDispatchSummary(
                DadAlliancePfNativeAction.EndRecruitment);

        Assert.Contains(
            "LookingForGroupDetail.FireCallback updateVisibility=false payload [Int 11]",
            summary,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "LookingForGroupCondition",
            summary,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AgentLookingForGroup.ReceiveEvent",
            summary,
            StringComparison.Ordinal);
    }

    private static void AssertAddonSpec(
        DadAlliancePfNativeAction action,
        DadAlliancePfAddonCallbackReceiver receiver,
        bool updateVisibility,
        params DadAlliancePfNativeValue[] expected)
    {
        var spec =
            DadAlliancePartyFinderNativeActionRules.GetAddonCallbackSpec(action);

        Assert.Equal(receiver, spec.Receiver);
        Assert.Equal(updateVisibility, spec.UpdateVisibility);
        Assert.Equal(expected, spec.Values);
    }

    [Fact]
    public void OwnedZeroHandleCleanupRequiresFreshConfirmationAndCondition66Closure()
    {
        var fixture = new Fixture();

        Assert.Equal(DadAlliancePfCleanupStage.OpenMainWindow, fixture.Tick().Stage);
        Assert.Equal(DadAlliancePfNativeAction.ShowOwnedRecruitment, fixture.Ui.Actions[^1]);

        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            MainVisible = true,
            MainReady = true,
            DetailsControlUsable = true,
        };
        Assert.Equal(DadAlliancePfCleanupStage.OpenDetails, fixture.Tick().Stage);
        fixture.Tick();
        Assert.Equal(DadAlliancePfNativeAction.OpenOwnedDetails, fixture.Ui.Actions[^1]);

        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            DetailVisible = true,
            DetailReady = true,
            ConfirmationVisible = true,
            ConfirmationIdentity = "stale",
            ConfirmationText = "Leave the party?",
        };
        Assert.Equal(DadAlliancePfCleanupStage.RequestEndRecruitment, fixture.Tick().Stage);
        Assert.Equal(DadAlliancePfCleanupStage.AwaitConfirmation, fixture.Tick().Stage);
        Assert.Equal(DadAlliancePfNativeAction.EndRecruitment, fixture.Ui.Actions[^1]);

        var stale = fixture.Tick();
        Assert.Equal(DadAlliancePfCreateResultKind.Waiting, stale.Kind);
        Assert.DoesNotContain(
            DadAlliancePfNativeAction.ConfirmEndRecruitment,
            fixture.Ui.Actions);

        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            ConfirmationIdentity = "fresh-recruitment-only",
            ConfirmationText = "End recruitment?",
        };
        Assert.Equal(DadAlliancePfCleanupStage.ConfirmEndRecruitment, fixture.Tick().Stage);
        Assert.Equal(DadAlliancePfCleanupStage.AwaitClosure, fixture.Tick().Stage);
        Assert.Equal(DadAlliancePfNativeAction.ConfirmEndRecruitment, fixture.Ui.Actions[^1]);

        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            ActiveRecruitment = true,
            OwnerHandle = 888,
            ConfirmationVisible = false,
        };
        Assert.Equal(DadAlliancePfCreateResultKind.Waiting, fixture.Tick().Kind);

        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            ActiveRecruitment = false,
            OwnerHandle = 999,
        };
        var completed = fixture.Tick();

        Assert.Equal(DadAlliancePfCreateResultKind.Succeeded, completed.Kind);
        Assert.Equal(DadAlliancePfCleanupStage.Complete, completed.Stage);
        Assert.Equal(
            [
                DadAlliancePfNativeAction.ShowOwnedRecruitment,
                DadAlliancePfNativeAction.OpenOwnedDetails,
                DadAlliancePfNativeAction.EndRecruitment,
                DadAlliancePfNativeAction.ConfirmEndRecruitment,
            ],
            fixture.Ui.Actions);
    }

    [Fact]
    public void FreshNonRecruitmentOrDisbandConfirmationBlocksWithoutConfirming()
    {
        var fixture = new Fixture();
        fixture.ReachAwaitConfirmation();
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            ConfirmationVisible = true,
            ConfirmationIdentity = "fresh-danger",
            ConfirmationText = "Disband the alliance?",
        };

        var result = fixture.Tick();

        Assert.Equal(DadAlliancePfCreateResultKind.Blocked, result.Kind);
        Assert.DoesNotContain(
            DadAlliancePfNativeAction.ConfirmEndRecruitment,
            fixture.Ui.Actions);
    }

    [Fact]
    public void MissingTypedDetailsControlIsVisibleBlocker()
    {
        var fixture = new Fixture();
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            MainVisible = true,
            MainReady = true,
            DetailsControlUsable = false,
            HardBlocker = "The owned Party Finder window is missing its typed details control.",
        };

        var result = fixture.Tick();

        Assert.Equal(DadAlliancePfCreateResultKind.Blocked, result.Kind);
        Assert.Empty(fixture.Ui.Actions);
    }

    [Fact]
    public void NativeOwnerHandleChangesAreDiagnosticDuringOwnedCleanup()
    {
        var fixture = new Fixture();
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with { OwnerHandle = 888 };

        var first = fixture.Tick();
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            OwnerHandle = 0,
            MainVisible = true,
            MainReady = true,
            DetailsControlUsable = true,
        };
        var second = fixture.Tick();

        Assert.Equal(DadAlliancePfCreateResultKind.Progress, first.Kind);
        Assert.Equal(DadAlliancePfCleanupStage.OpenDetails, second.Stage);
        Assert.Equal(
            [DadAlliancePfNativeAction.ShowOwnedRecruitment],
            fixture.Ui.Actions);
    }

    [Fact]
    public void UnownedCleanupBlocksBeforeAnyAction()
    {
        var fixture = new Fixture
        {
            DadOwnsRecruitment = false,
        };

        var result = fixture.Tick();

        Assert.Equal(DadAlliancePfCreateResultKind.Blocked, result.Kind);
        Assert.Contains(
            "retained DAD ownership",
            result.Summary,
            StringComparison.Ordinal);
        Assert.Empty(fixture.Ui.Actions);
    }

    [Fact]
    public void Condition66FalseAcknowledgesClosureRegardlessOfOwnerHandle()
    {
        var fixture = new Fixture();
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            ActiveRecruitment = false,
            OwnerHandle = 999,
        };

        var result = fixture.Tick();

        Assert.Equal(DadAlliancePfCreateResultKind.Succeeded, result.Kind);
        Assert.Equal(DadAlliancePfCleanupStage.Complete, result.Stage);
        Assert.Empty(fixture.Ui.Actions);
    }

    [Fact]
    public void CallbackExceptionsRetryAtCappedBackoff()
    {
        var fixture = new Fixture();
        fixture.Ui.ThrowOnPerform = true;

        var first = fixture.Tick();
        Assert.Equal(DadAlliancePfCreateResultKind.Retry, first.Kind);
        Assert.Equal(TimeSpan.FromSeconds(2), first.NextRetryUtc!.Value - fixture.LastTickUtc);

        fixture.Advance(TimeSpan.FromSeconds(1.75));
        var second = fixture.Tick();
        Assert.Equal(TimeSpan.FromSeconds(4), second.NextRetryUtc!.Value - fixture.LastTickUtc);

        fixture.Advance(TimeSpan.FromSeconds(3.75));
        var third = fixture.Tick();
        Assert.Equal(TimeSpan.FromSeconds(8), third.NextRetryUtc!.Value - fixture.LastTickUtc);

        fixture.Advance(TimeSpan.FromSeconds(7.75));
        var fourth = fixture.Tick();
        Assert.Equal(TimeSpan.FromSeconds(15), fourth.NextRetryUtc!.Value - fixture.LastTickUtc);

        fixture.Advance(TimeSpan.FromSeconds(14.75));
        var fifth = fixture.Tick();
        Assert.Equal(TimeSpan.FromSeconds(15), fifth.NextRetryUtc!.Value - fixture.LastTickUtc);
        Assert.All(
            fixture.Ui.Actions,
            action => Assert.Equal(DadAlliancePfNativeAction.ShowOwnedRecruitment, action));
    }

    [Fact]
    public void StopWorksFromInProgressAndBlockedCleanup()
    {
        var inProgress = new Fixture();
        inProgress.Tick();
        Assert.Equal(DadAlliancePfCreateResultKind.Stopped, inProgress.Flow.Stop().Kind);
        Assert.Equal(DadAlliancePfCreateResultKind.Stopped, inProgress.Tick().Kind);

        var blocked = new Fixture();
        blocked.Ui.Snapshot = blocked.Ui.Snapshot with
        {
            HardBlocker = "synthetic blocker",
        };
        Assert.Equal(DadAlliancePfCreateResultKind.Blocked, blocked.Tick().Kind);
        Assert.Equal(DadAlliancePfCreateResultKind.Stopped, blocked.Flow.Stop().Kind);
        Assert.Equal(DadAlliancePfCreateResultKind.Stopped, blocked.Tick().Kind);
    }

    private sealed class Fixture
    {
        public DateTime Now { get; private set; } =
            new(2026, 7, 25, 20, 0, 0, DateTimeKind.Utc);
        public DateTime LastTickUtc { get; private set; }
        public MockUi Ui { get; } = new();
        public DadAlliancePartyFinderCleanupFlow Flow { get; }
        public bool DadOwnsRecruitment { get; set; } = true;

        public Fixture()
        {
            Flow = new DadAlliancePartyFinderCleanupFlow(Ui, () => Now);
        }

        public DadAlliancePfCleanupResult Tick()
        {
            LastTickUtc = Now;
            var result = Flow.Advance(DadOwnsRecruitment);
            Now += DadAlliancePartyFinderCleanupFlow.PollInterval;
            return result;
        }

        public void Advance(TimeSpan duration)
            => Now += duration;

        public void ReachAwaitConfirmation()
        {
            Ui.Snapshot = Ui.Snapshot with
            {
                MainVisible = true,
                MainReady = true,
                DetailsControlUsable = true,
            };
            Assert.Equal(DadAlliancePfCleanupStage.OpenDetails, Tick().Stage);
            Tick();
            Assert.Equal(
                DadAlliancePfNativeAction.OpenOwnedDetails,
                Ui.Actions[^1]);
            Ui.Snapshot = Ui.Snapshot with
            {
                DetailVisible = true,
                DetailReady = true,
                ConfirmationVisible = true,
                ConfirmationIdentity = "stale",
                ConfirmationText = "Leave the party?",
            };
            Assert.Equal(DadAlliancePfCleanupStage.RequestEndRecruitment, Tick().Stage);
            Assert.Equal(DadAlliancePfCleanupStage.AwaitConfirmation, Tick().Stage);
        }
    }

    private sealed class MockUi : IDadAlliancePartyFinderCleanupUi
    {
        public DadAlliancePfCleanupSnapshot Snapshot { get; set; } = new()
        {
            ActiveRecruitment = true,
            OwnerHandle = 0,
            Readiness = "synthetic cleanup readiness",
        };

        public List<DadAlliancePfNativeAction> Actions { get; } = [];
        public bool ThrowOnPerform { get; set; }

        public DadAlliancePfCleanupSnapshot ReadCleanup()
            => Snapshot;

        public DadAlliancePfCreateActionResult PerformCleanup(
            DadAlliancePfNativeAction action)
        {
            Actions.Add(action);
            if (ThrowOnPerform)
                throw new InvalidOperationException("synthetic callback failure");
            return new DadAlliancePfCreateActionResult(true, $"sent {action}");
        }
    }
}
