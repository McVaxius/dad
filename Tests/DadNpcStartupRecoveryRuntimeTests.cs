extern alias DadRuntime;
extern alias DalamudApi;

using System.Reflection;
using System.Runtime.CompilerServices;
using DalamudApi::Dalamud.Plugin.Services;
using Xunit;
using M = DadRuntime::dad.Models;
using S = DadRuntime::dad.Services;

namespace dad.Tests;

public sealed class DadNpcStartupRecoveryRuntimeTests
{
    [Fact]
    public void ColdDutySupportOpensBeforeAgentDataAndRegistersOnlyAfterReadyReadback()
    {
        using var f = new QueueFixture();
        Assert.Equal(S.DadNpcDutyQueuePulseKind.OpenedDutySupport, f.Pulse().Kind);
        Assert.Equal(1, f.Opens);
        Assert.Equal(0, f.Reads);
        Assert.Equal(S.DadNpcDutyQueuePulseKind.Waiting, f.Pulse().Kind);
        Assert.Equal(1, f.Opens);
        f.Agent = true;
        Assert.Equal(S.DadNpcDutyQueuePulseKind.Waiting, f.Pulse().Kind);
        Assert.Equal(0, f.Reads);
        f.Window = true;
        Assert.Equal(S.DadNpcDutyQueuePulseKind.RegisteredForDuty, f.Pulse().Kind);
        Assert.Equal(1, f.Registers);
    }

    [Fact]
    public void DisabledMainCommandWaitsWithoutOpeningAndRecoversWhenEnabled()
    {
        using var f = new QueueFixture { CommandEnabled = false };
        Assert.Equal(S.DadNpcDutyQueuePulseKind.Waiting, f.Pulse().Kind);
        Assert.Equal(0, f.Opens);
        f.CommandEnabled = true;
        Assert.Equal(S.DadNpcDutyQueuePulseKind.OpenedDutySupport, f.Pulse().Kind);
        Set(f.Queue, "nextOpenAttemptUtc", DateTime.MinValue);
        Assert.Equal(S.DadNpcDutyQueuePulseKind.OpenedDutySupport, f.Pulse().Kind);
        Assert.Equal(2, f.Opens);
        Assert.Equal(0, f.Registers);
    }

