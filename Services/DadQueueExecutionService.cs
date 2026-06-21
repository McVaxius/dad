using dad.Models;

namespace dad.Services;

public sealed class DadQueueExecutionService
{
    private readonly DadLocalDutyExecutor localDutyExecutor;
    private readonly DadPremadeDutyExecutor premadeDutyExecutor;
    private readonly DadMsqExecutor msqExecutor;
    private readonly DadDutySupportExecutor dutySupportExecutor;
    private readonly DadTrustExecutor trustExecutor;
    private readonly DadDailyMsqExecutor dailyMsqExecutor;
    private readonly DadBlundervilleExecutor blundervilleExecutor;
    private readonly DadMogtomeExecutor mogtomeExecutor;
    private readonly DadCommendationExecutor commendationExecutor;
    private readonly DadAstropeExecutor astropeExecutor;
    private readonly DadCustomDutyExecutor customDutyExecutor;
    private IDadModuleExecutor? activeExecutor;
    private DadModuleId activeReportedModuleId = DadModuleId.None;

    public DadQueueExecutionService(
        DadModuleRegistry moduleRegistry,
        DadMogtomeIpcService mogtomeIpcService,
        DadDutyQueueService dutyQueueService,
        DadLocalDutyQueueService localDutyQueueService,
        DadNpcDutyQueueService npcDutyQueueService,
        DadDutySupportAdsService dutySupportAdsService,
        DadCombatRotationService combatRotationService)
    {
        localDutyExecutor = new DadLocalDutyExecutor(localDutyQueueService, combatRotationService);
        premadeDutyExecutor = new DadPremadeDutyExecutor(localDutyQueueService, combatRotationService);
        msqExecutor = new DadMsqExecutor(
            moduleRegistry,
            _ => moduleRegistry.GetCapability(DadModuleId.Msq).Blockers
                .FirstOrDefault(blocker => blocker.Capability == "CanStartQueue")?.Summary
                 ?? moduleRegistry.GetCapability(DadModuleId.Msq).Notes);
        dutySupportExecutor = new DadDutySupportExecutor(npcDutyQueueService, dutySupportAdsService, combatRotationService);
        trustExecutor = new DadTrustExecutor(npcDutyQueueService, combatRotationService);
        dailyMsqExecutor = new DadDailyMsqExecutor(
            moduleRegistry,
            _ => dutyQueueService.DescribeDailyMsqExecutionDeferral());
        blundervilleExecutor = new DadBlundervilleExecutor(
            moduleRegistry,
            _ => moduleRegistry.GetCapability(DadModuleId.Blunderville).Blockers
                .FirstOrDefault(blocker => blocker.Capability == "CanStartQueue")?.Summary
                 ?? moduleRegistry.GetCapability(DadModuleId.Blunderville).Notes);
        mogtomeExecutor = new DadMogtomeExecutor(mogtomeIpcService);
        commendationExecutor = new DadCommendationExecutor(
            moduleRegistry,
            _ => dutyQueueService.DescribeCommendationExecutionDeferral());
        astropeExecutor = new DadAstropeExecutor(
            moduleRegistry,
            _ => dutyQueueService.DescribeAstropeExecutionDeferral());
        customDutyExecutor = new DadCustomDutyExecutor(
            moduleRegistry,
            _ => moduleRegistry.GetCapability(DadModuleId.CustomDuty).Blockers
                .FirstOrDefault(blocker => blocker.Capability == "CanStartQueue")?.Summary
                 ?? moduleRegistry.GetCapability(DadModuleId.CustomDuty).Notes);
    }

    public DadRunStepResultDto ExecuteModule(DadRunPlan plan, DadPlannedModuleExecution module, IReadOnlyList<DadParticipantSnapshot> participants)
    {
        activeReportedModuleId = module.ModuleId;
        if (IsCommendationTruthUnavailable(plan, module))
        {
            return BuildUnavailableTruthResult(
                plan,
                module,
                "Commendation total/gained target requires guarded API15 commendation truth; runtime adapter is unavailable.");
        }

        // Review M5: resolve the effective plan/module exactly the same way preview does, so the executor
        // that runs matches what the UI previewed.
        var (effectivePlan, effectiveModule) = ResolveEffective(plan, module, participants);
        var nextExecutor = ResolveExecutor(effectivePlan, effectiveModule);

        // Review M6: don't orphan a still-running executor when switching modules — cancel it first so it
        // doesn't keep driving game state behind the new one.
        if (activeExecutor != null && !ReferenceEquals(activeExecutor, nextExecutor))
            activeExecutor.Cancel("Superseded by next Dad module.");

        activeExecutor = nextExecutor;
        return NormalizeReportedModule(activeExecutor.Start(effectivePlan, participants));
    }

