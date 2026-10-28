extern alias DadRuntime;
extern alias DalamudApi;

using System.Reflection;
using DalamudApi::Dalamud.Plugin.Services;
using Xunit;
using M = DadRuntime::dad.Models;

namespace dad.Tests;

[Collection("DAD retry clock boundary")]
public sealed class DadPartyInviteReassemblyRuntimeTests
{
    [Fact]
    public void PassiveDebugObservationNeverArmsOrAcceptsAnInvitation()
    {
        using var f = new Fixture(debug: true);
        f.Invite(102);
        f.Pulse();
        Assert.Equal(0, f.Accepts);
        Assert.Contains("present=True", f.DiagnosticReport());
        Assert.Contains("expectedArmed=False", f.DiagnosticReport());
        Assert.DoesNotContain("Synthetic Leader", f.DiagnosticReport());
        Assert.False(f.Arm());
        f.Pulse();
        Assert.Equal(1, f.Accepts);
        Assert.Contains("respond request=same-run", f.DiagnosticReport());
        f.SetMembers(100, 200);
        f.Pulse();
        Assert.Contains("membership-confirmed", f.DiagnosticReport());
    }

    [Theory]
    [InlineData("Cross", 100ul)]
    [InlineData("Serial", 100ul)]
    [InlineData("Reverse", 300ul)]
    public void GatewayUsesScopedCrossWorldPolicyAndRealSerialMembership(string mode, ulong first)
    {
        using var f = new Fixture(debug: true);
        f.SetMembers(200);
        f.StartComparison(mode, 100, 300);
        var attempt = f.Send(first);
        Assert.NotNull(attempt);
        Assert.Equal(M.DadNativePartyInviteType.CrossWorldContentId, attempt.InviteType);
        var other = first == 100 ? 300ul : 100ul;
        if (mode != "Cross")
        {
            Assert.Null(f.Send(other));
            f.SetMembers(200, first);
        }
        Assert.NotNull(f.Send(other));
        f.Call("Reset");
        f.Advance(5);
        Assert.Null(f.Send(first));
        Assert.NotNull(f.Send(first, "unrelated-run"));
    }

    [Fact]
    public void AdmissionResetPreservesOnlyTheExplicitExactComparison()
    {
        using var f = new Fixture(debug: true);
        f.SetMembers(200);
        f.StartComparison("Reverse", 100, 300);
        f.CallInternal("ResetForRun", "same-run");
        Assert.Null(f.Send(100));
        Assert.Equal(M.DadNativePartyInviteType.CrossWorldContentId, f.Send(300)?.InviteType);
        f.CallInternal("ResetForRun", "other-run");
        Assert.Null(f.Send(100));
        Assert.Contains("policy revoked", f.DiagnosticReport());
    }

    [Fact]
    public void SameRunReassemblyAcceptsFreshExactInvitationAfterStableSolo()
    {
        using var f = new Fixture();
        f.JoinFirstParty();
        f.DisbandWithInvitation(102);
        f.Pulse(); // Losing party membership alone must not authorize acceptance.
        Assert.Equal(1, f.Accepts);
        f.ObserveSoloForTwoSeconds();
        Assert.False(f.Arm()); // Await the actual native acceptance/membership observation.
        f.Pulse();
        Assert.Equal(2, f.Accepts);
        f.SetMembers(100, 200);
        f.Pulse();
        Assert.True(f.Arm());
    }

    [Fact]
    public void PassivePartyLossAndRepeatedBeginCannotRearmAcceptance()
    {
        using var f = new Fixture();
        f.JoinFirstParty();
        f.DisbandWithInvitation(102);
        f.Call("BeginParticipantRun", f.Expected.RunId);
        f.Pulse();
        f.ObserveSoloForTwoSeconds();
        Assert.Equal(1, f.Accepts);
    }

    [Theory]
    [InlineData(0u, "Synthetic Leader", (ushort)21)]
    [InlineData(101u, "Synthetic Leader", (ushort)21)]
    [InlineData(102u, "Unrelated Leader", (ushort)21)]
    [InlineData(102u, "Synthetic Leader", (ushort)22)]
    public void MissingOldForeignAndWrongWorldInvitationsCannotRearm(uint time, string name, ushort world)
    {
        using var f = new Fixture();
        f.JoinFirstParty();
        f.DisbandWithInvitation(time);
        f.Invite(time, name, world);
        f.Pulse();
        f.ObserveSoloForTwoSeconds();
        Assert.False(f.Arm());
        f.Pulse();
        Assert.Equal(1, f.Accepts);
    }

