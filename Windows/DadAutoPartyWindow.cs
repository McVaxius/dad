using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using dad.Models;

namespace dad.Windows;

public sealed class DadAutoPartyWindow : Window
{
    private readonly Plugin plugin;
    private string pilotExchangeRoot = DadAutoPartyConfiguration.DefaultPilotExchangeRoot;
    private string endpointAlias = string.Empty;
    private string botToken = string.Empty;
    private string guildId = string.Empty;
    private string channelId = string.Empty;
    private string status = "Discord discovery and pairing are disabled.";
    private Task<string>? operationTask;

    public DadAutoPartyWindow(Plugin plugin)
        : base("DAD AutoParty###DadAutoParty", ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;
        RespectCloseHotkey = false;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(680f, 560f),
            MaximumSize = new Vector2(1200f, 1000f),
        };
        Size = new Vector2(820f, 760f);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void OnOpen()
    {
        var configuration = plugin.Configuration.AutoParty;
        pilotExchangeRoot = configuration.PilotExchangeRoot;
        endpointAlias = configuration.EndpointAlias;
        guildId = configuration.DiscordGuildId == 0 ? string.Empty : configuration.DiscordGuildId.ToString();
        channelId = configuration.DiscordChannelId == 0 ? string.Empty : configuration.DiscordChannelId.ToString();
        botToken = string.Empty;
    }