    // Review M5: single source of truth for plan/module transforms, shared by ExecuteModule and PreviewModuleStart.
    private (DadRunPlan Plan, DadPlannedModuleExecution Module) ResolveEffective(
        DadRunPlan plan,
        DadPlannedModuleExecution module,
        IReadOnlyList<DadParticipantSnapshot> participants)
    {
        if (module.ModuleId == DadModuleId.Msq && plan.Request.Msq != null)
        {
            var trust = BuildMsqPlan(plan, module, useTrust: true);
            return trustExecutor.CanStart(trust.Plan, participants).CanStart
                ? trust
                : BuildMsqPlan(plan, module, useTrust: false);
        }

        if (module.ModuleId == DadModuleId.Commendation && plan.Request.Commendation != null)
            return BuildCommendationPlan(plan, module);

        if (module.ModuleId == DadModuleId.CustomDuty && plan.Request.CustomDuty != null)
            return BuildCustomDutyPlan(plan, module);

        return (plan, module);
    }

    private static bool IsCommendationTruthUnavailable(DadRunPlan plan, DadPlannedModuleExecution module)
        => module.ModuleId == DadModuleId.Commendation &&
           plan.Request.Commendation is { } commendation &&
           !string.Equals(commendation.StopMode, DadCommendationStopModes.Attempts, StringComparison.OrdinalIgnoreCase);

    public void SetWorkerRole(DadWorkerExecutionRole role)
        => mogtomeExecutor.SetWorkerRole(role);

    public DadRunStepResultDto UpdateActiveExecutor()
        => NormalizeReportedModule(activeExecutor?.Update() ?? new DadRunStepResultDto());

    public DadRunStepResultDto CancelActiveExecutor(string reason)
        => NormalizeReportedModule(activeExecutor?.Cancel(reason) ?? new DadRunStepResultDto());

    public DadModuleExecutionStatusDto GetActiveExecutorStatus()
        => activeExecutor?.GetStatus() ?? new DadModuleExecutionStatusDto();

    public DadModuleExecutionStatusDto PreviewModuleStart(DadRunPlan plan)
    {
        var module = ResolvePreviewModule(plan);

        // Review M5: mirror the runtime "commendation truth unavailable" gate so preview can't show a
        // commendation lane as startable when ExecuteModule would immediately fail it.
        if (IsCommendationTruthUnavailable(plan, module))
        {
            return new DadModuleExecutionStatusDto
            {
                RunId = plan.Request.RequestId,
                ModuleId = module.ModuleId,
                DisplayName = module.DisplayName,
                CanStart = false,
                Summary = "Commendation total/gained target requires guarded API15 commendation truth; runtime adapter is unavailable.",
                BlockedReason = "Commendation runtime truth adapter is unavailable.",
            };
        }

        var participantCount = Math.Max(1, Math.Max(module.ExpectedPartySize, plan.RequiredParticipantCount));
        var participants = Enumerable.Range(0, participantCount)
            .Select(index => new DadParticipantSnapshot
            {
                IsLocalClient = index == 0,
                IsAuthority = index == 0,
                IsAvailable = true,
                IsEligibleForRun = true,
                PostArReady = true,
                State = DadParticipantState.Ready,
                ClaimState = DadClaimState.Granted,
                LeaseState = DadParticipantLeaseState.Granted,
                ActiveCharacterKey = index == 0 && !string.IsNullOrWhiteSpace(plan.LeaderCharacterKey)
                    ? new DadCharacterKey(plan.LeaderCharacterKey)
                    : new DadCharacterKey($"Preview-{index + 1}"),
                AssignedSlotId = index == 0 ? "Leader" : $"Party {index + 1}",
            })
            .ToList();

        var (effectivePlan, effectiveModule) = ResolveEffective(plan, module, participants);
        return ResolveExecutor(effectivePlan, effectiveModule).CanStart(effectivePlan, participants);
    }

