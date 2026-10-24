extern alias DadRuntime;
extern alias DalamudApi;

using System.Reflection;
using DalamudApi::Dalamud.Plugin.Services;
using QueueService = DadRuntime::dad.Services.DadNpcDutyQueueService;
using Content = DadRuntime::dad.Services.DadNpcDutyResolvedContent;
using Task = DadRuntime::dad.Models.DadTrustTask;
using Party = DadRuntime::dad.Models.DadTrustFixedParty;
using Npc = DadRuntime::dad.Models.DadTrustNpcId;
using Roles = DadRuntime::dad.Models.DadTrustRoles;
using Kind = DadRuntime::dad.Services.DadNpcDutyQueuePulseKind;
using Xunit;

namespace dad.Tests;

public sealed class DadTrustQueueRuntimeTests
{
    [Fact]
    public void FixedPartyUsesNativeIdentitiesIncludingZeroAndVerifiesBeforeRegistering()
    {
        using var f = new Fixture();
        f.Prepare();
        Assert.Equal(new byte[] { 1, 2, 3 }, f.Adds);
        Assert.Equal(1, f.Clears);
        Assert.Equal(0, f.Registers);
        Assert.Equal(Kind.RegisteredForDuty, f.Pulse());
        Assert.Equal(1, f.Registers);
    }

    [Theory]
    [InlineData("wrong-job")]
    [InlineData("missing-member")]
    [InlineData("duplicate-member")]
    [InlineData("underleveled-member")]
    [InlineData("uninitialized-member")]
    [InlineData("invalid-count")]
    [InlineData("locked-duty")]
    [InlineData("locked-member")]
    public void InvalidFixedPartyDoesNotClearAnExistingPartyOrSubstituteAnotherNpc(string failure)
    {
        using var f = new Fixture();
        f.Selected.Add(0);
        switch (failure)
        {
            case "wrong-job": f.Request.FixedParty!.PlayerJobId = 24; break;
            case "missing-member": f.Request.FixedParty!.Npcs[0] = Npc.Ryne; break;
            case "duplicate-member": f.Request.FixedParty!.Npcs[0] = Npc.Alisaie; break;
            case "underleveled-member": f.Members[2] = f.Members[2] with { Level = 70 }; break;
            case "uninitialized-member": f.Members[2] = f.Members[2] with { Initialized = false }; break;
            case "locked-duty": f.DutyUnlocked = false; break;
            case "locked-member": f.Members[2] = f.Members[2] with { Unlocked = false }; break;
            default: f.Count = 13; break;
        }
        f.Resolve();
        Assert.Equal(Kind.PreparedTrustParty, f.Pulse()); // existing level refresh
        Assert.Equal(Kind.Failed, f.Pulse());
        Assert.Equal(0, f.Clears);
        Assert.Equal(new byte[] { 0 }, f.Selected);
        Assert.Empty(f.Adds);
        Assert.Equal(0, f.Registers);
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("ignored")]
    [InlineData("throws")]
    public void UnverifiedNativeAddCannotRegisterOrPickAnotherCompanion(string failure)
    {
        using var f = new Fixture { AddFailure = failure };
        Assert.Equal(Kind.PreparedTrustParty, f.Pulse());
        Assert.Equal(Kind.PreparedTrustParty, f.Pulse());
        Assert.Equal(Kind.Failed, f.Pulse());
        Assert.All(f.Adds, index => Assert.Contains(index, new byte[] { 1, 2, 3 }));
        Assert.Equal(0, f.Registers);
    }

    [Theory]
    [InlineData("party")]
    [InlineData("job")]
    [InlineData("character")]
    [InlineData("duty")]
    [InlineData("native-member-identity")]
    [InlineData("availability")]
    [InlineData("unlock")]
    public void ChangedNativeReadbackHoldsRegistration(string change)
    {
        using var f = new Fixture();
        f.Prepare();
        switch (change)
        {
            case "party": f.Selected.Remove(2); f.Selected.Add(0); break;
            case "job": f.JobId = 24; break;
            case "character": f.ContentId = 1002; break;
            case "duty": f.SelectedDuty = 2; break;
            case "availability": f.Available = false; break;
            case "unlock": f.DutyUnlocked = false; break;
            default: f.Members[2] = f.Members[0]; break;
        }
        Assert.Equal(Kind.Failed, f.Pulse());
        Assert.Equal(0, f.Registers);
        Assert.Equal(1, f.Clears);
        Assert.Equal(3, f.Adds.Count);
    }

    [Fact]
    public void RequestAndResolvedPartyCopiesCannotBeChangedByLaterEditorMutations()
    {
        using var f = new Fixture();
        var clone = f.Request.Clone();
        clone.FixedParty!.Npcs.Clear();
        Assert.Equal(3, f.Request.FixedParty!.Npcs.Count);
        f.Request.FixedParty.Npcs.Clear();
        f.Prepare();
        Assert.Equal(Kind.RegisteredForDuty, f.Pulse());
    }

