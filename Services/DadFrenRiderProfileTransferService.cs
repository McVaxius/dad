using System.Text;
using AutoParty.Contracts;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using dad.Models;

namespace dad.Services;

internal sealed class DadFrenRiderProfileTransferService
{
    internal const string ResolveChannel = "FrenRider.Dad.ResolveOrCreateProfile";
    internal const string ApplyChannel = "FrenRider.Dad.ApplyProfile";
    internal const string ReleaseChannel = "FrenRider.Dad.ReleaseTemporaryProfile";
    private const int ContractVersion = 1;
    private const int MaximumResponseCharacters = 32 * 1024;

    private readonly ICallGateSubscriber<string, string> resolveOrCreateProfile;
    private readonly ICallGateSubscriber<string, string> applyProfile;
    private readonly ICallGateSubscriber<string, string> releaseTemporaryProfile;
    private readonly IPluginLog log;

    public DadFrenRiderProfileTransferService(
        IDalamudPluginInterface pluginInterface,
        IPluginLog log)
    {
        ArgumentNullException.ThrowIfNull(pluginInterface);
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        resolveOrCreateProfile = pluginInterface.GetIpcSubscriber<string, string>(ResolveChannel);
        applyProfile = pluginInterface.GetIpcSubscriber<string, string>(ApplyChannel);
        releaseTemporaryProfile = pluginInterface.GetIpcSubscriber<string, string>(ReleaseChannel);
    }

    internal DadAutoPartyRemoteProfileResult ResolveAndEncode(DadAutoPartyRemoteProfileRequest request)
    {
        if (!IsValidOwnership(request.ProposalId, request.IslandId, request.OwnerId, request.OpaqueCharacterId))
            return DadAutoPartyRemoteProfileResult.Unavailable("dad-frenrider-profile-route-invalid");
        try
        {
            var responseJson = resolveOrCreateProfile.InvokeFunc(DadIpcJson.Serialize(new ResolveProfileRequest
            {
                OwnerId = request.OwnerId,
                IslandId = request.IslandId,
                CharacterId = request.OpaqueCharacterId,
                ProposalId = request.ProposalId.ToString("D"),
                DisplayLabel = request.DisplayLabel ?? string.Empty,
            }));
            var response = ReadResponse(responseJson);
            if (response is not { Version: ContractVersion, Code: "ok", Outcome: "exported" } ||
                string.IsNullOrWhiteSpace(response.ProfileJson))
            {
                return DadAutoPartyRemoteProfileResult.Unavailable(
                    SafeFailureCode(response?.Code, "dad-frenrider-profile-unavailable"));
            }

            var frame = FrenRiderProfileCodec.Encode(response.ProfileJson);
            return new DadAutoPartyRemoteProfileResult(true, frame, "dad-frenrider-profile-exported");
        }
        catch (ProtocolException exception)
        {
            log.Warning("[dad][FrenRiderProfile] Sender profile rejected ({SafeCode}).", exception.SafeCode);
            return DadAutoPartyRemoteProfileResult.Unavailable("dad-frenrider-profile-invalid-or-oversized");
        }
        catch (Exception exception)
        {
            log.Warning(exception, "[dad][FrenRiderProfile] Sender profile IPC failed.");
            return DadAutoPartyRemoteProfileResult.Unavailable("dad-frenrider-profile-ipc-unavailable");
        }
    }