    private IDadModuleExecutor ResolveExecutor(DadRunPlan plan, DadPlannedModuleExecution module)
        => module.ModuleId switch
        {
            DadModuleId.Duty when plan.Request.Dungeon?.QueueViaLanParty == true => premadeDutyExecutor,
            DadModuleId.Duty => localDutyExecutor,
            DadModuleId.Msq => msqExecutor,
            DadModuleId.DutySupport => dutySupportExecutor,
            DadModuleId.Trust => trustExecutor,
            DadModuleId.PremadeDuty => premadeDutyExecutor,
            DadModuleId.DailyMsq => dailyMsqExecutor,
            DadModuleId.Blunderville => blundervilleExecutor,
            DadModuleId.Mogtome => mogtomeExecutor,
            DadModuleId.Commendation => commendationExecutor,
            DadModuleId.Astrope => astropeExecutor,
            DadModuleId.CustomDuty => customDutyExecutor,
            _ => localDutyExecutor,
        };

    private static (DadRunPlan Plan, DadPlannedModuleExecution Module) BuildCustomDutyPlan(
        DadRunPlan plan,
        DadPlannedModuleExecution module)
    {
        var task = plan.Request.CustomDuty!;
        var premade = Math.Max(1, task.ExpectedPartySize) > 1;
        var effectiveModule = new DadPlannedModuleExecution
        {
            ModuleId = premade ? DadModuleId.PremadeDuty : DadModuleId.Duty,
            DisplayName = task.DutyName,
            OwnerLabel = module.OwnerLabel,
            ExpectedPartySize = Math.Max(1, task.ExpectedPartySize),
            RequiresPeers = premade,
            Summary = module.Summary,
        };
        var request = new DadRunRequest
        {
            RequestId = plan.Request.RequestId,
            RequestedAtUtc = plan.Request.RequestedAtUtc,
            RequestedBy = plan.Request.RequestedBy,
            StopPolicy = plan.Request.StopPolicy,
            Orchestration = plan.Request.Orchestration,
        };
        if (premade)
        {
            request.PremadeDuty = new DadPremadeDutyTask
            {
                ContentFinderConditionId = task.ContentFinderConditionId,
                DutyName = task.DutyName,
                ExpectedPartySize = task.ExpectedPartySize,
                Unsynced = task.Unsynced,
                Attempts = task.Attempts,
            };
        }
        else
        {
            request.Dungeon = new DadDungeonTask
            {
                ContentFinderConditionId = task.ContentFinderConditionId,
                SelectedDungeon = task.DutyName,
                Count = task.Attempts,
                Unsynced = task.Unsynced,
                QueueViaLanParty = false,
            };
        }

        return (new DadRunPlan
        {
            Request = request,
            CompositeModuleId = effectiveModule.ModuleId,
            Orchestration = plan.Orchestration,
            Summary = plan.Summary,
            RequiredParticipantCount = plan.RequiredParticipantCount,
            RequiresRemoteParticipants = plan.RequiresRemoteParticipants,
            LeaderCharacterKey = plan.LeaderCharacterKey,
            Modules = [effectiveModule],
            PlannerWarnings = [..plan.PlannerWarnings],
        }, effectiveModule);
    }

    private static (DadRunPlan Plan, DadPlannedModuleExecution Module) BuildMsqPlan(
        DadRunPlan plan,
        DadPlannedModuleExecution module,
        bool useTrust)
    {
        var task = plan.Request.Msq!;
        var effectiveModule = new DadPlannedModuleExecution
        {
            ModuleId = useTrust ? DadModuleId.Trust : DadModuleId.DutySupport,
            DisplayName = useTrust ? "MSQ Trust" : "MSQ Duty Support",
            OwnerLabel = module.OwnerLabel,
            ExpectedPartySize = 1,
            RequiresPeers = false,
            Summary = module.Summary,
        };
        var request = new DadRunRequest
        {
            RequestId = plan.Request.RequestId,
            RequestedAtUtc = plan.Request.RequestedAtUtc,
            RequestedBy = plan.Request.RequestedBy,
            StopPolicy = plan.Request.StopPolicy,
            Orchestration = plan.Request.Orchestration,
        };
        if (useTrust)
        {
            request.Trust = new DadTrustTask
            {
                ContentFinderConditionId = task.ContentFinderConditionId,
                DutyName = task.DutyName,
                Attempts = task.Attempts,
            };
        }
        else
        {
            request.DutySupport = new DadDutySupportTask
            {
                ContentFinderConditionId = task.ContentFinderConditionId,
                DutyName = task.DutyName,
                Attempts = task.Attempts,
            };
        }

        return (new DadRunPlan
        {
            Request = request,
            CompositeModuleId = effectiveModule.ModuleId,
            Orchestration = plan.Orchestration,
            Summary = plan.Summary,
            RequiredParticipantCount = 1,
            RequiresRemoteParticipants = false,
            LeaderCharacterKey = plan.LeaderCharacterKey,
            Modules = [effectiveModule],
            PlannerWarnings = [..plan.PlannerWarnings],
        }, effectiveModule);
    }

