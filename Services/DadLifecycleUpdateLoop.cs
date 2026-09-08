namespace dad.Services;

// The plugin and each isolated DAD process use this single lifecycle sequence.
// A focused lab scenario exposes its unbound steps as unexecuted coverage.
internal sealed class DadLifecycleUpdateLoop
{
    public static IReadOnlyList<string> StepOrder { get; } =
    [
        "FlushDebouncedUiWrites",
        "UpdateDtrBar",
        "RuntimeIdentity",
        "Dependencies",
        "KranglerPrivacyLease",
        "DependencyWindow",
        "CharacterIntelligence",
        "VermaxionReservation",
        "Presence",
        "PartyInviteAcceptance",
        "WakeTakeover",
        "RuntimeReadinessEdges",
        "TransportHeartbeat",
        "PlannerDependencyRevision",
        "AutoPartyRelayAttach",
        "AutoPartyEndpoint",
        "AlliancePartyFinder",
        "AutoParty",
        "PendingTakeoverCancellation",
        "PendingEarlyAssignmentCancellation",
        "PendingRewardProbeCancellation",
        "RetainedRosterKnowledge",
        "DeferredRosterPersistence",
        "ClientReconnectWindow",
        "ProfileDirectory",
        "WorkerExecution",
        "SchedulerEnqueue",
        "SchedulerUpdate",
        "Coordinator",
        "AutoPartyRuntimeBindings",
        "CrewToolsDisband",
        "CompletionActions",
        "DutyIpc",
        "DutyIpcRegister",
        "ConfigurationPersistence"
    ];
    private readonly IReadOnlyDictionary<string, Action> steps;
    private readonly Action<string, Action> runStep;
    public IReadOnlyList<string> UnboundSteps { get; }

    public DadLifecycleUpdateLoop(IReadOnlyDictionary<string, Action> steps,
        Action<string, Action> runStep, bool requireAllSteps = false)
    {
        var unknown = steps.Keys.Except(StepOrder, StringComparer.Ordinal).ToArray();
        if (unknown.Length != 0) throw new ArgumentException($"Unknown lifecycle steps: {string.Join(", ", unknown)}");
        UnboundSteps = StepOrder.Except(steps.Keys, StringComparer.Ordinal).ToArray();
        if (requireAllSteps && UnboundSteps.Count != 0)
            throw new ArgumentException($"Missing lifecycle steps: {string.Join(", ", UnboundSteps)}");
        this.steps = steps;
        this.runStep = runStep;
    }

    public void Update()
    {
        foreach (var name in StepOrder)
            if (steps.TryGetValue(name, out var step)) runStep(name, step);
    }
}
