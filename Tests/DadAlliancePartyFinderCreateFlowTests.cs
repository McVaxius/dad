using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadAlliancePartyFinderCreateFlowTests
{
    private const int Passcode = 9752;

    [Fact]
    public void Api15SelectorConstantsAreExact()
    {
        Assert.Equal(0x20u, DadAlliancePartyFinderCreateFlow.RaidsCategoryMask);
        Assert.Equal(5, DadAlliancePartyFinderCreateFlow.RaidsCategoryBitIndex);
        Assert.Equal(92, DadAlliancePartyFinderCreateFlow.LabyrinthDutyId);
        Assert.Equal(
            TimeSpan.FromMilliseconds(250),
            DadAlliancePartyFinderCreateFlow.PollInterval);
        Assert.Equal(
            TimeSpan.FromSeconds(5),
            DadAlliancePartyFinderCreateFlow.ObservationTimeout);
    }

    [Fact]
    public void LocalStatusClonePreservesCreateDiagnostics()
    {
        var source = new DadAlliancePartyFinderStatus
        {
            CreateStage = "ApplyPreset",
            CreateAttempt = 1,
            CreateNextRetryUtc = DateTime.UtcNow,
            CreateLastError = "not acknowledged",
            CreateElapsedMilliseconds = 1234,
            CreateActiveRecruitment = false,
            CreateEditorVisible = true,
            CreateSubmitDispatched = false,
            CreateConfigurationTarget = string.Empty,
            CreateObservedSettings = "groups=1",
        };

        var clone = source.Clone();

        Assert.Equal(source.CreateStage, clone.CreateStage);
        Assert.Equal(source.CreateAttempt, clone.CreateAttempt);
        Assert.Equal(source.CreateNextRetryUtc, clone.CreateNextRetryUtc);
        Assert.Equal(source.CreateLastError, clone.CreateLastError);
        Assert.Equal(
            source.CreateElapsedMilliseconds,
            clone.CreateElapsedMilliseconds);
        Assert.Equal(
            source.CreateActiveRecruitment,
            clone.CreateActiveRecruitment);
        Assert.Equal(source.CreateEditorVisible, clone.CreateEditorVisible);
        Assert.Equal(
            source.CreateSubmitDispatched,
            clone.CreateSubmitDispatched);
        Assert.Equal(
            source.CreateConfigurationTarget,
            clone.CreateConfigurationTarget);
        Assert.Equal(
            source.CreateObservedSettings,
            clone.CreateObservedSettings);
    }

    [Fact]
    public void PreparationDispatchesAllianceRaidsAndDutyOnceInOrder()
    {
        var fixture = new Fixture();

        fixture.ReachApplyPreset();

        Assert.Equal(
            [
                DadAlliancePfCreateAction.OpenMainWindow,
                DadAlliancePfCreateAction.OpenConditions,
                DadAlliancePfCreateAction.SelectAlliance,
                DadAlliancePfCreateAction.SelectRaids,
                DadAlliancePfCreateAction.SelectDuty,
            ],
            fixture.Ui.Actions);
        Assert.Single(
            fixture.Ui.Actions,
            static action =>
                action == DadAlliancePfCreateAction.OpenConditions);
        Assert.Single(
            fixture.Ui.Actions,
            static action =>
                action == DadAlliancePfCreateAction.SelectAlliance);
        Assert.Single(
            fixture.Ui.Actions,
            static action =>
                action == DadAlliancePfCreateAction.SelectRaids);
        Assert.Single(
            fixture.Ui.Actions,
            static action =>
                action == DadAlliancePfCreateAction.SelectDuty);
        Assert.DoesNotContain(
            fixture.Ui.Actions,
            static action =>
                action is DadAlliancePfCreateAction.ReloadCloseConditions or
                    DadAlliancePfCreateAction.ReloadRestoreAllianceTab or
                    DadAlliancePfCreateAction.ReloadMainWindow or
                    DadAlliancePfCreateAction.ReloadOpenConditions);
    }

    [Fact]
    public void FirstRunReloadsAllianceEditorWhenMainWindowRemainsAvailable()
    {
        var fixture = new Fixture();
        fixture.ReachReloadCloseConditions();
        Assert.Equal(
            DadAlliancePfCreateAction.ReloadCloseConditions,
            fixture.SendCurrentStage());
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            ConditionVisible = false,
            ConditionReady = false,
            MainVisible = true,
            MainReady = true,
            MainRecruitUsable = true,
        };
        Assert.Equal(
            DadAlliancePfCreateStage.ReloadMainWindow,
            fixture.Tick().Stage);

        Assert.Equal(
            DadAlliancePfCreateStage.ReloadOpenConditions,
            fixture.Tick().Stage);
        Assert.Equal(
            DadAlliancePfCreateAction.ReloadOpenConditions,
            fixture.SendCurrentStage());
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            ConditionVisible = true,
            ConditionReady = true,
            GroupTypeTab =
                DadAlliancePartyFinderPresetDefinition.AllianceGroupTypeTab,
            AllianceSelected = true,
        };
        Assert.Equal(
            DadAlliancePfCreateStage.SelectRaids,
            fixture.Tick().Stage);
        Assert.Equal(
            DadAlliancePfCreateAction.SelectRaids,
            fixture.SendCurrentStage());

        Assert.Single(
            fixture.Ui.Actions,
            static action =>
                action == DadAlliancePfCreateAction.SelectAlliance);
        Assert.DoesNotContain(
            DadAlliancePfCreateAction.ReloadMainWindow,
            fixture.Ui.Actions);
        Assert.DoesNotContain(
            DadAlliancePfCreateAction.ReloadRestoreAllianceTab,
            fixture.Ui.Actions);
    }

    [Fact]
    public void FirstRunReopensMainWindowOnlyWhenCancelClosedIt()
    {
        var fixture = new Fixture();
        fixture.ReachReloadCloseConditions();
        Assert.Equal(
            DadAlliancePfCreateAction.ReloadCloseConditions,
            fixture.SendCurrentStage());
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            ConditionVisible = false,
            ConditionReady = false,
            MainVisible = false,
            MainReady = false,
            MainRecruitUsable = false,
        };
        Assert.Equal(
            DadAlliancePfCreateStage.ReloadMainWindow,
            fixture.Tick().Stage);

        Assert.Equal(
            DadAlliancePfCreateAction.ReloadMainWindow,
            fixture.SendCurrentStage());
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            MainVisible = true,
            MainReady = true,
            MainRecruitUsable = true,
        };
        Assert.Equal(
            DadAlliancePfCreateStage.ReloadOpenConditions,
            fixture.Tick().Stage);
        Assert.Equal(
            DadAlliancePfCreateAction.ReloadOpenConditions,
            fixture.SendCurrentStage());
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            ConditionVisible = true,
            ConditionReady = true,
            GroupTypeTab =
                DadAlliancePartyFinderPresetDefinition.AllianceGroupTypeTab,
            AllianceSelected = true,
        };
        Assert.Equal(
            DadAlliancePfCreateStage.SelectRaids,
            fixture.Tick().Stage);

        Assert.Single(
            fixture.Ui.Actions,
            static action =>
                action == DadAlliancePfCreateAction.SelectAlliance);
        Assert.Single(
            fixture.Ui.Actions,
            static action =>
                action == DadAlliancePfCreateAction.ReloadMainWindow);
        Assert.DoesNotContain(
            DadAlliancePfCreateAction.ReloadRestoreAllianceTab,
            fixture.Ui.Actions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OneCreateRestoresCancelResetTabOnceAndReachesSubmit(
        bool mainWindowRemainsAvailable)
    {
        var fixture = new Fixture();
        fixture.ReachReloadCloseConditions();
        Assert.Equal(
            DadAlliancePfCreateAction.ReloadCloseConditions,
            fixture.SendCurrentStage());
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            ConditionVisible = false,
            ConditionReady = false,
            GroupTypeTab = 0,
            MainVisible = mainWindowRemainsAvailable,
            MainReady = mainWindowRemainsAvailable,
            MainRecruitUsable = mainWindowRemainsAvailable,
        };
        Assert.Equal(
            DadAlliancePfCreateStage.ReloadRestoreAllianceTab,
            fixture.Tick().Stage);
        Assert.Equal(
            DadAlliancePfCreateAction.ReloadRestoreAllianceTab,
            fixture.SendCurrentStage());

        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            GroupTypeTab =
                DadAlliancePartyFinderPresetDefinition.AllianceGroupTypeTab,
        };
        Assert.Equal(
            DadAlliancePfCreateStage.ReloadMainWindow,
            fixture.Tick().Stage);
        if (!mainWindowRemainsAvailable)
        {
            Assert.Equal(
                DadAlliancePfCreateAction.ReloadMainWindow,
                fixture.SendCurrentStage());
            fixture.Ui.Snapshot = fixture.Ui.Snapshot with
            {
                MainVisible = true,
                MainReady = true,
                MainRecruitUsable = true,
            };
        }
        Assert.Equal(
            DadAlliancePfCreateStage.ReloadOpenConditions,
            fixture.Tick().Stage);
        Assert.Equal(
            DadAlliancePfCreateAction.ReloadOpenConditions,
            fixture.SendCurrentStage());
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            ConditionVisible = true,
            ConditionReady = true,
            AllianceSelected = true,
        };
        Assert.Equal(
            DadAlliancePfCreateStage.SelectRaids,
            fixture.Tick().Stage);

        Assert.Equal(
            DadAlliancePfCreateAction.SelectRaids,
            fixture.SendCurrentStage());
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            SelectedCategory =
                DadAlliancePartyFinderCreateFlow.RaidsCategoryMask,
        };
        Assert.Equal(
            DadAlliancePfCreateStage.SelectDuty,
            fixture.Tick().Stage);
        fixture.Ui.Snapshot = fixture.DutyReadySnapshot();
        Assert.Equal(
            DadAlliancePfCreateAction.SelectDuty,
            fixture.SendCurrentStage());
        fixture.Ui.Snapshot = fixture.PreparedSelectorSnapshot();
        Assert.Equal(
            DadAlliancePfCreateStage.ApplyPreset,
            fixture.Tick().Stage);
        Assert.Equal(
            DadAlliancePfCreateAction.ApplyPreset,
            fixture.SendCurrentStage());
        fixture.Ui.Snapshot = fixture.ExactSnapshot();
        Assert.Equal(
            DadAlliancePfCreateStage.Submit,
            fixture.Tick().Stage);

        Assert.Single(
            fixture.Ui.Actions,
            static action =>
                action == DadAlliancePfCreateAction.SelectAlliance);
        Assert.Single(
            fixture.Ui.Actions,
            static action =>
                action == DadAlliancePfCreateAction.ReloadCloseConditions);
        Assert.Single(
            fixture.Ui.Actions,
            static action =>
                action ==
                DadAlliancePfCreateAction.ReloadRestoreAllianceTab);
        Assert.Equal(
            mainWindowRemainsAvailable ? 0 : 1,
            fixture.Ui.Actions.Count(
                static action =>
                    action ==
                    DadAlliancePfCreateAction.ReloadMainWindow));
        Assert.Single(
            fixture.Ui.Actions,
            static action =>
                action == DadAlliancePfCreateAction.ApplyPreset);
    }

    [Fact]
    public void UnavailablePresetLoaderBlocksBeforeAnyMutation()
    {
        var fixture = new Fixture();
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            PresetLoaderAvailable = false,
            PresetLoaderBlocker =
                "The DAD-owned Party Finder refresh signature is unavailable.",
        };

        var result = fixture.Tick();

        Assert.Equal(DadAlliancePfCreateResultKind.Blocked, result.Kind);
        Assert.Equal(DadAlliancePfCreateStage.Blocked, result.Stage);
        Assert.Contains("signature is unavailable", result.Summary);
        Assert.Empty(fixture.Ui.Actions);
    }

    [Theory]
    [InlineData((int)DadAlliancePfCreateStage.CloseStaleWindows)]
    [InlineData((int)DadAlliancePfCreateStage.OpenMainWindow)]
    [InlineData((int)DadAlliancePfCreateStage.OpenConditions)]
    [InlineData((int)DadAlliancePfCreateStage.SelectAlliance)]
    [InlineData((int)DadAlliancePfCreateStage.ReloadCloseConditions)]
    [InlineData((int)DadAlliancePfCreateStage.ReloadRestoreAllianceTab)]
    [InlineData((int)DadAlliancePfCreateStage.ReloadMainWindow)]
    [InlineData((int)DadAlliancePfCreateStage.ReloadOpenConditions)]
    [InlineData((int)DadAlliancePfCreateStage.SelectRaids)]
    [InlineData((int)DadAlliancePfCreateStage.SelectDuty)]
    [InlineData((int)DadAlliancePfCreateStage.ApplyPreset)]
    [InlineData((int)DadAlliancePfCreateStage.Submit)]
    public void ObservationTimeoutNeverRedispatchesAnyMutation(
        int stageValue)
    {
        var stage = (DadAlliancePfCreateStage)stageValue;
        var fixture = new Fixture();
        fixture.ReachStage(stage);
        fixture.PrepareCurrentStageForDispatch();

        var dispatched = fixture.Tick();
        var actionCount = fixture.Ui.Actions.Count;
        var dispatchedAction = fixture.Ui.Actions[^1];
        fixture.AdvancePastObservationTimeout();
        var blocked = fixture.Tick();
        fixture.AdvancePastObservationTimeout();
        var later = fixture.Tick();

        Assert.Equal(stage, dispatched.Stage);
        Assert.Equal(DadAlliancePfCreateResultKind.Progress, dispatched.Kind);
        Assert.Equal(DadAlliancePfCreateResultKind.Blocked, blocked.Kind);
        Assert.Contains(
            "will not",
            blocked.Summary,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DadAlliancePfCreateResultKind.Blocked, later.Kind);
        Assert.Equal(actionCount, fixture.Ui.Actions.Count);
        Assert.Single(
            fixture.Ui.Actions,
            action => action == dispatchedAction);
    }

    [Fact]
    public void DutySelectionWaitsForOneExactEnabledRowWithoutDispatching()
    {
        var fixture = new Fixture();
        fixture.ReachSelectDuty();

        var first = fixture.Tick();
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        var later = fixture.Tick();

        Assert.Equal(DadAlliancePfCreateResultKind.Waiting, first.Kind);
        Assert.Equal(DadAlliancePfCreateResultKind.Waiting, later.Kind);
        Assert.DoesNotContain(
            DadAlliancePfCreateAction.SelectDuty,
            fixture.Ui.Actions);
    }

    [Theory]
    [InlineData(0, 92)]
    [InlineData(2, 92)]
    [InlineData(1, 91)]
    public void LabyrinthMustResolveUniquelyBeforeDutyMutation(
        int sheetMatches,
        ushort dutyId)
    {
        var fixture = new Fixture();
        fixture.ReachSelectDuty();
        fixture.Ui.Snapshot = fixture.DutyReadySnapshot() with
        {
            TargetDutySheetMatches = sheetMatches,
            TargetDutyId = dutyId,
        };

        var result = fixture.Tick();

        Assert.Equal(DadAlliancePfCreateResultKind.Blocked, result.Kind);
        Assert.DoesNotContain(
            DadAlliancePfCreateAction.SelectDuty,
            fixture.Ui.Actions);
    }

    [Fact]
    public void ApplyPresetDispatchDoesNotAdvanceWithoutExactLaterSnapshot()
    {
        var fixture = new Fixture();
        fixture.ReachApplyPreset();

        var dispatched = fixture.Tick();
        fixture.AdvancePoll();
        var observed = fixture.Tick();

        Assert.Equal(DadAlliancePfCreateStage.ApplyPreset, dispatched.Stage);
        Assert.Equal(DadAlliancePfCreateResultKind.Progress, dispatched.Kind);
        Assert.Equal(DadAlliancePfCreateResultKind.Waiting, observed.Kind);
        Assert.Single(
            fixture.Ui.Actions,
            static action =>
                action == DadAlliancePfCreateAction.ApplyPreset);
    }

    [Theory]
    [InlineData("group-type")]
    [InlineData("alliance-tab")]
    [InlineData("alliance-a")]
    [InlineData("category")]
    [InlineData("visible-duty")]
    [InlineData("stored-duty")]
    [InlineData("private-visible")]
    [InlineData("private-stored")]
    [InlineData("passcode-visible")]
    [InlineData("passcode-stored")]
    [InlineData("cross-world-visible")]
    [InlineData("cross-world-stored")]
    [InlineData("one-job-visible")]
    [InlineData("one-job-stored")]
    [InlineData("comment-visible")]
    [InlineData("comment-stored")]
    [InlineData("roles-stored")]
    [InlineData("stale-members")]
    [InlineData("groups")]
    [InlineData("slots")]
    [InlineData("stored-exact")]
    public void EveryPresetFieldRequiresLaterAcknowledgement(string missing)
    {
        var fixture = new Fixture();
        fixture.ReachApplyPreset();
        fixture.Tick();
        fixture.Ui.Snapshot = BreakExact(
            fixture.ExactSnapshot(),
            missing);

        var observed = fixture.Tick();
        fixture.AdvancePastObservationTimeout();
        var blocked = fixture.Tick();

        Assert.Equal(DadAlliancePfCreateResultKind.Waiting, observed.Kind);
        Assert.Equal(DadAlliancePfCreateResultKind.Blocked, blocked.Kind);
        Assert.Single(
            fixture.Ui.Actions,
            static action =>
                action == DadAlliancePfCreateAction.ApplyPreset);
        Assert.DoesNotContain(
            DadAlliancePfCreateAction.Submit,
            fixture.Ui.Actions);
    }

    [Fact]
    public void ExactLaterSnapshotAdvancesToSubmit()
    {
        var fixture = new Fixture();
        fixture.ReachApplyPreset();
        fixture.Tick();
        fixture.Ui.Snapshot = fixture.ExactSnapshot();

        var acknowledged = fixture.Tick();

        Assert.Equal(DadAlliancePfCreateStage.Submit, acknowledged.Stage);
        Assert.Equal("acknowledgement", acknowledged.Event);
        Assert.Equal(0, fixture.Flow.Attempt);
        Assert.Single(
            fixture.Ui.Actions,
            static action =>
                action == DadAlliancePfCreateAction.ApplyPreset);
    }

    [Fact]
    public void AcknowledgedStageDispatchesNextActionOnNextPoll()
    {
        var fixture = new Fixture();
        fixture.ReachOpenMainWindow();
        fixture.Tick();
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            MainVisible = true,
            MainReady = true,
            MainRecruitUsable = true,
        };
        var acknowledged = fixture.Tick();

        var dispatched = fixture.Tick();
        var observed = fixture.Tick();

        Assert.Equal("acknowledgement", acknowledged.Event);
        Assert.Equal(DadAlliancePfCreateResultKind.Progress, dispatched.Kind);
        Assert.Equal("action", dispatched.Event);
        Assert.Equal(DadAlliancePfCreateResultKind.Waiting, observed.Kind);
        Assert.Single(
            fixture.Ui.Actions,
            static action =>
                action == DadAlliancePfCreateAction.OpenConditions);
        Assert.DoesNotContain(
            "pacing",
            new[] { acknowledged.Event, dispatched.Event, observed.Event });
    }

    [Fact]
    public void StaleUncheckedUnrestrictedCheckboxDoesNotBlockStoredSlotPayload()
    {
        var fixture = new Fixture();
        fixture.ReachApplyPreset();
        fixture.Tick();
        fixture.Ui.Snapshot = fixture.ExactSnapshot() with
        {
            UnrestrictedJobs = false,
        };

        var acknowledged = fixture.Tick();
        var submit = fixture.Tick();

        Assert.Equal(DadAlliancePfCreateStage.Submit, acknowledged.Stage);
        Assert.Equal(DadAlliancePfCreateAction.Submit, fixture.Ui.Actions[^1]);
        Assert.True(submit.SubmitDispatched);
        Assert.Contains(
            "unrestricted-visible=False",
            submit.ObservedSettings);
    }

    [Fact]
    public void FailedActionAndExceptionBlockWithoutRetry()
    {
        var failed = new Fixture();
        failed.ReachSelectAlliance();
        failed.Ui.Handler = static (action, _) =>
            action == DadAlliancePfCreateAction.SelectAlliance
                ? new(false, "alliance failed", "native unavailable")
                : new(true, $"sent {action}");

        var failedResult = failed.Tick();
        failed.AdvancePastObservationTimeout();
        failed.Tick();

        Assert.Equal(DadAlliancePfCreateResultKind.Blocked, failedResult.Kind);
        Assert.Single(
            failed.Ui.Actions,
            static action =>
                action == DadAlliancePfCreateAction.SelectAlliance);

        var threw = new Fixture();
        threw.ReachSelectAlliance();
        threw.Ui.Handler = static (action, _) =>
            action == DadAlliancePfCreateAction.SelectAlliance
                ? throw new InvalidOperationException("event threw")
                : new(true, $"sent {action}");

        var exceptionResult = threw.Tick();
        threw.AdvancePastObservationTimeout();
        threw.Tick();

        Assert.Equal(
            DadAlliancePfCreateResultKind.Blocked,
            exceptionResult.Kind);
        Assert.Single(
            threw.Ui.Actions,
            static action =>
                action == DadAlliancePfCreateAction.SelectAlliance);
    }

    [Fact]
    public void ReadExceptionAndErrorToastAfterMutationNeverRedispatch()
    {
        var readFailure = new Fixture();
        readFailure.ReachSelectAlliance();
        readFailure.Tick();
        readFailure.Ui.ThrowOnRead = true;
        var readBlocked = readFailure.Tick();

        Assert.Equal(DadAlliancePfCreateResultKind.Blocked, readBlocked.Kind);
        Assert.Single(
            readFailure.Ui.Actions,
            static action =>
                action == DadAlliancePfCreateAction.SelectAlliance);

        var toastFailure = new Fixture();
        toastFailure.ReachSelectAlliance();
        toastFailure.Tick();
        toastFailure.Ui.Snapshot = toastFailure.Ui.Snapshot with
        {
            ErrorToastSequence = 1,
            ErrorToast = "Unable to recruit.",
        };
        var toastBlocked = toastFailure.Tick();

        Assert.Equal(DadAlliancePfCreateResultKind.Blocked, toastBlocked.Kind);
        Assert.Single(
            toastFailure.Ui.Actions,
            static action =>
                action == DadAlliancePfCreateAction.SelectAlliance);
    }

    [Fact]
    public void PreExistingActiveRecruitmentBlocksBeforeAnyCreateAction()
    {
        var fixture = new Fixture();
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            ActiveRecruitment = true,
        };

        var result = fixture.Tick();

        Assert.Equal(DadAlliancePfCreateResultKind.Blocked, result.Kind);
        Assert.Empty(fixture.Ui.Actions);
    }

    [Fact]
    public void ExactPresetDriftBeforeSubmitBlocksWithoutAnotherWrite()
    {
        var fixture = new Fixture();
        fixture.ReachSubmit();
        fixture.Ui.Snapshot = fixture.ExactSnapshot() with
        {
            NumberOfGroups = 1,
            StoredSettingsExactBeforeSubmit = false,
            StoredSettingsExact = false,
        };

        var result = fixture.Tick();

        Assert.Equal(DadAlliancePfCreateResultKind.Blocked, result.Kind);
        Assert.DoesNotContain(
            DadAlliancePfCreateAction.Submit,
            fixture.Ui.Actions);
        Assert.Single(
            fixture.Ui.Actions,
            static action =>
                action == DadAlliancePfCreateAction.ApplyPreset);
    }

    [Fact]
    public void SubmitIsSentOnceBeforePublicationCanSucceed()
    {
        var fixture = new Fixture();
        fixture.ReachSubmit();

        var submit = fixture.Tick();
        fixture.Ui.Snapshot = fixture.ExactSnapshot() with
        {
            ConditionVisible = false,
            ConditionReady = false,
            ActiveRecruitment = true,
            ParticipatingInCrossWorldPartyOrAlliance = true,
            OwnerHandle = 0,
        };
        var success = fixture.Tick();

        Assert.Equal(DadAlliancePfCreateStage.Submit, submit.Stage);
        Assert.True(submit.SubmitDispatched);
        Assert.Equal(DadAlliancePfCreateResultKind.Succeeded, success.Kind);
        Assert.Equal(DadAlliancePfCreateStage.Complete, success.Stage);
        Assert.Equal(0ul, success.ListingId);
        Assert.Contains(
            "diagnostic only",
            success.Summary,
            StringComparison.Ordinal);
        Assert.Single(
            fixture.Ui.Actions,
            static action => action == DadAlliancePfCreateAction.Submit);
    }

    [Theory]
    [InlineData(0ul, 123ul)]
    [InlineData(123ul, 0ul)]
    [InlineData(123ul, 456ul)]
    public void NativeOwnerHandleChangesDoNotAffectPublication(
        ulong submitOwnerHandle,
        ulong publishedOwnerHandle)
    {
        var fixture = new Fixture();
        fixture.ReachSubmit();
        fixture.Ui.Snapshot = fixture.ExactSnapshot() with
        {
            OwnerHandle = submitOwnerHandle,
        };
        fixture.Tick();
        fixture.Ui.Snapshot = fixture.ExactSnapshot() with
        {
            ConditionVisible = false,
            ConditionReady = false,
            ActiveRecruitment = true,
            ParticipatingInCrossWorldPartyOrAlliance = true,
            OwnerHandle = publishedOwnerHandle,
        };

        var success = fixture.Tick();

        Assert.Equal(DadAlliancePfCreateResultKind.Succeeded, success.Kind);
        Assert.Equal(publishedOwnerHandle, success.ListingId);
    }

    public static IEnumerable<object[]> IncompletePublicationStates()
    {
        for (var mask = 0; mask < 32; mask++)
        {
            var editorHidden = (mask & 1) != 0;
            var usingPartyFinder = (mask & 2) != 0;
            var participating = (mask & 4) != 0;
            var ownerNonzero = (mask & 8) != 0;
            var exact = (mask & 16) != 0;
            if (editorHidden &&
                usingPartyFinder &&
                participating &&
                exact)
            {
                continue;
            }

            yield return
            [
                editorHidden,
                usingPartyFinder,
                participating,
                ownerNonzero,
                exact,
            ];
        }
    }

    [Theory]
    [MemberData(nameof(IncompletePublicationStates))]
    public void PublicationRejectsEveryIncompleteAuthoritativeSnapshot(
        bool editorHidden,
        bool usingPartyFinder,
        bool participating,
        bool ownerNonzero,
        bool exact)
    {
        var fixture = new Fixture();
        fixture.ReachSubmit();
        fixture.Tick();
        fixture.Ui.Snapshot = fixture.ExactSnapshot() with
        {
            ConditionVisible = !editorHidden,
            ConditionReady = !editorHidden,
            ActiveRecruitment = usingPartyFinder,
            ParticipatingInCrossWorldPartyOrAlliance =
                participating,
            OwnerHandle = ownerNonzero ? 123ul : 0ul,
            StoredSettingsExact = exact,
            StoredSettingsContradictory =
                usingPartyFinder &&
                editorHidden &&
                !exact,
        };

        var result = fixture.Tick();

        Assert.NotEqual(DadAlliancePfCreateResultKind.Succeeded, result.Kind);
        Assert.Single(
            fixture.Ui.Actions,
            static action => action == DadAlliancePfCreateAction.Submit);
    }

    [Fact]
    public void PostSubmitConditionFlagsAreObservationOnly()
    {
        var fixture = new Fixture();
        fixture.ReachSubmit();
        Assert.True(fixture.Flow.RequiresMutationSafety);

        var submit = fixture.Tick();
        Assert.True(submit.SubmitDispatched);
        Assert.False(fixture.Flow.RequiresMutationSafety);

        fixture.Ui.Snapshot = fixture.ExactSnapshot() with
        {
            SafeToMutate = false,
            SafetyBlocker = "The Party Finder creator must be solo.",
            ConditionVisible = false,
            ConditionReady = false,
            ActiveRecruitment = true,
            ParticipatingInCrossWorldPartyOrAlliance = true,
            OwnerHandle = 0,
        };
        var success = fixture.Tick();

        Assert.Equal(DadAlliancePfCreateResultKind.Succeeded, success.Kind);
        Assert.Contains(
            "condition-66-using-party-finder=True",
            success.Readiness,
            StringComparison.Ordinal);
        Assert.Contains(
            "condition-84-participating-cross-world-party-or-alliance=True",
            success.Readiness,
            StringComparison.Ordinal);
        Assert.Single(
            fixture.Ui.Actions,
            static action => action == DadAlliancePfCreateAction.Submit);
    }

    [Fact]
    public void StopIsIdempotentAndPreventsFurtherActions()
    {
        var fixture = new Fixture();
        fixture.ReachSelectDuty();
        fixture.PrepareCurrentStageForDispatch();
        fixture.Tick();
        var actionCount = fixture.Ui.Actions.Count;

        var first = fixture.Flow.Stop();
        var second = fixture.Flow.Stop();
        fixture.AdvancePastObservationTimeout();
        var later = fixture.Tick();

        Assert.Equal(DadAlliancePfCreateResultKind.Stopped, first.Kind);
        Assert.Equal(DadAlliancePfCreateResultKind.Stopped, second.Kind);
        Assert.Equal(DadAlliancePfCreateResultKind.Stopped, later.Kind);
        Assert.Equal(actionCount, fixture.Ui.Actions.Count);
    }

    [Fact]
    public void FullyLoadedAddonMissingControlsIsVisibleBlocker()
    {
        var fixture = new Fixture();
        fixture.Ui.Snapshot = fixture.Ui.Snapshot with
        {
            HardBlocker =
                "The fully loaded Party Finder conditions window is missing required alliance controls.",
        };

        var result = fixture.Tick();

        Assert.Equal(DadAlliancePfCreateResultKind.Blocked, result.Kind);
        Assert.Contains("missing required alliance controls", result.Summary);
    }

    private static DadAlliancePfCreateSnapshot BreakExact(
        DadAlliancePfCreateSnapshot snapshot,
        string missing)
        => missing switch
        {
            "group-type" => snapshot with { GroupTypeTab = 0 },
            "alliance-tab" => snapshot with { AllianceSelected = false },
            "alliance-a" => snapshot with { AllianceASelected = false },
            "category" => snapshot with { SelectedCategory = 0 },
            "visible-duty" => snapshot with
            {
                SelectedDutyDropDownIndex = -1,
            },
            "stored-duty" => snapshot with { SelectedDutyId = 1117 },
            "private-visible" => snapshot with
            {
                PrivateRecruitment = false,
            },
            "private-stored" => snapshot with
            {
                StoredPrivateRecruitment = false,
            },
            "passcode-visible" => snapshot with { Passcode = 1111 },
            "passcode-stored" => snapshot with
            {
                StoredPasscode = 1111,
            },
            "cross-world-visible" => snapshot with
            {
                CrossWorldRecruitment = false,
            },
            "cross-world-stored" => snapshot with
            {
                StoredCrossWorldRecruitment = false,
            },
            "one-job-visible" => snapshot with
            {
                OnePlayerPerJob = true,
            },
            "one-job-stored" => snapshot with
            {
                StoredOnePlayerPerJob = true,
            },
            "comment-visible" => snapshot with { EmptyComment = false },
            "comment-stored" => snapshot with
            {
                StoredEmptyComment = false,
            },
            "roles-visible" => snapshot with { UnrestrictedJobs = false },
            "roles-stored" => snapshot with
            {
                StoredOpenSlotsUnrestricted = false,
            },
            "stale-members" => snapshot with
            {
                StoredStaleMembersCleared = false,
            },
            "groups" => snapshot with { NumberOfGroups = 1 },
            "slots" => snapshot with { SlotsPerGroup = 4 },
            "stored-exact" => snapshot with
            {
                StoredSettingsExactBeforeSubmit = false,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(missing)),
        };

    private sealed class Fixture
    {
        public FakeClock Clock { get; } = new();
        public FakeUi Ui { get; } = new();
        public DadAlliancePartyFinderCreateFlow Flow { get; }

        public Fixture()
        {
            Flow = new DadAlliancePartyFinderCreateFlow(
                Ui,
                () => Clock.UtcNow);
        }

        public DadAlliancePfCreateResult Tick()
        {
            var result = Flow.Advance(Passcode);
            AdvancePoll();
            return result;
        }

        public void AdvancePoll()
            => Clock.Advance(
                DadAlliancePartyFinderCreateFlow.PollInterval);

        public void AdvancePastObservationTimeout()
            => Clock.Advance(
                DadAlliancePartyFinderCreateFlow.ObservationTimeout);

        public void ReachOpenMainWindow()
        {
            Assert.Equal(
                DadAlliancePfCreateStage.OpenMainWindow,
                Tick().Stage);
        }

        public void ReachOpenConditions()
        {
            ReachOpenMainWindow();
            Assert.Equal(
                DadAlliancePfCreateAction.OpenMainWindow,
                SendCurrentStage());
            Ui.Snapshot = Ui.Snapshot with
            {
                MainVisible = true,
                MainReady = true,
                MainRecruitUsable = true,
            };
            Assert.Equal(
                DadAlliancePfCreateStage.OpenConditions,
                Tick().Stage);
        }

        public void ReachSelectAlliance()
        {
            ReachOpenConditions();
            Assert.Equal(
                DadAlliancePfCreateAction.OpenConditions,
                SendCurrentStage());
            Ui.Snapshot = Ui.Snapshot with
            {
                ConditionVisible = true,
                ConditionReady = true,
            };
            Assert.Equal(
                DadAlliancePfCreateStage.SelectAlliance,
                Tick().Stage);
        }

        public void ReachReloadCloseConditions()
        {
            ReachSelectAlliance();
            Assert.Equal(
                DadAlliancePfCreateAction.SelectAlliance,
                SendCurrentStage());
            Ui.Snapshot = Ui.Snapshot with
            {
                GroupTypeTab =
                    DadAlliancePartyFinderPresetDefinition.AllianceGroupTypeTab,
                AllianceSelected = false,
            };
            Assert.Equal(
                DadAlliancePfCreateStage.ReloadCloseConditions,
                Tick().Stage);
        }

        public void ReachReloadMainWindow()
        {
            ReachReloadCloseConditions();
            Assert.Equal(
                DadAlliancePfCreateAction.ReloadCloseConditions,
                SendCurrentStage());
            Ui.Snapshot = Ui.Snapshot with
            {
                ConditionVisible = false,
                ConditionReady = false,
                MainVisible = false,
                MainReady = false,
                MainRecruitUsable = false,
            };
            Assert.Equal(
                DadAlliancePfCreateStage.ReloadMainWindow,
                Tick().Stage);
        }

        public void ReachReloadRestoreAllianceTab()
        {
            ReachReloadCloseConditions();
            Assert.Equal(
                DadAlliancePfCreateAction.ReloadCloseConditions,
                SendCurrentStage());
            Ui.Snapshot = Ui.Snapshot with
            {
                ConditionVisible = false,
                ConditionReady = false,
                GroupTypeTab = 0,
            };
            Assert.Equal(
                DadAlliancePfCreateStage.ReloadRestoreAllianceTab,
                Tick().Stage);
        }

        public void ReachReloadOpenConditions()
        {
            ReachReloadMainWindow();
            Assert.Equal(
                DadAlliancePfCreateAction.ReloadMainWindow,
                SendCurrentStage());
            Ui.Snapshot = Ui.Snapshot with
            {
                MainVisible = true,
                MainReady = true,
                MainRecruitUsable = true,
            };
            Assert.Equal(
                DadAlliancePfCreateStage.ReloadOpenConditions,
                Tick().Stage);
        }

        public void ReachSelectRaids()
        {
            ReachSelectAlliance();
            Assert.Equal(
                DadAlliancePfCreateAction.SelectAlliance,
                SendCurrentStage());
            Ui.Snapshot = Ui.Snapshot with
            {
                GroupTypeTab =
                    DadAlliancePartyFinderPresetDefinition.AllianceGroupTypeTab,
                AllianceSelected = true,
            };
            Assert.Equal(
                DadAlliancePfCreateStage.SelectRaids,
                Tick().Stage);
        }

        public void ReachSelectDuty()
        {
            ReachSelectRaids();
            Assert.Equal(
                DadAlliancePfCreateAction.SelectRaids,
                SendCurrentStage());
            Ui.Snapshot = Ui.Snapshot with
            {
                SelectedCategory =
                    DadAlliancePartyFinderCreateFlow.RaidsCategoryMask,
            };
            Assert.Equal(
                DadAlliancePfCreateStage.SelectDuty,
                Tick().Stage);
        }

        public void ReachApplyPreset()
        {
            ReachSelectDuty();
            Ui.Snapshot = DutyReadySnapshot();
            Assert.Equal(
                DadAlliancePfCreateAction.SelectDuty,
                SendCurrentStage());
            Ui.Snapshot = PreparedSelectorSnapshot();
            Assert.Equal(
                DadAlliancePfCreateStage.ApplyPreset,
                Tick().Stage);
        }

        public void ReachSubmit()
        {
            ReachApplyPreset();
            Assert.Equal(
                DadAlliancePfCreateAction.ApplyPreset,
                SendCurrentStage());
            Ui.Snapshot = ExactSnapshot();
            Assert.Equal(
                DadAlliancePfCreateStage.Submit,
                Tick().Stage);
        }

        public void ReachStage(DadAlliancePfCreateStage target)
        {
            switch (target)
            {
                case DadAlliancePfCreateStage.CloseStaleWindows:
                    Ui.Snapshot = Ui.Snapshot with
                    {
                        MainVisible = true,
                        MainReady = true,
                    };
                    return;
                case DadAlliancePfCreateStage.OpenMainWindow:
                    ReachOpenMainWindow();
                    return;
                case DadAlliancePfCreateStage.OpenConditions:
                    ReachOpenConditions();
                    return;
                case DadAlliancePfCreateStage.SelectAlliance:
                    ReachSelectAlliance();
                    return;
                case DadAlliancePfCreateStage.ReloadCloseConditions:
                    ReachReloadCloseConditions();
                    return;
                case DadAlliancePfCreateStage.ReloadRestoreAllianceTab:
                    ReachReloadRestoreAllianceTab();
                    return;
                case DadAlliancePfCreateStage.ReloadMainWindow:
                    ReachReloadMainWindow();
                    return;
                case DadAlliancePfCreateStage.ReloadOpenConditions:
                    ReachReloadOpenConditions();
                    return;
                case DadAlliancePfCreateStage.SelectRaids:
                    ReachSelectRaids();
                    return;
                case DadAlliancePfCreateStage.SelectDuty:
                    ReachSelectDuty();
                    return;
                case DadAlliancePfCreateStage.ApplyPreset:
                    ReachApplyPreset();
                    return;
                case DadAlliancePfCreateStage.Submit:
                    ReachSubmit();
                    return;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(target),
                        target,
                        null);
            }
        }

        public void PrepareCurrentStageForDispatch()
        {
            if (Flow.Stage == DadAlliancePfCreateStage.SelectDuty)
                Ui.Snapshot = DutyReadySnapshot();
        }

        public DadAlliancePfCreateSnapshot DutyReadySnapshot()
            => Ui.Snapshot with
            {
                AgentAvailable = true,
                MainVisible = true,
                MainReady = true,
                MainRecruitUsable = true,
                ConditionVisible = true,
                ConditionReady = true,
                GroupTypeTab =
                    DadAlliancePartyFinderPresetDefinition.AllianceGroupTypeTab,
                AllianceSelected = true,
                SelectedCategory =
                    DadAlliancePartyFinderCreateFlow.RaidsCategoryMask,
                TargetDutyId =
                    DadAlliancePartyFinderCreateFlow.LabyrinthDutyId,
                TargetDutySheetMatches = 1,
                DutyListLoaded = true,
                TargetDutyDropDownMatches = 1,
                TargetDutyEntryEnabled = true,
                TargetDutyDropDownIndex = 17,
                SelectedDutyDropDownIndex = -1,
                SelectedDutyId = 0,
            };

        public DadAlliancePfCreateSnapshot PreparedSelectorSnapshot()
            => DutyReadySnapshot() with
            {
                SelectedDutyDropDownIndex = 17,
                SelectedDutyId =
                    DadAlliancePartyFinderCreateFlow.LabyrinthDutyId,
                AllianceASelected = true,
            };

        public DadAlliancePfCreateSnapshot ExactSnapshot()
            => PreparedSelectorSnapshot() with
            {
                PresetLoaderAvailable = true,
                PresetLoaderBlocker = string.Empty,
                PrivateRecruitment = true,
                StoredPrivateRecruitment = true,
                Passcode = Passcode,
                StoredPasscode = Passcode,
                CrossWorldRecruitment = true,
                StoredCrossWorldRecruitment = true,
                OnePlayerPerJob = false,
                StoredOnePlayerPerJob = false,
                EmptyComment = true,
                StoredEmptyComment = true,
                UnrestrictedJobs = true,
                StoredOpenSlotsUnrestricted = true,
                StoredStaleMembersCleared = true,
                NumberOfGroups = 3,
                SlotsPerGroup = 8,
                StoredSettingsExactBeforeSubmit = true,
                StoredSettingsExact = true,
                StoredSettingsContradictory = false,
                ErrorToastSequence = 0,
                ErrorToast = string.Empty,
                HardBlocker = string.Empty,
            };

        public DadAlliancePfCreateAction SendCurrentStage()
        {
            var before = Ui.Actions.Count;
            var result = Tick();
            Assert.Equal(DadAlliancePfCreateResultKind.Progress, result.Kind);
            Assert.Equal(before + 1, Ui.Actions.Count);
            return Ui.Actions[^1];
        }

    }

    private sealed class FakeClock
    {
        public DateTime UtcNow { get; private set; } =
            new(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc);

        public void Advance(TimeSpan duration)
            => UtcNow += duration;
    }

    private sealed class FakeUi : IDadAlliancePartyFinderCreateUi
    {
        public DadAlliancePfCreateSnapshot Snapshot { get; set; } =
            new()
            {
                AgentAvailable = true,
                PresetLoaderAvailable = true,
                TargetDutyId =
                    DadAlliancePartyFinderCreateFlow.LabyrinthDutyId,
                TargetDutySheetMatches = 1,
            };

        public List<DadAlliancePfCreateAction> Actions { get; } = [];
        public Func<
            DadAlliancePfCreateAction,
            int,
            DadAlliancePfCreateActionResult>? Handler { get; set; }
        public bool ThrowOnRead { get; set; }

        public DadAlliancePfCreateSnapshot Read(int passcode)
        {
            if (ThrowOnRead)
            {
                ThrowOnRead = false;
                throw new InvalidOperationException("read failed");
            }

            return Snapshot;
        }

        public DadAlliancePfCreateActionResult Perform(
            DadAlliancePfCreateAction action,
            int passcode)
        {
            Actions.Add(action);
            return Handler?.Invoke(action, passcode) ??
                   new DadAlliancePfCreateActionResult(
                       true,
                       $"sent {action}");
        }
    }
}
