extern alias DadRuntime;
extern alias DalamudApi;

using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using DalamudApi::Dalamud.Plugin.Services;
using Xunit;
using M = DadRuntime::dad.Models;
using S = DadRuntime::dad.Services;

namespace dad.Tests;

public sealed class DadWorkerAdmissionReadinessRuntimeTests
{
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
