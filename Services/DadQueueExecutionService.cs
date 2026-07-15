using dad.Models;

namespace dad.Services;

public sealed class DadQueueExecutionService
{
    private readonly DadLocalDutyQueueService localDutyQueueService;
    private readonly DadLocalDutyExecutor localDutyExecutor;
    private readonly DadPremadeDutyExecutor premadeDutyExecutor;
    private readonly DadMsqExecutor msqExecutor;
    private readonly DadDutySupportExecutor dutySupportExecutor;
    private readonly DadTrustExecutor trustExecutor;
    private readonly DadPremadeDutyExecutor dailyRouletteExecutor;
    private readonly DadBlundervilleExecutor blundervilleExecutor;
    private readonly DadMogtomeExecutor mogtomeExecutor;
    private readonly DadCommendationExecutor commendationExecutor;
    private readonly DadAstropeExecutor astropeExecutor;
    private readonly DadCustomDutyExecutor customDutyExecutor;
    private readonly DadSquadronExecutor squadronExecutor;
    private readonly DadVariantVvdExecutor variantVvdExecutor;
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
        this.localDutyQueueService = localDutyQueueService;
        localDutyExecutor = new DadLocalDutyExecutor(localDutyQueueService, combatRotationService);
        premadeDutyExecutor = new DadPremadeDutyExecutor(
            localDutyQueueService,
            combatRotationService,
            DadModuleId.PremadeDuty,
            "Premade Duty",
            "dad-premade-duty",
            (DadRunPlan plan, out string blocker) =>
            {
                if (plan.Request.PremadeDuty != null)
                    return localDutyQueueService.Resolve(plan.Request.PremadeDuty, out blocker);

                if (plan.Request.Dungeon?.QueueViaLanParty == true)
                    return localDutyQueueService.ResolvePremade(plan.Request.Dungeon, out blocker);

                blocker = "Premade Duty request is missing a premade duty task.";
                return null;
            });
        msqExecutor = new DadMsqExecutor(
            moduleRegistry,
            _ => moduleRegistry.GetCapability(DadModuleId.Msq).Blockers
                .FirstOrDefault(blocker => blocker.Capability == "CanStartQueue")?.Summary
                 ?? moduleRegistry.GetCapability(DadModuleId.Msq).Notes);
        dutySupportExecutor = new DadDutySupportExecutor(npcDutyQueueService, dutySupportAdsService, combatRotationService);
        trustExecutor = new DadTrustExecutor(npcDutyQueueService, combatRotationService);
        dailyRouletteExecutor = new DadPremadeDutyExecutor(
            localDutyQueueService,
            combatRotationService,
            DadModuleId.DailyMsq,
            "Daily Roulette",
            "dad-daily-roulette",
            (DadRunPlan plan, out string blocker) => localDutyQueueService.Resolve(plan.Request.DailyMsq, out blocker));
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
        squadronExecutor = new DadSquadronExecutor(
            moduleRegistry,
            _ => moduleRegistry.GetCapability(DadModuleId.Squadron).Blockers
                .FirstOrDefault(blocker => blocker.Capability == "CanStartQueue")?.Summary
                 ?? moduleRegistry.GetCapability(DadModuleId.Squadron).Notes);
        variantVvdExecutor = new DadVariantVvdExecutor(
            moduleRegistry,
            _ => moduleRegistry.GetCapability(DadModuleId.VariantVvd).Blockers
                .FirstOrDefault(blocker => blocker.Capability == "CanStartQueue")?.Summary
                 ?? moduleRegistry.GetCapability(DadModuleId.VariantVvd).Notes);
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
            return DadEffectivePlanFactory.BuildCommendationPlan(plan, module);

        if (module.ModuleId == DadModuleId.CustomDuty && plan.Request.CustomDuty != null)
            return DadEffectivePlanFactory.BuildCustomDutyPlan(plan, module);

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

    public DadRunStepResultDto CancelAll(string reason)
    {
        var result = CancelActiveExecutor(reason);
        activeExecutor = null;
        activeReportedModuleId = DadModuleId.None;
        return result;
    }

    public DadModuleExecutionStatusDto GetActiveExecutorStatus()
        => activeExecutor?.GetStatus() ?? new DadModuleExecutionStatusDto();

    public bool TryResolveParticipantQueueContent(
        DadRunPlan plan,
        DadPlannedModuleExecution module,
        out DadLocalDutyResolvedContent? content,
        out string blocker)
    {
        content = null;
        blocker = string.Empty;
        if (!DadParticipantQueueFollowThroughRules.IsObserveAcceptOnlyLane(plan, module))
        {
            blocker = $"{module.ModuleId} is not a multiplayer Duty Finder participant lane.";
            return false;
        }

        content = module.ModuleId switch
        {
            DadModuleId.PremadeDuty => localDutyQueueService.Resolve(plan.Request.PremadeDuty, out blocker),
            DadModuleId.DailyMsq => localDutyQueueService.Resolve(plan.Request.DailyMsq, out blocker),
            DadModuleId.Duty when plan.Request.Dungeon?.QueueViaLanParty == true
                => localDutyQueueService.ResolvePremade(plan.Request.Dungeon, out blocker),
            DadModuleId.CustomDuty when plan.Request.CustomDuty != null
                => localDutyQueueService.Resolve(
                    DadEffectivePlanFactory.BuildCustomDutyPlan(plan, module).Plan.Request.PremadeDuty,
                    out blocker),
            DadModuleId.Commendation when plan.Request.Commendation != null
                => localDutyQueueService.Resolve(
                    DadEffectivePlanFactory.BuildCommendationPlan(plan, module).Plan.Request.PremadeDuty,
                    out blocker),
            _ => null,
        };
        return content != null && string.IsNullOrWhiteSpace(blocker);
    }

    public DadLocalDutyQueuePulse ObserveParticipantQueue(
        string runId,
        DadLocalDutyResolvedContent content)
        => localDutyQueueService.ObserveParticipant(runId, content);

    public void ResetParticipantQueueObserver(string runId)
        => localDutyQueueService.ResetParticipantObserver(runId);

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
                AssignedSlotId = DadPlannerSlotRules.FormatSlotId(index + 1),
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
            DadModuleId.DailyMsq => dailyRouletteExecutor,
            DadModuleId.Blunderville => blundervilleExecutor,
            DadModuleId.Mogtome => mogtomeExecutor,
            DadModuleId.Commendation => commendationExecutor,
            DadModuleId.Astrope => astropeExecutor,
            DadModuleId.CustomDuty => customDutyExecutor,
            DadModuleId.Squadron => squadronExecutor,
            DadModuleId.VariantVvd => variantVvdExecutor,
            _ => localDutyExecutor,
        };

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
            PreDutyRepairPolicy = (plan.Request.PreDutyRepairPolicy ?? new DadPreDutyRepairPolicy()).Clone(),
            CompletionActions = plan.Request.CompletionActions?.Clone(),
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
            InviterCharacterKey = plan.InviterCharacterKey,
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