    [Fact]
    public void ShorterNativeDeadlineAfterDisbandDoesNotCauseFourMinuteWait()
    {
        using var f = new Fixture();
        Assert.False(f.Arm());
        f.Invite(1793114768); // 16:21:08 invite with a five-minute native deadline.
        f.Pulse();
        f.SetMembers(100, 200);
        f.Pulse();
        Assert.True(f.Arm());
        f.DisbandWithInvitation(1793114590); // Fresh cross-world invitation with a shorter deadline.
        f.Pulse();
        f.ObserveSoloForTwoSeconds();
        Assert.False(f.Arm());
        f.Pulse();
        Assert.Equal(2, f.Accepts);
        f.SetMembers(100, 200);
        f.Pulse();
        Assert.True(f.Arm());
        f.DisbandWithInvitation(1793114768); // Replaying the original higher token remains forbidden.
        f.Pulse();
        f.ObserveSoloForTwoSeconds();
        Assert.False(f.Arm());
        f.Pulse();
        Assert.Equal(2, f.Accepts);
    }

    [Fact]
    public void FreshLowerIdentityStillRequiresExplicitAssemblyAuthorization()
    {
        using var f = new Fixture();
        f.JoinFirstParty();
        f.DisbandWithInvitation(100);
        f.Pulse();
        f.ObserveSoloForTwoSeconds();
        Assert.Equal(1, f.Accepts);
        Assert.False(f.Arm());
        f.Pulse();
        Assert.Equal(2, f.Accepts);
    }

    [Theory]
    [InlineData("RunId", "other-run")]
    [InlineData("WorkerSessionId", "other-worker")]
    [InlineData("AccountKey", "other-account")]
    [InlineData("CharacterKey", "Other Character@World")]
    [InlineData("ContentId", "999")]
    [InlineData("CharacterName", "Other Character")]
    [InlineData("WorldId", "22")]
    public void ReassemblyCannotReplaceAnyFrozenInviterIdentity(string property, string replacement)
    {
        using var f = new Fixture();
        f.JoinFirstParty();
        f.DisbandWithInvitation(102);
        f.Pulse();
        f.ObserveSoloForTwoSeconds();
        var changed = f.Expected.Clone();
        var member = changed.GetType().GetProperty(property)!;
        object value = property switch
        {
            "WorkerSessionId" => new M.DadWorkerSessionId(replacement),
            "AccountKey" => new M.DadAccountKey(replacement),
            "CharacterKey" => new M.DadCharacterKey(replacement),
            "ContentId" => ulong.Parse(replacement),
            "WorldId" => ushort.Parse(replacement),
            _ => replacement,
        };
        member.SetValue(changed, value);
        Assert.False(f.Arm(changed));
        f.Pulse();
        Assert.Equal(1, f.Accepts);
        Assert.False(f.Arm()); // The rejected replacement cannot poison the original frozen inviter.
        f.Pulse();
        Assert.Equal(2, f.Accepts);
    }

    [Theory]
    [InlineData("BoundByDuty")]
    [InlineData("BoundByDuty56")]
    [InlineData("InDutyQueue")]
    [InlineData("WaitingForDuty")]
    [InlineData("WaitingForDutyFinder")]
    [InlineData("BetweenAreas")]
    [InlineData("BetweenAreas51")]
    [InlineData("InCombat")]
    [InlineData("OccupiedInCutSceneEvent")]
    [InlineData("TradeOpen")]
    public void UnsafeNativeStateCannotAuthorizeReassembly(string condition)
    {
        using var f = new Fixture();
        f.JoinFirstParty();
        f.DisbandWithInvitation(102);
        f.UnsafeConditions.Add(condition);
        f.Pulse();
        f.ObserveSoloForTwoSeconds();
        Assert.False(f.Arm());
        f.Pulse();
        Assert.Equal(1, f.Accepts);
    }

    [Theory]
    [InlineData("foreign-party")]
    [InlineData("cross-realm")]
    [InlineData("different-local-cid")]
    public void SoloProofRequiresExactOriginalLocalCharacterAndNoOtherParty(string scenario)
    {
        using var f = new Fixture();
        f.JoinFirstParty();
        f.DisbandWithInvitation(102);
        if (scenario == "foreign-party") f.SetMembers(200, 999);
        if (scenario == "cross-realm") f.SetCrossRealm(true);
        if (scenario == "different-local-cid") { f.LocalContentId = 999; f.SetMembers(999); }
        f.Pulse();
        f.ObserveSoloForTwoSeconds();
        Assert.False(f.Arm());
        f.Pulse();
        Assert.Equal(1, f.Accepts);
    }

