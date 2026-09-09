using dad.Models;

namespace dad.Services;

internal static class DadLevelingRuntime
{
    public static DadPlannerRunRequestPreview Preview(DadLevelingChildBuild build)
    {
        if (build.PlannerPreview != null) return build.PlannerPreview;
        var compilation = build.Compilation ?? new DadLevelingCompilation();
        var complete = compilation.Status == DadLevelingCompilationStatus.Complete;
        var contract = new DadPlannerRequestContractPreview
        {
            Startability = complete ? "Complete" : "Blocked", CanStart = complete, CanSchedule = complete,
            StaticBlockers = complete ? [] : [compilation.Summary], Blockers = complete ? [] : [compilation.Summary],
        };
        return new DadPlannerRunRequestPreview
        {
            CanStart = complete, CanSchedule = complete, StatusSummary = compilation.Summary,
            ReadinessSummary = complete ? compilation.Summary : string.Empty,
            BlockedReason = complete ? string.Empty : compilation.Summary,
            StaticBlockers = complete ? [] : [compilation.Summary],
            ContractPreview = contract, ContractPreviewJson = DadIpcJson.Serialize(contract),
            ModuleBlockers = complete ? [] : [new DadModuleBlockerDto
            {
                ModuleId = DadModuleId.None, Capability = "PlannerGroup", Severity = DadModuleBlockerSeverity.Blocked,
                Summary = compilation.Summary,
            }],
        };
    }

    public static DadLevelingCompilation Compile(DadPlannerGroup source, DadCharacterPool pool, int iteration,
        IReadOnlyList<DadLevelingJobDescriptor> jobs, DadPresetProviderService presets)
    {
        var duties = (source.LevelingMode?.DutyThresholds ?? [])
            .Where(static threshold => threshold != null && threshold.ContentFinderConditionId > 0)
            .Select(threshold => presets.GetPlannerDutyOption(threshold.ContentFinderConditionId))
            .Where(static duty => duty != null).Select(static duty => duty!)
            .DistinctBy(static duty => duty.ContentFinderConditionId).ToList();
        return DadLevelingModeCompiler.Compile(source, pool, jobs, duties, iteration);
    }

    public static DadLevelingChildBuild BuildChild(DadPlannerGroup source, DadCharacterPool pool, int iteration,
        IReadOnlyList<DadLevelingJobDescriptor> jobs, DadPresetProviderService presets, DadCompletionActions completion,
        Func<DadPlannerRunRequestPreview, DadCharacterPool, DadPlannerGroup, DadPlannerRunRequestPreview> validate)
    {
        var compilation = Compile(source, pool, iteration, jobs, presets);
        var build = new DadLevelingChildBuild { Compilation = compilation };
        if (!compilation.CanStartChild || compilation.ChildGroup == null) return build;
        var child = compilation.ChildGroup;
        var options = presets.BuildOptionsForGroup(child, null);
        var preview = presets.BuildPlannerPreview(pool, options, child);
        var requestPreview = presets.BuildPlannerRunRequestPreview(pool, options,
            requestId: compilation.ChildRequestId, requestedAtUtc: DadClock.UtcNow,
            plannerPreviewOverride: preview, selectedGroup: child, completionFallback: completion);
        if (requestPreview.Request is { } request)
        {
            request.RefreshRecommendedGear = child.LevelingMode.RefreshRecommendedGear;
            requestPreview.RequestJson = DadIpcJson.Serialize(request);
        }
        build.PlannerPreview = validate(requestPreview, pool, child);
        return build;
    }
}
