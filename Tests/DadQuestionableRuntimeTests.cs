extern alias DadRuntime;
extern alias DalamudApi;

using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using Bridge = DadRuntime::dad.Services.DadQuestionableReflectionBridge;
using DalamudApi::Dalamud.Plugin;
using DalamudApi::Dalamud.Plugin.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadQuestionableRuntimeTests
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type WrapperType = CreateWrapperType();

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
        var plugin = module.DefineType("Questionable.QuestionablePlugin", TypeAttributes.Public).CreateType()!;
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
}

public class DadQuestionableTestProxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Handler = null!;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
}
