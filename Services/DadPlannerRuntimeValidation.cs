using dad.Models;

namespace dad.Services;

internal static class DadPlannerRuntimeValidation
{
    internal static DadPlannerRunRequestPreview Apply(
        DadPlannerRunRequestPreview requestPreview,
        DadCharacterPool pool,
        DadPlannerGroup? selectedGroup,
        DadPlannerService planner, DadQueueExecutionService queue,
        Func<DadParticipantSnapshot> liveSnapshot,
        Action<DadRunRequest, DadParticipantSnapshot>? provenance = null)
    {
        if (requestPreview.Request == null)
        {
            RefreshPlannerContractPreview(requestPreview);
            return requestPreview;
        }

        var previewOnly = string.Equals(requestPreview.Request.RequestedBy, "planner-preview", StringComparison.OrdinalIgnoreCase);
        if (!previewOnly)
        {
            var requireLiveReadiness = requestPreview.CanStart || !requestPreview.CanSchedule;
            var allowWakeableCoordinatorLeader = HasWakeableEffectiveCoordinatorSlot(selectedGroup, requestPreview);
            var liveLocalRuntimeTruth = liveSnapshot();
            provenance?.Invoke(requestPreview.Request, liveLocalRuntimeTruth);
            var plan = planner.BuildPlan(
                requestPreview.Request,
                pool,
                out var rejectionReason,
                requireLiveReadiness,
                allowWakeableCoordinatorLeader,
                liveLocalRuntimeTruth);
            if (plan == null)
            {
                var relaxedPlanBuilt = requireLiveReadiness &&
                                       planner.BuildPlan(
                                           requestPreview.Request,
                                           pool,
                                           out _,
                                           requireLiveReadiness: false,
                                           allowWakeableCoordinatorLeader: allowWakeableCoordinatorLeader,
                                           liveLocalRuntimeTruth: liveLocalRuntimeTruth) != null;
                if (DadPlannerValidationRules.IsStrictRuntimeOnlyFailure(
                        requireLiveReadiness,
                        strictPlanBuilt: false,
                        relaxedPlanBuilt))
                {
                    MergePlannerReadinessBlocker(requestPreview, rejectionReason);
                }
                else
                {
                    MergePlannerPreviewBlocker(requestPreview, rejectionReason);
                }
            }
            else if (!requestPreview.Request.Orchestration.AutoPartyFormationOnly &&
                     DadFullPartyExecutionRules.IsQueueAuthorityLocal(plan, liveLocalRuntimeTruth))
            {
                var runtimeStatus = queue.PreviewModuleStart(plan);
                MergePlannerRuntimeStatus(requestPreview, runtimeStatus);
            }
        }

        RefreshPlannerContractPreview(requestPreview);
        return requestPreview;
    }

    private static bool HasWakeableEffectiveCoordinatorSlot(
        DadPlannerGroup? selectedGroup,
        DadPlannerRunRequestPreview requestPreview)
    {
        if (selectedGroup == null)
            return false;

        var projected = DadEffectivePlannerGroupProjection.Project(
            selectedGroup,
            requestPreview.PlannerPreview.ActivityMode,
            requestPreview.ExpectedPartySize);
        var bound = DadEffectivePlannerGroupProjection.BindResolvedSchedulerSlots(
            projected,
            requestPreview.PlannerPreview.SelectedCharacters);
        var slotOne = DadPlannerSlotRules.GetPrimaryRows(bound.Slots)
            .FirstOrDefault(static slot =>
                string.Equals(slot.SlotId, DadPlannerSlotRules.LeaderSlotId, StringComparison.OrdinalIgnoreCase));
        return slotOne?.WakePolicy == DadSchedulerWakePolicy.LaunchIfOffline;
    }

    private static void MergePlannerRuntimeStatus(DadPlannerRunRequestPreview requestPreview, DadModuleExecutionStatusDto runtimeStatus)
    {
        MergePlannerModuleBlockers(requestPreview.ModuleBlockers, runtimeStatus.Blockers);

        if (!runtimeStatus.CanStart)
        {
            var decision = DadPlannerValidationRules.EvaluateModuleRuntimeStatus(
                requestPreview.CanSchedule,
                runtimeStatus);
            var reason = decision.Reason;
            if (decision.IsTransientRuntimeReadiness)
            {
                MergePlannerReadinessBlocker(requestPreview, reason);
                return;
            }

            requestPreview.CanStart = false;
            requestPreview.CanSchedule = decision.CanSchedule;
            if (!string.IsNullOrWhiteSpace(reason) &&
                requestPreview.ModuleBlockers.All(existing =>
                    !string.Equals(existing.Summary, reason, StringComparison.OrdinalIgnoreCase)))
            {
                requestPreview.ModuleBlockers.Add(new DadModuleBlockerDto
                {
                    ModuleId = requestPreview.ModuleId,
                    Capability = "PlannerRuntime",
                    Severity = DadModuleBlockerSeverity.Blocked,
                    Summary = reason,
                });
            }

            requestPreview.BlockedReason = DadPlannerValidationRules.BuildBlockedReason(requestPreview);
            requestPreview.StatusSummary = $"Planner request blocked by module runtime: {requestPreview.BlockedReason}";

            return;
        }

        if (requestPreview.CanStart && !string.IsNullOrWhiteSpace(runtimeStatus.Summary))
            requestPreview.StatusSummary = $"Planner request ready to start. {runtimeStatus.Summary}";
    }