    private static (DadRunPlan Plan, DadPlannedModuleExecution Module) BuildCommendationPlan(
        DadRunPlan plan,
        DadPlannedModuleExecution module)
    {
        var task = plan.Request.Commendation!;
        var effectiveModule = new DadPlannedModuleExecution
        {
            ModuleId = DadModuleId.PremadeDuty,
            DisplayName = task.DutyName,
            OwnerLabel = module.OwnerLabel,
            ExpectedPartySize = 4,
            RequiresPeers = true,
            Summary = module.Summary,
        };
        var request = new DadRunRequest
        {
            RequestId = plan.Request.RequestId,
            RequestedAtUtc = plan.Request.RequestedAtUtc,
            RequestedBy = plan.Request.RequestedBy,
            StopPolicy = plan.Request.StopPolicy,
            Orchestration = plan.Request.Orchestration,
            PremadeDuty = new DadPremadeDutyTask
            {
                ContentFinderConditionId = task.ContentFinderConditionId,
                DutyName = string.IsNullOrWhiteSpace(task.DutyName) ? "Under the Armour" : task.DutyName,
                ExpectedPartySize = 4,
                Attempts = task.Attempts,
            },
        };
        return (new DadRunPlan
        {
            Request = request,
            CompositeModuleId = DadModuleId.PremadeDuty,
            Orchestration = plan.Orchestration,
            Summary = plan.Summary,
            RequiredParticipantCount = plan.RequiredParticipantCount,
            RequiresRemoteParticipants = true,
            LeaderCharacterKey = plan.LeaderCharacterKey,
            Modules = [effectiveModule],
            PlannerWarnings = [..plan.PlannerWarnings],
        }, effectiveModule);
    }

    private static DadRunStepResultDto BuildUnavailableTruthResult(
        DadRunPlan plan,
        DadPlannedModuleExecution module,
        string reason)
    {
        var now = DateTime.UtcNow;
        return new DadRunStepResultDto
        {
            RunId = plan.Request.RequestId,
            ModuleId = module.ModuleId,
            StepName = module.DisplayName,
            ParticipantState = DadParticipantState.Failed,
            Success = false,
            FailureReason = reason,
            BlockedReason = reason,
            Summary = reason,
            ExecutorStatus = new DadModuleExecutionStatusDto
            {
                RunId = plan.Request.RequestId,
                ModuleId = module.ModuleId,
                DisplayName = module.DisplayName,
                Phase = DadRunPhase.Finalizing,
                Status = DadRunStatus.Failed,
                StepName = "Runtime truth",
                CanStart = false,
                StartedAtUtc = now,
                UpdatedAtUtc = now,
                CompletedAtUtc = now,
                Summary = reason,
                FailureReason = reason,
                BlockedReason = reason,
            },
        };
    }

    private DadRunStepResultDto NormalizeReportedModule(DadRunStepResultDto result)
    {
        if (activeReportedModuleId == DadModuleId.None || result.ModuleId == DadModuleId.None)
            return result;

        result.ModuleId = activeReportedModuleId;
        result.ExecutorStatus.ModuleId = activeReportedModuleId;
        foreach (var blocker in result.ModuleBlockers)
            blocker.ModuleId = activeReportedModuleId;
        return result;
    }

    private static DadPlannedModuleExecution ResolvePreviewModule(DadRunPlan plan)
        => plan.Modules.FirstOrDefault()
           ?? new DadPlannedModuleExecution
           {
               ModuleId = plan.CompositeModuleId,
               DisplayName = plan.CompositeModuleId == DadModuleId.None ? "Dad" : plan.CompositeModuleId.ToString(),
               ExpectedPartySize = Math.Max(1, plan.RequiredParticipantCount),
           };
}
