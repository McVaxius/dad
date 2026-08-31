using Xunit;

namespace dad.Tests;

public sealed class DadReliabilityIntegrationSourceContractTests
{
    [Fact]
    public void CoordinatorCuratesRunStartPoolBeforePlanningAndManifestBinding()
    {
        var source = ReadRepositorySource("Services", "DadCoordinatorService.cs");
        var rawRefresh = source.IndexOf(
            "var rawPool = characterIntelligenceService.RefreshLocalCharacterPool(\"run-start\", logRefresh: false);",
            StringComparison.Ordinal);
        var curatedPool = source.IndexOf(
            "var pool = rosterCatalogService.BuildCuratedPool(rawPool);",
            rawRefresh,
            StringComparison.Ordinal);
        var planning = source.IndexOf(
            "plannerService.BuildPlan(",
            curatedPool,
            StringComparison.Ordinal);
        var manifestBinding = source.IndexOf(
            "var onlineParticipants = BuildOnlineParticipantSet(pool, liveCoordinatorTruth);",
            planning,
            StringComparison.Ordinal);
        var dependencyBinding = source.IndexOf(
            "var currentParticipants = BuildOnlineParticipantSet(pool, liveCoordinatorTruth);",
            manifestBinding,
            StringComparison.Ordinal);

        Assert.True(rawRefresh >= 0);
        Assert.True(curatedPool > rawRefresh);
        Assert.True(planning > curatedPool);
        Assert.True(manifestBinding > planning);
        Assert.True(dependencyBinding > manifestBinding);
        Assert.Contains(
            "private readonly DadRosterCatalogService rosterCatalogService;",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PartyFinderObservesExactlyOneVisibleRendererListAndNeverScansNativeListingIds()
    {
        var source = ReadRepositorySource("Services", "DadAlliancePartyFinderNativeGateway.cs");
        var standardView = source.IndexOf(
            "ReadListingView(main->StandardViewList)",
            StringComparison.Ordinal);
        var compactView = source.IndexOf(
            "ReadListingView(main->CompactViewList)",
            standardView,
            StringComparison.Ordinal);
        var reader = source.IndexOf(
            "private static DadAlliancePfListingViewSnapshot ReadListingView(",
            compactView,
            StringComparison.Ordinal);
        var ownerNode = source.IndexOf(
            "var root = (AtkResNode*)list->OwnerNode;",
            reader,
            StringComparison.Ordinal);
        var rendererIndex = source.IndexOf(
            "renderer->ListItemIndex,",
            ownerNode,
            StringComparison.Ordinal);
        var recruiterNode = source.IndexOf(
            "renderer->GetTextNodeById(ListingRecruiterTextNodeId)",
            rendererIndex,
            StringComparison.Ordinal);
        var seString = source.IndexOf(
            "ReadSeStringNullTerminated((nint)value)",
            recruiterNode,
            StringComparison.Ordinal);

        Assert.True(standardView >= 0);
        Assert.True(compactView > standardView);
        Assert.True(reader > compactView);
        Assert.True(ownerNode > reader);
        Assert.True(rendererIndex > ownerNode);
        Assert.True(recruiterNode > rendererIndex);
        Assert.True(seString > recruiterNode);
        Assert.Contains(
            "private const uint ListingRecruiterTextNodeId = 28;",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (standardReady == compactReady)",
            ReadRepositorySource("Services", "DadAlliancePartyFinderJoinFlow.cs"),
            StringComparison.Ordinal);
        Assert.DoesNotContain("ListingIds", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PopulateListingData", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadNativeListingView", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SchedulerReevaluatesFrozenLevelSeekAfterReadinessAndJobAcknowledgementBeforeDispatch()
    {
        var source = ReadRepositorySource("Services", "DadSchedulerService.cs");
        var initialDailyPreflight = source.IndexOf(
            "if (frozenLevelSeekGroup == null &&",
            StringComparison.Ordinal);
        var initialLevelSeekSkip = source.IndexOf(
            "if (levelSeek.ShouldSkip)",
            StringComparison.Ordinal);
        var acknowledgement = source.IndexOf(
            "if (!assignmentsAcknowledged)",
            StringComparison.Ordinal);
        var postWakeCheck = source.IndexOf(
            "if (TrySkipSatisfiedPostWakeLevelTargets())",
            acknowledgement,
            StringComparison.Ordinal);
        var deferredDailyPreflight = source.IndexOf(
            "TryBeginDailyRewardPreflight(frozenLevelSeekGroup)",
            postWakeCheck,
            StringComparison.Ordinal);
        var plannerDispatch = source.IndexOf(
            "if (!DadSchedulerRoutingRules.TryInvokeCallback(",
            postWakeCheck,
            StringComparison.Ordinal);

        Assert.True(initialDailyPreflight >= 0);
        Assert.True(initialLevelSeekSkip >= 0);
        Assert.True(initialDailyPreflight > initialLevelSeekSkip);
        Assert.True(acknowledgement >= 0);
        Assert.True(postWakeCheck > acknowledgement);
        Assert.True(deferredDailyPreflight > postWakeCheck);
        Assert.True(plannerDispatch > postWakeCheck);
        Assert.True(plannerDispatch > deferredDailyPreflight);
        Assert.Contains("if (dailyRewardPreflightAttempted)", source, StringComparison.Ordinal);
        Assert.Contains("dailyRewardPreflightAttempted = true;", source, StringComparison.Ordinal);
        Assert.Contains(
            "characterIntelligenceService.RequestPeerSnapshots()",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "DadLevelSeekEvaluationRules.Evaluate(",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SchedulerDispatchesPreparedCrewBeforeOrdinaryStrictRevalidation()
    {
        var source = ReadRepositorySource("Services", "DadSchedulerService.cs");
        var acknowledgement = source.IndexOf(
            "if (!assignmentsAcknowledged)",
            StringComparison.Ordinal);
        var autoPartyAuthorization = source.IndexOf(
            "var autoPartyAuthorization = autoPartyAuthorizationGate?.Invoke(frozenPlannerRequest)",
            acknowledgement,
            StringComparison.Ordinal);
        var deniedAuthorization = source.IndexOf(
            "if (autoPartyAuthorization.State == DadAutoPartyAuthorizationState.Denied)",
            autoPartyAuthorization,
            StringComparison.Ordinal);
        var preparedCrewDispatch = source.IndexOf(
            "if (TryStartPreparedCrewFormation())",
            deniedAuthorization,
            StringComparison.Ordinal);
        var strictPreview = source.IndexOf(
            "() => plannerPreviewBuilder(currentState.GroupId)",
            preparedCrewDispatch,
            StringComparison.Ordinal);
        var frozenContract = source.IndexOf(
            "DadSchedulerRoutingRules.MatchesFrozenRequestContract(",
            strictPreview,
            StringComparison.Ordinal);
        var ordinaryStart = source.IndexOf(
            "() => startPlannerRequest(strictRequest, repeatBoundary)",
            frozenContract,
            StringComparison.Ordinal);

        Assert.True(acknowledgement >= 0);
        Assert.True(autoPartyAuthorization > acknowledgement);
        Assert.True(deniedAuthorization > autoPartyAuthorization);
        Assert.True(preparedCrewDispatch > deniedAuthorization);
        Assert.True(strictPreview > preparedCrewDispatch);
        Assert.True(frozenContract > strictPreview);
        Assert.True(ordinaryStart > frozenContract);
        Assert.Equal(1, source.Split("if (TryStartPreparedCrewFormation())", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void AlliancePartyFinderRetainsExactOwnedListingAfterCrewVerification()
    {
        var source = ReadRepositorySource("Services", "DadAlliancePartyFinderService.cs");
        var verification = source.IndexOf(
            "if (successful == coordinatorTargets.Count && successful > 0)",
            StringComparison.Ordinal);
        var nextMethod = source.IndexOf(
            "private void UpdateRemoteHostCoordinator",
            verification,
            StringComparison.Ordinal);
        var verificationBranch = source[verification..nextMethod];

        Assert.True(verification >= 0);
        Assert.True(nextMethod > verification);
        Assert.Contains("status.State = DadAllianceRecruitmentState.Complete;", verificationBranch, StringComparison.Ordinal);
        Assert.Contains("status.OwnsRecruitment = true;", verificationBranch, StringComparison.Ordinal);
        Assert.Contains("retaining the owned recruitment for operator Stop.", verificationBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("BeginCoordinatorCleanup(", verificationBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("QueueDiscordCleanup()", verificationBranch, StringComparison.Ordinal);
    }

    [Fact]
    public void CoordinatorRefreshesAggregateEvidenceBeforePostRunStopDecision()
    {
        var source = ReadRepositorySource("Services", "DadCoordinatorService.cs");
        var loop = source.IndexOf(
            "private bool TryContinueStopPolicyLoop",
            StringComparison.Ordinal);
        var refresh = source.IndexOf(
            "RefreshStopProgressSummary(activePlan, refreshPool: true);",
            loop,
            StringComparison.Ordinal);
        var stopDecision = source.IndexOf(
            "if (stopProgress.StopReached)",
            refresh,
            StringComparison.Ordinal);
        var aggregateEvaluation = source.IndexOf(
            "DadResolvedLevelTargetRules.Evaluate(policy, pool)",
            StringComparison.Ordinal);

        Assert.True(loop >= 0);
        Assert.True(refresh > loop);
        Assert.True(stopDecision > refresh);
        Assert.True(aggregateEvaluation >= 0);
    }

    [Fact]
    public void PlannerActionsKeepDirectSchedulerAndCancellationOwnersSeparate()
    {
        var windowSource = ReadRepositorySource("Windows", "MainWindow.cs");
        var actionStart = windowSource.IndexOf(
            "private void DrawPlannerActionStrip(",
            StringComparison.Ordinal);
        var cancelOwnerStart = windowSource.IndexOf(
            "private void CancelOwnedOperation(",
            actionStart,
            StringComparison.Ordinal);
        var actionEnd = cancelOwnerStart;
        var action = windowSource[actionStart..actionEnd];

        var directLabel = action.IndexOf("Run now — online participants", StringComparison.Ordinal);
        var directStart = action.IndexOf("plugin.StartPlannerRunFromShell();", directLabel, StringComparison.Ordinal);
        var schedulerLabel = action.IndexOf("Wake/relog and run", directStart, StringComparison.Ordinal);
        var schedulerStart = action.IndexOf(
            "EnqueueSelectedPreset(DadSchedulerJobType.ScheduledPreset",
            schedulerLabel,
            StringComparison.Ordinal);
        var ownerAwareCancel = action.IndexOf(
            "CancelOwnedOperation(schedulerJobToCancel, \"Planner\");",
            StringComparison.Ordinal);

        Assert.True(actionStart >= 0);
        Assert.True(actionEnd > actionStart);
        Assert.True(directLabel >= 0);
        Assert.True(directStart > directLabel);
        Assert.True(schedulerLabel > directStart);
        Assert.True(schedulerStart > schedulerLabel);
        Assert.Contains("plugin.Configuration.RunAsServerDad &&", action, StringComparison.Ordinal);
        Assert.Contains("!cancellationCleanupPending", action, StringComparison.Ordinal);
        Assert.Contains("visibleCoordinatorCleanupPending", action, StringComparison.Ordinal);
        Assert.Contains("DadRunCancellationState.Cancelling", action, StringComparison.Ordinal);
        Assert.Contains("Cancel preset operation", action, StringComparison.Ordinal);
        Assert.True(ownerAwareCancel > schedulerStart);

        var cancelOwnerEnd = windowSource.IndexOf(
            "private static string ResolveSchedulerJobPhase(",
            cancelOwnerStart,
            StringComparison.Ordinal);
        var cancelOwner = windowSource[cancelOwnerStart..cancelOwnerEnd];
        var exactSchedulerOwner = cancelOwner.IndexOf(
            "GetQueueSnapshot().ActiveJob ?? fallbackSchedulerJob",
            StringComparison.Ordinal);
        var exactJobId = cancelOwner.IndexOf(
            "JobId = schedulerJob.JobId",
            exactSchedulerOwner,
            StringComparison.Ordinal);
        var directCancel = cancelOwner.IndexOf(
            "plugin.CancelActiveRunFromShell();",
            StringComparison.Ordinal);

        Assert.True(cancelOwnerStart > actionStart);
        Assert.True(cancelOwnerEnd > cancelOwnerStart);
        Assert.True(exactSchedulerOwner >= 0);
        Assert.True(exactJobId > exactSchedulerOwner);
        Assert.True(directCancel >= 0);

        var shellHeaderStart = windowSource.IndexOf("private void DrawShellHeader(", StringComparison.Ordinal);
        var activeBannerStart = windowSource.IndexOf(
            "private void DrawActiveRunBanner(",
            shellHeaderStart,
            StringComparison.Ordinal);
        var configurationWarningStart = windowSource.IndexOf(
            "private void DrawConfigurationPersistenceWarning(",
            activeBannerStart,
            StringComparison.Ordinal);
        var shellHeader = windowSource[shellHeaderStart..activeBannerStart];
        var activeBanner = windowSource[activeBannerStart..configurationWarningStart];

        Assert.Contains("CancelOwnedOperation();", shellHeader, StringComparison.Ordinal);
        Assert.Contains("CancelOwnedOperation();", activeBanner, StringComparison.Ordinal);
        Assert.DoesNotContain("CancelActiveRunFromShell", shellHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("CancelActiveRunFromShell", activeBanner, StringComparison.Ordinal);

        var pluginSource = ReadRepositorySource("Plugin.cs");
        var enqueueStart = pluginSource.IndexOf(
            "public string EnqueueScheduledPresetFromJson(",
            StringComparison.Ordinal);
        var enqueueEnd = pluginSource.IndexOf(
            "public string CancelScheduledJobFromJson(",
            enqueueStart,
            StringComparison.Ordinal);
        var enqueue = pluginSource[enqueueStart..enqueueEnd];
        var clientGuard = enqueue.IndexOf("if (!Configuration.RunAsServerDad)", StringComparison.Ordinal);
        var queueMutation = enqueue.IndexOf(
            "SchedulerService.EnqueueScheduledPresetWithDisposition(group, request)",
            StringComparison.Ordinal);

        Assert.True(clientGuard >= 0);
        Assert.True(queueMutation > clientGuard);
        Assert.Contains("Use Planner on the Coordinator", enqueue, StringComparison.Ordinal);

        var directMethodStart = pluginSource.IndexOf(
            "public DadRunResult StartPlannerRunFromShell()",
            StringComparison.Ordinal);
        var directMethodEnd = pluginSource.IndexOf(
            "private DadPlannerRunRequestPreview? BuildSchedulerPlannerPreview(",
            directMethodStart,
            StringComparison.Ordinal);
        var directMethod = pluginSource[directMethodStart..directMethodEnd];

        Assert.Contains(
            "StartDemoRunFromShell(\"Planner run\", requestPreview.Request)",
            directMethod,
            StringComparison.Ordinal);
        Assert.Contains("HasPendingCancellationCleanup", directMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("EnqueueScheduledPreset", directMethod, StringComparison.Ordinal);

        var coordinatorSource = ReadRepositorySource("Services", "DadCoordinatorService.cs");
        var coordinatorStart = coordinatorSource.IndexOf(
            "private DadRunResult StartTasksCore(",
            StringComparison.Ordinal);
        var dependencyGate = coordinatorSource.IndexOf(
            "presenceService.BuildSnapshotCopy().Dependencies.IsReady",
            coordinatorStart,
            StringComparison.Ordinal);
        var cleanupGate = coordinatorSource.IndexOf(
            "if (HasPendingCancellationCleanup)",
            coordinatorStart,
            StringComparison.Ordinal);

        Assert.True(cleanupGate > coordinatorStart);
        Assert.True(dependencyGate > cleanupGate);
        Assert.Contains(
            "status == DadRunStatus.Cancelled && HasPendingCancellationCleanup",
            coordinatorSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "CurrentResult.CancellationState = DadRunCancellationState.Finalized;",
            coordinatorSource,
            StringComparison.Ordinal);
    }

    private static string ReadRepositorySource(params string[] pathParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "dad.csproj")))
            directory = directory.Parent;
        var repositoryRoot = directory?.FullName ?? throw new DirectoryNotFoundException(
            "Could not locate the Dad repository root from the test output directory.");
        return File.ReadAllText(Path.Combine([repositoryRoot, .. pathParts]));
    }
}