    [Fact]
    public void LegacyTaskUsesObservedRolesAndMemberIdsRatherThanExpansionSlotNames()
    {
        using var f = new Fixture();
        f.Request.FixedParty = null;
        f.Resolve();
        f.Prepare();
        Assert.Equal(new byte[] { 1, 2, 3 }, f.Adds);
        Assert.Equal(Kind.RegisteredForDuty, f.Pulse());
    }

    [Fact]
    public void CurrentNativeCaptureIsReadOnlyAndDoesNotClaimACompleteRosterRefresh()
    {
        using var f = new Fixture();
        f.Members.Add(new(5, 80, 29, Roles.Dps, "Ryne"));
        f.Count = f.Members.Count;
        var observed = f.Queue.CaptureSelectedTrustProgress(Owner());
        Assert.NotNull(observed);
        Assert.Null(observed.RosterObservedAtUtc);
        Assert.Contains(observed.Npcs, npc => npc.NpcId == Npc.Alphinaud && npc.Level == 100);
        Assert.Contains(observed.Npcs, npc => npc.NpcId == Npc.GrahaTia && npc.Level == 100);
        Assert.Contains(observed.Npcs, npc => npc.NpcId == Npc.Ryne && npc.LevelCap == 80);
        Assert.Equal(4u, Assert.Single(observed.Duties).ContentFinderConditionId);
        Assert.Equal(0, f.Clears);
        Assert.Empty(f.Adds);
        Assert.Equal(0, f.Registers);
    }

    [Fact]
    public void ACurrentLockedNpcRetainsFalseUnlockAndUnknownLevelWithoutInventingZero()
    {
        using var f = new Fixture();
        f.Members[4] = f.Members[4] with { Level = 0, Unlocked = false };
        var progress = f.Queue.CaptureSelectedTrustProgress(Owner())!;
        var npc = Assert.Single(progress.Npcs, npc => npc.NpcId == Npc.GrahaTia);
        Assert.False(npc.Unlocked);
        Assert.Null(npc.Level);
    }

    [Theory]
    [InlineData("wrong-character")]
    [InlineData("missing-account")]
    [InlineData("character-changes-during-read")]
    [InlineData("duty-changes-during-read")]
    [InlineData("loading")]
    [InlineData("closed-window")]
    public void UnavailableOrChangingCaptureCannotAttachProgressToTheWrongCharacter(string state)
    {
        using var f = new Fixture();
        var owner = Owner();
        switch (state)
        {
            case "wrong-character": owner.ContentId = 1002; break;
            case "missing-account": owner.AccountId = string.Empty; break;
            case "character-changes-during-read": f.AfterMemberRead = index => { if (index == 2) f.ContentId = 1002; }; break;
            case "duty-changes-during-read": f.AfterMemberRead = index => { if (index == 2) f.SelectedCfc = 6; }; break;
            case "loading": f.Loading = true; break;
            default: f.WindowReady = false; break;
        }
        Assert.Null(f.Queue.CaptureSelectedTrustProgress(owner));
        Assert.Equal(0, f.Clears);
        Assert.Empty(f.Adds);
        Assert.Equal(0, f.Registers);
    }

    private static DadRuntime::dad.Models.DadAcquiredCharacter Owner() => new()
    {
        AccountId = "fixture-account", CharacterKey = "Fixture One@Example", ContentId = 1001,
        CurrentJobId = 19, CurrentLevel = 100,
    };

    [Fact]
    public void FullRefreshVisitsEveryUnlockedDungeonBeforePublishingCompleteRoster()
    {
        using var f = new Fixture { MultipleDuties = true };
        Assert.True(f.Queue.RequestTrustRosterRefresh(Owner()));
        f.UpdateRefresh(3);
        Assert.True(f.Queue.IsTrustRosterRefreshActive);
        Assert.Null(f.Queue.CaptureTrustProgress(Owner())!.RosterObservedAtUtc);
        f.UpdateRefresh(3);
        Assert.False(f.Queue.IsTrustRosterRefreshActive);
        var progress = f.Queue.CaptureTrustProgress(Owner())!;
        Assert.NotNull(progress.RosterObservedAtUtc);
        Assert.Equal(new uint[] { 4, 6 }, progress.Duties.Select(duty => duty.ContentFinderConditionId));
        Assert.Equal(new uint[] { 4, 6 }, f.Opens);
        Assert.Equal(5, progress.Npcs.Count);
        Assert.Equal(0, f.Clears);
        Assert.Empty(f.Adds);
        Assert.Equal(0, f.Registers);
        Assert.Equal(progress.RosterObservedAtUtc, f.Queue.CaptureTrustProgress(Owner())!.RosterObservedAtUtc);
    }