    [Fact]
    public void AlreadyInitializedWindowDoesNotReopenAndClearsTheWaitBudget()
    {
        using var f = new QueueFixture { Agent = true, Window = true };
        Set(f.Queue, "dutySupportWindowWaitStartedUtc", DateTime.UtcNow.AddMinutes(-2));
        Assert.Equal(S.DadNpcDutyQueuePulseKind.RegisteredForDuty, f.Pulse().Kind);
        Assert.Equal(0, f.Opens);
        Assert.Null(f.Queue.GetType().GetField("dutySupportWindowWaitStartedUtc", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Queue));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UninitializedWindowWaitIsBoundedAndNeverRegisters(bool commandEnabled)
    {
        using var f = new QueueFixture { CommandEnabled = commandEnabled };
        Assert.True(f.Pulse().IsActive);
        Set(f.Queue, "dutySupportWindowWaitStartedUtc", DateTime.UtcNow.AddMinutes(-2));
        var pulse = f.Pulse();
        Assert.Equal(S.DadNpcDutyQueuePulseKind.Failed, pulse.Kind);
        Assert.Contains("initializ", pulse.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, f.Registers);
        Assert.Equal(commandEnabled ? 1 : 0, f.Opens);
    }

    [Fact]
    public void CancelledInitializationCannotMutateForAConflictingOwnerAndNewRunGetsFreshBudget()
    {
        using var f = new QueueFixture();
        Assert.True(f.Pulse().IsActive);
        Assert.False(f.Queue.Pulse("other-run", f.Content).IsActive);
        Assert.Equal(1, f.Opens);
        Set(f.Queue, "dutySupportWindowWaitStartedUtc", DateTime.UtcNow.AddMinutes(-2));
        f.Queue.Cancel("run", S.DadNpcDutyQueueMode.DutySupport, "fixture cancellation");
        Assert.Equal(S.DadNpcDutyQueuePulseKind.OpenedDutySupport,
            f.Queue.Pulse("new-run", f.Content).Kind);
        Assert.Equal(2, f.Opens);
        Assert.Equal(0, f.Registers);
    }

    [Fact]
    public void InitializationClockRollbackFailsWithoutRefreshingBudgetOrOpeningAgain()
    {
        using var f = new QueueFixture();
        Assert.True(f.Pulse().IsActive);
        Set(f.Queue, "dutySupportWindowWaitStartedUtc", DateTime.UtcNow.AddMinutes(1));
        Set(f.Queue, "nextOpenAttemptUtc", DateTime.MinValue);
        var pulse = f.Pulse();
        Assert.Equal(S.DadNpcDutyQueuePulseKind.Failed, pulse.Kind);
        Assert.Contains("clock", pulse.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, f.Opens);
        Assert.Equal(0, f.Registers);
    }

    [Theory]
    [InlineData(M.DadPlannerActivityMode.DutySupport, false)]
    [InlineData(M.DadPlannerActivityMode.DutySupport, true)]
    [InlineData(M.DadPlannerActivityMode.Trust, false)]
    [InlineData(M.DadPlannerActivityMode.Trust, true)]
    public void UnknownOfflineRequestedJobCanWakeButCannotStartUntilFreshValidation(M.DadPlannerActivityMode lane, bool rowTarget)
    {
        var (provider, pool, group, target) = PlannerFixture(lane);
        if (rowTarget) group.Slots[0].LevelSeekTarget = 41;
        var preview = provider.BuildPlannerPreview(pool, provider.BuildOptionsForGroup(group, null), group);
        Assert.True(preview.CanSchedule, string.Join(" | ", preview.StaticBlockers));
        Assert.NotEqual(M.DadReadinessState.Ready, preview.ValidationState);
        Assert.Empty(preview.StaticBlockers);
        Assert.NotEmpty(preview.ReadinessBlockers);
        Assert.Empty(target.JobLevels); // No fabricated job or level evidence.
        var requestPreview = provider.BuildPlannerRunRequestPreview(pool, provider.BuildOptionsForGroup(group, null), selectedGroup: group);
        Assert.True(requestPreview.CanSchedule, requestPreview.BlockedReason);
        Assert.False(requestPreview.CanStart);
        Assert.Equal(26u, Assert.Single(requestPreview.Request!.Orchestration.RequiredRosterCharacters).RequiredJobId);

        // Arrival without proof remains blocking. Preparing another character never supplies proof.
        target.Source = M.DadCharacterSource.LocalRuntime;
        target.Freshness = M.DadSnapshotFreshness.Live;
        target.Readiness = M.DadReadinessState.Ready;
        pool.Characters.RemoveAt(0);
        preview = provider.BuildPlannerPreview(pool, provider.BuildOptionsForGroup(group, null), group);
        Assert.False(preview.CanSchedule);
        Assert.NotEmpty(preview.StaticBlockers);

        target.CurrentJobId = 26;
        target.CurrentLevel = 14;
        target.JobLevels[26] = 14;
        preview = provider.BuildPlannerPreview(pool, provider.BuildOptionsForGroup(group, null), group);
        Assert.False(preview.CanSchedule);
        Assert.Contains(preview.StaticBlockers, blocker => blocker.Contains("requires level 20"));

        target.CurrentLevel = 25;
        target.JobLevels[26] = 25;
        preview = provider.BuildPlannerPreview(pool, provider.BuildOptionsForGroup(group, null), group);
        Assert.Equal(M.DadReadinessState.Ready, preview.ValidationState);
    }

    [Theory]
    [InlineData(40, false)]
    [InlineData(41, true)]
    [InlineData(50, true)]
    public void KnownOfflineRequestedJobUsesExistingSchedulerPreWakeSkip(int level, bool shouldSkip)
    {
        var (provider, pool, group, target) = PlannerFixture(M.DadPlannerActivityMode.DutySupport);
        group.Slots[0].LevelSeekTarget = 41;
        target.JobLevels[26] = level;
        // Another equipped job's level must not determine whether ACN has reached 41.
        target.CurrentJobId = 24;
        target.CurrentLevel = 90;
        target.JobLevels[24] = 90;
        var preview = provider.BuildPlannerRunRequestPreview(pool, provider.BuildOptionsForGroup(group, null), selectedGroup: group);
        // Exercise the production scheduler preflight with the real provider's offline projection.
        // This method has no wake/queue dependencies; a skip must be provable before they are needed.
        var scheduler = (S.DadSchedulerService)RuntimeHelpers.GetUninitializedObject(typeof(S.DadSchedulerService));
        Assert.Equal(shouldSkip, scheduler.EvaluateLevelSeek(group, preview).ShouldSkip);
        Assert.Equal(M.DadCharacterSource.XadbOnly, target.Source);
        Assert.Equal("Other@World", pool.Characters[0].CharacterKey);
        Assert.False(preview.CanStart);
    }

    [Theory]
    [InlineData("already-online")]
    [InlineData("unsupported-wake")]
    [InlineData("wrong-account")]
    [InlineData("invalid-job")]
    [InlineData("limited-job")]
    [InlineData("zero-level")]
    [InlineData("underlevel")]
    [InlineData("unresolved")]
    [InlineData("ambiguous")]
    [InlineData("stale-peer")]
    [InlineData("underlevel-current")]
    public void OfflineDeferralNeverBypassesHardGuards(string failure)
    {
        var (provider, pool, group, target) = PlannerFixture(M.DadPlannerActivityMode.DutySupport);
        switch (failure)
        {
            case "already-online": group.Slots[0].WakePolicy = M.DadSchedulerWakePolicy.AlreadyOnlineOnly; break;
            case "unsupported-wake": group.Slots[0].WakePolicy = M.DadSchedulerWakePolicy.LoadCharacterIfOnline; break;
            case "wrong-account": target.AccountId = "foreign-account"; break;
            case "invalid-job": group.Slots[0].RequiredJobId = 8; break;
            case "limited-job": group.Slots[0].RequiredJobId = 36; break;
            case "zero-level": target.JobLevels[26] = 0; break;
            case "underlevel": target.JobLevels[26] = 14; break;
            case "unresolved": target.Source = M.DadCharacterSource.ManualUnresolved; break;
            case "ambiguous": pool.Characters.Add(target.Clone()); break;
            case "stale-peer": target.Source = M.DadCharacterSource.PeerRuntime; target.Freshness = M.DadSnapshotFreshness.Stale; break;
            case "underlevel-current": target.CurrentJobId = 26; target.CurrentLevel = 14; break;
        }
        var preview = provider.BuildPlannerPreview(pool, provider.BuildOptionsForGroup(group, null), group);
        Assert.False(preview.CanSchedule);
        Assert.NotEqual(M.DadReadinessState.Ready, preview.ValidationState);
    }

    private static (S.DadPresetProviderService Provider, M.DadCharacterPool Pool, M.DadPlannerGroup Group,
        M.DadAcquiredCharacter Target) PlannerFixture(M.DadPlannerActivityMode lane)
    {
        var target = new M.DadAcquiredCharacter
        {
            AccountId = "own-account", CharacterKey = "Runner@World", ContentId = 1001,
            Source = M.DadCharacterSource.XadbOnly, Freshness = M.DadSnapshotFreshness.Unknown,
        };
        var current = new M.DadAcquiredCharacter
        {
            AccountId = "own-account", CharacterKey = "Other@World", ContentId = 2002,
            Source = M.DadCharacterSource.LocalRuntime, Freshness = M.DadSnapshotFreshness.Live,
            Readiness = M.DadReadinessState.Ready, CurrentJobId = 26, CurrentLevel = 90,
            JobLevels = new() { [26] = 90 },
        };
        var group = new M.DadPlannerGroup
        {
            GroupId = "npc-preset", DisplayName = "Synthetic NPC leveling", ActivityMode = lane,
            DutyContentFinderConditionId = 4, DutyDisplayName = "Synthetic duty", DutyExpectedPartySize = 1,
            StopPolicy = new() { Mode = M.DadPlannerStopMode.TargetLevel, TargetLevel = 41,
                TargetCharacterKey = new("Runner@World"), SafetyCap = 200 },
            ConnectedOnly = false, SameDatacenterOnly = false,
            Slots = [new() { SlotId = "Slot1", RequiredAccountKey = new("own-account"),
                RequiredCharacterKey = new("Runner@World"), RequiredJobId = 26,
                WakePolicy = M.DadSchedulerWakePolicy.LaunchIfOffline }],
        };
        var provider = new S.DadPresetProviderService(new S.DadModuleRegistry(), () => [], dutyCatalogProvider: () =>
            [new() { ContentFinderConditionId = 4, TerritoryType = 1036, DutyDisplayName = "Synthetic duty",
                JobLevelRequired = 20, SupportsDutySupport = true, SupportsTrust = true }]);
        return (provider, new() { Characters = [current, target] }, group, target);
    }

    private static void Set(object target, string field, object value)
        => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private sealed class QueueFixture : IDisposable
    {
        internal readonly S.DadNpcDutyQueueService Queue;
        internal readonly S.DadNpcDutyResolvedContent Content = new()
        { ContentFinderConditionId = 4, ContentId = 44, TerritoryType = 1036, DutyName = "Synthetic duty" };
        internal bool Agent, Window, CommandEnabled = true;
        internal int Opens, Reads, Registers;

        internal QueueFixture()
        {
            var ctor = typeof(S.DadNpcDutyQueueService).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(c => c.GetParameters().Length == 3);
            var p = ctor.GetParameters();
            Queue = (S.DadNpcDutyQueueService)ctor.Invoke([
                Proxy(typeof(IPluginLog), (m, _) => m.ReturnType != typeof(void) && m.ReturnType.IsValueType ? Activator.CreateInstance(m.ReturnType) : null),
                Proxy(p[1].ParameterType, (m, _) => m.Name switch
                {
                    "add_DutyCompleted" or "remove_DutyCompleted" => null,
                    "get_IsLoggedIn" or "get_HasLocalPlayer" => true,
                    "get_ContentId" => 1001ul,
                    "get_TerritoryType" => 1u,
                    "Condition" => false,
                    "Addon" => Activator.CreateInstance(m.ReturnType, false, false),
                    _ => throw new InvalidOperationException("Unexpected game call: " + m.Name),
                }),
                Proxy(p[2].ParameterType, (m, args) => m.Name switch
                {
                    "AgentAvailable" => Agent,
                    "AddonReady" => Agent ? Window : throw new InvalidOperationException("Agent data read before initialization"),
                    "MainCommandEnabled" => CommandEnabled,
                    "Open" => Open((uint)args![1]!),
                    "ExpansionCount" => Read(6),
                    "SelectedContentId" => Read(4),
                    "Register" => Register(),
                    _ => throw new InvalidOperationException("Unexpected native call: " + m.Name),
                })]);
        }

        private object? Open(uint id) { Assert.Equal(44u, id); Opens++; return null; }
        private uint Read(uint value) { Assert.True(Agent && Window); Reads++; return value; }
        private object? Register() { Assert.True(Agent && Window); Registers++; return null; }
        internal S.DadNpcDutyQueuePulse Pulse() => Queue.Pulse("run", Content);
        public void Dispose() => Queue.Dispose();
    }

    private static object Proxy(Type type, Func<MethodInfo, object?[]?, object?> handler)
    {
        var result = DispatchProxy.Create(type, typeof(NativeProxy));
        ((NativeProxy)result).Handler = handler;
        return result;
    }

    public class NativeProxy : DispatchProxy
    {
        internal Func<MethodInfo, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    }
}
