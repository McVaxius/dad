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
    private string enrollmentReceiptPath = string.Empty;
    private string status = "AutoParty transport, pairing, and execution are disabled.";
    private Task<DadAutoPartyIdentityOperationResult>? identityTask;

    public DadAutoPartyWindow(Plugin plugin)
        : base("DAD AutoParty Pilot###DadAutoPartyPilot", ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;
        RespectCloseHotkey = false;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(640f, 500f),
            MaximumSize = new Vector2(1100f, 900f),
        };
        Size = new Vector2(760f, 650f);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void OnOpen()
    {
        pilotExchangeRoot = plugin.Configuration.AutoParty.PilotExchangeRoot;
        endpointAlias = plugin.Configuration.AutoParty.EndpointAlias;
        enrollmentReceiptPath = BuildReceiptPath();
    }

    public override void Draw()
    {
        ObserveTask();
        var configuration = plugin.Configuration.AutoParty;
        DadUi.Heading("AutoParty Pilot", "Public identity enrollment and three independent local safety gates.");
        ImGui.TextWrapped("This pilot uses only outbound file courier transport. It never opens an inbound listening socket. Owner Stop, DAD disable, expiry, revocation, requested-job mismatch, and local safety override all remote input.");
        ImGui.Separator();

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("Pilot exchange root", ref pilotExchangeRoot, 512);
        if (ImGui.Button("Apply pilot exchange root"))
        {
            var applied = plugin.AutoPartyService.ApplyPilotExchangeRoot(pilotExchangeRoot);
            status = applied.SafeCode;
            if (applied.Allowed)
            {
                pilotExchangeRoot = configuration.PilotExchangeRoot;
                enrollmentReceiptPath = BuildReceiptPath();
            }
        }
        ImGui.TextDisabled("Apply is available only while transport, pairing, and typed execution are disabled.");
        ImGui.TextWrapped($"Input: {configuration.GetPilotInputRoot()} | Receipts: {configuration.GetPilotReceiptRoot()} | Courier: {configuration.GetPilotCourierRoot()}");
        ImGui.Separator();

        ImGui.SetNextItemWidth(280f);
        ImGui.InputText("Island alias", ref endpointAlias, 48);
        if (ImGui.Button("Generate endpoint identity"))
            Start(plugin.AutoPartyService.IdentityPackages.GenerateAsync(endpointAlias).AsTask());
        ImGui.SameLine();
        if (ImGui.Button("Export public pilot identity"))
            Start(plugin.AutoPartyService.IdentityPackages.ExportPublicAsync(configuration.GetPilotInputRoot()).AsTask());

        ImGui.TextDisabled(string.IsNullOrWhiteSpace(configuration.RegistrationFingerprint)
            ? "No endpoint fingerprint yet."
            : $"Endpoint fingerprint: {configuration.RegistrationFingerprint[..Math.Min(16, configuration.RegistrationFingerprint.Length)]}…");
        ImGui.TextWrapped($"Public exports are written to {configuration.GetPilotInputRoot()}. Private signing and encryption keys remain CurrentUser-DPAPI protected in the DAD configuration directory.");

        ImGui.Separator();
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("Enrollment receipt (.apregistration)", ref enrollmentReceiptPath, 512);
        if (ImGui.Button("Import enrollment receipt"))
            Start(plugin.AutoPartyService.IdentityPackages.ImportEnrollmentReceiptAsync(enrollmentReceiptPath).AsTask());
        ImGui.SameLine();
        if (ImGui.Button("Use default receipt path"))
            enrollmentReceiptPath = BuildReceiptPath();

        ImGui.Separator();
        var transport = configuration.Enabled;
        if (ImGui.Checkbox("Enable AutoParty transport", ref transport))
        {
            plugin.AutoPartyService.SetEnabled(transport);
            status = transport ? "dad-autoparty-transport-enabled" : "dad-autoparty-transport-disabled";
        }

        var pairing = configuration.PairingEnabled;
        if (ImGui.Checkbox("Enable registration and pairing", ref pairing))
            status = plugin.AutoPartyService.SetPairingEnabled(pairing).SafeCode;
        if (ImGui.Button("Confirm pilot pairings locally"))
            status = plugin.AutoPartyService.ConfirmEnrollmentPairings().SafeCode;

        if (ImGui.Button("Import formation-only pilot fixture"))
            Start(plugin.AutoPartyPilotFixtureService.ImportAsync(configuration.GetPilotFixturePath()).AsTask());
        ImGui.TextDisabled($"Fixture: {configuration.GetPilotFixturePath()}");

        if (ImGui.Button("Send pilot courier probe"))
            Start(plugin.AutoPartyService.SendPilotCourierProbeAsync().AsTask());
        ImGui.TextDisabled(configuration.PilotCourierProbeVerified
            ? "A paired endpoint courier probe completed."
            : "A paired endpoint courier probe is still required.");

        var execution = configuration.ExecutionEnabled;
        if (ImGui.Checkbox("Enable typed execution", ref execution))
            status = plugin.AutoPartyService.SetExecutionEnabled(execution).SafeCode;

        ImGui.TextDisabled(configuration.OwnerAcceptanceConfirmed
            ? "Artifact-bound owner acceptance receipt loaded."
            : "Artifact-bound owner acceptance is pending.");
        ImGui.TextDisabled($"Pairings: {configuration.Pairings.Count} | Grants: {configuration.Grants.Count} | State generation: {configuration.StateGeneration}");

        ImGui.Separator();
        if (ImGui.Button("Owner Stop"))
        {
            plugin.AutoPartyService.StopAll("dad-owner-stop-button");
            status = "dad-owner-stop-active";
        }
        ImGui.SameLine();
        if (ImGui.Button("Rotate / revoke endpoint"))
            status = plugin.AutoPartyService.IdentityPackages.Rotate().SafeCode;
        ImGui.SameLine();
        if (ImGui.Button("Export pilot status receipt"))
            Start(plugin.AutoPartyService.IdentityPackages.ExportPilotStatusAsync(configuration.GetPilotReceiptRoot()).AsTask());

        ImGui.Separator();
        ImGui.TextWrapped($"Status: {status}");
    }

    private void Start(Task<DadAutoPartyIdentityOperationResult> task)
    {
        if (identityTask is { IsCompleted: false })
        {
            status = "dad-autoparty-operation-already-running";
            return;
        }
        identityTask = task;
        status = "dad-autoparty-operation-running";
    }

    private void ObserveTask()
    {
        if (identityTask == null || !identityTask.IsCompleted)
            return;
        if (identityTask.IsCompletedSuccessfully)
        {
            var result = identityTask.Result;
            status = result.SafeCode;
            endpointAlias = plugin.Configuration.AutoParty.EndpointAlias;
            enrollmentReceiptPath = BuildReceiptPath();
        }
        else if (identityTask.IsCanceled)
        {
            status = "dad-autoparty-operation-cancelled";
        }
        else
        {
            status = "dad-autoparty-operation-failed";
        }
        identityTask = null;
    }

    private string BuildReceiptPath()
    {
        var alias = plugin.Configuration.AutoParty.EndpointAlias;
        var inputRoot = plugin.Configuration.AutoParty.GetPilotInputRoot();
        return string.IsNullOrWhiteSpace(alias)
            ? Path.Combine(inputRoot, "endpoint.apregistration")
            : Path.Combine(inputRoot, alias + ".apregistration");
    }
}
