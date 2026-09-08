using dad.Models;

namespace dad.Services;

internal static class DadRuntimeHandlers
{
    internal static void ConfigureAlliance(DadTransportService transport, DadAlliancePartyFinderService alliance)
        => transport.ConfigureAlliancePartyFinderHandlers(alliance.AcceptHubInstruction,
            alliance.AcceptCancellation, alliance.BuildUiSnapshot);

    internal static void ConfigureAutoPartyExecution(DadAutoPartyRelayPump relay, DadAutoPartyService service,
        DadAutoPartyInboundRuntime runtime, DadWorkerExecutionService worker)
    {
        relay.ConfigureFormExecutionHandler(runtime.ExecuteInboundAutoPartyForm);
        relay.ConfigureRegisteredIslandExecutionHandler(runtime.ExecuteInboundAutoPartyOperation);
        service.ConfigureExecutionFacade(new DadAutoPartyRuntimeExecutionFacade(service.Policy,
            runtime.ExecuteInboundAutoPartyOperation, reason => worker.CancelAll(reason)));
    }

    internal static DadAutoPartyInboundAdmissionService CreateAutoPartyAdmission(
        Configuration configuration, DadPresenceService presence, DadTransportService transport,
        DadWakeTakeoverService wake, DadClaimService claims)
        => new(configuration.AutoParty.RegisteredOwnerId, configuration.AutoParty.RegisteredIslandId,
            presence.WorkerSessionId,
            (route, request) => string.Equals(route.WorkerSessionId.Value, presence.WorkerSessionId.Value,
                StringComparison.OrdinalIgnoreCase) ? wake.Handle(request) : transport.SendWakeTakeoverRequest(route.OwnerSnapshot, request),
            (participant, request) => string.Equals(participant.WorkerSessionId.Value, presence.WorkerSessionId.Value,
                StringComparison.OrdinalIgnoreCase) ? presence.HandleWakeRequest(request) : transport.SendWakeRequest(participant, request),
            claims.IssueLease,
            (participant, request) =>
            {
                if (!string.Equals(participant.WorkerSessionId.Value, presence.WorkerSessionId.Value, StringComparison.OrdinalIgnoreCase))
                    return transport.RequestClaim(participant, request);
                var decision = claims.TryClaimLocal(request, participant);
                presence.ApplyClaimState(request.RunId, decision.ClaimState, decision.LeaseState, decision.Lease, decision.Reason);
                return decision;
            }, registrationIdentity: () => (configuration.AutoParty.RegisteredOwnerId, configuration.AutoParty.RegisteredIslandId));

    internal static string SchedulerAdmissionBlocker(Configuration configuration,
        DadSchedulerService scheduler, DadCoordinatorService coordinator, bool disbandActive, DadRunResult visibleRun)
        => DadSchedulerRoutingRules.GetAdmissionBlocker(configuration.RunAsServerDad,
            scheduler.CurrentState.IsActive, scheduler.IsCrewFormationActive, disbandActive,
            visibleRun.Status is DadRunStatus.Queued or DadRunStatus.WaitingForParticipants or DadRunStatus.Running,
            scheduler.HasPendingCancellationCleanup, coordinator.HasPendingCancellationCleanup);

    internal static bool CanUpdateScheduler(Configuration configuration, DadSchedulerService scheduler,
        DadCoordinatorService coordinator, bool disbandActive, DadRunResult visibleRun)
        => configuration.RunAsServerDad && !disbandActive &&
           (scheduler.IsCrewFormationActive || scheduler.CurrentState.IsActive ||
            string.IsNullOrWhiteSpace(SchedulerAdmissionBlocker(configuration, scheduler, coordinator, disbandActive, visibleRun)));