    [Fact]
    public void TransientSoloAndObservationGapCannotSupplySustainedProof()
    {
        using var f = new Fixture();
        f.JoinFirstParty();
        f.DisbandWithInvitation(102);
        f.Pulse();
        f.Advance(.5);
        Assert.False(f.Arm());
        f.SetMembers(200, 999);
        f.Pulse();
        f.SetMembers(200);
        f.Advance(.5);
        f.Pulse();
        f.Advance(5); // No framework observations during this gap.
        Assert.False(f.Arm());
        f.Pulse();
        Assert.Equal(1, f.Accepts);
        f.ObserveSoloForTwoSeconds();
        Assert.False(f.Arm());
        f.Pulse();
        Assert.Equal(2, f.Accepts);
    }

    [Fact]
    public void SafetyIsReadAgainBeforeNativeAcceptanceAfterRearm()
    {
        using var f = new Fixture();
        f.JoinFirstParty();
        f.DisbandWithInvitation(102);
        f.Pulse();
        f.ObserveSoloForTwoSeconds();
        Assert.False(f.Arm());
        f.UnsafeConditions.Add("BetweenAreas");
        f.Pulse();
        Assert.Equal(1, f.Accepts);
        f.UnsafeConditions.Clear();
        f.ObserveSoloForTwoSeconds();
        Assert.Equal(2, f.Accepts);
    }

    [Fact]
    public void RearmedAcceptanceCannotConsumeAnOlderInviteBeforeNativeDispatch()
    {
        using var f = new Fixture();
        f.JoinFirstParty();
        f.DisbandWithInvitation(102);
        f.Pulse();
        f.ObserveSoloForTwoSeconds();
        Assert.False(f.Arm());
        f.Invite(101); // Native observation changed after authorization but before the next framework pulse.
        f.Pulse();
        Assert.Equal(1, f.Accepts);
        f.Invite(102);
        f.Pulse();
        Assert.Equal(2, f.Accepts);
    }

    [Fact]
    public void RepeatedDisbandRequiresANewerInvitationEachTime()
    {
        using var f = new Fixture();
        f.JoinFirstParty();
        foreach (var time in new uint[] { 102, 103 })
        {
            f.DisbandWithInvitation(time - 1);
            f.Pulse();
            f.ObserveSoloForTwoSeconds();
            Assert.False(f.Arm());
            f.Pulse();
            Assert.Equal((int)time - 101, f.Accepts);
            f.Invite(time);
            Assert.False(f.Arm());
            f.Pulse();
            Assert.Equal((int)time - 100, f.Accepts);
            f.SetMembers(100, 200);
            f.Pulse();
            Assert.True(f.Arm());
        }
    }

    [Fact]
    public void ResetOrDifferentRunCannotReuseOldReassemblyAuthorization()
    {
        using var f = new Fixture();
        f.JoinFirstParty();
        f.DisbandWithInvitation(102);
        f.Pulse();
        f.ObserveSoloForTwoSeconds();
        f.Call("Reset");
        Assert.False(f.Arm());
        f.Pulse();
        Assert.Equal(1, f.Accepts);
        f.Call("BeginParticipantRun", "new-run");
        Assert.False(f.Arm());
        f.Pulse();
        Assert.Equal(1, f.Accepts);
    }

    [Theory]
    [InlineData(101u, 1)]
    [InlineData(0u, 0)]
    public void ManuallyObservedMembershipNeedsAnExactInvitationWatermarkToRearm(uint firstInvite, int accepts)
    {
        using var f = new Fixture();
        f.Invite(firstInvite);
        f.SetMembers(100, 200);
        Assert.True(f.Arm()); // Membership may have been accepted manually or by another plugin.
        Assert.Equal(0, f.Accepts);
        f.DisbandWithInvitation(102);
        f.Pulse();
        f.ObserveSoloForTwoSeconds();
        Assert.False(f.Arm());
        f.Pulse();
        Assert.Equal(accepts, f.Accepts); // No observed old invite means freshness cannot be proven.
    }

    private sealed class Fixture : IDisposable
    {
        private readonly object gateway;
        private readonly object native;
        private readonly PropertyInfo clockProperty;
        private readonly object previousClock;
        private readonly Clock clock = new();
        private readonly List<string> events = [];
        public HashSet<string> UnsafeConditions { get; } = [];
        public ulong LocalContentId { get; set; } = 200;
        public M.DadExpectedPartyInviter Expected { get; } = new()
        {
            RunId = "same-run", WorkerSessionId = new("leader-worker"), AccountKey = new("leader-account"),
            CharacterKey = new("Synthetic Leader@World"), ContentId = 100, CharacterName = "Synthetic Leader", WorldId = 21,
        };
        public int Accepts => events.Count(e => e == "native:accept-invitation");

