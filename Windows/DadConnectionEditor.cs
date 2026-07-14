using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using Dalamud.Bindings.ImGui;
using dad.Services;

namespace dad.Windows;

/// <summary>
/// Shared UI-only editor for the Coordinator/Client endpoint and LAN secret.
/// Callers decide when to commit; every commit still goes through Plugin's
/// transport wrappers so reconnect and persistence behavior stays unchanged.
/// </summary>
internal sealed class DadConnectionEditor
{
    private readonly Plugin plugin;
    private string draftHost = string.Empty;
    private int draftPort;
    private bool endpointDraftInitialized;
    private bool endpointDraftRole;
    private string draftSharedSecret = string.Empty;
    private bool sharedSecretDraftInitialized;
    private IReadOnlyList<DadEndpointHostOption> endpointHostOptions = [];
    private DateTime endpointHostOptionsLoadedUtc = DateTime.MinValue;

    public DadConnectionEditor(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public string DraftHost => draftHost.Trim();
    public int DraftPort => Math.Clamp(draftPort, 1, 65535);
    public string DraftSharedSecret => draftSharedSecret.Trim();
    public string DraftEndpoint => $"{DraftHost}:{DraftPort}";
    public bool DraftRequiresSharedSecret => IsHostLikelyNonLoopback(DraftHost);

    public void GenerateDraftSharedSecret()
        => draftSharedSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public void Reset(Configuration configuration)
    {
        ResetEndpoint(configuration);
        ResetSharedSecret(configuration);
    }

    public void ResetEndpoint(Configuration configuration)
    {
        draftHost = configuration.RunAsServerDad
            ? configuration.ServerListenHost
            : configuration.ServerDadHost;
        draftPort = configuration.RunAsServerDad
            ? configuration.ServerListenPort
            : configuration.ServerDadPort;
        endpointDraftRole = configuration.RunAsServerDad;
        endpointDraftInitialized = true;
    }

    public void ResetSharedSecret(Configuration configuration)
    {
        draftSharedSecret = configuration.TransportSharedSecret;
        sharedSecretDraftInitialized = true;
    }

    public bool DrawEndpointFields(
        Configuration configuration,
        string idPrefix,
        bool showApplyActions,
        bool compact = false)
    {
        EnsureEndpointDraft(configuration);

        ImGui.TextUnformatted(configuration.RunAsServerDad ? "Listen host" : "Coordinator host");
        var comboWidth = ImGui.GetFontSize() * (compact ? 10f : 13f);
        var hostInputWidth = MathF.Max(
            compact ? 150f : 180f,
            ImGui.GetContentRegionAvail().X - comboWidth - ImGui.GetStyle().ItemSpacing.X);
        ImGui.SetNextItemWidth(hostInputWidth);
        ImGui.InputText($"##{idPrefix}-host", ref draftHost, 128);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(comboWidth);
        DrawEndpointHostDropdown(idPrefix);

        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 8f);
        ImGui.InputInt(
            configuration.RunAsServerDad
                ? $"Listen port##{idPrefix}-port"
                : $"Coordinator port##{idPrefix}-port",
            ref draftPort);
        draftPort = Math.Clamp(draftPort, 1, 65535);

        var pending = HasPendingEndpointChanges(configuration);
        if (pending)
            ImGui.TextDisabled("Endpoint draft has unapplied changes.");

        if (!showApplyActions)
            return pending;

        if (ImGui.Button($"Apply endpoint changes##{idPrefix}-apply"))
            CommitEndpoint(configuration);
        ImGui.SameLine();
        ImGui.BeginDisabled(!pending);
        if (ImGui.Button($"Revert endpoint draft##{idPrefix}-revert"))
            ResetEndpoint(configuration);
        ImGui.EndDisabled();
        return pending;
    }

    public bool DrawSharedSecretFields(
        Configuration configuration,
        string idPrefix,
        bool showApplyActions,
        bool showGenerateAndCopy = true)
    {
        EnsureSharedSecretDraft(configuration);

        ImGui.SetNextItemWidth(MathF.Min(420f, ImGui.GetContentRegionAvail().X));
        ImGui.InputText(
            configuration.RunAsServerDad
                ? $"Shared secret##{idPrefix}-secret"
                : $"Paste shared secret##{idPrefix}-secret",
            ref draftSharedSecret,
            128);

        var pending = HasPendingSharedSecretChanges(configuration);
        if (pending)
            ImGui.TextDisabled("Shared secret draft has unapplied changes.");

        if (showApplyActions)
        {
            if (ImGui.Button($"Apply shared secret##{idPrefix}-apply-secret"))
                CommitSharedSecret(configuration);
            ImGui.SameLine();
            ImGui.BeginDisabled(!pending);
            if (ImGui.Button($"Revert shared secret##{idPrefix}-revert-secret"))
                ResetSharedSecret(configuration);
            ImGui.EndDisabled();
        }

        if (showGenerateAndCopy && configuration.RunAsServerDad)
        {
            if (showApplyActions)
                ImGui.SameLine();
            if (ImGui.Button($"Generate LAN shared secret##{idPrefix}-generate"))
            {
                draftSharedSecret = plugin.GenerateAndApplyTransportSharedSecret();
                ResetSharedSecret(configuration);
            }

            ImGui.SameLine();
            ImGui.BeginDisabled(string.IsNullOrWhiteSpace(configuration.TransportSharedSecret));
            if (ImGui.Button($"Copy shared secret##{idPrefix}-copy"))
            {
                ImGui.SetClipboardText(configuration.TransportSharedSecret);
                plugin.PrintStatus("Copied LAN shared secret.");
            }
            ImGui.EndDisabled();
        }

        return pending;
    }