    [Theory]
    [InlineData("character")]
    [InlineData("loading")]
    [InlineData("duty-selection")]
    [InlineData("unknown-npc")]
    [InlineData("duplicate-npc")]
    [InlineData("locked-duty")]
    [InlineData("exception")]
    public void InterruptedFullRefreshNeverPublishesACompleteRosterOrRegisters(string reason)
    {
        using var f = new Fixture { MultipleDuties = true };
        Assert.True(f.Queue.RequestTrustRosterRefresh(Owner()));
        f.UpdateRefresh(5); // first dungeon captured, second selected and updated
        switch (reason)
        {
            case "character": f.ContentId = 1002; break;
            case "loading": f.Loading = true; break;
            case "duty-selection": f.SelectedCfc = 99; break;
            case "unknown-npc": f.Members[0] = f.Members[0] with { Id = 250 }; break;
            case "duplicate-npc": f.Members[0] = f.Members[1]; break;
            case "locked-duty": f.DutyUnlocked = false; break;
            default: f.AfterMemberRead = _ => throw new InvalidOperationException("Synthetic unread member."); break;
        }
        f.UpdateRefresh(3);
        Assert.False(f.Queue.IsTrustRosterRefreshActive);
        Assert.Null(f.Queue.CaptureTrustProgress(Owner())?.RosterObservedAtUtc);
        Assert.Equal(0, f.Registers);
        Assert.Equal(0, f.Clears);
    }

    [Fact]
    public void UninitializedMemberWaitsWithoutReopeningAndManualCancellationDropsPartialData()
    {
        using var f = new Fixture();
        f.Members[1] = f.Members[1] with { Initialized = false };
        Assert.True(f.Queue.RequestTrustRosterRefresh(Owner()));
        f.UpdateRefresh(8);
        Assert.True(f.Queue.IsTrustRosterRefreshActive);
        Assert.Single(f.Opens);
        f.Queue.CancelTrustRosterRefresh("fixture stop");
        Assert.False(f.Queue.IsTrustRosterRefreshActive);
        Assert.Null(f.Queue.CaptureTrustProgress(Owner())?.RosterObservedAtUtc);
        Assert.Equal(0, f.Registers);
    }

    [Fact]
    public void QueueStartCancelsRefreshAndBusyQueueCannotStartAnotherRefresh()
    {
        using var f = new Fixture();
        Assert.True(f.Queue.RequestTrustRosterRefresh(Owner()));
        f.UpdateRefresh(1);
        Assert.Equal(Kind.PreparedTrustParty, f.Pulse());
        Assert.False(f.Queue.IsTrustRosterRefreshActive);
        Assert.False(f.Queue.RequestTrustRosterRefresh(Owner()));
    }

    private sealed record Member(uint Id, int Level, uint Job, Roles Roles, string Name, bool Initialized = true, bool Unlocked = true);

    private sealed class Fixture : IDisposable
    {
        private static readonly Assembly Runtime = typeof(QueueService).Assembly;
        internal readonly QueueService Queue;
        internal readonly Task Request = new() { ContentFinderConditionId = 4, DutyName = "Fixture duty",
            FixedParty = new Party { PlayerJobId = 19, Npcs = [Npc.Alphinaud, Npc.Alisaie, Npc.Yshtola] } };
        internal Content Content = null!;
        internal readonly List<Member> Members =
        [
            new(2, 100, 37, Roles.Tank, "Thancred"),
            new(4, 100, 25, Roles.Dps, "Y'shtola"),
            new(0, 100, 28, Roles.Healer, "Alphinaud"),
            new(1, 100, 35, Roles.Dps, "Alisaie"),
            new(9, 100, 25, Roles.All, "G'raha Tia"),
        ];
        internal readonly List<byte> Adds = [];
        internal readonly HashSet<byte> Selected = [];
        internal uint JobId = 19, SelectedDuty = 1;
        internal ulong ContentId = 1001;
        internal int Count = 5, Clears, Registers;
        internal string? AddFailure;
        internal bool DutyUnlocked = true, Available = true;
        internal bool Loading, WindowReady = true;
        internal uint SelectedCfc = 4;
        internal Action<byte>? AfterMemberRead;
        internal bool MultipleDuties;
        internal readonly List<uint> Opens = [];

        internal void UpdateRefresh(int ticks)
        {
            for (var index = 0; index < ticks; index++)
            {
                // Advance only this instance's read eligibility; no sleeps or global clock mutation.
                typeof(QueueService).GetField("trustRefreshNextRead", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Queue, DateTime.MinValue);
                Queue.UpdateTrustRosterRefresh();
            }
        }