        public Fixture(bool debug = false)
        {
            var assembly = typeof(M.DadExpectedPartyInviter).Assembly;
            clockProperty = assembly.GetType("dad.DadClock")!.GetProperty("Provider", BindingFlags.Static | BindingFlags.NonPublic)!;
            previousClock = clockProperty.GetValue(null)!;
            clockProperty.SetValue(null, clock);
            var nativeType = assembly.GetType("dad.Headless.VirtualPartyGateway")!;
            native = Activator.CreateInstance(nativeType, [200UL, new Action<string>(events.Add)])!;
            var framework = Proxy<IFramework>((method, _) => method.Name == "get_IsInFrameworkUpdateThread" ? true : throw new InvalidOperationException(method.Name));
            var player = Proxy<IPlayerState>((method, _) => method.Name == "get_ContentId" ? LocalContentId : throw new InvalidOperationException(method.Name));
            var conditions = Proxy<ICondition>((method, args) => method.Name == "get_Item" ? UnsafeConditions.Contains(args[0]!.ToString()!) : throw new InvalidOperationException(method.Name));
            var log = DispatchProxy.Create<IPluginLog, DadScheduleDutyExitRetryRuntimeTests.QuietLog>();
            gateway = Activator.CreateInstance(assembly.GetType("dad.Services.InfoProxyPartyInviteGateway")!,
                [new DadRuntime::dad.Configuration { DebugUiEnabled = debug, PluginEnabled = true }, framework, player, null, conditions, log, native])!;
            Call("BeginParticipantRun", Expected.RunId);
        }

        public void JoinFirstParty()
        {
            Assert.False(Arm());
            Invite(101);
            Pulse();
            Assert.Equal(1, Accepts);
            SetMembers(100, 200);
            Pulse();
            Assert.True(Arm());
        }

        public void DisbandWithInvitation(uint time)
        {
            Advance(10);
            SetMembers(200);
            Invite(time);
        }

        public void Invite(uint time, string name = "Synthetic Leader", ushort world = 21)
            => native.GetType().GetProperty("Invitation")!.SetValue(native, new M.DadPendingPartyInvitation(time, name, world));
        public void SetMembers(params ulong[] ids)
            => native.GetType().GetProperty("Members")!.SetValue(native,
                ids.Select(id => new M.DadPartyMemberSnapshot { ContentId = id, IsLocalPlayer = id == 200 }).ToList());
        public void SetCrossRealm(bool value) => native.GetType().GetProperty("CrossRealm")!.SetValue(native, value);
        private object Diagnostics => gateway.GetType().GetProperty("Diagnostics", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(gateway)!;
        public string DiagnosticReport() => (string)Diagnostics.GetType().GetMethod("Report", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Diagnostics, null)!;
        public void StartComparison(string mode, params ulong[] targets)
        {
            var type = Diagnostics.GetType().Assembly.GetType("dad.Models.DadInviteTestMode")!;
            Diagnostics.GetType().GetMethod("Start", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Diagnostics,
                [Expected.RunId, Enum.Parse(type, mode), targets, clock.Now.UtcDateTime]);
        }
        public M.DadNativePartyInviteAttempt? Send(ulong id, string? run = null)
        {
            object?[] args = [new M.DadNativePartyInviteTarget
            {
                RunId = run ?? Expected.RunId, SlotId = id == 100 ? "Slot2" : "Slot3", AccountKey = new("account-" + id),
                CharacterKey = new("Synthetic Target@World"), ContentId = id, CharacterName = "Synthetic Target", WorldId = 21,
                WorkerSessionId = new("worker-" + id),
            }, false, null];
            return (M.DadNativePartyInviteAttempt?)gateway.GetType().GetMethod("TryInvite")!.Invoke(gateway, args);
        }
        public void Advance(double seconds) => clock.Now = clock.Now.AddSeconds(seconds);
        public void ObserveSoloForTwoSeconds()
        {
            for (var i = 0; i < 4; i++) { Advance(.5); Pulse(); }
        }
        public bool Arm(M.DadExpectedPartyInviter? expected = null)
        {
            object?[] args = [expected ?? Expected, null];
            return (bool)gateway.GetType().GetMethod("TryArmAcceptance")!.Invoke(gateway, args)!;
        }
        public void Pulse() => Call("UpdateAcceptance");
        public void Call(string method, params object?[] args) => gateway.GetType().GetMethod(method)!.Invoke(gateway, args);
        public void CallInternal(string method, params object?[] args) => gateway.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(gateway, args);
        public void Dispose() => clockProperty.SetValue(null, previousClock);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 25, 18, 49, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    public class NativeServiceProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args ?? []);
    }

    private static T Proxy<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, NativeServiceProxy>();
        ((NativeServiceProxy)(object)proxy).Handler = handler;
        return proxy;
    }
}
