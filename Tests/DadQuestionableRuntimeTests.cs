extern alias DadRuntime;
extern alias DalamudApi;

using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using Bridge = DadRuntime::dad.Services.DadQuestionableReflectionBridge;
using DutyIpc = DadRuntime::dad.Services.DadDutyIpcService;
using DalamudApi::Dalamud.Plugin;
using DalamudApi::Dalamud.Plugin.Services;
using DalamudApi::Dalamud.Plugin.Ipc;
using Xunit;

namespace dad.Tests;

public sealed class DadQuestionableRuntimeTests
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type WrapperType = CreateWrapperType();

    [Fact]
    public void WigglyQuestLootHandshakeUsesFixedResponsesWithoutStateChangesOrOutboundActions()
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
            var existingKeys = new[] { "Unsynced", "dutyModeEnum", "leveling", "AutoDutyModeEnum",
                "AutoManageRotationPluginState", "AutoManageBossModAISettings", "UnknownKey" };
            var existingResponses = existingKeys.ToDictionary(key => key, get);
            var savedConfiguration = JsonSerializer.Serialize(configuration);
            var savedStatus = JsonSerializer.Serialize(duty.GetStatus());
            var savedFields = typeof(DutyIpc).GetFields(PrivateInstance).Where(field => !field.IsInitOnly)
                .ToDictionary(field => field, field => field.GetValue(duty));
            var statusField = typeof(DutyIpc).GetField("status", PrivateInstance)!;
            var loot = new[] { (Key: "LootTreasure", Expected: "True", Opposite: "False"),
                (Key: "LootBossTreasureOnly", Expected: "False", Opposite: "True"),
                (Key: "LootMethodEnum", Expected: "AutoDuty", Opposite: "None") };
            var original = loot.ToDictionary(item => item.Key, item => get(item.Key));
            Assert.True(stopped());
            Assert.Empty(outbound);
            foreach (var item in loot)
                Assert.Equal(item.Expected, original[item.Key]);

            // Force the caller's values, try contrary values, then restore its captured values.
            foreach (var phase in new[] { "force", "opposite", "restore" })
            foreach (var item in loot)
            {
                var value = phase == "force" ? item.Expected : phase == "opposite" ? item.Opposite : original[item.Key];
                set(phase == "force" ? item.Key : $" \t{item.Key.ToUpperInvariant()} ", $" {value} ");
                foreach (var expected in loot)
                {
                    Assert.Equal(expected.Expected, get(expected.Key));
                    Assert.Equal(expected.Expected, get($" \t{expected.Key.ToLowerInvariant()} "));
                }
                foreach (var (key, response) in existingResponses)
                    Assert.Equal(response, get(key));
                Assert.Equal(savedConfiguration, JsonSerializer.Serialize(configuration));
                Assert.Equal(savedStatus, JsonSerializer.Serialize(statusField.GetValue(duty)));
                foreach (var (field, valueBefore) in savedFields)
                    Assert.Equal(valueBefore, field.GetValue(duty));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartSubscriberIsPatchedWithExactTypeAndRestoredOnlyWhileOwned(bool externalStart)
    {
        var exposed = Exposed("Questionable");
        var calls = new List<string>();
        var pi = Proxy<IDalamudPluginInterface>((method, args) => method.Name switch
        {
            "get_InstalledPlugins" => new[] { exposed },
            "GetIpcSubscriber" => Proxy(method.ReturnType, (call, values) =>
            {
                calls.Add($"{args![0]}:{call.Name}:{values![0]}");
                return null;
            }),
            _ => null,
        });
        using var bridge = NewBridge(pi);
        var instance = typeof(Bridge).GetMethod("ResolveQuestionableInstance", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [exposed])!;
        var assembly = instance.GetType().Assembly;
        var config = Activator.CreateInstance(assembly.GetType("Questionable.Configuration")!)!;
        var auto = Activator.CreateInstance(assembly.GetType("Questionable.External.AutoDutyIpc")!)!;
        auto.GetType().GetField("_configuration")!.SetValue(auto, config);
        var originals = new Dictionary<FieldInfo, object>();
        foreach (var field in auto.GetType().GetFields().Where(field => field.Name != "_configuration"))
        {
            var original = Proxy(field.FieldType, (_, _) => null);
            originals.Add(field, original);
            field.SetValue(auto, original);
        }
        instance.GetType().GetField("_serviceProvider")!.SetValue(instance,
            Proxy<IServiceProvider>((_, args) => (Type)args![0]! == auto.GetType() ? auto : config));
        var prepared = Invoke(bridge, "PreparePatch", exposed, instance)!;
        Invoke(bridge, "ApplyPatch", prepared);
        Assert.Equal(7, originals.Count);
        var start = auto.GetType().GetField("_start")!;
        Assert.Equal(typeof(ICallGateSubscriber<bool, object>), start.FieldType);
        ((ICallGateSubscriber<bool, object>)start.GetValue(auto)!).InvokeAction(true);
        Assert.Equal(new[] { "dad.Duty.Start:InvokeAction:True" }, calls);
        var external = Proxy<ICallGateSubscriber<bool, object>>((_, _) => null);
        if (externalStart) start.SetValue(auto, external);
        bridge.Dispose();
        foreach (var (field, original) in originals)
            Assert.Same(externalStart && field.Name == "_start" ? external : original, field.GetValue(auto));
        Assert.False(((QuestionableConfigFixture)config).Duties.RunInstancedContentWithAutoDuty);
    }

    [Theory]
    [InlineData("Questionable", "WigglyQuest")]
    [InlineData("WigglyQuest", "Questionable")]
    public void DiscoveryReloadEventsAndFalseIpcUseTheSelectedNameOnce(string name, string otherName)
    {
        var active = Exposed(name);
        IExposedPlugin[] installed = [Exposed(otherName, loaded: false), active];
        var calls = new List<string>();
        var fail = false;
        var pi = Proxy<IDalamudPluginInterface>((method, args) => method.Name switch
        {
            "get_InstalledPlugins" => installed,
            "GetIpcSubscriber" => Proxy(method.ReturnType, (call, _) =>
            {
                Assert.Equal("InvokeFunc", call.Name);
                calls.Add((string)args![0]!);
                if (fail) throw new InvalidOperationException("IPC unavailable");
                return false;
            }),
            _ => null,
        });
        using var bridge = NewBridge(pi);
        Assert.Same(active, Invoke(bridge, "FindLoadedQuestionable"));
        Assert.Equal(false, Invoke(bridge, "QueryQuestionableIsRunning", active.InternalName));
        Assert.Equal(new[] { name + ".IsRunning" }, calls);
        fail = true;
        calls.Clear();
        Assert.Throws<TargetInvocationException>(() => Invoke(bridge, "QueryQuestionableIsRunning", active.InternalName));
        Assert.Equal(new[] { name + ".IsRunning" }, calls);
        installed = [active, Exposed(otherName)];
        Assert.Contains("Multiple", Assert.Throws<TargetInvocationException>(() => Invoke(bridge, "FindLoadedQuestionable")).InnerException!.Message);
        installed = [Exposed(name, loaded: false)];
        Assert.Null(Invoke(bridge, "FindLoadedQuestionable"));
        installed = [];
        Assert.Null(Invoke(bridge, "FindLoadedQuestionable"));
        var reloaded = Exposed(otherName);
        installed = [reloaded];
        Assert.Same(reloaded, Invoke(bridge, "FindLoadedQuestionable"));
        typeof(Bridge).GetField("probeRequested", PrivateInstance)!.SetValue(bridge, false);
        Invoke(bridge, "OnActivePluginsChanged", Proxy<IActivePluginsChangedEventArgs>((method, _) =>
            method.Name == "get_AffectedInternalNames" ? new HashSet<string> { otherName } : null));
        Assert.Equal(true, typeof(Bridge).GetField("probeRequested", PrivateInstance)!.GetValue(bridge));
    }

    [Theory]
    [InlineData("Questionable", "WigglyQuest", false)]
    [InlineData("WigglyQuest", "Questionable", false)]
    [InlineData("Questionable", "Questionable", false)]
    [InlineData("WigglyQuest", "Questionable", true)]
    public void AmbiguityRestoresOnlyOwnedValuesOnTheCapturedInstance(string name, string otherName, bool externallyChanged)
    {
        var captured = Exposed(name);
        var other = Exposed(otherName);
        IExposedPlugin[] installed = [other, captured];
        var pi = Proxy<IDalamudPluginInterface>((method, _) => method.Name == "get_InstalledPlugins" ? installed : null);
        using var bridge = NewBridge(pi);
        var fixture = SeedOwnership(bridge, captured);
        var externalValue = new object();
        if (externallyChanged)
        {
            fixture.Subscriber = externalValue;
            fixture.Recommendations = externalValue;
            fixture.Gate = false;
        }

        // Calls the production ambiguity handling and both production reflection restore paths.
        Invoke(bridge, "MaintainBridge");

        Assert.Same(externallyChanged ? externalValue : fixture.OriginalSubscriber, fixture.Subscriber);
        Assert.Same(externallyChanged ? externalValue : fixture.OriginalRecommendations, fixture.Recommendations);
        Assert.False(fixture.Gate);
        Assert.Null(typeof(Bridge).GetField("ownership", PrivateInstance)!.GetValue(bridge));
        Assert.Null(typeof(Bridge).GetField("cosmeticOwnership", PrivateInstance)!.GetValue(bridge));
        Assert.False(bridge.GetStatus().Patched);
        Assert.False(bridge.GetStatus().CosmeticPatched);
        Assert.Contains("Multiple", bridge.GetStatus().LastBlocker);
    }

    [Fact]
    public void UnloadedOwnershipCannotBeAppliedToTheReplacementInstance()
    {
        var captured = Exposed("Questionable");
        IExposedPlugin[] installed = [captured];
        var pi = Proxy<IDalamudPluginInterface>((method, _) => method.Name == "get_InstalledPlugins" ? installed : null);
        using var bridge = NewBridge(pi);
        var oldFixture = SeedOwnership(bridge, captured);
        var oldSubscriber = oldFixture.Subscriber;
        var reloaded = Exposed("WigglyQuest");
        installed = [reloaded];
        Invoke(bridge, "RestoreOwnedValues");
        Invoke(bridge, "RestoreOwnedCosmeticValue");
        Assert.Null(typeof(Bridge).GetField("ownership", PrivateInstance)!.GetValue(bridge));
        Assert.Null(typeof(Bridge).GetField("cosmeticOwnership", PrivateInstance)!.GetValue(bridge));
        Assert.Same(oldSubscriber, oldFixture.Subscriber);
        var newFixture = SeedOwnership(bridge, reloaded);
        bridge.Dispose();
        Assert.Same(newFixture.OriginalSubscriber, newFixture.Subscriber);
        Assert.Same(newFixture.OriginalRecommendations, newFixture.Recommendations);
        Assert.Same(oldSubscriber, oldFixture.Subscriber);
    }

    [Fact]
    public void CleanupSearchContinuesPastAnUnreflectableDuplicate()
    {
        var captured = Exposed("Questionable");
        var broken = Proxy<IExposedPlugin>((method, _) => method.Name switch
        {
            "get_InternalName" => "WigglyQuest",
            "get_IsLoaded" => true,
            _ => null,
        });
        var pi = Proxy<IDalamudPluginInterface>((method, _) => method.Name == "get_InstalledPlugins"
            ? new[] { broken, captured } : null);
        using var bridge = NewBridge(pi);
        var fixture = SeedOwnership(bridge, captured);
        bridge.Dispose();
        Assert.Same(fixture.OriginalSubscriber, fixture.Subscriber);
        Assert.Same(fixture.OriginalRecommendations, fixture.Recommendations);
        Assert.False(fixture.Gate);
    }

    private static Bridge NewBridge(IDalamudPluginInterface pi)
        => new(pi, Proxy<IFramework>((_, _) => null), null!, Proxy<IPluginLog>((_, _) => null), () => true);

    private static object? Invoke(Bridge bridge, string method, params object[] args)
        => typeof(Bridge).GetMethod(method, PrivateInstance)!.Invoke(bridge, args);

    private static RestoreFixture SeedOwnership(Bridge bridge, IExposedPlugin captured)
    {
        var fixture = new RestoreFixture();
        var instance = typeof(Bridge).GetMethod("ResolveQuestionableInstance", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [captured]);
        object Create(string typeName, params (string Name, object? Value)[] properties)
        {
            var type = typeof(Bridge).GetNestedType(typeName, BindingFlags.NonPublic)!;
            var result = Activator.CreateInstance(type)!;
            foreach (var (name, value) in properties) type.GetProperty(name)!.SetValue(result, value);
            return result;
        }
        var subscriber = Create("SubscriberOwnership",
            ("Field", typeof(RestoreFixture).GetField(nameof(RestoreFixture.Subscriber))),
            ("Original", fixture.OriginalSubscriber), ("Replacement", fixture.Subscriber));
        var subscribers = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(subscriber.GetType()))!;
        subscribers.Add(subscriber);
        var patch = Create("PatchOwnership", ("QuestionableInstance", instance),
            ("AutoDutyIpc", fixture), ("Duties", fixture),
            ("DutyGateProperty", typeof(RestoreFixture).GetProperty(nameof(RestoreFixture.Gate))),
            ("PreviousDutyGateValue", false), ("Version", "test"), ("Subscribers", subscribers),
            ("ExpectedSubscriberCount", 1), ("DutyGateOwned", true));
        typeof(Bridge).GetField("ownership", PrivateInstance)!.SetValue(bridge, patch);
        var cosmetic = Create("CosmeticPatchOwnership", ("QuestionableInstance", instance),
            ("PluginConfigComponent", fixture),
            ("RecommendedPluginsField", typeof(RestoreFixture).GetField(nameof(RestoreFixture.Recommendations))),
            ("OriginalList", fixture.OriginalRecommendations), ("ReplacementList", fixture.Recommendations),
            ("ReplacementEntry", new object()), ("ReplacementIndex", 0), ("ExpectedEntries", Array.Empty<object>()));
        typeof(Bridge).GetField("cosmeticOwnership", PrivateInstance)!.SetValue(bridge, cosmetic);
        return fixture;
    }

    private static Type CreateWrapperType()
    {
        var module = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("QuestionableRenameDoubles"), AssemblyBuilderAccess.Run)
            .DefineDynamicModule("Doubles");
        var configuration = module.DefineType("Questionable.Configuration", TypeAttributes.Public, typeof(QuestionableConfigFixture)).CreateType()!;
        var auto = module.DefineType("Questionable.External.AutoDutyIpc", TypeAttributes.Public);
        auto.DefineField("_configuration", configuration, FieldAttributes.Public);
        foreach (var (name, type) in new (string, Type)[]
                 { ("_contentHasPath", typeof(ICallGateSubscriber<uint, bool>)), ("_getConfig", typeof(ICallGateSubscriber<string, string>)),
                   ("_setConfig", typeof(ICallGateSubscriber<string, string, object>)), ("_run", typeof(ICallGateSubscriber<uint, int, bool, object>)),
                   ("_start", typeof(ICallGateSubscriber<bool, object>)), ("_isStopped", typeof(ICallGateSubscriber<bool>)), ("_stop", typeof(ICallGateSubscriber<object>)) })
            auto.DefineField(name, type, FieldAttributes.Public);
        auto.CreateType();
        var pluginBuilder = module.DefineType("Questionable.QuestionablePlugin", TypeAttributes.Public);
        pluginBuilder.DefineField("_serviceProvider", typeof(IServiceProvider), FieldAttributes.Public);
        var plugin = pluginBuilder.CreateType()!;
        var local = module.DefineType("Dalamud.Plugin.Internal.Types.LocalPlugin", TypeAttributes.Public);
        local.DefineField("instance", typeof(object), FieldAttributes.Public);
        var localType = local.CreateType()!;
        var wrapper = module.DefineType("Dalamud.Plugin.ExposedPlugin", TypeAttributes.Public, typeof(object), [typeof(IExposedPlugin)]);
        wrapper.DefineField("<plugin>P", localType, FieldAttributes.Public);
        var nameField = wrapper.DefineField("TestName", typeof(string), FieldAttributes.Public);
        var loadedField = wrapper.DefineField("TestLoaded", typeof(bool), FieldAttributes.Public);
        foreach (var member in typeof(IExposedPlugin).GetMethods())
        {
            var method = wrapper.DefineMethod(member.Name, MethodAttributes.Public | MethodAttributes.Virtual,
                member.ReturnType, member.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
            var il = method.GetILGenerator();
            if (member.Name is "get_InternalName" or "get_IsLoaded")
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, member.Name == "get_InternalName" ? nameField : loadedField);
            }
            else if (member.ReturnType != typeof(void))
            {
                var result = il.DeclareLocal(member.ReturnType);
                il.Emit(OpCodes.Ldloca_S, result);
                il.Emit(OpCodes.Initobj, member.ReturnType);
                il.Emit(OpCodes.Ldloc, result);
            }
            il.Emit(OpCodes.Ret);
            wrapper.DefineMethodOverride(method, member);
        }
        return wrapper.CreateType()!;
    }

    private static IExposedPlugin Exposed(string name, bool loaded = true)
    {
        var wrapper = Activator.CreateInstance(WrapperType)!;
        WrapperType.GetField("TestName")!.SetValue(wrapper, name);
        WrapperType.GetField("TestLoaded")!.SetValue(wrapper, loaded);
        var localField = WrapperType.GetField("<plugin>P")!;
        var local = Activator.CreateInstance(localField.FieldType)!;
        localField.FieldType.GetField("instance")!.SetValue(local,
            Activator.CreateInstance(WrapperType.Assembly.GetType("Questionable.QuestionablePlugin")!));
        localField.SetValue(wrapper, local);
        return (IExposedPlugin)wrapper;
    }

    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
        => (T)Proxy(typeof(T), handler);

    private static object Proxy(Type type, Func<MethodInfo, object?[]?, object?> handler)
    {
        var proxy = DispatchProxy.Create(type, typeof(DadQuestionableTestProxy));
        ((DadQuestionableTestProxy)proxy).Handler = handler;
        return proxy;
    }

    public sealed class RestoreFixture
    {
        public object OriginalSubscriber = new();
        public object OriginalRecommendations = new();
        public object Subscriber = new();
        public object Recommendations = new();
        public bool Gate { get; set; } = true;
    }

    public class QuestionableConfigFixture
    {
        public QuestionableDutyFixture Duties { get; } = new();
    }

    public sealed class QuestionableDutyFixture
    {
        public bool RunInstancedContentWithAutoDuty { get; set; }
    }
}

public class DadQuestionableTestProxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Handler = null!;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
}