    public bool ValidateEndpoint(out string blocker)
    {
        if (string.IsNullOrWhiteSpace(DraftHost))
        {
            blocker = "Enter a host such as 127.0.0.1 for one PC or the Coordinator's LAN address for multiple PCs.";
            return false;
        }

        if (draftPort is <= 0 or > 65535)
        {
            blocker = "Choose a port from 1 through 65535. DAD defaults to 4647.";
            return false;
        }

        blocker = string.Empty;
        return true;
    }

    public bool ValidateSecurity(out string blocker)
    {
        if (DraftRequiresSharedSecret && string.IsNullOrWhiteSpace(DraftSharedSecret))
        {
            blocker = "A non-loopback address requires a shared secret. Generate one on the Coordinator and paste the same value into every Client.";
            return false;
        }

        blocker = string.Empty;
        return true;
    }

    public void CommitEndpoint(Configuration configuration)
    {
        if (!ValidateEndpoint(out _))
            return;

        plugin.ApplyTransportEndpoint(DraftHost, DraftPort);
        ResetEndpoint(configuration);
    }

    public void CommitSharedSecret(Configuration configuration)
    {
        plugin.SetTransportSharedSecret(DraftSharedSecret);
        ResetSharedSecret(configuration);
    }

    public bool HasPendingEndpointChanges(Configuration configuration)
    {
        EnsureEndpointDraft(configuration);
        var configuredHost = configuration.RunAsServerDad
            ? configuration.ServerListenHost
            : configuration.ServerDadHost;
        var configuredPort = configuration.RunAsServerDad
            ? configuration.ServerListenPort
            : configuration.ServerDadPort;
        return !string.Equals(DraftHost, configuredHost, StringComparison.Ordinal)
               || DraftPort != configuredPort;
    }

    public bool HasPendingSharedSecretChanges(Configuration configuration)
    {
        EnsureSharedSecretDraft(configuration);
        return !string.Equals(DraftSharedSecret, configuration.TransportSharedSecret, StringComparison.Ordinal);
    }

    private void EnsureEndpointDraft(Configuration configuration)
    {
        if (!endpointDraftInitialized || endpointDraftRole != configuration.RunAsServerDad)
            ResetEndpoint(configuration);
    }

    private void EnsureSharedSecretDraft(Configuration configuration)
    {
        if (!sharedSecretDraftInitialized)
        {
            ResetSharedSecret(configuration);
            return;
        }

        if (!HasUnsavedSecretDraft(configuration) &&
            !string.Equals(draftSharedSecret, configuration.TransportSharedSecret, StringComparison.Ordinal))
        {
            ResetSharedSecret(configuration);
        }
    }

    private bool HasUnsavedSecretDraft(Configuration configuration)
        => !string.Equals(draftSharedSecret.Trim(), configuration.TransportSharedSecret, StringComparison.Ordinal);

    private void DrawEndpointHostDropdown(string idPrefix)
    {
        var options = GetEndpointHostOptions();
        var current = options.FirstOrDefault(option =>
            string.Equals(option.Host, DraftHost, StringComparison.OrdinalIgnoreCase));
        var preview = current?.Label ?? "Select IP/host";
        if (!ImGui.BeginCombo($"##{idPrefix}-host-options", preview))
            return;

        foreach (var option in options)
        {
            var selected = string.Equals(option.Host, DraftHost, StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable($"{option.Label}##{idPrefix}-{option.Host}", selected))
                draftHost = option.Host;
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private IReadOnlyList<DadEndpointHostOption> GetEndpointHostOptions()
    {
        if (endpointHostOptions.Count > 0 &&
            DateTime.UtcNow - endpointHostOptionsLoadedUtc < TimeSpan.FromSeconds(10))
        {
            return endpointHostOptions;
        }

        endpointHostOptions = DadEndpointHostOptions.GetLocalIpv4Options();
        endpointHostOptionsLoadedUtc = DateTime.UtcNow;
        return endpointHostOptions;
    }

    private static bool IsHostLikelyNonLoopback(string host)
    {
        var normalized = host.Trim();
        if (normalized.StartsWith("[", StringComparison.Ordinal) &&
            normalized.EndsWith("]", StringComparison.Ordinal) &&
            normalized.Length > 2)
        {
            normalized = normalized[1..^1];
        }

        if (string.Equals(normalized, "localhost", StringComparison.OrdinalIgnoreCase))
            return false;

        return !IPAddress.TryParse(normalized, out var address) || !IPAddress.IsLoopback(address);
    }
}
