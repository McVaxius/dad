extern alias DadRuntime;
extern alias DalamudApi;

using System.Reflection;
using DalamudApi::Dalamud.Plugin.Services;
using Xunit;
using M = DadRuntime::dad.Models;
using Presence = DadRuntime::dad.Services.DadPresenceService;

namespace dad.Tests;

[Collection("DAD retry clock boundary")]
public sealed class DadTeardownIdentityRecoveryRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingIdentityDefersWithoutNativeActionsAndExactRestorationCompletes(bool leader)
    {
        using var f = new Fixture(leader);
        f.HideIdentity();
        var waiting = f.Pulse();
        Assert.True(waiting.Deferred, waiting.Summary);
        Assert.False(waiting.Success);
        Assert.Empty(waiting.FailureReason);
        Assert.Empty(waiting.AuthoritativePartyMembers);
        Assert.Empty(f.Actions);
        f.Clock.Now += TimeSpan.FromSeconds(3);
        f.RestoreIdentity();
        var complete = f.Pulse();
        if (leader)
        {
            Assert.True(complete.Deferred, complete.Summary);
            Assert.Single(f.Actions);
            f.Pulse(); // Approve only the new, owned disband confirmation.
            f.Pulse(); // Start sustained solo confirmation.
            f.Clock.Now += TimeSpan.FromSeconds(1);
            complete = f.Pulse();
            Assert.Equal(2, f.Actions.Count);
        }
        Assert.True(complete.Success, complete.Summary);
        Assert.False(complete.Deferred);
        if (!leader) Assert.Empty(f.Actions); // Native source already proves follower solo.
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PartialIdentityAlsoWaitsButDoesNotCountAsTerminalTeardown(bool missingCharacter)
    {
        using var f = new Fixture(false);
        if (missingCharacter) f.Presence.CurrentParticipant.ActiveCharacterKey = new("");
        else f.Presence.CurrentParticipant.Character.ContentId = 0;
        var result = f.Pulse();
        Assert.True(result.Deferred, result.Summary);
        Assert.Empty(f.Actions);
        var terminal = new Dictionary<string, M.DadRunStepResultDto>();
        if (!result.Deferred) terminal["Slot2"] = result;
        Assert.False(DadRuntime::dad.Services.DadExactPartyTeardownRules.Aggregate([f.Instruction], terminal).Complete);
    }

    [Fact]
    public void ChangedTeardownPayloadCannotRefreshOrReuseTheWait()
    {
        using var f = new Fixture(false);
        f.HideIdentity();
        Assert.True(f.Pulse().Deferred);
        f.Instruction.Summary = "changed payload";
        var result = f.Pulse();
        Assert.False(result.Deferred);
        Assert.Contains("changed", result.FailureReason);
        Assert.Empty(f.Actions);
    }

    [Fact]
    public void CancellationClearsPendingWaitAndOldRunCannotResume()
    {
        using var f = new Fixture(false);
        f.HideIdentity();
        Assert.True(f.Pulse().Deferred);
        var ack = f.Presence.HandleCancelRun(new() { RunId = "run", Reason = "operator cancellation" });
        Assert.True(ack.Acknowledged);
        f.RestoreIdentity();
        var result = f.Pulse();
        Assert.False(result.Deferred);
        Assert.False(result.Success);
        Assert.Contains("different run", result.FailureReason);
        Assert.Null(typeof(Presence).GetField("partyTeardownIdentityWaitStartedUtc", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f.Presence));
        Assert.Empty(f.Actions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedMissingIdentityDoesNotResetTheSixtySecondDeadline(bool restoreTooLate)
    {
        using var f = new Fixture(false);
        f.HideIdentity();
        Assert.True(f.Pulse().Deferred);
        f.Clock.Now += TimeSpan.FromSeconds(59);
        Assert.True(f.Pulse().Deferred);
        f.Clock.Now += TimeSpan.FromSeconds(1);
        if (restoreTooLate) f.RestoreIdentity();
        var failed = f.Pulse();
        Assert.False(failed.Deferred);
        Assert.False(failed.Success);
        Assert.True(failed.TimedOut, failed.Summary);
        Assert.Empty(f.Actions);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("worker")]
    [InlineData("slot")]
    [InlineData("authority")]
    [InlineData("stored-character")]
    [InlineData("stored-content")]
    [InlineData("observed-character")]
    [InlineData("observed-content")]
    [InlineData("run")]
    public void ContradictionsNeverBecomeIdentityWaits(string contradiction)
    {
        using var f = new Fixture(false);
        f.HideIdentity();
        switch (contradiction)
        {
            case "account": f.Presence.CurrentParticipant.ManagedAccountKey = new("foreign"); break;
            case "worker": Set(f.Presence, "<WorkerSessionId>k__BackingField", new M.DadWorkerSessionId("foreign")); break;
            case "slot": Set(f.Presence, "assignedSlotId", "Slot3"); break;
            case "authority": Set(f.Presence, "currentAuthorityWorkerSessionId", new M.DadWorkerSessionId("foreign")); break;
            case "stored-character": Set(f.Presence, "requiredCharacterKey", new M.DadCharacterKey("Foreign@World")); break;
            case "stored-content": Set(f.Presence, "requiredContentId", 99UL); break;
            case "observed-character": f.Presence.CurrentParticipant.ActiveCharacterKey = new("Foreign@World"); break;
            case "observed-content": f.Presence.CurrentParticipant.Character.ContentId = 99; break;
            case "run": Set(f.Presence, "currentRunId", "other-run"); break;
        }
        var failed = f.Pulse();
        Assert.False(failed.Success);
        Assert.False(failed.Deferred);
        Assert.NotEmpty(failed.FailureReason);
        Assert.Empty(f.Actions);
    }

    [Fact]
    public void OrdinaryJoinStillFailsWhenItsCharacterIsUnavailable()
    {
        using var f = new Fixture(false);
        f.HideIdentity();
        f.Instruction.InstructionKind = M.DadAssemblyInstructionKind.JoinParty;
        var result = f.Pulse();
        Assert.False(result.Deferred);
        Assert.False(result.Success);
        Assert.NotEmpty(result.FailureReason);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly PropertyInfo clockProperty;
        private readonly object? previousClock;
        private readonly ulong content;
        private readonly string character;
        public Presence Presence { get; }
        public M.DadAssemblyInstructionDto Instruction { get; }
        public ManualClock Clock { get; } = new();
        public List<string> Actions { get; } = [];

        public Fixture(bool leader)
        {
            clockProperty = typeof(Presence).Assembly.GetType("dad.DadClock")!
                .GetProperty("Provider", BindingFlags.NonPublic | BindingFlags.Static)!;
            previousClock = clockProperty.GetValue(null);
            clockProperty.SetValue(null, Clock);
            content = leader ? 1UL : 2UL;
            character = leader ? "Leader@World" : "Follower@World";
            var worker = new M.DadWorkerSessionId(leader ? "worker-1" : "worker-2");
            var account = new M.DadAccountKey(leader ? "account-1" : "account-2");
            var config = new DadRuntime::dad.Configuration();
            var log = DispatchProxy.Create<IPluginLog, DadScheduleDutyExitRetryRuntimeTests.QuietLog>();
            var player = DispatchProxy.Create<IPlayerState, ServiceProxy>();
            ((ServiceProxy)(object)player).Handler = method => method.Name == "get_ContentId" ? content : null;
            var condition = DispatchProxy.Create<ICondition, ServiceProxy>();
            ((ServiceProxy)(object)condition).Handler = _ => false;
            var framework = DispatchProxy.Create<IFramework, ServiceProxy>();
            ((ServiceProxy)(object)framework).Handler = _ => true;
            var native = New("dad.Headless.VirtualPartyGateway", content, (Action<string>)Actions.Add);
            var teardown = New("dad.Services.DadPartyTeardownService", config, null!, player, condition, log, native);
            var gateway = New("dad.Services.InfoProxyPartyInviteGateway", config, framework, player, null!, condition, log, native);
            var ctor = typeof(Presence).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
            Presence = (Presence)ctor.Invoke(ctor.GetParameters().Select(p => p.Name switch
            {
                "configuration" => (object)config,
                "partyInviteGateway" => gateway,
                "partyTeardownService" => teardown,
                "requestedJobPreparationGate" => Activator.CreateInstance(p.ParameterType),
                "log" => log,
                _ => null,
            }).ToArray());
            Set(Presence, "<WorkerSessionId>k__BackingField", worker);
            Set(Presence, "currentRunId", "run");
            Set(Presence, "currentAuthorityWorkerSessionId", new M.DadWorkerSessionId("worker-1"));
            Set(Presence, "requiredAccountKey", account);
            Set(Presence, "requiredCharacterKey", new M.DadCharacterKey(character));
            Set(Presence, "requiredContentId", content);
            Set(Presence, "assignedSlotId", leader ? "Slot1" : "Slot2");
            Presence.CurrentParticipant.ManagedAccountKey = account;
            Instruction = new()
            {
                RunId = "run", ModuleId = M.DadModuleId.DailyMsq,
                SlotId = leader ? "Slot1" : "Slot2", RequiredCharacterKey = new(character),
                AuthorityWorkerSessionId = new("worker-1"),
                InstructionKind = leader ? M.DadAssemblyInstructionKind.DisbandParty : M.DadAssemblyInstructionKind.LeaveParty,
                FrozenInviter = new()
                {
                    RunId = "run", WorkerSessionId = new("worker-1"), AccountKey = new("account-1"),
                    CharacterKey = new("Leader@World"), ContentId = 1, CharacterName = "Leader", WorldId = 1,
                },
                InviteTargets = [new()
                {
                    RunId = "run", ModuleId = M.DadModuleId.DailyMsq, SlotId = "Slot2", WorkerSessionId = new("worker-2"),
                    AccountKey = new("account-2"), CharacterKey = new("Follower@World"),
                    ContentId = 2, CharacterName = "Follower", WorldId = 1,
                }],
            };
            RestoreIdentity();
        }

        public M.DadRunStepResultDto Pulse() => Presence.HandleAssemblyInstruction(Instruction);
        public void HideIdentity() { Presence.CurrentParticipant.ActiveCharacterKey = new(""); Presence.CurrentParticipant.Character.ContentId = 0; }
        public void RestoreIdentity() { Presence.CurrentParticipant.ActiveCharacterKey = new(character); Presence.CurrentParticipant.Character.ContentId = content; }
        public void Dispose() => clockProperty.SetValue(null, previousClock);
    }

    public class ServiceProxy : DispatchProxy
    {
        public Func<MethodInfo, object?> Handler { get; set; } = _ => null;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!);
    }
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static object New(string name, params object[] args)
        => Activator.CreateInstance(typeof(Presence).Assembly.GetType(name)!, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, args, null)!;
    private static void Set(object target, string field, object value)
        => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
}
