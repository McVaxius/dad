using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace dad;

// Composition boundary only. Lifecycle code is linked unchanged from Services.
// Unconfigured external services throw before any native or IPC action can run.
internal static class Plugin
{
    public static IDalamudPluginInterface PluginInterface { get; set; } = null!;
    public static IClientState ClientState => throw External(nameof(ClientState));
    public static IObjectTable ObjectTable => throw External(nameof(ObjectTable));
    public static IPlayerState PlayerState => throw External(nameof(PlayerState));
    public static IPartyList PartyList => throw External(nameof(PartyList));
    public static IDataManager DataManager => throw External(nameof(DataManager));
    public static ICondition Condition { get; set; } = null!;
    public static IFramework Framework { get; set; } = null!;
    public static IDutyState DutyState { get; set; } = null!;
    public static ICommandManager CommandManager { get; set; } = null!;

    private static Exception External(string name) => new InvalidOperationException($"Unexpected external request: {name}");
}
