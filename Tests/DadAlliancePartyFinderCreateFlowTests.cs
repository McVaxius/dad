using dad.Services;
using dad.Models;
using Xunit;

namespace dad.Tests;

public sealed class DadAlliancePartyFinderCreateFlowTests
{
    [Fact]
    public void RaidsCategoryUsesFlagBitIndex()
    {
        Assert.Equal(0x20u, DadAlliancePartyFinderCreateFlow.RaidsCategoryMask);
        Assert.Equal((byte)5, DadAlliancePartyFinderCreateFlow.RaidsCategoryBitIndex);
        Assert.Equal(TimeSpan.FromMilliseconds(250), DadAlliancePartyFinderCreateFlow.PollInterval);
    }

    [Fact]
    public void LocalStatusClonePreservesCreateDiagnostics()
    {
        var retry = new DateTime(2026, 7, 25, 12, 34, 56, DateTimeKind.Utc);
        var status = new DadAlliancePartyFinderStatus
        {
            CreateStage = DadAlliancePfCreateStage.SelectDuty.ToString(),
            CreateAttempt = 4,
            CreateNextRetryUtc = retry,
            CreateLastError = "synthetic error",
            CreateElapsedMilliseconds = 12345,
        };

        var clone = status.Clone();

        Assert.Equal(status.CreateStage, clone.CreateStage);
        Assert.Equal(status.CreateAttempt, clone.CreateAttempt);
        Assert.Equal(retry, clone.CreateNextRetryUtc);
        Assert.Equal(status.CreateLastError, clone.CreateLastError);
        Assert.Equal(status.CreateElapsedMilliseconds, clone.CreateElapsedMilliseconds);
    }

    [Fact]
    public void VisibleLoadingMainWindowDoesNotClickOrAdvance()
    {
        var fixture = new Fixture();
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with { MainVisible = false };
        Assert.Equal(DadAlliancePfCreateStage.OpenMainWindow, fixture.Tick().Stage);

        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            MainVisible = true,
            MainReady = false,
            MainRecruitUsable = false,
            Readiness = "main-visible-loading",
        };
        var result = fixture.Tick();

        Assert.Equal(DadAlliancePfCreateResultKind.Waiting, result.Kind);
        Assert.Equal(DadAlliancePfCreateStage.OpenMainWindow, result.Stage);
        Assert.Empty(fixture.Ui.Actions);
        Assert.Contains("fully usable", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SendingRecruitMembersDoesNotAdvanceUntilConditionsAreReady()
    {
        var fixture = new Fixture();
        fixture.ReachOpenConditions();

        var sent = fixture.Tick();
        Assert.Equal(DadAlliancePfCreateResultKind.Progress, sent.Kind);
        Assert.Equal(DadAlliancePfCreateStage.OpenConditions, sent.Stage);
        Assert.Equal([DadAlliancePfCreateAction.OpenConditions], fixture.Ui.Actions);

        fixture.Ui.Snapshot = fixture.Ui.Snapshot with { ConditionVisible = false, ConditionReady = false };
        var unacknowledged = fixture.Tick();
        Assert.Equal(DadAlliancePfCreateStage.OpenConditions, unacknowledged.Stage);
        Assert.Equal(DadAlliancePfCreateResultKind.Waiting, unacknowledged.Kind);

        fixture.Ui.Snapshot = fixture.Ui.Snapshot with { ConditionVisible = true, ConditionReady = true };
        var acknowledged = fixture.Tick();
        Assert.Equal(DadAlliancePfCreateStage.SelectAlliance, acknowledged.Stage);
        Assert.Equal("acknowledgement", acknowledged.Event);
    }

    [Fact]
    public void AllianceAndRaidsActionsRequireObservedAcknowledgements()
    {
        var fixture = new Fixture();
        fixture.ReachSelectAlliance();

        var allianceSent = fixture.Tick();
        Assert.Equal(DadAlliancePfCreateStage.SelectAlliance, allianceSent.Stage);
        Assert.Equal(DadAlliancePfCreateAction.SelectAlliance, fixture.Ui.Actions[^1]);

        fixture.Ui.Snapshot = fixture.Ui.Snapshot with { AllianceSelected = true };
        Assert.Equal(DadAlliancePfCreateStage.SelectRaids, fixture.Tick().Stage);
        var raidsSent = fixture.Tick();
        Assert.Equal(DadAlliancePfCreateStage.SelectRaids, raidsSent.Stage);
        Assert.Equal(DadAlliancePfCreateAction.SelectRaids, fixture.Ui.Actions[^1]);

        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            SelectedCategory = DadAlliancePartyFinderCreateFlow.RaidsCategoryMask,
        };
        Assert.Equal(DadAlliancePfCreateStage.SelectDuty, fixture.Tick().Stage);
    }

