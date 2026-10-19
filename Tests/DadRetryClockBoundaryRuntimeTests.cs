extern alias DadRuntime;
extern alias DalamudApi;

using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using DalamudApi::Dalamud.Plugin.Services;
using Xunit;
using Scheduler = DadRuntime::dad.Services.DadSchedulerService;
using Presence = DadRuntime::dad.Services.DadPresenceService;
using RuntimeState = DadRuntime::dad.Models.DadScheduleRunState;
using RuntimeRun = DadRuntime::dad.Models.DadRunResult;

namespace dad.Tests;

[CollectionDefinition("DAD retry clock boundary", DisableParallelization = true)]
public sealed class DadRetryClockBoundaryCollection;

[Collection("DAD retry clock boundary")]
public sealed class DadRetryClockBoundaryRuntimeTests
{
    [Fact]
    public void RealPresenceSnapshotTakenAfterSchedulerTickMustNotBeRejectedAsFutureHeartbeat()
    {
        var clockType = typeof(Scheduler).Assembly.GetType("dad.DadClock")!;
        var provider = clockType.GetProperty("Provider", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = provider.GetValue(null);
        var completed = new DateTime(2026, 10, 19, 3, 41, 34, DateTimeKind.Utc);
        var clock = new AdvancingClock(new DateTimeOffset(completed.AddSeconds(2)));
        provider.SetValue(null, clock);
        try
        {
            var config = new DadRuntime::dad.Configuration { RunAsServerDad = true, ClientAccountId = "account" };
            var local = new DadRuntime::dad.Models.DadParticipantSnapshot
            {
                WorkerSessionId = new("local-worker"), ActiveCharacterKey = new("Character@World"),
                ManagedAccountKey = new("account"), IsLocalClient = true, IsAvailable = true,
                WorldReadyStable = true, PostArReady = true,
                Character = new() { CharacterKey = "Character@World", ContentId = 1, Readiness = DadRuntime::dad.Models.DadReadinessState.Ready },
            };
            // Use the production presence projection. Only its external game observation
            // is replaced; BuildLiveSafetySnapshot itself stamps a later clock read.
            var presence = (Presence)RuntimeHelpers.GetUninitializedObject(typeof(Presence));
            Set(presence, "configuration", config);
            Set(presence, "configManager", RuntimeHelpers.GetUninitializedObject(typeof(DadRuntime::dad.Services.ConfigManager)));
            Set(presence, "<CurrentParticipant>k__BackingField", local);
            Set(presence, "<WorkerSessionId>k__BackingField", local.WorkerSessionId);
            Set(presence, "<ClientInstanceId>k__BackingField", "local-worker");
            Set(presence, "dependencySnapshotProvider", (Func<DadRuntime::dad.Models.DadDependencySnapshot>)(() => new()));
            var observationType = typeof(Presence).Assembly.GetType("dad.Services.DadLiveRuntimeObservation")!;
            var observation = Activator.CreateInstance(observationType,
                [true, local.Character.Clone(), false, null, "", null])!;
            var observeType = typeof(Func<>).MakeGenericType(observationType);
            Set(presence, "observeLiveRuntime", Expression.Lambda(observeType,
                Expression.Constant(observation, observationType)).Compile());
            var intelligence = (DadRuntime::dad.Services.DadCharacterIntelligenceService)
                RuntimeHelpers.GetUninitializedObject(typeof(DadRuntime::dad.Services.DadCharacterIntelligenceService));
            Set(intelligence, "<CurrentPool>k__BackingField", new DadRuntime::dad.Models.DadCharacterPool());
            var scheduler = new Scheduler(config, null!, null!, intelligence, presence, null!, null!, null!,
                DispatchProxy.Create<IPluginLog, DadScheduleDutyExitRetryRuntimeTests.QuietLog>());
            var source = new RuntimeRun
            {
                RequestId = "failed-run", CompletedAtUtc = completed,
                CurrentExecutorStatus = new() { DutyExitedWithoutCompletion = true, DutyExitPartyTeardownCompleted = true },
                Participants = [local.Clone()],
                Request = new() { Orchestration = new() { RequiredRosterCharacters =
                    [new() { AccountKey = new("account"), CharacterKey = new("Character@World"), ContentId = 1 }] } },
            };
            var state = new RuntimeState
            {
                DutyExitRetryCount = 1, DutyExitRetrySource = source,
                DutyExitRetryPendingSinceUtc = completed,
            };
            var tickStarted = clock.GetUtcNow().UtcDateTime;
            var ready = (bool)typeof(Scheduler).GetMethod("IsDutyExitRetryReady", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(scheduler, [state, tickStarted])!;
            Assert.True(ready, state.Summary);
            Assert.Contains("ready for normal", state.Summary);
        }
        finally { provider.SetValue(null, previous); }
    }

    private static void Set(object instance, string field, object value)
        => instance.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);

    private sealed class AdvancingClock(DateTimeOffset start) : TimeProvider
    {
        private long reads;
        public override DateTimeOffset GetUtcNow() => start.AddTicks(Interlocked.Increment(ref reads));
    }
}
