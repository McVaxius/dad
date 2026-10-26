extern alias DadRuntime;
extern alias DalamudApi;

using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using DalamudApi::Dalamud.Plugin.Services;
using Xunit;
using M = DadRuntime::dad.Models;
using S = DadRuntime::dad.Services;

namespace dad.Tests;

[Collection("DAD retry clock boundary")]
public sealed class DadWorkerAdmissionReadinessRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemoteReadinessResendAtOriginalDeadlineRetainsExactCancellationUntilBothAcks(bool replyCompleted)
    {
        using var fixture = new RemoteReadinessFixture();
        var command = fixture.Command;
        fixture.Defer();
        var originalWait = fixture.Waits["worker-1"]!;
        var started = (DateTime)originalWait.GetType().GetProperty("StartedAtUtc")!.GetValue(originalWait)!;
        fixture.Advance(TimeSpan.FromSeconds(2));
        fixture.Local.PostArReady = true;
        // External I/O adapter: the real remote worker admits the immutable resend,
        // while the real transport's operation remains in flight without its reply.
        fixture.Operations[fixture.CommandKey] = new TaskCompletionSource().Task;
        var accepted = fixture.Worker.Accept(command);
        Assert.True(accepted.Accepted, accepted.Summary);
        fixture.Dispatch();
        Assert.Same(command, ((Dictionary<string, M.DadWorkerExecutionCommand>)Get(fixture.Coordinator, "workerCommands"))["worker-1"]);
        Assert.Equal(started, fixture.Waits["worker-1"]!.GetType().GetProperty("StartedAtUtc")!.GetValue(fixture.Waits["worker-1"]));
        Assert.Single(fixture.PendingWorkerCommands.Cast<object>());
        fixture.Operations.TryRemove(fixture.CommandKey, out _); // Delivery finished; acknowledgement may be lost.
        fixture.Advance(command.Plan.Orchestration.WaitPolicy.GetParticipantReadyTimeout() - TimeSpan.FromSeconds(2));
        if (replyCompleted) fixture.Complete(fixture.CommandKey, accepted);
        fixture.Dispatch(); // The production deadline and FinalizeRun path must own cancellation.
        Assert.True(fixture.Coordinator.CurrentResult.IsTerminal);
        Assert.Null(GetNullable(fixture.Coordinator, "activePlan"));
        Assert.Empty(fixture.Waits);
        Assert.Empty((Dictionary<string, M.DadWorkerExecutionCommand>)Get(fixture.Coordinator, "workerCommands"));
        Assert.True(fixture.Cancellations.Contains("exact-run|worker-1"), "A remotely accepted resend was orphaned by readiness expiry.");
        Assert.True(fixture.Cancellations.Contains("exact-run|worker-2"));
        Assert.Equal(2, fixture.Cancellations.Count);
        Assert.DoesNotContain("no command was accepted", fixture.Coordinator.CurrentResult.Summary);
        var pending = fixture.Cancellations["exact-run|worker-1"]!;
        var deadline = pending.GetType().GetProperty("CancellationDeadlineUtc")!.GetValue(pending);
        if (!replyCompleted) fixture.Complete(fixture.CommandKey, accepted); // Late reply cannot resurrect the run.
        fixture.Coordinator.Update();
        fixture.Coordinator.Update();
        Assert.Equal(2, fixture.Cancellations.Count);
        Assert.Same(pending, fixture.Cancellations["exact-run|worker-1"]);
        Assert.Single(fixture.PendingWorkerCommands.Cast<object>());
        Assert.Empty((Dictionary<string, M.DadWorkerExecutionStatus>)Get(fixture.Coordinator, "workerStatuses"));

        fixture.ConnectRemote();
        foreach (var fault in new[] { "wrong-run", "wrong-worker" })
        {
            var run = fault == "wrong-run" ? "stale-run" : command.RunId;
            var worker = new M.DadWorkerSessionId(fault == "wrong-worker" ? "replacement-worker" : "worker-1");
            fixture.Complete($"cancel-run:worker-1:{command.RunId}", new M.DadCancelAckDto
                { RunId = run, WorkerSessionId = worker, Acknowledged = true });
            fixture.Complete($"worker-cancel:worker-1:{command.RunId}", new M.DadWorkerExecutionAck
                { RunId = run, WorkerSessionId = worker, Accepted = true });
            fixture.Advance(TimeSpan.FromSeconds(2));
            fixture.Coordinator.Update();
            Assert.Same(pending, fixture.Cancellations["exact-run|worker-1"]);
            Assert.False((bool)pending.GetType().GetProperty("RunAcknowledged")!.GetValue(pending)!);
            Assert.False((bool)pending.GetType().GetProperty("WorkerAcknowledged")!.GetValue(pending)!);
            Assert.Equal(deadline, pending.GetType().GetProperty("CancellationDeadlineUtc")!.GetValue(pending));
        }

        var cancellation = new M.DadWorkerExecutionCancel { RunId = command.RunId, Reason = "Exact expiry cleanup" };
        var workerAck = fixture.Worker.Cancel(cancellation);
        Assert.True(workerAck.Accepted, workerAck.Summary);
        Assert.True(fixture.Worker.Cancel(cancellation).Accepted);
        Assert.Empty(fixture.PendingWorkerCommands);
        fixture.Complete($"cancel-run:worker-1:{command.RunId}",
            ((S.DadPresenceService)Get(fixture.Worker, "presenceService")).HandleCancelRun(new() { RunId = command.RunId }));
        fixture.Complete($"worker-cancel:worker-1:{command.RunId}", workerAck);
        fixture.Advance(TimeSpan.FromSeconds(2));
        fixture.Coordinator.Update();
        Assert.False(fixture.Cancellations.Contains("exact-run|worker-1"));
        Assert.True(fixture.Cancellations.Contains("exact-run|worker-2")); // Its acknowledgement remains independently required.
        fixture.Coordinator.Update();
        Assert.Single(fixture.Cancellations.Keys.Cast<object>());
        Assert.False(fixture.Worker.Accept(command).Accepted); // Existing exact cancellation tombstone blocks a late replay.
        Assert.Empty(fixture.PendingWorkerCommands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadinessExpiryExcludesOfflineNoQueueAndBuiltButNeverDispatchedLeaders(bool neverDispatched)
    {
        using var fixture = new RemoteReadinessFixture();
        if (neverDispatched)
        {
            // Building a command cannot grant cancellation ownership. Contradictory
            // frozen leader identity fails the real barrier before any dispatch.
            fixture.Command.Plan.LeaderCharacterKey = "Unknown@World";
            fixture.Dispatch();
        }
        else
        {
            fixture.Defer();
            fixture.Advance(TimeSpan.FromSeconds(2));
            fixture.Dispatch(); // No route, no pending operation, no queued resend.
            Assert.Empty(fixture.Operations);
            fixture.Advance(fixture.Command.Plan.Orchestration.WaitPolicy.GetParticipantReadyTimeout());
            fixture.Dispatch();
        }
        Assert.True(fixture.Coordinator.CurrentResult.IsTerminal);
        Assert.False(fixture.Cancellations.Contains("exact-run|worker-1"));
        Assert.True(fixture.Cancellations.Contains("exact-run|worker-2"));
        Assert.Empty(fixture.PendingWorkerCommands);
    }

    [Fact]
    public void BusyExactLeaderDefersWithoutAcceptingOrExecutingItsCommand()
    {
        var (worker, _, command) = Fixture();
        var reply = worker.Accept(command);
        Assert.False(reply.Accepted);
        Assert.True(IsDeferred(reply), reply.Summary);
        Assert.Equal(command.CommandId, reply.Status.CommandId);
        Assert.Equal(M.DadWorkerExecutionState.Idle, reply.Status.State);
        Assert.Empty((Dictionary<string, M.DadWorkerExecutionStatus>)Get(worker, "commandStatuses"));
        Assert.Null(GetNullable(worker, "activeCommand"));
    }

    [Fact]
    public void BusyThenSafeAdmitsOriginalCommandExactlyOnce()
    {
        var (worker, local, command) = Fixture();
        var deferred = worker.Accept(command);
        Assert.True(IsDeferred(deferred), deferred.Summary);
        local.PostArReady = true;
        Assert.True(worker.Accept(command).Accepted);
        Assert.True(worker.Accept(command).Accepted);
        Assert.Single((Dictionary<string, M.DadWorkerExecutionStatus>)Get(worker, "commandStatuses"));
        Assert.Null(GetNullable(worker, "activeCommand"));
        var pending = Get(worker, "pendingCommands");
        var queue = (System.Collections.ICollection)Get(pending, "commands");
        Assert.Single(queue.Cast<object>());
    }

    [Fact]
    public void DeferredCommandStillRejectsImmutablePayloadCollision()
    {
        var (worker, _, command) = Fixture();
        Assert.True(IsDeferred(worker.Accept(command)));
        command.TimeoutSeconds++;
        var collision = worker.Accept(command);
        Assert.False(collision.Accepted);
        Assert.False(IsDeferred(collision));
        Assert.Contains("collision", collision.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RealCoordinatorRetainsAcceptedFollowerAndResumesExactLeaderOnceReady()
    {
        var (worker, local, command) = Fixture();
        var presence = (S.DadPresenceService)Get(worker, "presenceService");
        var transport = (S.DadTransportService)RuntimeHelpers.GetUninitializedObject(typeof(S.DadTransportService));
        var config = new DadRuntime::dad.Configuration { RunAsServerDad = true };
        var log = DispatchProxy.Create<IPluginLog, DadScheduleDutyExitRetryRuntimeTests.QuietLog>();
        var constructor = typeof(S.DadCoordinatorService).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        var coordinator = (S.DadCoordinatorService)constructor.Invoke(constructor.GetParameters().Select(parameter => parameter.Name switch
        {
            "configuration" => (object)config, "presenceService" => presence,
            "workerExecutionService" => worker, "transportService" => transport,
            "claimService" => new S.DadClaimService(), "log" => log, _ => null,
        }).ToArray());
        Set(coordinator, "activePlan", command.Plan);
        Set(coordinator, "activeModuleIndex", 0);
        var participants = (List<M.DadParticipantSnapshot>)Get(coordinator, "activeParticipants");
        participants.AddRange(command.Participants.Select(row => row.Clone()));
        var commands = (Dictionary<string, M.DadWorkerExecutionCommand>)Get(coordinator, "workerCommands");
        commands[local.WorkerSessionId.Value] = command;
        var followerCommand = new M.DadWorkerExecutionCommand { CommandId = "follower-command", RunId = command.RunId };
        commands["worker-2"] = followerCommand;
        var statuses = (Dictionary<string, M.DadWorkerExecutionStatus>)Get(coordinator, "workerStatuses");
        var follower = new M.DadWorkerExecutionStatus { State = M.DadWorkerExecutionState.WaitingForQueue };
        statuses["worker-2"] = follower;
        var dispatch = typeof(S.DadCoordinatorService).GetMethod("DispatchWorkerExecution", BindingFlags.Instance | BindingFlags.NonPublic)!;
        dispatch.Invoke(coordinator, [command.Plan.Modules[0]]);
        Assert.False(coordinator.CurrentResult.IsTerminal);
        Assert.Contains("not accepted or executed", coordinator.CurrentResult.ActiveTaskStatus);
        Assert.Single(statuses);
        Assert.Same(follower, statuses["worker-2"]);
        Assert.Same(command, commands[local.WorkerSessionId.Value]);
        // A framework tick inside the cadence must not send another command or reset the deadline.
        var waits = (System.Collections.IDictionary)Get(coordinator, "workerReadinessWaits");
        var originalWait = waits[local.WorkerSessionId.Value]!;
        dispatch.Invoke(coordinator, [command.Plan.Modules[0]]);
        Assert.Same(originalWait, waits[local.WorkerSessionId.Value]);
        local.PostArReady = true;
        var waitType = originalWait.GetType();
        waits[local.WorkerSessionId.Value] = Activator.CreateInstance(waitType,
            command.CommandId, (DateTime)waitType.GetProperty("StartedAtUtc")!.GetValue(originalWait)!, DateTime.MinValue);
        dispatch.Invoke(coordinator, [command.Plan.Modules[0]]);
        Assert.Equal(2, statuses.Count);
        Assert.Same(follower, statuses["worker-2"]);
        Assert.Equal(M.DadWorkerExecutionState.Accepted, statuses[local.WorkerSessionId.Value].State);
        Assert.Empty(waits);
        dispatch.Invoke(coordinator, [command.Plan.Modules[0]]);
        Assert.Single((Dictionary<string, M.DadWorkerExecutionStatus>)Get(worker, "commandStatuses"));
    }

    [Theory]
    [InlineData("worker")]
    [InlineData("character")]
    [InlineData("content")]
    [InlineData("role")]
    [InlineData("cancelled")]
    public void BusyCannotHideContradictoryOrCancelledAssignment(string fault)
    {
        var (worker, local, command) = Fixture();
        switch (fault)
        {
            case "worker": Set(Get(worker, "presenceService"), "<WorkerSessionId>k__BackingField", new M.DadWorkerSessionId("replacement")); break;
            case "character": local.Character.CharacterKey = "Other@World"; break;
            case "content": local.Character.ContentId++; break;
            case "role": command.Role = M.DadWorkerExecutionRole.Participant; break;
            case "cancelled":
                Get(worker, "cancelledRuns").GetType().GetMethod("Record")!
                    .Invoke(Get(worker, "cancelledRuns"), [command.RunId]);
                break;
        }
        var reply = worker.Accept(command);
        Assert.False(reply.Accepted);
        Assert.False(IsDeferred(reply));
        Assert.Empty((Dictionary<string, M.DadWorkerExecutionStatus>)Get(worker, "commandStatuses"));
    }

    internal static (S.DadWorkerExecutionService Worker, M.DadParticipantSnapshot Local,
        M.DadWorkerExecutionCommand Command) Fixture()
    {
        var local = Participant(1);
        local.IsLocalClient = true;
        local.PostArReady = false;
        var other = Participant(2);
        var orchestration = new M.DadOrchestrationIntent
        {
            RequirePostArReady = true,
            RosterIntent = new() { ExpectedPartySize = 2, RequireExactCharacters = true, RequireRemoteParticipants = true },
            PreferredLeaderCharacterKey = local.ActiveCharacterKey,
            PreferredInviterCharacterKey = local.ActiveCharacterKey,
            RequiredRosterCharacters = [Reference(local), Reference(other)],
            RequiredAccountKeys = [local.ManagedAccountKey, other.ManagedAccountKey],
            RequiredCharacterKeys = [local.ActiveCharacterKey, other.ActiveCharacterKey],
        };
        var command = new M.DadWorkerExecutionCommand
        {
            CommandId = "exact-command", RunId = "exact-run", Role = M.DadWorkerExecutionRole.QueueLeader,
            Plan = new()
            {
                Request = new() { RequestId = "exact-run", Orchestration = orchestration,
                    PremadeDuty = new() { ContentFinderConditionId = 4, ExpectedPartySize = 2 } },
                Orchestration = orchestration, CompositeModuleId = M.DadModuleId.PremadeDuty,
                RequiredParticipantCount = 2, RequiresRemoteParticipants = true, LeaderCharacterKey = local.ActiveCharacterKey.Value,
                InviterCharacterKey = local.ActiveCharacterKey.Value,
                Modules = [new() { ModuleId = M.DadModuleId.PremadeDuty, DisplayName = "Premade duty", ExpectedPartySize = 2, RequiresPeers = true }],
            },
            Participants = [local.Clone(), other.Clone()],
        };
        // Replace only native observation, retaining production presence and worker admission.
        var presence = (S.DadPresenceService)RuntimeHelpers.GetUninitializedObject(typeof(S.DadPresenceService));
        Set(presence, "configuration", new DadRuntime::dad.Configuration { ClientAccountId = "account-1" });
        Set(presence, "configManager", RuntimeHelpers.GetUninitializedObject(typeof(S.ConfigManager)));
        Set(presence, "<CurrentParticipant>k__BackingField", local);
        Set(presence, "<WorkerSessionId>k__BackingField", local.WorkerSessionId);
        Set(presence, "<ClientInstanceId>k__BackingField", local.WorkerSessionId.Value);
        var dependencies = new M.DadDependencySnapshot
        {
            AggregateState = M.DadDependencyState.Ready,
            Entries = M.DadDependencyRules.Requirements.Select(requirement => new M.DadDependencyEntry
                { RequirementId = requirement.RequirementId, State = M.DadDependencyState.Ready }).ToList(),
        };
        local.Dependencies = dependencies;
        Set(presence, "dependencySnapshotProvider", (Func<M.DadDependencySnapshot>)(() => dependencies));
        var observationType = typeof(S.DadPresenceService).Assembly.GetType("dad.Services.DadLiveRuntimeObservation")!;
        var observation = Activator.CreateInstance(observationType, [true, local.Character, false, null, "", null])!;
        Set(presence, "observeLiveRuntime", Expression.Lambda(typeof(Func<>).MakeGenericType(observationType),
            Expression.Constant(observation, observationType)).Compile());
        var queue = (S.DadQueueExecutionService)RuntimeHelpers.GetUninitializedObject(typeof(S.DadQueueExecutionService));
        Set(queue, "gatherPartyExecutor", RuntimeHelpers.GetUninitializedObject(typeof(S.DadGatherPartyExecutor)));
        var log = DispatchProxy.Create<IPluginLog, DadScheduleDutyExitRetryRuntimeTests.QuietLog>();
        return (new S.DadWorkerExecutionService(queue, presence, null!, null!, null!, null!, null!, log), local, command);
    }

    private sealed class RemoteReadinessFixture : IDisposable
    {
        private readonly PropertyInfo clockProvider;
        private readonly object previousClock;
        private readonly TimeProvider clock;
        private readonly S.DadTransportService transport;
        private TcpClient? client;
        private TcpClient? peer;
        private IDisposable? connection;
        public S.DadWorkerExecutionService Worker { get; }
        public M.DadParticipantSnapshot Local { get; }
        public M.DadWorkerExecutionCommand Command { get; }
        public S.DadCoordinatorService Coordinator { get; }
        public IDictionary Waits => (IDictionary)Get(Coordinator, "workerReadinessWaits");
        public IDictionary Cancellations => (IDictionary)Get(Coordinator, "pendingCoordinatorCancellations");
        public ConcurrentDictionary<string, Task> Operations => (ConcurrentDictionary<string, Task>)Get(transport, "operations");
        public ICollection PendingWorkerCommands => (ICollection)Get(Get(Worker, "pendingCommands"), "commands");
        public string CommandKey => $"worker-command:worker-1:{Command.CommandId}";

        public RemoteReadinessFixture()
        {
            var assembly = typeof(S.DadCoordinatorService).Assembly;
            clockProvider = assembly.GetType("dad.DadClock")!.GetProperty("Provider", BindingFlags.Static | BindingFlags.NonPublic)!;
            previousClock = clockProvider.GetValue(null)!;
            clock = (TimeProvider)Activator.CreateInstance(assembly.GetType("dad.Headless.VirtualClock")!, nonPublic: true)!;
            clockProvider.SetValue(null, clock);
            (Worker, Local, Command) = Fixture();
            Command.Plan.Orchestration.WaitPolicy.ParticipantReadyTimeoutSeconds = 10;
            var config = new DadRuntime::dad.Configuration { RunAsServerDad = true, ClientAccountId = "coordinator-account" };
            var log = DispatchProxy.Create<IPluginLog, DadScheduleDutyExitRetryRuntimeTests.QuietLog>();
            object Service(string name) => Activator.CreateInstance(assembly.GetType($"dad.Services.{name}")!,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                name == "InfoProxyPartyInviteGateway"
                    ? [config, null, null, null, null, log, null]
                    : [config, null, null, null, log, null], null)!;
            var invites = Service("InfoProxyPartyInviteGateway");
            var teardown = Service("DadPartyTeardownService");
            // Use the real presence constructor so finalization executes its normal resets.
            var presenceConstructor = typeof(S.DadPresenceService).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
            var presence = (S.DadPresenceService)presenceConstructor.Invoke(presenceConstructor.GetParameters().Select(parameter => parameter.Name switch
            {
                "configuration" => (object)config,
                "partyInviteGateway" => invites, "partyTeardownService" => teardown,
                "requestedJobPreparationGate" => new M.DadRequestedJobPreparationGate(), "log" => log, _ => null,
            }).ToArray());
            Set(presence, "<WorkerSessionId>k__BackingField", new M.DadWorkerSessionId("coordinator"));
            var remotePresence = (S.DadPresenceService)Get(Worker, "presenceService");
            Set(remotePresence, "cancelledAssignmentRuns", new M.DadRunCancellationLedger());
            transport = (S.DadTransportService)RuntimeHelpers.GetUninitializedObject(typeof(S.DadTransportService));
            Set(transport, "configuration", config);
            Set(transport, "presenceService", presence);
            Set(transport, "workerProcessLifetimes", new S.DadWorkerProcessLifetime("coordinator", _ => null));
            Set(transport, "operations", new ConcurrentDictionary<string, Task>());
            foreach (var name in new[] { "completedOperations", "serverSessions" })
            {
                var field = typeof(S.DadTransportService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
                field.SetValue(transport, Activator.CreateInstance(field.FieldType));
            }
            Set(transport, "<CurrentTransport>k__BackingField", new M.DadPeerTransportSnapshot());
            var constructor = typeof(S.DadCoordinatorService).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
            Coordinator = (S.DadCoordinatorService)constructor.Invoke(constructor.GetParameters().Select(parameter => parameter.Name switch
            {
                "configuration" => (object)config, "presenceService" => presence,
                "workerExecutionService" => Worker, "transportService" => transport,
                "partyInviteGateway" => invites, "partyTeardownService" => teardown,
                "claimService" => new S.DadClaimService(), "log" => log, _ => null,
            }).ToArray());
            Set(Coordinator, "activePlan", Command.Plan);
            Set(Coordinator, "activeModuleIndex", 0);
            Coordinator.CurrentResult.RequestId = Command.RunId;
            Coordinator.CurrentResult.Request = Command.Plan.Request;
            var participants = (List<M.DadParticipantSnapshot>)Get(Coordinator, "activeParticipants");
            participants.AddRange(Command.Participants.Select(row => { var copy = row.Clone(); copy.IsLocalClient = false; return copy; }));
            var commands = (Dictionary<string, M.DadWorkerExecutionCommand>)Get(Coordinator, "workerCommands");
            commands["worker-1"] = Command;
            commands["worker-2"] = new() { CommandId = "follower-command", RunId = Command.RunId };
            ((Dictionary<string, M.DadWorkerExecutionStatus>)Get(Coordinator, "workerStatuses"))["worker-2"] =
                new() { RunId = Command.RunId, CommandId = "follower-command", WorkerSessionId = new("worker-2"),
                    State = M.DadWorkerExecutionState.WaitingForQueue };
        }

        public void Defer()
        {
            var deferred = Worker.Accept(Command);
            Assert.True(IsDeferred(deferred), deferred.Summary);
            Complete(CommandKey, deferred);
            Dispatch();
            Assert.Single(Waits.Keys.Cast<object>());
            Assert.False(Coordinator.CurrentResult.IsTerminal);
        }

        public void Dispatch() => typeof(S.DadCoordinatorService).GetMethod("DispatchWorkerExecution", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Coordinator, [Command.Plan.Modules[0]]);

        public void Advance(TimeSpan amount) => clock.GetType().GetProperty("Now")!.SetValue(clock, clock.GetUtcNow() + amount);

        public void Complete(string key, object reply)
        {
            var completed = Get(transport, "completedOperations");
            var value = Activator.CreateInstance(completed.GetType().GetGenericArguments()[1], nonPublic: true)!;
            var json = typeof(S.DadTransportService).Assembly.GetType("dad.Services.DadIpcJson")!.GetMethod("Serialize")!
                .MakeGenericMethod(reply.GetType()).Invoke(null, [reply]);
            value.GetType().GetProperty("PayloadJson")!.SetValue(value, json);
            value.GetType().GetProperty("CompletedAtUtc")!.SetValue(value, clock.GetUtcNow().UtcDateTime);
            completed.GetType().GetProperty("Item")!.SetValue(completed, value, [key]);
        }

        public void ConnectRemote()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                client = new TcpClient();
                client.Connect((IPEndPoint)listener.LocalEndpoint);
                peer = listener.AcceptTcpClient();
            }
            finally { listener.Stop(); }
            var type = typeof(S.DadTransportService).GetNestedType("DadHubConnection", BindingFlags.NonPublic)!;
            var role = typeof(S.DadTransportService).Assembly.GetType("dad.Services.DadHubHandshakeRole")!;
            var route = Activator.CreateInstance(type, [client, CancellationToken.None, Enum.ToObject(role, 0)])!;
            connection = (IDisposable)route;
            type.GetProperty("WorkerSessionId")!.SetValue(route, Local.WorkerSessionId);
            type.GetMethod("MarkHandshakeReady")!.Invoke(route, null);
            var sessions = Get(transport, "serverSessions");
            sessions.GetType().GetMethod("Register")!.Invoke(sessions, [Local.WorkerSessionId, route]);
            Assert.True(transport.IsWorkerOnline(Local.WorkerSessionId));
        }

        public void Dispose()
        {
            connection?.Dispose();
            client?.Dispose();
            peer?.Dispose();
            clockProvider.SetValue(null, previousClock);
        }
    }

    private static M.DadParticipantSnapshot Participant(int index) => new()
    {
        WorkerSessionId = new($"worker-{index}"), ManagedAccountKey = new($"account-{index}"),
        ActiveCharacterKey = new($"Character{index}@World"), AssignedSlotId = $"Slot{index}",
        IsAvailable = true, IsEligibleForRun = true, PostArReady = true, WorldReadyStable = true,
        State = M.DadParticipantState.Ready,
        Character = new() { CharacterKey = $"Character{index}@World", ContentId = (ulong)index,
            Readiness = M.DadReadinessState.Ready },
    };
    private static M.DadRosterCharacterRef Reference(M.DadParticipantSnapshot row) => new()
        { AccountKey = row.ManagedAccountKey, CharacterKey = row.ActiveCharacterKey, ContentId = row.Character.ContentId };
    private static bool IsDeferred(M.DadWorkerExecutionAck reply)
        => reply.GetType().GetProperty("RuntimeReadinessDeferred")?.GetValue(reply) is true;
    internal static object Get(object instance, string field) => GetNullable(instance, field)!;
    internal static object? GetNullable(object instance, string field)
        => instance.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance);
    internal static void Set(object instance, string field, object value)
        => instance.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
}