    internal static void UpdateScheduler(Configuration configuration, DadSchedulerService scheduler,
        DadCoordinatorService coordinator, bool disbandActive, Func<DadRunResult> visibleRun,
        Func<string, DadPlannerGroup?> groupResolver, Func<string, DadPlannerRunRequestPreview?> preview,
        Func<DadRunRequest, DadScheduleRepeatBoundary, DadRunResult> start)
    {
        if (CanUpdateScheduler(configuration, scheduler, coordinator, disbandActive, visibleRun()))
            scheduler.UpdateWithScheduleRepeatBoundary(groupResolver, preview, start, visibleRun);
    }

    internal static DadAutoPartyCrewReconciliation ReconcileAutoPartyCrew(
        Configuration configuration, DadRosterCatalogService roster, DadCharacterIntelligenceService intelligence,
        DadPresenceService presence, DadTransportService transport, DateTime observedAt)
    {
        var result = DadAutoPartyCrewSharingRules.Reconcile(configuration.AutoParty, configuration.AutoPartyFleet,
            roster.BuildCuratedPool(intelligence.CurrentPool).Characters, observedAt);
        if (result.Changed) { configuration.AutoParty.StateGeneration++; configuration.Save(); }
        var catalog = roster.BuildPlannerPreviewCatalog(intelligence.CurrentPool);
        var peers = transport.CurrentTransport.KnownParticipants.Where(p => transport.IsWorkerOnline(p.WorkerSessionId)).ToList();
        var routed = DadAutoPartyCrewSharingRules.AttachInboundRoutes(result.Candidates, catalog.Characters,
            presence.BuildLiveSafetySnapshot(), peers, new DateTimeOffset(DateTime.SpecifyKind(observedAt, DateTimeKind.Utc)));
        return new(result.Changed, routed);
    }
    internal static void AttachAutoPartyRelayAfterValidatedBootstrap(
        DadAutoPartyConfiguration configuration, DadAutoPartyEndpointService endpoint,
        DadAutoPartyRelayPump relay, DadAutoPartyService service)
    {
        if (endpoint.RelayStatus.Attached || !configuration.HasImportedBootstrap ||
            (configuration.RegistrationState == DadAutoPartyRegistrationState.BootstrapImported &&
             configuration.BootstrapExpiresAtUtc <= DadClock.UtcNow)) return;
        endpoint.AttachRelayPump(relay, service);
    }
    public static void ConfigureCore(
        DadTransportService transport,
        DadCoordinatorService coordinator,
        DadRosterCatalogService roster,
        DadCharacterIntelligenceService intelligence,
        DadPresenceService presence,
        DadProfileDirectoryService profiles,
        ConfigManager configManager,
        DadWorkerExecutionService worker,
        Action? statusObserved = null,
        Action<dad.Models.DadWorkerExecutionAck>? assignmentObserved = null)
    {
        transport.ConfigureAuthorityHandlers(coordinator.GetLocalResult,
            request => coordinator.StartTasks(request), _ => coordinator.CancelActiveRun());
        transport.ConfigureRosterHandlers(
            () => roster.BuildLocalTransportCatalog(intelligence.CurrentPool, presence.BuildSnapshotCopy()),
            command => roster.RefreshLocalRosterCharacter(command, presence.BuildSnapshotCopy()));
        transport.ConfigureProfileHandlers(profiles.BuildLocalCatalog, configManager.ApplyProfileUpdate);
        ConfigureWorkers(transport, worker, statusObserved, assignmentObserved);
    }

    public static void ConfigureWorkers(
        DadTransportService transport,
        DadWorkerExecutionService worker,
        Action? statusObserved = null,
        Action<dad.Models.DadWorkerExecutionAck>? assignmentObserved = null)
    {
        transport.ConfigureWorkerExecutionHandlers(command =>
        {
            var acknowledgement = worker.Accept(command);
            assignmentObserved?.Invoke(acknowledgement);
            return acknowledgement;
        }, () =>
        {
            statusObserved?.Invoke();
            return worker.GetStatus();
        }, worker.Cancel);
    }
}