    private static void MergePlannerPreviewBlocker(DadPlannerRunRequestPreview requestPreview, string blocker)
    {
        if (string.IsNullOrWhiteSpace(blocker))
            return;

        requestPreview.CanSchedule = false;
        requestPreview.CanStart = false;
        AddPlannerValidationBlocker(requestPreview.StaticBlockers, blocker);

        if (requestPreview.ModuleBlockers.All(existing => !string.Equals(existing.Summary, blocker, StringComparison.OrdinalIgnoreCase)))
        {
            requestPreview.ModuleBlockers.Add(new DadModuleBlockerDto
            {
                ModuleId = requestPreview.ModuleId,
                Capability = "Planner",
                Severity = DadModuleBlockerSeverity.Blocked,
                Summary = blocker,
            });
        }

        requestPreview.BlockedReason = DadPlannerValidationRules.BuildBlockedReason(requestPreview);
        requestPreview.StatusSummary = $"Planner request blocked: {requestPreview.BlockedReason}";
    }

    private static void MergePlannerReadinessBlocker(DadPlannerRunRequestPreview requestPreview, string blocker)
    {
        if (string.IsNullOrWhiteSpace(blocker))
            return;

        requestPreview.CanStart = false;
        AddPlannerValidationBlocker(requestPreview.ReadinessBlockers, blocker);
        requestPreview.ReadinessSummary = $"Waiting for refreshed strict-runtime readiness: {blocker}";
        if (requestPreview.ModuleBlockers.All(existing => !string.Equals(existing.Summary, blocker, StringComparison.OrdinalIgnoreCase)))
        {
            requestPreview.ModuleBlockers.Add(new DadModuleBlockerDto
            {
                ModuleId = requestPreview.ModuleId,
                Capability = "PlannerRuntimeReadiness",
                Severity = DadModuleBlockerSeverity.Blocked,
                Summary = blocker,
            });
        }

        requestPreview.BlockedReason = DadPlannerValidationRules.BuildBlockedReason(requestPreview);
        requestPreview.StatusSummary = requestPreview.CanSchedule
            ? $"Planner request remains schedulable while runtime truth refreshes: {requestPreview.BlockedReason}"
            : $"Planner request remains terminally blocked while retaining readiness detail: {requestPreview.BlockedReason}";
    }

    private static void MergePlannerModuleBlockers(List<DadModuleBlockerDto> target, IReadOnlyList<DadModuleBlockerDto> source)
    {
        foreach (var blocker in source)
        {
            if (target.Any(existing =>
                    existing.ModuleId == blocker.ModuleId &&
                    string.Equals(existing.Capability, blocker.Capability, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.Summary, blocker.Summary, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            target.Add(blocker.Clone());
        }
    }

    private static void AddPlannerValidationBlocker(List<string> blockers, string blocker)
    {
        if (string.IsNullOrWhiteSpace(blocker) ||
            blockers.Any(existing => string.Equals(existing, blocker, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        blockers.Add(blocker.Trim());
    }

    private static void RefreshPlannerContractPreview(DadPlannerRunRequestPreview requestPreview)
    {
        requestPreview.BlockedReason = requestPreview.CanStart
            ? string.Empty
            : DadPlannerValidationRules.BuildBlockedReason(requestPreview);
        requestPreview.StopPolicy = requestPreview.Request?.StopPolicy.Clone()
                                    ?? requestPreview.PlannerPreview.StopPolicy.Clone();
        requestPreview.ContractPreview.StopPolicy = requestPreview.StopPolicy.Clone();
        requestPreview.ContractPreview.CanStart = requestPreview.CanStart;
        requestPreview.ContractPreview.CanSchedule = requestPreview.CanSchedule;
        requestPreview.ContractPreview.ReadinessSummary = requestPreview.ReadinessSummary;
        requestPreview.ContractPreview.StaticBlockers = [..requestPreview.StaticBlockers];
        requestPreview.ContractPreview.ReadinessBlockers = [..requestPreview.ReadinessBlockers];
        requestPreview.ContractPreview.ScheduleBlockers = [..requestPreview.ScheduleBlockers];
        requestPreview.ContractPreview.Startability = BuildPlannerStartabilityLabel(requestPreview);
        requestPreview.ContractPreview.Blockers = BuildPlannerContractBlockers(requestPreview);
        requestPreview.ContractPreviewJson = DadIpcJson.Serialize(requestPreview.ContractPreview);
    }

    private static string BuildPlannerStartabilityLabel(DadPlannerRunRequestPreview requestPreview)
        => requestPreview.CanStart
            ? "Startable"
            : requestPreview.CanSchedule
                ? "Schedulable"
            : string.Equals(requestPreview.Request?.RequestedBy, "planner-preview", StringComparison.OrdinalIgnoreCase)
                ? "PreviewOnly"
                : "Blocked";

    private static List<string> BuildPlannerContractBlockers(DadPlannerRunRequestPreview requestPreview)
    {
        var blockers = new List<string>();
        if (!string.IsNullOrWhiteSpace(requestPreview.BlockedReason))
            blockers.Add(requestPreview.BlockedReason);

        blockers.AddRange(requestPreview.PlannerPreview.Blockers);
        blockers.AddRange(requestPreview.ModuleBlockers
            .Select(static blocker => blocker.Summary)
            .Where(static summary => !string.IsNullOrWhiteSpace(summary)));
        return blockers
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

}
