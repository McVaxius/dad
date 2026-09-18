using System.Text.Json;
using dad.Models;
using dad.Services;

namespace dad.Headless;

// Supplies only observations/responses at external automation IPC and command boundaries.
internal sealed class VirtualAutomationIpc(Action<string> observe)
{
    public bool PostprocessRequested { get; private set; }
    private bool readyDelivered;
    public bool TakeReadyCallback()
    {
        if (!PostprocessRequested || readyDelivered) return false;
        readyDelivered = true; return true;
    }
    public bool Suppressed { get; private set; }
    public bool MultiMode { get; set; }
    public bool Busy { get; set; }
    public bool LifestreamBusy { get; set; }
    public bool UncertainLogin { get; set; }
    public string Surface { get; set; } = "world";
    public DadTitleNativeObservation Title() => Surface switch
    {
        "world" => new(new(false, false, false, false, false, false), false, true, false, false),
        "title" => new(new(true, false, false, false, false, false), true, true, true, false),
        "movie" => new(new(false, true, false, false, false, false), true, true, false, true),
        "connecting" => new(new(false, false, true, false, false, false), true, true, false, false),
        "character-select" => new(new(false, false, false, true, false, false), true, true, false, false),
        "dialog" => new(new(true, false, false, false, false, true), true, true, true, false),
        _ => throw new InvalidOperationException($"Unknown title observation: {Surface}")
    };
    public object? Invoke(string endpoint, string kind, object?[] values)
    {
        if (kind == "InvokeFunc")
        {
            switch (endpoint)
            {
                case "VERMAXION.GetAutomationStatusJson": return JsonSerializer.Serialize(new
                    { version = 1, isBusy = false, activity = "Idle", state = "Idle", generatedAtUtc = DadClock.UtcNow });
                case "AutoRetainer.GetSuppressed": return Suppressed;
                case "AutoRetainer.GetMultiModeEnabled": return MultiMode;
                case "AutoRetainer.PluginState.IsBusy": return Busy;
                case "Lifestream.IsBusy": return LifestreamBusy;
                case "Lifestream.CanAutoLogin": return Surface == "title";
                case "Lifestream.ConnectAndLogin":
                    observe($"ipc:login:{values[0]}@{values[1]}");
                    if (UncertainLogin) throw new IOException("Injected lost login response.");
                    return true;
                case "Lifestream.ChangeWorld":
                    if ((string?)values[0] != "Synthetic") throw new InvalidOperationException("Unknown world target.");
                    observe("ipc:change-world:Synthetic"); return true;
            }
        }
        if (kind == "InvokeAction")
        {
            switch (endpoint)
            {
                case "AutoRetainer.SetSuppressed": Suppressed = (bool)values[0]!; break;
                case "AutoRetainer.SetMultiModeEnabled": MultiMode = (bool)values[0]!; break;
                case "AutoRetainer.RequestCharacterPostprocess": PostprocessRequested = true; readyDelivered = false; break;
                case "AutoRetainer.FinishCharacterPostprocessRequest": PostprocessRequested = false; break;
                default: throw new InvalidOperationException($"Unknown automation IPC: {endpoint}:{kind}");
            }
            observe($"ipc:{endpoint}:{JsonSerializer.Serialize(values)}");
            return null;
        }
        throw new InvalidOperationException($"Unknown automation IPC: {endpoint}:{kind}");
    }
    public bool Command(string command)
    {
        if (command is not ("/ays d" or "/ays reset" or "/ays m d" or "/fr on" or "/fr off" or
            "/shopping-fixture" or "/ads outside" or "/ads stop" or "/ads leave" or "/rotation cancel" or
            "/vbmai off" or "/bmrai off" or "/wrath auto off" or "/bmrai on" or "/rotation auto") &&
            !(command.StartsWith("/ays relog Lab ", StringComparison.Ordinal) && command.EndsWith("@Synthetic", StringComparison.Ordinal)))
            throw new InvalidOperationException($"Unknown automation command: {command}");
        if (command == "/ays m d") MultiMode = false;
        observe($"command:{command}");
        return true;
    }
}
