extern alias DadRuntime;
extern alias DalamudApi;

using System.Collections;
using System.Reflection;
using System.Text.Json;
using DutyIpc = DadRuntime::dad.Services.DadDutyIpcService;
using DalamudApi::Dalamud.Plugin;
using DalamudApi::Dalamud.Plugin.Services;
using DalamudApi::Dalamud.Plugin.Ipc;
using Xunit;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.Enums;

namespace dad.Tests;

public sealed class DadQuestionableRuntimeTests
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    [Fact]
    public void HeadlessQuestionableSettingsRequireValidPayloadAndMatchingOwner()
    {
        var type = typeof(DutyIpc).Assembly.GetType("dad.Headless.VirtualProfileIpc")!;
        var fake = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Single()
            .Invoke([new Action<string>(_ => { })]);
        var invoke = type.GetMethod("Invoke")!;
        var settings = (string)typeof(DutyIpc).GetField("FrenRiderDutySettings", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
        bool Apply(string owner, string json) => Assert.IsType<bool>(invoke.Invoke(fake,
            ["FrenRider.Dad.ApplyQuestionableDutySettings", new object?[] { owner, json }]));
        bool Release(string owner) => Assert.IsType<bool>(invoke.Invoke(fake,
            ["FrenRider.Dad.ReleaseQuestionableDutySettings", new object?[] { owner }]));

        Assert.False(Apply("", settings));
        Assert.False(Apply("owner-a", "{}"));
        Assert.True(Apply("owner-a", settings));
        Assert.True(Apply("owner-a", settings));
        Assert.False(Apply("owner-b", settings));
        Assert.False(Release("owner-b"));
        Assert.True(Release("owner-a"));
        Assert.True(Apply("owner-b", settings));
        Assert.True(Release("owner-b"));
    }
    [Fact]
    public void DungeonTargetingPrecedesLeaderActivationAndReacquiresForTheNextDuty()
    {
        using var fixture = new DungeonTargetingFixture();
        Assert.Equal("Sent", fixture.Enable("run-a"));
        Assert.Equal("AlreadySent", fixture.Enable("run-a"));
        Assert.Equal(new[] { "acquire:run-a:4", "/fr on" }, fixture.Events);
        Assert.True(fixture.Release("different-run"));
        Assert.Equal(2, fixture.Events.Count);
        Assert.True(fixture.Release("run-a"));
        Assert.True(fixture.Release("run-a"));
        fixture.Enable("run-a", completed: true);
        Assert.Equal(new[] { "acquire:run-a:4", "/fr on", "release:run-a" }, fixture.Events);
        fixture.Enable("run-a");
        Assert.Equal("acquire:run-a:4", fixture.Events[^1]);
        Assert.Equal(1, fixture.Events.Count(value => value == "/fr on"));
        Assert.True(fixture.Release("run-a"));
        Assert.Equal("Sent", fixture.Enable("run-b"));
        Assert.Equal(new[] { "acquire:run-b:4", "/fr on" }, fixture.Events.TakeLast(2));
        Assert.Equal(DadRuntime::dad.DadCombatRotationMode.UseFrenRider, fixture.Configuration.CombatRotationMode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EveryDungeonRoleAcquiresBeforeItsExistingHandoff(int role)
    {
        using var fixture = new DungeonTargetingFixture();
        if (role == 0)
            fixture.Enable("four-role-run");
        else
        {
            var result = fixture.Invoke("TryConfigureAndEnableDungeonParticipant", "Synthetic Leader@World", "four-role-run", 4u)!;
            Assert.True((bool)result.GetType().GetProperty("Succeeded")!.GetValue(result)!);
        }
        Assert.Equal("acquire:four-role-run:4", fixture.Events[0]);
        Assert.Equal(role == 0 ? "/fr on" : "configure:Synthetic Leader@World", fixture.Events[1]);
        Assert.True(fixture.Release("four-role-run"));
        Assert.Equal("release:four-role-run", fixture.Events[^1]);
    }

    [Fact]
    public void UnrelatedDutyAndDeliberateOffModesNeverAcquireDungeonTargeting()
    {
        using var fixture = new DungeonTargetingFixture();
        fixture.ScopeAvailable = false;
        fixture.Enable("unrelated-duty");
        Assert.Equal(new[] { "/fr on" }, fixture.Events);
        Assert.True(fixture.Combat.TryApplyDutySupportEntryMode(DadRuntime::dad.DadCombatRotationMode.DoNothing,
            "off-run", out _, out _));
        Assert.True(fixture.Combat.TryApplyDutySupportEntryMode(DadRuntime::dad.DadCombatRotationMode.ForceCommands,
            "force-run", out _, out _));
        Assert.Equal(new[] { "/fr on", "/bmrai on" }, fixture.Events);
        Assert.True(fixture.Release("unrelated-duty"));
        Assert.DoesNotContain(fixture.Events, value => value.StartsWith("release:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ForceCommandsUsesOnlyBossModRotationAndReportsRejectedCommand(bool commandAccepted)
    {
        using var fixture = new DungeonTargetingFixture { CommandAccepted = commandAccepted };

        Assert.Equal(commandAccepted, fixture.Combat.TryApplyDutySupportEntryMode(
            DadRuntime::dad.DadCombatRotationMode.ForceCommands, "force-bmr-run", out var summary, out var shouldFailRun));

        Assert.Equal(new[] { "/bmrai on" }, fixture.Events);
        Assert.Contains("/bmrai on", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("/rotation", summary, StringComparison.Ordinal);
        Assert.False(shouldFailRun);
        Assert.True(fixture.Release("force-bmr-run"));
        Assert.Equal(new[] { "/bmrai on" }, fixture.Events);
        if (!commandAccepted)
            Assert.True(fixture.WarningCount > 0);
    }

    [Fact]
    public void RejectedProviderPreservesNormalHandoffWithoutInventingOwnership()
    {
        using var fixture = new DungeonTargetingFixture { AcquireAccepted = false };
        Assert.Equal("Sent", fixture.Enable("non-rsr-run"));
        fixture.Enable("non-rsr-run");
        Assert.True(fixture.Release("non-rsr-run"));
        Assert.Equal(new[] { "acquire:non-rsr-run:4", "/fr on" }, fixture.Events);
        Assert.True(fixture.WarningCount > 0);
    }

    [Fact]
    public void LostAcquisitionAcknowledgementRetainsExactCleanupAndReportsFailedRelease()
    {
        using var fixture = new DungeonTargetingFixture { ThrowAcquisition = true, ReleaseAccepted = false };
        Assert.Equal("Sent", fixture.Enable("uncertain-run"));
        Assert.False(fixture.Release("uncertain-run"));
        var count = fixture.Events.Count;
        Assert.True(fixture.Release("foreign-run"));
        Assert.Equal(count, fixture.Events.Count);
        fixture.Enable("another-run");
        Assert.DoesNotContain("acquire:another-run:4", fixture.Events);
        fixture.ReleaseAccepted = true;
        Assert.True(fixture.Release("uncertain-run"));
        fixture.ThrowAcquisition = false;
        fixture.Enable("fresh-run");
        Assert.Contains("acquire:fresh-run:4", fixture.Events);
        Assert.True(fixture.WarningCount >= 3);
    }

    private sealed class DungeonTargetingFixture : IDisposable
    {
        private readonly PropertyInfo commandProperty = typeof(DutyIpc).Assembly.GetType("dad.Plugin")!.GetProperty("CommandManager")!;
        private readonly object? previousCommands;
        public readonly List<string> Events = [];
        public readonly DadRuntime::dad.Configuration Configuration = new()
            { CombatRotationMode = DadRuntime::dad.DadCombatRotationMode.UseFrenRider };
        public readonly DadRuntime::dad.Services.DadCombatRotationService Combat;
        public bool ScopeAvailable = true;
        public bool AcquireAccepted = true;
        public bool ThrowAcquisition;
        public bool ReleaseAccepted = true;
        public bool CommandAccepted = true;
        public int WarningCount;

        public DungeonTargetingFixture()
        {
            previousCommands = commandProperty.GetValue(null);
            commandProperty.SetValue(null, Proxy<ICommandManager>((method, args) =>
            {
                Assert.Equal("ProcessCommand", method.Name);
                Events.Add((string)args![0]!);
                return CommandAccepted;
            }));
            var pi = Proxy<IDalamudPluginInterface>((method, args) =>
            {
                Assert.Equal("GetIpcSubscriber", method.Name);
                var endpoint = (string)args![0]!;
                return Proxy(method.ReturnType, (call, values) =>
                {
                    Assert.Equal("InvokeFunc", call.Name);
                    if (endpoint == "FrenRider.Dad.AcquireDungeonRsrAggro")
                    {
                        Assert.IsType<uint>(values![1]);
                        Events.Add($"acquire:{values[0]}:{values[1]}");
                        if (ThrowAcquisition) throw new IOException("Synthetic acknowledgement lost after acceptance.");
                        return AcquireAccepted;
                    }
                    if (endpoint == "FrenRider.Dad.ReleaseDungeonRsrAggro")
                    {
                        Events.Add($"release:{values![0]}");
                        return ReleaseAccepted;
                    }
                    Assert.Equal("FrenRider.Dad.ConfigureAndEnable", endpoint);
                    Events.Add($"configure:{values![0]}");
                    return true;
                });
            });
            Combat = new(Configuration, pi, Proxy<IPluginLog>((method, _) =>
                { if (method.Name == "Warning") WarningCount++; return null; }),
                expected => ScopeAvailable && (expected == 0 || expected == 4) ? 4u : 0u);
        }

        public object? Invoke(string method, params object?[] args)
            => Combat.GetType().GetMethod(method, PrivateInstance)!.Invoke(Combat, args);
        public string Enable(string runId, bool completed = false)
            => Invoke("TryEnableFrenRiderAfterDutyEntry", runId, DadRuntime::dad.Models.DadModuleId.Duty,
                DateTime.UtcNow, null, 4u, !completed)!.ToString()!;
        public bool Release(string runId) => (bool)Invoke("ReleaseDungeonRsrAggro", runId)!;
        public void Dispose() => commandProperty.SetValue(null, previousCommands);
    }

    private unsafe delegate int ReadSelectedRegularDuty(AgentContentsFinder* agent);

    [Fact]
    public unsafe void RegularDutyProofReadsTheOnlyCheckedDutyRatherThanStaleRewardDetails()
    {
        var read = typeof(DutyIpc).Assembly.GetType("dad.Services.DadDutyFinderNativeAccess")!
            .GetMethod("ReadSelectedRegularDutyId", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<ReadSelectedRegularDuty>();
        var agent = new AgentContentsFinder();
        var selected = stackalloc ContentsId[2];
        selected[0] = new ContentsId { ContentType = ContentsType.Regular, Id = 76 };
        selected[1] = new ContentsId { ContentType = ContentsType.Regular, Id = 81 };
        agent.SelectedDuty = selected[0];
        agent.InterfaceSub.SelectedDutyId = 20021; // Observed stale reward-pane roulette value.
        agent.SelectedContent.First = selected;
        agent.SelectedContent.Last = selected + 1;
        agent.SelectedContent.End = selected + 2;

        Assert.Equal(76, read(&agent));
        agent.SelectedContent.Last = selected;
        Assert.Equal(0, read(&agent)); // Browsing the target does not select it.
        agent.SelectedContent.Last = selected + 2;
        Assert.Equal(0, read(&agent)); // Never admit several checked duties.
        agent.SelectedContent.Last = selected + 1;
        selected[0] = new ContentsId { ContentType = ContentsType.Roulette, Id = 76 };
        Assert.Equal(0, read(&agent)); // Equal numeric ids cannot mix content types.
        selected[0] = selected[1];
        Assert.Equal(81, read(&agent)); // Report the checked duty, not the browsed one.
        Assert.Equal(0, read(null));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("Meta.", "Meta.")]
    [InlineData("Meta.", "DutyConfig.")]
    public void WigglyQuestCheckedSettingsAndLootHandshakeDoNotDispatchWork(string prefix, string dutyPrefix)
    {
        var handlers = new Dictionary<string, Delegate>();
        var outbound = new List<string>();
        object? Unexpected(string call)
        {
            outbound.Add(call);
            throw new InvalidOperationException($"Unexpected outbound action: {call}");
        }
        var pi = Proxy<IDalamudPluginInterface>((method, args) => method.Name switch
        {
            "GetIpcProvider" => Proxy(method.ReturnType, (call, values) =>
            {
                var channel = (string)args![0]!;
                if (call.Name is "RegisterFunc" or "RegisterAction")
                    handlers.Add(channel, (Delegate)values![0]!);
                else if (call.Name is "UnregisterFunc" or "UnregisterAction")
                    handlers.Remove(channel);
                else
                    return Unexpected($"{channel}.{call.Name}");
                return null;
            }),
            "GetIpcSubscriber" => Proxy(method.ReturnType, (call, _) => Unexpected($"{args![0]}.{call.Name}")),
            _ => Unexpected(method.Name),
        });
        var pluginType = typeof(DutyIpc).Assembly.GetType("dad.Plugin")!;
        var replacements = new Dictionary<string, object>
        {
            ["PluginInterface"] = pi,
            ["Framework"] = Proxy<IFramework>((method, _) => method.Name == "get_IsInFrameworkUpdateThread"
                ? true : Unexpected(method.Name)),
            ["CommandManager"] = Proxy<ICommandManager>((method, _) => Unexpected(method.Name)),
        };
        var previous = replacements.Keys.ToDictionary(name => name, name => pluginType.GetProperty(name)!.GetValue(null));
        try
        {
            foreach (var (name, replacement) in replacements)
                pluginType.GetProperty(name)!.SetValue(null, replacement);
            var log = Proxy<IPluginLog>((_, _) => null);
            var configuration = new DadRuntime::dad.Configuration { CombatRotationMode = DadRuntime::dad.DadCombatRotationMode.ForceCommands };
            var combat = new DadRuntime::dad.Services.DadCombatRotationService(configuration, pi, log);
            var ads = new DadRuntime::dad.Services.DadDutySupportAdsService(pi, log);
            using var duty = new DutyIpc(pi, null!, null!, null!, ads, combat, log);
            var get = Assert.IsType<Func<string, string>>(handlers["dad.Duty.GetConfig"]);
            var set = Assert.IsType<Action<string, string>>(handlers["dad.Duty.SetConfig"]);
            var stopped = Assert.IsType<Func<bool>>(handlers["dad.Duty.IsStopped"]);
            set(" Unsynced ", " True ");
            set(" dutyModeEnum ", " Regular ");
            set(" leveling ", " Support ");
            set(" AutoDutyModeEnum ", " Once ");
            // WigglyQuest writes and immediately reads these names
            // before it will request a duty. All naming generations must
            // address the same session values without starting any work.
            foreach (var (key, value) in new[] { ("AutoDutyModeEnum", "Looping"), ("Unsynced", "True"), ("DutyModeEnum", "Regular") })
            {
                set(prefix + key, value);
                Assert.Equal(value, get(prefix + key));
                Assert.Equal(value, get(key));
                Assert.Equal(value, get("Meta." + key));
            }
            Assert.Equal("True", get(dutyPrefix + "AutoManageRotationPluginState"));
            Assert.Equal("True", get(dutyPrefix + "AutoManageBossModAISettings"));
            var existingKeys = new[] { "Unsynced", "dutyModeEnum", "leveling", "AutoDutyModeEnum",
                "AutoManageRotationPluginState", "AutoManageBossModAISettings", "UnknownKey" };
            var existingResponses = existingKeys.ToDictionary(key => key, get);
            var savedConfiguration = JsonSerializer.Serialize(configuration);
            var loot = new[] { (Key: "LootTreasure", Expected: "True", Opposite: "False"),
                (Key: "LootBossTreasureOnly", Expected: "False", Opposite: "True"),
                (Key: "LootMethodEnum", Expected: "AutoDuty", Opposite: "Pandora") };
            var original = loot.ToDictionary(item => item.Key, item => get(dutyPrefix + item.Key));
            Assert.True(stopped());
            Assert.Empty(outbound);
            foreach (var item in loot)
                Assert.Equal(item.Expected, original[item.Key]);

            var current = new Dictionary<string, string>(original);
            Assert.Equal((1, 0), Assert.IsType<Func<(int Major, int Minor)>>(handlers["dad.Duty.ApiVersion"])());
            // Force the caller's values, try contrary values, then restore its captured values.
            foreach (var phase in new[] { "force", "opposite", "restore" })
            foreach (var item in loot)
            {
                var value = phase == "force" ? item.Expected : phase == "opposite" ? item.Opposite : original[item.Key];
                set(phase == "force" ? dutyPrefix + item.Key : $" \t{dutyPrefix}{item.Key.ToUpperInvariant()} ", $" {value} ");
                current[item.Key] = value;
                foreach (var expected in loot)
                {
                    Assert.Equal(current[expected.Key], get(expected.Key));
                    Assert.Equal(current[expected.Key], get(dutyPrefix + expected.Key));
                    Assert.Equal(current[expected.Key], get($" \t{expected.Key.ToLowerInvariant()} "));
                }
                foreach (var (key, response) in existingResponses)
                    Assert.Equal(response, get(key));
                Assert.Equal(savedConfiguration, JsonSerializer.Serialize(configuration));
                Assert.True(stopped());
                Assert.Empty(outbound);
            }
        }
        finally
        {
            foreach (var (name, value) in previous)
                pluginType.GetProperty(name)!.SetValue(null, value);
        }
    }

    [Fact]
    public void ContentHasPathUsesTerritoryCatalogWithoutStartingDuty()
    {
        var handlers = new Dictionary<string, Delegate>();
        var outbound = new List<string>();
        object? Unexpected(string call)
        {
            outbound.Add(call);
            throw new InvalidOperationException($"Unexpected outbound action: {call}");
        }
        var pi = Proxy<IDalamudPluginInterface>((method, args) => method.Name switch
        {
            "add_ActivePluginsChanged" or "remove_ActivePluginsChanged" => null,
            "GetIpcProvider" => Proxy(method.ReturnType, (call, values) =>
            {
                var channel = (string)args![0]!;
                if (call.Name is "RegisterFunc" or "RegisterAction") handlers.Add(channel, (Delegate)values![0]!);
                else if (call.Name is "UnregisterFunc" or "UnregisterAction") handlers.Remove(channel);
                else return Unexpected($"{channel}.{call.Name}");
                return null;
            }),
            "GetIpcSubscriber" => Proxy(method.ReturnType, (call, values) =>
            {
                var channel = (string)args![0]!;
                if (channel == "ADS.Duty.GetMaturityJson" && call.Name == "InvokeFunc")
                    return System.Text.Json.JsonSerializer.Serialize(new
                    {
                        schemaVersion = 1, success = true, territoryTypeId = (uint)values![0]!,
                        contentFinderConditionId = (uint)values[1]!, clearanceLevel = 1,
                        clearanceStatus = "OnePlayerUnsyncCleared", eligibleForQuest = true,
                    });
                return channel == "dad.Duty.ContentHasPath" && call.Name == "InvokeFunc"
                    ? Assert.IsType<Func<uint, bool>>(handlers[channel])((uint)values![0]!)
                    : Unexpected($"{channel}.{call.Name}");
            }),
            _ => Unexpected(method.Name),
        });
        var pluginType = typeof(DutyIpc).Assembly.GetType("dad.Plugin")!;
        var replacements = new Dictionary<string, object>
        {
            ["PluginInterface"] = pi,
            ["Framework"] = Proxy<IFramework>((method, _) => method.Name == "get_IsInFrameworkUpdateThread"
                ? true : Unexpected(method.Name)),
            ["CommandManager"] = Proxy<ICommandManager>((method, _) => Unexpected(method.Name)),
        };
        var previous = replacements.Keys.ToDictionary(name => name, name => pluginType.GetProperty(name)!.GetValue(null));
        try
        {
            foreach (var (name, replacement) in replacements) pluginType.GetProperty(name)!.SetValue(null, replacement);
            var log = Proxy<IPluginLog>((_, _) => null);
            var catalog = new DadRuntime::dad.Services.DadPresetProviderService(new DadRuntime::dad.Services.DadModuleRegistry(),
                () => [], dutyCatalogProvider: () =>
                [
                    new() { ContentFinderConditionId = 41, TerritoryType = 901, DutyDisplayName = "Synthetic supported duty", SupportsDutySupport = true },
                    new() { ContentFinderConditionId = 0, TerritoryType = 902, DutyDisplayName = "Synthetic invalid duty", SupportsDutySupport = true },
                ]);
            var combat = new DadRuntime::dad.Services.DadCombatRotationService(new DadRuntime::dad.Configuration(), pi, log);
            var ads = new DadRuntime::dad.Services.DadDutySupportAdsService(pi, log);
            using var duty = new DutyIpc(pi, catalog, null!, null!, ads, combat, log);
            var contentHasPath = pi.GetIpcSubscriber<uint, bool>("dad.Duty.ContentHasPath");
            Assert.True(contentHasPath.InvokeFunc(901));
            var status = duty.GetStatus();
            Assert.True(status.LastContentHasPathResult);
            Assert.Equal(901u, status.LastContentHasPathTerritoryType);
            Assert.Equal(41u, status.LastContentHasPathSelectedContentFinderConditionId);
            Assert.False(contentHasPath.InvokeFunc(41));
            Assert.False(contentHasPath.InvokeFunc(902));
            Assert.False(contentHasPath.InvokeFunc(903));
            Assert.Empty(outbound);
            Assert.Equal("Stopped", duty.GetStatus().SessionState);
            var set = Assert.IsType<Action<string, string>>(handlers["dad.Duty.SetConfig"]);
            var failure = Assert.IsType<Func<string>>(handlers["dad.Duty.GetLastFailure"]);
            var policy = Assert.IsType<Func<string>>(handlers["dad.Duty.GetLootPolicyJson"]);
            var run = Assert.IsType<Action<uint, int, bool>>(handlers["dad.Duty.Run"]);
            set("DutyConfig.LootTreasure", "invalid");
            Assert.Contains("Invalid LootTreasure", failure());
            run(901, 1, true);
            Assert.Contains("Invalid LootTreasure", failure());
            Assert.Empty(outbound); // Invalid settings cannot touch ADS or queue state.
            set("DutyConfig.LootTreasure", "True");
            run(901, 1, true);
            Assert.Contains("ADS temporary duty loot IPC is unavailable", failure());
            Assert.Equal(new[] { "ADS.Duty.ValidateLootPolicy.InvokeFunc" }, outbound);
            Assert.Equal("Stopped", duty.GetStatus().SessionState);
            Assert.True(duty.GetStatus().LastBareMode);
            using var state = JsonDocument.Parse(policy());
            Assert.False(state.RootElement.GetProperty("active").GetBoolean());
        }
        finally
        {
            foreach (var (name, value) in previous) pluginType.GetProperty(name)!.SetValue(null, value);
        }
    }

    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
        => (T)Proxy(typeof(T), handler);

    private static object Proxy(Type type, Func<MethodInfo, object?[]?, object?> handler)
    {
        var proxy = DispatchProxy.Create(type, typeof(DadQuestionableTestProxy));
        ((DadQuestionableTestProxy)proxy).Handler = handler;
        return proxy;
    }

}

public class DadQuestionableTestProxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Handler = null!;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
}