        internal Fixture()
        {
            var constructor = typeof(QueueService).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(c => c.GetParameters().Length == 3);
            var parameters = constructor.GetParameters();
            var log = Proxy(typeof(IPluginLog), static (method, _) => method.ReturnType != typeof(void) && method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null);
            Queue = (QueueService)constructor.Invoke([log, Proxy(parameters[1].ParameterType, Game), Proxy(parameters[2].ParameterType, Native)]);
            Resolve();
        }

        internal void Resolve()
        {
            Content = Queue.Resolve(Request, out var blocker)!;
            Assert.NotNull(Content);
            Assert.Equal(string.Empty, blocker);
        }

        internal Kind Pulse() => Queue.Pulse("fixture-run", Content).Kind;
        internal void Prepare()
        {
            Assert.Equal(Kind.PreparedTrustParty, Pulse());
            Assert.Equal(Kind.PreparedTrustParty, Pulse());
            Assert.Equal(Kind.PreparedTrustParty, Pulse());
        }

        private object? Game(MethodInfo method, object?[]? args) => method.Name switch
        {
            "add_DutyCompleted" or "remove_DutyCompleted" => null,
            "get_IsLoggedIn" or "get_HasLocalPlayer" => true,
            "get_ContentId" => ContentId,
            "get_TerritoryType" => 1u,
            "Condition" => Loading && args![0]!.ToString() == "BetweenAreas",
            "Addon" => Activator.CreateInstance(method.ReturnType, false, false),
            _ => throw new InvalidOperationException("Unexpected game boundary: " + method.Name),
        };

        private object? Native(MethodInfo method, object?[]? args)
        {
            switch (method.Name)
            {
                case "get_LocalJobId": return JobId;
                case "DutyCatalog": return Rows("DadNpcDutyCatalogRow", 4u, "Fixture duty", 1036u, (uint?)3, 44u, (byte)71);
                case "DawnCatalog":
                    var rows = Rows("DadDawnCatalogRow", 1u, 4u, true, 4);
                    if (!MultipleDuties) return rows;
                    var expanded = Array.CreateInstance(rows.GetType().GetElementType()!, 2);
                    expanded.SetValue(rows.GetValue(0), 0);
                    expanded.SetValue(Activator.CreateInstance(rows.GetType().GetElementType()!, 2u, 6u, true, 4), 1);
                    return expanded;
                case "TrustDutyUnlocks":
                    var dutyType = Runtime.GetType("dad.Services.DadTrustNativeDuty", true)!;
                    var duties = Array.CreateInstance(dutyType, MultipleDuties ? 2 : 1);
                    duties.SetValue(Activator.CreateInstance(dutyType, 4u, true, false), 0);
                    if (MultipleDuties) duties.SetValue(Activator.CreateInstance(dutyType, 6u, true, false), 1);
                    return duties;
                case "AgentAvailable": return true;
                case "AddonReady": return WindowReady;
                case "MainCommandEnabled": return true;
                case "Open": Opens.Add((uint)args![1]!); SelectedCfc = (uint)args[1]!; return null;
                case "ExpansionCount": return 6u;
                case "SelectedContentId": return SelectedDuty;
                case "get_TrustMembersAvailable": return true;
                case "get_TrustMemberCount": return Count;
                case "UpdateTrustAddon": return null;
                case "ClearTrustParty": Clears++; Selected.Clear(); return null;
                case "TrustMember":
                    var member = Members[(byte)args![0]!];
                    AfterMemberRead?.Invoke((byte)args[0]!);
                    return Activator.CreateInstance(method.ReturnType, member.Id, member.Level, member.Job, member.Roles, member.Name, member.Initialized, member.Unlocked);
                case "AddTrustMember":
                    var index = (byte)args![0]!;
                    Adds.Add(index);
                    if (AddFailure == "throws") throw new InvalidOperationException("Synthetic native rejection.");
                    if (AddFailure == "rejected") return false;
                    if (AddFailure != "ignored") Selected.Add(index);
                    return true;
                case "get_TrustPartyFull": return Selected.Count == 3;
                case "SelectedTrustDuty": return Activator.CreateInstance(method.ReturnType, SelectedCfc, DutyUnlocked, Available && Selected.Count == 3);
                case "SelectedTrustMemberIndices": return Selected.Order().ToArray();
                case "Register": Registers++; return null;
                default: throw new InvalidOperationException("Unexpected native boundary: " + method.Name);
            }
        }

        private static Array Rows(string name, params object?[] values)
        {
            var type = Runtime.GetType("dad.Services." + name, true)!;
            var result = Array.CreateInstance(type, 1);
            result.SetValue(Activator.CreateInstance(type, values), 0);
            return result;
        }

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