    internal DadFrenRiderProfileApplicationResult Apply(
        DadFrenRiderProfileOwnership ownership,
        string profileJson)
    {
        if (!IsValidOwnership(
                ownership.ProposalId,
                ownership.SenderIslandId,
                ownership.OwnerId,
                ownership.CharacterId) ||
            string.IsNullOrWhiteSpace(profileJson) ||
            Encoding.UTF8.GetByteCount(profileJson) > AutoPartyProtocol.MaximumFrenRiderProfileJsonBytes)
        {
            return DadFrenRiderProfileApplicationResult.Failed("dad-frenrider-profile-apply-invalid");
        }
        try
        {
            var response = ReadResponse(applyProfile.InvokeFunc(DadIpcJson.Serialize(new ApplyProfileRequest
            {
                OwnerId = ownership.OwnerId,
                IslandId = ownership.SenderIslandId,
                CharacterId = ownership.CharacterId,
                ProposalId = ownership.ProposalId.ToString("D"),
                ProfileJson = profileJson,
            })));
            if (response is not { Version: ContractVersion, Code: "ok" })
            {
                return DadFrenRiderProfileApplicationResult.Failed(
                    SafeFailureCode(response?.Code, "dad-frenrider-profile-apply-rejected"));
            }
            var outcome = response.Outcome switch
            {
                "temporary-applied" => DadFrenRiderProfileApplicationOutcome.TemporaryApplied,
                "permanent-applied" => DadFrenRiderProfileApplicationOutcome.PermanentApplied,
                "opted-out" => DadFrenRiderProfileApplicationOutcome.OptedOut,
                _ => DadFrenRiderProfileApplicationOutcome.None,
            };
            return outcome == DadFrenRiderProfileApplicationOutcome.None
                ? DadFrenRiderProfileApplicationResult.Failed("dad-frenrider-profile-apply-response-invalid")
                : new DadFrenRiderProfileApplicationResult(true, outcome, $"dad-frenrider-{response.Outcome}");
        }
        catch (Exception exception)
        {
            log.Warning(exception, "[dad][FrenRiderProfile] Receiver profile application IPC failed.");
            return DadFrenRiderProfileApplicationResult.Failed("dad-frenrider-profile-apply-unavailable");
        }
    }

    internal bool ReleaseTemporary(
        DadFrenRiderProfileOwnership ownership,
        out string safeCode)
    {
        safeCode = "dad-frenrider-profile-release-invalid";
        if (!IsValidOwnership(
                ownership.ProposalId,
                ownership.SenderIslandId,
                ownership.OwnerId,
                ownership.CharacterId))
            return false;
        try
        {
            var response = ReadResponse(releaseTemporaryProfile.InvokeFunc(DadIpcJson.Serialize(new ReleaseProfileRequest
            {
                OwnerId = ownership.OwnerId,
                IslandId = ownership.SenderIslandId,
                CharacterId = ownership.CharacterId,
                ProposalId = ownership.ProposalId.ToString("D"),
            })));
            if (response is { Version: ContractVersion, Code: "ok", Outcome: "released" })
            {
                safeCode = "dad-frenrider-profile-released";
                return true;
            }
            safeCode = SafeFailureCode(response?.Code, "dad-frenrider-profile-release-rejected");
            return false;
        }
        catch (Exception exception)
        {
            log.Warning(exception, "[dad][FrenRiderProfile] Temporary profile release IPC failed.");
            safeCode = "dad-frenrider-profile-release-unavailable";
            return false;
        }
    }

    private static ProfileResponse? ReadResponse(string? json)
        => string.IsNullOrWhiteSpace(json) || json.Length > MaximumResponseCharacters
            ? null
            : DadIpcJson.DeserializeRaw<ProfileResponse>(json);

    private static bool IsValidOwnership(
        Guid proposalId,
        string islandId,
        string ownerId,
        string characterId)
        => proposalId != Guid.Empty &&
           IsIdentifier(islandId) &&
           IsIdentifier(ownerId) &&
           IsIdentifier(characterId);

    private static bool IsIdentifier(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           value.Length <= AutoPartyProtocol.MaximumIdentifierLength &&
           string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
           value.All(static character => !char.IsControl(character));

    private static string SafeFailureCode(string? code, string fallback)
    {
        var normalized = DadAutoPartyConfiguration.NormalizeSafeCode(code);
        return string.IsNullOrWhiteSpace(normalized)
            ? fallback
            : $"dad-frenrider-{normalized}";
    }

    private abstract class ProfileRequestBase
    {
        public int Version { get; set; } = ContractVersion;
        public string OwnerId { get; set; } = string.Empty;
        public string IslandId { get; set; } = string.Empty;
        public string CharacterId { get; set; } = string.Empty;
        public string ProposalId { get; set; } = string.Empty;
    }

    private sealed class ResolveProfileRequest : ProfileRequestBase
    {
        public string DisplayLabel { get; set; } = string.Empty;
    }

    private sealed class ApplyProfileRequest : ProfileRequestBase
    {
        public string ProfileJson { get; set; } = string.Empty;
    }

    private sealed class ReleaseProfileRequest : ProfileRequestBase
    {
    }

    private sealed class ProfileResponse
    {
        public int Version { get; set; }
        public string Code { get; set; } = string.Empty;
        public string Outcome { get; set; } = string.Empty;
        public string? ProfileJson { get; set; }
    }
}