    [Fact]
    public void LabyrinthMustResolveUniquelyAndBeEnabled()
    {
        var fixture = new Fixture();
        fixture.ReachSelectDuty();
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            DutyListLoaded = true,
            TargetDutySheetMatches = 1,
            TargetDutyDropDownMatches = 1,
            TargetDutyEntryEnabled = false,
            TargetDutyId = 174,
        };

        var result = fixture.Tick();

        Assert.Equal(DadAlliancePfCreateResultKind.Blocked, result.Kind);
        Assert.Contains("disabled", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(DadAlliancePfCreateAction.SelectDuty, fixture.Ui.Actions);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 2)]
    public void LabyrinthNonUniqueResolutionBlocks(int sheetMatches, int dropdownMatches)
    {
        var fixture = new Fixture();
        fixture.ReachSelectDuty();
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            DutyListLoaded = true,
            TargetDutySheetMatches = sheetMatches,
            TargetDutyDropDownMatches = dropdownMatches,
            TargetDutyEntryEnabled = true,
            TargetDutyId = 174,
        };

        Assert.Equal(DadAlliancePfCreateResultKind.Blocked, fixture.Tick().Kind);
    }

    [Fact]
    public void ExactDutyIdMustBeRetainedBeforeConfiguration()
    {
        var fixture = new Fixture();
        fixture.ReachSelectDuty();
        fixture.Ui.Snapshot = ExactDutySnapshot(fixture.Ui.Snapshot) with { SelectedDutyId = 999 };

        var sent = fixture.Tick();
        Assert.Equal(DadAlliancePfCreateAction.SelectDuty, fixture.Ui.Actions[^1]);
        Assert.Equal(DadAlliancePfCreateStage.SelectDuty, sent.Stage);

        fixture.Ui.Snapshot = fixture.Ui.Snapshot with { SelectedDutyId = 174 };
        Assert.Equal(DadAlliancePfCreateStage.Configure, fixture.Tick().Stage);
    }

    [Fact]
    public void ExactPrivateAllianceSettingsMustBeAcknowledgedBeforeSubmit()
    {
        var fixture = new Fixture();
        fixture.ReachConfigure();
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            AllianceASelected = true,
            PrivateRecruitment = true,
            Passcode = 9752,
            CrossWorldRecruitment = true,
            OnePlayerPerJob = false,
            EmptyComment = true,
            UnrestrictedJobs = true,
            NumberOfGroups = 2,
            SlotsPerGroup = 8,
        };

        var configured = fixture.Tick();
        Assert.Equal(DadAlliancePfCreateAction.ConfigureNextSetting, fixture.Ui.Actions[^1]);
        Assert.Equal(DadAlliancePfCreateStage.Configure, configured.Stage);

        fixture.Ui.Snapshot = ExactSettingsSnapshot(fixture.Ui.Snapshot);
        var acknowledged = fixture.Tick();
        Assert.Equal(DadAlliancePfCreateStage.Submit, acknowledged.Stage);
        Assert.DoesNotContain(DadAlliancePfCreateAction.Submit, fixture.Ui.Actions);
    }

    [Theory]
    [InlineData("alliance-mode")]
    [InlineData("alliance-a")]
    [InlineData("private")]
    [InlineData("passcode")]
    [InlineData("cross-world")]
    [InlineData("one-job")]
    [InlineData("comment")]
    [InlineData("unrestricted")]
    [InlineData("groups")]
    [InlineData("slots")]
    public void EveryConfiguredSettingIsRequired(string contradiction)
    {
        var fixture = new Fixture();
        fixture.ReachConfigure();
        var exact = ExactSettingsSnapshot(fixture.Ui.Snapshot);
        fixture.Ui.Snapshot = contradiction switch
        {
            "alliance-mode" => exact with { AllianceSelected = false },
            "alliance-a" => exact with { AllianceASelected = false },
            "private" => exact with { PrivateRecruitment = false },
            "passcode" => exact with { Passcode = 1234 },
            "cross-world" => exact with { CrossWorldRecruitment = false },
            "one-job" => exact with { OnePlayerPerJob = true },
            "comment" => exact with { EmptyComment = false },
            "unrestricted" => exact with { UnrestrictedJobs = false },
            "groups" => exact with { NumberOfGroups = 2 },
            "slots" => exact with { SlotsPerGroup = 7 },
            _ => throw new ArgumentOutOfRangeException(nameof(contradiction)),
        };

        var result = fixture.Tick();

        Assert.Equal(DadAlliancePfCreateStage.Configure, result.Stage);
        Assert.Equal(DadAlliancePfCreateAction.ConfigureNextSetting, fixture.Ui.Actions[^1]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AcknowledgedCategoryAndDutyCannotContradictConfiguration(bool contradictCategory)
    {
        var fixture = new Fixture();
        fixture.ReachConfigure();
        fixture.Ui.Snapshot = contradictCategory
            ? fixture.Ui.Snapshot with { SelectedCategory = 0 }
            : fixture.Ui.Snapshot with { SelectedDutyId = 999 };

        var result = fixture.Tick();

        Assert.Equal(DadAlliancePfCreateResultKind.Blocked, result.Kind);
        Assert.Contains(
            contradictCategory ? "Raids" : "Labyrinth",
            result.Summary,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnacknowledgedActionRetriesAtCappedBackoffWithoutStageAdvance()
    {
        var fixture = new Fixture();
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with { MainVisible = false };
        fixture.Tick();

        var first = fixture.Tick();
        Assert.Equal(1, first.Attempt);
        Assert.Equal(fixture.Now + TimeSpan.FromSeconds(1.75), first.NextRetryUtc);

        fixture.Advance(TimeSpan.FromSeconds(1.75));
        var second = fixture.Tick();
        Assert.Equal(2, second.Attempt);
        Assert.Equal(TimeSpan.FromSeconds(4), second.NextRetryUtc!.Value - fixture.LastTickUtc);

        fixture.Advance(TimeSpan.FromSeconds(3.75));
        var third = fixture.Tick();
        Assert.Equal(3, third.Attempt);
        Assert.Equal(TimeSpan.FromSeconds(8), third.NextRetryUtc!.Value - fixture.LastTickUtc);

        fixture.Advance(TimeSpan.FromSeconds(7.75));
        var fourth = fixture.Tick();
        Assert.Equal(4, fourth.Attempt);
        Assert.Equal(TimeSpan.FromSeconds(15), fourth.NextRetryUtc!.Value - fixture.LastTickUtc);

        fixture.Advance(TimeSpan.FromSeconds(14.75));
        var fifth = fixture.Tick();
        Assert.Equal(5, fifth.Attempt);
        Assert.Equal(TimeSpan.FromSeconds(15), fifth.NextRetryUtc!.Value - fixture.LastTickUtc);
        Assert.All(fixture.Ui.Actions, action => Assert.Equal(DadAlliancePfCreateAction.OpenMainWindow, action));
        Assert.Equal(DadAlliancePfCreateStage.OpenMainWindow, fifth.Stage);
    }

    [Fact]
    public void ErrorToastIsCapturedAndSchedulesRetry()
    {
        var fixture = new Fixture();
        fixture.ReachSubmit();
        fixture.Tick();
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            ErrorToastSequence = 1,
            ErrorToast = "Unable to recruit at this time.",
        };

        var result = fixture.Tick();

        Assert.Equal(DadAlliancePfCreateResultKind.Retry, result.Kind);
        Assert.Equal("error-toast", result.Event);
        Assert.Equal("Unable to recruit at this time.", result.LastError);
        Assert.NotNull(result.NextRetryUtc);
    }

    [Fact]
    public void ActionExceptionIsRetryableAndAuditable()
    {
        var fixture = new Fixture();
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with { MainVisible = false };
        fixture.Tick();
        fixture.Ui.ThrowOnPerform = true;

        var result = fixture.Tick();

        Assert.Equal(DadAlliancePfCreateResultKind.Retry, result.Kind);
        Assert.Equal("exception", result.Event);
        Assert.True(result.ShouldAudit);
        Assert.Contains("synthetic action failure", result.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void StopIsIdempotentAndPreventsResend()
    {
        var fixture = new Fixture();
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with { MainVisible = false };
        fixture.Tick();
        fixture.Tick();
        var countBeforeStop = fixture.Ui.Actions.Count;

        var first = fixture.Flow.Stop();
        var second = fixture.Flow.Stop();
        fixture.Advance(TimeSpan.FromMinutes(1));
        var after = fixture.Tick();

        Assert.True(first.ShouldAudit);
        Assert.False(second.ShouldAudit);
        Assert.Equal(DadAlliancePfCreateResultKind.Stopped, after.Kind);
        Assert.Equal(countBeforeStop, fixture.Ui.Actions.Count);
    }

    [Fact]
    public void ListingRequiresNonzeroIdAndExactStoredSettings()
    {
        var fixture = new Fixture();
        fixture.ReachSubmit();
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            OwnListingId = 777,
            StoredSettingsExact = true,
            StoredSettingsContradictory = false,
        };

        var success = fixture.Tick();

        Assert.Equal(DadAlliancePfCreateResultKind.Succeeded, success.Kind);
        Assert.Equal(777ul, success.ListingId);
        Assert.Equal(DadAlliancePfCreateStage.Complete, success.Stage);
    }

    [Fact]
    public void ContradictoryListingNeverReportsSuccess()
    {
        var fixture = new Fixture();
        fixture.ReachSubmit();
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            OwnListingId = 778,
            StoredSettingsExact = false,
            StoredSettingsContradictory = true,
        };

        var result = fixture.Tick();

        Assert.Equal(DadAlliancePfCreateResultKind.Blocked, result.Kind);
        Assert.Contains("contradicts", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FullyLoadedAddonMissingControlsIsVisibleBlocker()
    {
        var fixture = new Fixture();
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            MainVisible = true,
            MainReady = true,
            HardBlocker = "The fully loaded Party Finder window is missing Recruit Members.",
        };

        var result = fixture.Tick();

        Assert.Equal(DadAlliancePfCreateResultKind.Blocked, result.Kind);
        Assert.Equal(fixture.Ui.Snapshot.HardBlocker, result.LastError);
    }

    private static DadAlliancePfCreateSnapshot ExactDutySnapshot(DadAlliancePfCreateSnapshot source)
        => source with
        {
            ConditionVisible = true,
            ConditionReady = true,
            AllianceSelected = true,
            SelectedCategory = DadAlliancePartyFinderCreateFlow.RaidsCategoryMask,
            DutyListLoaded = true,
            TargetDutySheetMatches = 1,
            TargetDutyDropDownMatches = 1,
            TargetDutyEntryEnabled = true,
            TargetDutyId = 174,
        };

    private static DadAlliancePfCreateSnapshot ExactSettingsSnapshot(DadAlliancePfCreateSnapshot source)
        => source with
        {
            AllianceSelected = true,
            AllianceASelected = true,
            PrivateRecruitment = true,
            Passcode = 9752,
            CrossWorldRecruitment = true,
            OnePlayerPerJob = false,
            EmptyComment = true,
            UnrestrictedJobs = true,
            NumberOfGroups = 3,
            SlotsPerGroup = 8,
            StoredSettingsExact = true,
        };

    private sealed class Fixture
    {
        public DateTime Now { get; private set; } =
            new(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc);
        public DateTime LastTickUtc { get; private set; }
        public MockUi Ui { get; } = new();
        public DadAlliancePartyFinderCreateFlow Flow { get; }

        public Fixture()
        {
            Flow = new DadAlliancePartyFinderCreateFlow(Ui, () => Now);
        }

        public DadAlliancePfCreateResult Tick()
        {
            LastTickUtc = Now;
            var result = Flow.Advance(9752);
            Now += DadAlliancePartyFinderCreateFlow.PollInterval;
            return result;
        }

        public void Advance(TimeSpan duration)
            => Now += duration;

        public void ReachOpenConditions()
        {
            Ui.Snapshot = Ui.Snapshot with { MainVisible = false, ConditionVisible = false };
            Assert.Equal(DadAlliancePfCreateStage.OpenMainWindow, Tick().Stage);
            Ui.Snapshot = Ui.Snapshot with
            {
                MainVisible = true,
                MainReady = true,
                MainRecruitUsable = true,
            };
            Assert.Equal(DadAlliancePfCreateStage.OpenConditions, Tick().Stage);
        }

        public void ReachSelectAlliance()
        {
            ReachOpenConditions();
            Ui.Snapshot = Ui.Snapshot with { ConditionVisible = true, ConditionReady = true };
            Assert.Equal(DadAlliancePfCreateStage.SelectAlliance, Tick().Stage);
        }

        public void ReachSelectDuty()
        {
            ReachSelectAlliance();
            Ui.Snapshot = Ui.Snapshot with { AllianceSelected = true };
            Assert.Equal(DadAlliancePfCreateStage.SelectRaids, Tick().Stage);
            Ui.Snapshot = Ui.Snapshot with
            {
                SelectedCategory = DadAlliancePartyFinderCreateFlow.RaidsCategoryMask,
            };
            Assert.Equal(DadAlliancePfCreateStage.SelectDuty, Tick().Stage);
        }

        public void ReachConfigure()
        {
            ReachSelectDuty();
            Ui.Snapshot = ExactDutySnapshot(Ui.Snapshot) with { SelectedDutyId = 174 };
            Assert.Equal(DadAlliancePfCreateStage.Configure, Tick().Stage);
        }

        public void ReachSubmit()
        {
            ReachConfigure();
            Ui.Snapshot = ExactSettingsSnapshot(Ui.Snapshot);
            Assert.Equal(DadAlliancePfCreateStage.Submit, Tick().Stage);
        }
    }

    private sealed class MockUi : IDadAlliancePartyFinderCreateUi
    {
        public DadAlliancePfCreateSnapshot Snapshot { get; set; } = new();
        public List<DadAlliancePfCreateAction> Actions { get; } = [];
        public bool ThrowOnPerform { get; set; }

        public DadAlliancePfCreateSnapshot Read(int passcode)
            => Snapshot;

        public DadAlliancePfCreateActionResult Perform(DadAlliancePfCreateAction action, int passcode)
        {
            if (ThrowOnPerform)
                throw new InvalidOperationException("synthetic action failure");
            Actions.Add(action);
            return new DadAlliancePfCreateActionResult(true, $"sent {action}");
        }
    }
}