    public override void Draw()
    {
        ObserveTask();
        var configuration = plugin.Configuration.AutoParty;
        var discord = plugin.AutoPartyDiscordService;
        var health = discord.Health;

        DadUi.Heading("AutoParty Discord pairing", "Private signed discovery and coordinator-star pairing; all work stays on the DAD LAN hub.");
        ImGui.TextWrapped("Give every DAD its own Discord application and bot. Enable only Message Content Intent. The shared private #dad-pairing channel needs View Channel, Send Messages, and Read Message History; the invite itself requests zero server-wide permissions.");
        ImGui.TextWrapped("Discord messages never contain tokens, players, plans, schedules, Stop, or execution commands.");
        ImGui.Separator();

        ImGui.SetNextItemWidth(300f);
        ImGui.InputText("DAD identity alias", ref endpointAlias, 48);
        if (ImGui.Button("Generate immutable DAD identity"))
            StartIdentity(plugin.AutoPartyService.IdentityPackages.GenerateAsync(endpointAlias));
        ImGui.SameLine();
        ImGui.TextDisabled(string.IsNullOrWhiteSpace(configuration.RegistrationFingerprint)
            ? "Required before connecting"
            : $"fingerprint {Short(configuration.RegistrationFingerprint)}");

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("Bot token", ref botToken, 1024, ImGuiInputTextFlags.Password);
        ImGui.SetNextItemWidth(260f);
        ImGui.InputText("Guild ID", ref guildId, 24, ImGuiInputTextFlags.CharsDecimal);
        ImGui.SetNextItemWidth(260f);
        ImGui.InputText("#dad-pairing Channel ID", ref channelId, 24, ImGuiInputTextFlags.CharsDecimal);
        if (ImGui.Button("Save & Connect"))
        {
            if (!ulong.TryParse(guildId, out var parsedGuild) || !ulong.TryParse(channelId, out var parsedChannel))
                status = "dad-discord-settings-invalid";
            else
            {
                StartPolicy(discord.SaveAndConnectAsync(botToken.AsMemory(), parsedGuild, parsedChannel));
                botToken = string.Empty;
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Disconnect")) StartVoid(discord.DisconnectAsync(), "dad-discord-disconnected");
        ImGui.SameLine();
        if (ImGui.Button("Forget Token")) StartVoid(discord.ForgetTokenAsync(), "dad-discord-token-forgotten");
        ImGui.TextDisabled("The token is stored only through Windows CurrentUser DPAPI. It is never placed in DAD configuration or logs.");

        ImGui.TextUnformatted($"Connection: {health.State} ({health.SafeCode})");
        ImGui.TextUnformatted($"Authenticated Application ID: {(health.ApplicationId == 0 ? "pending" : health.ApplicationId)}");
        ImGui.TextUnformatted($"Authenticated Bot User ID: {(health.BotUserId == 0 ? "pending" : health.BotUserId)}");
        var invite = discord.GetZeroPermissionInviteLink();
        if (!string.IsNullOrWhiteSpace(invite))
        {
            ImGui.TextWrapped($"Zero-permission invite: {invite}");
            if (ImGui.Button("Copy invite link")) ImGui.SetClipboardText(invite);
        }
        foreach (var blocker in discord.GetBlockers())
            ImGui.TextColored(new Vector4(1f, .65f, .25f, 1f), $"Blocker: {blocker}");

        ImGui.Separator();
        DadUi.Heading("Discovered DAD clients", "Presence refreshes about every 60 seconds and becomes stale after three minutes.");
        var discovered = discord.GetDiscoveredClients();
        if (discovered.Count == 0) ImGui.TextDisabled("No signed DAD presence has been discovered.");
        foreach (var peer in discovered)
        {
            var age = Math.Max(0, (DateTime.UtcNow - peer.LastSeenUtc).TotalSeconds);
            ImGui.PushID(unchecked((int)peer.ApplicationId));
            ImGui.TextWrapped($"App {peer.ApplicationId} | {peer.Role} | {peer.PairingHealth} | heartbeat {age:0}s | identity {Short(peer.DadIdentity)} | endpoint {Short(peer.EndpointFingerprint)}");
            if (!string.IsNullOrWhiteSpace(peer.Blocker)) ImGui.TextColored(new Vector4(1f, .35f, .35f, 1f), peer.Blocker);
            var pending = configuration.PendingPairings.Any(pairing => pairing.ApplicationId == peer.ApplicationId);
            var pairing = configuration.Pairings.FirstOrDefault(candidate => candidate.ApplicationId == peer.ApplicationId);
            if (pending)
            {
                if (ImGui.Button("Accept")) StartPolicy(discord.AcceptAsync(peer.ApplicationId));
                ImGui.SameLine();
                if (ImGui.Button("Reject")) StartPolicy(discord.RejectAsync(peer.ApplicationId));
            }
            else if (pairing?.RevokedAtUtc != null)
            {
                if (ImGui.Button("Re-pair")) StartPolicy(discord.PairAsync(peer.ApplicationId));
            }
            else if (pairing == null)
            {
                if (ImGui.Button("Pair")) StartPolicy(discord.PairAsync(peer.ApplicationId));
            }
            else if (ImGui.Button("Revoke")) StartPolicy(discord.RevokeAsync(peer.ApplicationId));
            ImGui.PopID();
        }

        ImGui.Separator();
        DadUi.Heading("Measured DAD plan pilot", "Coordinator-only evidence persists across reloads and records until Stop & Evaluate.");
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("Pilot receipts root", ref pilotExchangeRoot, 512);
        if (ImGui.Button("Apply receipts root"))
        {
            var result = plugin.AutoPartyService.ApplyPilotExchangeRoot(pilotExchangeRoot);
            status = result.SafeCode;
            if (result.Allowed) pilotExchangeRoot = configuration.PilotExchangeRoot;
        }
        if (plugin.Configuration.RunAsServerDad)
        {
            if (configuration.MeasuredPilot.State == DadMeasuredPilotState.NotStarted && ImGui.Button("Start measured pilot"))
                status = plugin.MeasuredPilotService.Start().SafeCode;
            if (configuration.MeasuredPilot.State == DadMeasuredPilotState.Active && ImGui.Button("Stop & Evaluate"))
                status = plugin.MeasuredPilotService.StopAndEvaluate().State.ToString();
            if (configuration.MeasuredPilot.State == DadMeasuredPilotState.EvaluationIncomplete && ImGui.Button("Resume pilot"))
                status = plugin.MeasuredPilotService.Resume().SafeCode;
        }
        else ImGui.TextDisabled("Measured pilot controls are available on the Coordinator DAD.");

        var evaluation = plugin.MeasuredPilotService.CurrentEvaluation;
        ImGui.TextUnformatted($"Campaign: {configuration.MeasuredPilot.State} | qualifying {evaluation.QualifyingSuccesses}/10 | Plans {evaluation.PlanSuccesses}/3 | Schedules {evaluation.ScheduleSuccesses}/3 | requested jobs {evaluation.RequestedJobSuccesses}/2 | switches {evaluation.RequestedJobSwitches}/1");
        ImGui.TextUnformatted($"Stop/recovery: {configuration.MeasuredPilot.StopAllVerified}/{configuration.MeasuredPilot.RecoveryRunVerified} | Discord cycle: {configuration.MeasuredPilot.DiscordReconnectCycleVerified} | revoke exclusion/re-pair: {configuration.MeasuredPilot.RevokeExclusionVerified}/{configuration.MeasuredPilot.RePairVerified}");
        foreach (var missing in evaluation.Missing) ImGui.TextDisabled($"Missing: {missing}");
        foreach (var violation in evaluation.SafetyViolations) ImGui.TextColored(new Vector4(1f, .2f, .2f, 1f), $"SAFETY HARD FAIL: {violation}");
        if (!string.IsNullOrWhiteSpace(configuration.MeasuredPilot.ReceiptPath))
            ImGui.TextWrapped($"Signed receipt: {configuration.MeasuredPilot.ReceiptPath}");

        ImGui.Separator();
        if (ImGui.Button("Owner Stop"))
        {
            plugin.AutoPartyService.StopAll("dad-owner-stop-button");
            status = "dad-owner-stop-active";
        }
        ImGui.TextWrapped($"Status: {status}");
    }

    private void StartIdentity(ValueTask<DadAutoPartyIdentityOperationResult> task)
        => Start(async () => (await task.ConfigureAwait(false)).SafeCode);

    private void StartPolicy(ValueTask<DadAutoPartyPolicyDecision> task)
        => Start(async () => (await task.ConfigureAwait(false)).SafeCode);

    private void StartVoid(ValueTask task, string successCode)
        => Start(async () => { await task.ConfigureAwait(false); return successCode; });

    private void Start(Func<Task<string>> operation)
    {
        if (operationTask is { IsCompleted: false })
        {
            status = "dad-autoparty-operation-already-running";
            return;
        }
        operationTask = operation();
        status = "dad-autoparty-operation-running";
    }

    private void ObserveTask()
    {
        if (operationTask == null || !operationTask.IsCompleted) return;
        status = operationTask.IsCompletedSuccessfully ? operationTask.Result :
            operationTask.IsCanceled ? "dad-autoparty-operation-cancelled" : "dad-autoparty-operation-failed";
        endpointAlias = plugin.Configuration.AutoParty.EndpointAlias;
        operationTask = null;
    }

    private static string Short(string value)
        => string.IsNullOrWhiteSpace(value) ? "pending" : value[..Math.Min(12, value.Length)] + "…";
}
