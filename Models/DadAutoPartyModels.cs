using AutoParty.Contracts;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace dad.Models;

public sealed class DadAutoPartyConfiguration
{
    public bool Enabled { get; set; }
    public DadAutoPartyRegistrationState RegistrationState { get; set; }
    public string RegistrationId { get; set; } = string.Empty;
    public string RouteId { get; set; } = string.Empty;
    public string CentralBotApplicationId { get; set; } = string.Empty;
    public string HomeGuildScope { get; set; } = string.Empty;
    public string WebhookCredentialReference { get; set; } = string.Empty;
    public string UplinkEpochId { get; set; } = string.Empty;
    public string DownlinkEpochId { get; set; } = string.Empty;
    public long MailboxEpochGeneration { get; set; }
    public long DirectoryGeneration { get; set; } = 1;
    public long RelayKeyGeneration { get; set; } = 1;
    public string RelaySigningPublicKey { get; set; } = string.Empty;
    public string RelayAgreementPublicKey { get; set; } = string.Empty;
    public DateTime BootstrapExpiresAtUtc { get; set; }
    public bool LegacyDiscordTokenCleanupPending { get; set; }
    public string LegacyDiscordTokenCleanupWarning { get; set; } = string.Empty;

    // Schema-9 migration-only aliases. They load the old JSON names so the DPAPI token can be
    // retired, then normalize to null/zero and disappear from the next saved configuration.
    [JsonPropertyName("DiscordTokenReference")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyDiscordTokenReference { get; set; }

    [JsonPropertyName("DiscordApplicationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ulong LegacyDiscordApplicationId { get; set; }

    [JsonPropertyName("DiscordBotUserId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ulong LegacyDiscordBotUserId { get; set; }
    public string EndpointIdentityReference { get; set; } = string.Empty;
    public string RegisteredOwnerId { get; set; } = string.Empty;
    public string RegisteredIslandId { get; set; } = string.Empty;
    public string RegistrationFingerprint { get; set; } = string.Empty;
    public string EndpointAlias { get; set; } = string.Empty;
    public string SigningPublicKey { get; set; } = string.Empty;
    public string EncryptionPublicKey { get; set; } = string.Empty;
    public long EndpointKeyGeneration { get; set; } = 1;
    public long RevocationGeneration { get; set; } = 1;
    public long StateGeneration { get; set; } = 1;
    public DadAutoPartySharePolicy StandingSharePolicy { get; set; } = new()
    {
        Mode = DadAutoPartyCharacterShareMode.CharacterList,
        Enabled = false,
    };
    public DadAutoPartyCrewShareScope StandingShareScope { get; set; } =
        DadAutoPartyCrewShareScope.SpecificCharacters;
    public List<DadAutoPartyCrewIdentity> CrewIdentities { get; set; } = [];
    public Dictionary<string, string> PairedDadAliases { get; set; } = new(StringComparer.Ordinal);
    public List<DadAutoPartyPairing> Pairings { get; set; } = [];
    public List<DadAutoPartyGrant> Grants { get; set; } = [];
    public List<DadAutoPartyListing> Listings { get; set; } = [];
    public List<DadAutoPartyRemoteBinding> RemoteBindings { get; set; } = [];
    [JsonIgnore]
    public List<DadAutoPartyPairing> PendingPairings { get; set; } = [];
    public List<DadAutoPartyDeauthentication> Deauthentications { get; set; } = [];
    public string PairingInviteToken { get; set; } = string.Empty;
    public string PairingAttemptId { get; set; } = string.Empty;
    public DateTime PairingAttemptExpiresAtUtc { get; set; }
    public bool PairingAttemptSubmitted { get; set; }
    public string PairingPeerAttemptId { get; set; } = string.Empty;
    public string PairingPeerIslandId { get; set; } = string.Empty;
    public string PairingPeerInviteFingerprint { get; set; } = string.Empty;
    public DadAutoPartySharePolicy PairingAttemptSharePolicy { get; set; } = new();
    public string PairingIntentMessageId { get; set; } = string.Empty;
    public string PairingCancellationMessageId { get; set; } = string.Empty;

    [JsonIgnore]
    public DadAutoPartyRegistrationRecoveryState RegistrationRecoveryState { get; internal set; }

    public bool HasDurableRegistrationMaterial =>
        !string.IsNullOrWhiteSpace(RouteId) &&
        !string.IsNullOrWhiteSpace(WebhookCredentialReference) &&
        Guid.TryParse(UplinkEpochId, out _) &&
        Guid.TryParse(DownlinkEpochId, out _) &&
        !string.Equals(UplinkEpochId, DownlinkEpochId, StringComparison.Ordinal) &&
        MailboxEpochGeneration >= 1 &&
        RelayKeyGeneration >= 1 &&
        !string.IsNullOrWhiteSpace(RelaySigningPublicKey) &&
        !string.IsNullOrWhiteSpace(RelayAgreementPublicKey);

    public bool HasImportedBootstrap =>
        (RegistrationState is DadAutoPartyRegistrationState.BootstrapImported or DadAutoPartyRegistrationState.Active) &&
        Guid.TryParse(RegistrationId, out _) &&
        HasDurableRegistrationMaterial;

    public bool IsRegistrationActive =>
        RegistrationState == DadAutoPartyRegistrationState.Active && HasImportedBootstrap;

    public DadAutoPartyConfiguration Normalize()
    {
        RegistrationId = Guid.TryParse(RegistrationId, out var registrationId)
            ? registrationId.ToString("D")
            : string.Empty;
        RouteId = NormalizeIdentifier(RouteId);
        CentralBotApplicationId = NormalizeSnowflake(CentralBotApplicationId);
        HomeGuildScope = NormalizeIdentifier(HomeGuildScope);
        WebhookCredentialReference = NormalizeMailboxReference(WebhookCredentialReference);
        UplinkEpochId = Guid.TryParse(UplinkEpochId, out var epochId)
            ? epochId.ToString("D")
            : string.Empty;
        DownlinkEpochId = Guid.TryParse(DownlinkEpochId, out var downlinkEpochId)
            ? downlinkEpochId.ToString("D")
            : string.Empty;
        MailboxEpochGeneration = Math.Max(0, MailboxEpochGeneration);
        DirectoryGeneration = Math.Max(1, DirectoryGeneration);
        RelayKeyGeneration = Math.Max(1, RelayKeyGeneration);
        RelaySigningPublicKey = NormalizePublicKey(RelaySigningPublicKey);
        RelayAgreementPublicKey = NormalizePublicKey(RelayAgreementPublicKey);
        LegacyDiscordTokenReference = NormalizeTokenReference(LegacyDiscordTokenReference);
        if (string.IsNullOrWhiteSpace(LegacyDiscordTokenReference))
        {
            LegacyDiscordTokenReference = null;
            LegacyDiscordTokenCleanupPending = false;
            LegacyDiscordTokenCleanupWarning = string.Empty;
        }
        LegacyDiscordTokenCleanupWarning = NormalizeSafeCode(LegacyDiscordTokenCleanupWarning);
        LegacyDiscordApplicationId = 0;
        LegacyDiscordBotUserId = 0;
        EndpointIdentityReference = NormalizeIdentifier(EndpointIdentityReference);
        RegisteredOwnerId = NormalizeIdentifier(RegisteredOwnerId);
        RegisteredIslandId = NormalizeIdentifier(RegisteredIslandId);
        RegistrationFingerprint = NormalizeFingerprint(RegistrationFingerprint);
        EndpointAlias = NormalizeAlias(EndpointAlias);
        SigningPublicKey = NormalizePublicKey(SigningPublicKey);
        EncryptionPublicKey = NormalizePublicKey(EncryptionPublicKey);
        EndpointKeyGeneration = Math.Max(1, EndpointKeyGeneration);
        RevocationGeneration = Math.Max(1, RevocationGeneration);
        StateGeneration = Math.Max(1, StateGeneration);
        StandingSharePolicy = (StandingSharePolicy ?? new DadAutoPartySharePolicy
        {
            Mode = DadAutoPartyCharacterShareMode.CharacterList,
            Enabled = false,
        }).Normalize();
        if (StandingSharePolicy.Mode != DadAutoPartyCharacterShareMode.CharacterList)
        {
            StandingSharePolicy.Mode = DadAutoPartyCharacterShareMode.CharacterList;
            StandingSharePolicy.CharacterHandles.Clear();
            StandingSharePolicy.Enabled = false;
        }
        if (!Enum.IsDefined(StandingShareScope))
            StandingShareScope = DadAutoPartyCrewShareScope.SpecificCharacters;
        CrewIdentities = (CrewIdentities ?? [])
            .Where(static identity => identity != null)
            .Select(static identity => identity!.Normalize())
            .Where(static identity => identity.IsValid)
            .DistinctBy(static identity => identity.RosterIdentityKey, StringComparer.OrdinalIgnoreCase)
            .Take(256)
            .ToList();
        PairedDadAliases = (PairedDadAliases ?? new Dictionary<string, string>())
            .Select(static pair => new KeyValuePair<string, string>(
                NormalizeIdentifier(pair.Key),
                NormalizeAlias(pair.Value)))
            .Where(static pair => !string.IsNullOrWhiteSpace(pair.Key) &&
                                  !string.IsNullOrWhiteSpace(pair.Value))
            .GroupBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static group => group.First())
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Take(256)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        Pairings = (Pairings ?? [])
            .Where(static pairing => pairing != null)
            .Select(static pairing => pairing!.Normalize())
            .Where(static pairing => pairing.IsValid)
            .DistinctBy(static pairing => pairing.IslandId, StringComparer.Ordinal)
            .Take(256)
            .ToList();
        foreach (var pairing in Pairings)
        {
            var legacyAlias = pairing.TakeLegacyLocalAlias();
            if (!string.IsNullOrWhiteSpace(legacyAlias) &&
                PairedDadAliases.Count < 256 &&
                !PairedDadAliases.ContainsKey(pairing.IslandId))
            {
                PairedDadAliases.Add(pairing.IslandId, legacyAlias);
            }
        }
        Grants = (Grants ?? [])
            .Where(static grant => grant is { IsValid: true })
            .DistinctBy(static grant => grant.GrantId, StringComparer.Ordinal)
            .Take(256)
            .ToList();
        Listings = (Listings ?? [])
            .Where(static listing => listing != null)
            .Select(static listing => listing!.Normalize())
            .Where(static listing => listing.IsValid)
            .DistinctBy(static listing => listing.ListingId, StringComparer.Ordinal)
            .Take(256)
            .ToList();
        RemoteBindings = (RemoteBindings ?? [])
            .Where(static binding => binding != null)
            .Select(static binding => binding!.Normalize())
            .Where(static binding => binding.IsValid)
            .DistinctBy(static binding => binding.FleetRowId, StringComparer.OrdinalIgnoreCase)
            .Take(256)
            .ToList();
        PendingPairings = [];
        Deauthentications = (Deauthentications ?? [])
            .Where(static item => item != null)
            .Select(static item => item!.Normalize())
            .Where(static item => item.IsValid)
            .OrderByDescending(static item => item.RevokedAtUtc)
            .DistinctBy(static item => item.PeerIslandId, StringComparer.Ordinal)
            .Take(256)
            .ToList();
        PairingInviteToken = (PairingInviteToken ?? string.Empty).Trim();
        PairingAttemptId = Guid.TryParse(PairingAttemptId, out var pairingAttemptId)
            ? pairingAttemptId.ToString("D")
            : string.Empty;
        PairingPeerAttemptId = Guid.TryParse(PairingPeerAttemptId, out var peerAttemptId)
            ? peerAttemptId.ToString("D")
            : string.Empty;
        PairingPeerIslandId = NormalizeIdentifier(PairingPeerIslandId);
        PairingPeerInviteFingerprint = NormalizeFingerprint(PairingPeerInviteFingerprint);
        PairingAttemptSharePolicy = (PairingAttemptSharePolicy ?? new DadAutoPartySharePolicy()).Normalize();
        PairingIntentMessageId = Guid.TryParse(PairingIntentMessageId, out var intentMessageId)
            ? intentMessageId.ToString("D")
            : string.Empty;
        PairingCancellationMessageId = Guid.TryParse(
            PairingCancellationMessageId,
            out var cancellationMessageId)
            ? cancellationMessageId.ToString("D")
            : string.Empty;
        var pairingAttemptValid = PairingInviteToken.StartsWith(
                                      PairingCopyPasteCodec.Prefix,
                                      StringComparison.Ordinal) &&
                                  PairingInviteToken.Length <=
                                      AutoPartyProtocol.MaximumPairingInviteCharacters &&
                                  !string.IsNullOrWhiteSpace(PairingAttemptId) &&
                                  PairingAttemptExpiresAtUtc != default;
        if (!pairingAttemptValid)
            ClearPairingAttempt();
        if (!HasImportedBootstrap && RegistrationState != DadAutoPartyRegistrationState.Unregistered)
            RegistrationState = DadAutoPartyRegistrationState.Unregistered;
        return this;
    }

    public DadAutoPartyConfiguration Clone()
        => new()
        {
            Enabled = Enabled,
            RegistrationState = RegistrationState,
            RegistrationId = RegistrationId,
            RouteId = RouteId,
            CentralBotApplicationId = CentralBotApplicationId,
            HomeGuildScope = HomeGuildScope,
            WebhookCredentialReference = WebhookCredentialReference,
            UplinkEpochId = UplinkEpochId,
            DownlinkEpochId = DownlinkEpochId,
            MailboxEpochGeneration = MailboxEpochGeneration,
            DirectoryGeneration = DirectoryGeneration,
            RelayKeyGeneration = RelayKeyGeneration,
            RelaySigningPublicKey = RelaySigningPublicKey,
            RelayAgreementPublicKey = RelayAgreementPublicKey,
            BootstrapExpiresAtUtc = BootstrapExpiresAtUtc,
            LegacyDiscordTokenCleanupPending = LegacyDiscordTokenCleanupPending,
            LegacyDiscordTokenCleanupWarning = LegacyDiscordTokenCleanupWarning,
            LegacyDiscordTokenReference = LegacyDiscordTokenReference,
            EndpointIdentityReference = EndpointIdentityReference,
            RegisteredOwnerId = RegisteredOwnerId,
            RegisteredIslandId = RegisteredIslandId,
            RegistrationFingerprint = RegistrationFingerprint,
            EndpointAlias = EndpointAlias,
            SigningPublicKey = SigningPublicKey,
            EncryptionPublicKey = EncryptionPublicKey,
            EndpointKeyGeneration = EndpointKeyGeneration,
            RevocationGeneration = RevocationGeneration,
            StateGeneration = StateGeneration,
            StandingSharePolicy = StandingSharePolicy.Clone(),
            StandingShareScope = StandingShareScope,
            CrewIdentities = CrewIdentities.Select(static identity => identity.Clone()).ToList(),
            PairedDadAliases = new Dictionary<string, string>(PairedDadAliases, StringComparer.Ordinal),
            Pairings = Pairings.Select(static pairing => pairing.Clone()).ToList(),
            Grants = Grants.Select(static grant => grant with { }).ToList(),
            Listings = Listings.Select(static listing => listing.Clone()).ToList(),
            RemoteBindings = RemoteBindings.Select(static binding => binding.Clone()).ToList(),
            PendingPairings = PendingPairings.Select(static pairing => pairing.Clone()).ToList(),
            Deauthentications = Deauthentications.Select(static item => item.Clone()).ToList(),
            PairingInviteToken = PairingInviteToken,
            PairingAttemptId = PairingAttemptId,
            PairingAttemptExpiresAtUtc = PairingAttemptExpiresAtUtc,
            PairingAttemptSubmitted = PairingAttemptSubmitted,
            PairingPeerAttemptId = PairingPeerAttemptId,
            PairingPeerIslandId = PairingPeerIslandId,
            PairingPeerInviteFingerprint = PairingPeerInviteFingerprint,
            PairingAttemptSharePolicy = PairingAttemptSharePolicy.Clone(),
            PairingIntentMessageId = PairingIntentMessageId,
            PairingCancellationMessageId = PairingCancellationMessageId,
        };

    public void ClearPairingAttempt()
    {
        PairingInviteToken = string.Empty;
        PairingAttemptId = string.Empty;
        PairingAttemptExpiresAtUtc = default;
        PairingAttemptSubmitted = false;
        PairingPeerAttemptId = string.Empty;
        PairingPeerIslandId = string.Empty;
        PairingPeerInviteFingerprint = string.Empty;
        PairingAttemptSharePolicy = new DadAutoPartySharePolicy();
        PairingIntentMessageId = string.Empty;
        PairingCancellationMessageId = string.Empty;
    }

    internal static string NormalizeIdentifier(string? value)
        => (value ?? string.Empty).Trim() is { Length: <= 128 } normalized
            ? normalized
            : string.Empty;

    internal static string NormalizeTokenReference(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized.Length == 46 &&
               normalized.StartsWith("discord-token-", StringComparison.Ordinal) &&
               normalized[14..].All(char.IsAsciiHexDigit)
            ? normalized
            : string.Empty;
    }

    internal static string NormalizeMailboxReference(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized.Length == 48 &&
               normalized.StartsWith("webhook-mailbox-", StringComparison.Ordinal) &&
               normalized[16..].All(char.IsAsciiHexDigit)
            ? normalized
            : string.Empty;
    }

    internal static string NormalizeSnowflake(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length is >= 1 and <= 24 && normalized.All(char.IsAsciiDigit)
            ? normalized
            : string.Empty;
    }

    internal static string NormalizeSafeCode(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized.Length is > 0 and <= 128 && normalized.All(character =>
            character is >= 'a' and <= 'z' || char.IsAsciiDigit(character) || character is '-' or '.')
            ? normalized
            : string.Empty;
    }

    internal static string NormalizeFingerprint(string? value)
    {
        var normalized = (value ?? string.Empty)
            .Trim()
            .Replace(":", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
        return normalized.Length is >= 16 and <= 128 && normalized.All(Uri.IsHexDigit)
            ? normalized
            : string.Empty;
    }

    internal static string NormalizeAlias(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length is > 0 and <= 48 &&
               normalized.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            ? normalized
            : string.Empty;
    }

    internal static string NormalizePublicKey(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length is < 40 or > 64)
            return string.Empty;
        try
        {
            return Convert.FromBase64String(normalized).Length == 32 ? normalized : string.Empty;
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }

    internal static string NormalizeSha256(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.StartsWith("sha256:", StringComparison.Ordinal))
            normalized = normalized[7..];
        return normalized.Length == 64 && normalized.All(char.IsAsciiHexDigit)
            ? normalized
            : string.Empty;
    }

}

public enum DadAutoPartyCrewShareScope
{
    CurrentCharacter = 1,
    SpecificCharacters = 2,
    AllCharacters = 3,
}

/// <summary>
/// Stable local-to-opaque mapping for an active DAD Crew row. It is intentionally kept with
/// AutoParty rather than Fleet Matrix so publishing does not depend on matrix staging.
/// </summary>
public sealed class DadAutoPartyCrewIdentity
{
    public string RosterIdentityKey { get; set; } = string.Empty;
    public string OpaqueCharacterId { get; set; } = string.Empty;

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(RosterIdentityKey) &&
        !string.IsNullOrWhiteSpace(OpaqueCharacterId);

    public DadAutoPartyCrewIdentity Normalize()
    {
        RosterIdentityKey = DadAutoPartyConfiguration.NormalizeIdentifier(RosterIdentityKey);
        OpaqueCharacterId = DadAutoPartyConfiguration.NormalizeIdentifier(OpaqueCharacterId);
        return this;
    }

    public DadAutoPartyCrewIdentity Clone() => new()
    {
        RosterIdentityKey = RosterIdentityKey,
        OpaqueCharacterId = OpaqueCharacterId,
    };
}

public enum DadAutoPartyRegistrationState
{
    Unregistered = 0,
    BootstrapImported = 1,
    Active = 2,
    Quarantined = 3,
}

public enum DadAutoPartyRegistrationRecoveryState
{
    NewRegistration = 0,
    Active = 1,
    RecoveryAvailable = 2,
    IdentityLost = 3,
}

public sealed class DadAutoPartyRemoteBinding
{
    public string FleetRowId { get; set; } = string.Empty;
    public string OpaqueCharacterId { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public string IslandId { get; set; } = string.Empty;
    public string RequestedJobId { get; set; } = string.Empty;
    public bool OwnsQueueAuthority { get; set; }
    public bool OwnerConsentConfirmed { get; set; }

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(FleetRowId) &&
        !string.IsNullOrWhiteSpace(OpaqueCharacterId) &&
        !string.IsNullOrWhiteSpace(OwnerId) &&
        !string.IsNullOrWhiteSpace(IslandId) &&
        !string.IsNullOrWhiteSpace(RequestedJobId) &&
        OwnerConsentConfirmed;

    public DadAutoPartyRemoteBinding Normalize()
    {
        FleetRowId = DadAutoPartyConfiguration.NormalizeIdentifier(FleetRowId);
        OpaqueCharacterId = DadAutoPartyConfiguration.NormalizeIdentifier(OpaqueCharacterId);
        OwnerId = DadAutoPartyConfiguration.NormalizeIdentifier(OwnerId);
        IslandId = DadAutoPartyConfiguration.NormalizeIdentifier(IslandId);
        RequestedJobId = DadAutoPartyConfiguration.NormalizeIdentifier(RequestedJobId);
        return this;
    }

    public DadAutoPartyRemoteBinding Clone() => new()
    {
        FleetRowId = FleetRowId,
        OpaqueCharacterId = OpaqueCharacterId,
        OwnerId = OwnerId,
        IslandId = IslandId,
        RequestedJobId = RequestedJobId,
        OwnsQueueAuthority = OwnsQueueAuthority,
        OwnerConsentConfirmed = OwnerConsentConfirmed,
    };
}

public sealed class DadAutoPartyPairing
{
    public string PairingId { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public string IslandId { get; set; } = string.Empty;
    public string HomeGuildScope { get; set; } = string.Empty;
    // Schema-12 migration-only shim. Normalize moves this value into PairedDadAliases and clears it.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LocalAlias { get; set; }
    public string PublicKeyFingerprint { get; set; } = string.Empty;
    public string LocalFingerprint { get; set; } = string.Empty;
    public string TranscriptHash { get; set; } = string.Empty;
    public DadAutoPartySharePolicy LocalSharePolicy { get; set; } = new();
    public DadAutoPartySharePolicy PeerSharePolicy { get; set; } = new();
    public DateTime ExpiresAtUtc { get; set; }
    public long KeyGeneration { get; set; } = 1;
    public string SigningPublicKey { get; set; } = string.Empty;
    public string AgreementPublicKey { get; set; } = string.Empty;
    public string PeerEndpointAlias { get; set; } = string.Empty;
    public DateTime ConfirmedAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }

    public bool IsValid =>
        Guid.TryParse(PairingId, out _) &&
        !string.IsNullOrWhiteSpace(IslandId) &&
        !string.IsNullOrWhiteSpace(PublicKeyFingerprint) &&
        !string.IsNullOrWhiteSpace(LocalFingerprint) &&
        !string.IsNullOrWhiteSpace(TranscriptHash) &&
        KeyGeneration >= 1 &&
        !string.IsNullOrWhiteSpace(DadAutoPartyConfiguration.NormalizePublicKey(SigningPublicKey)) &&
        !string.IsNullOrWhiteSpace(DadAutoPartyConfiguration.NormalizePublicKey(AgreementPublicKey)) &&
        ExpiresAtUtc != default;

    public bool IsActive =>
        IsValid && RevokedAtUtc == null &&
        ConfirmedAtUtc != default;

    public DadAutoPartyPairing Normalize()
    {
        PairingId = Guid.TryParse(PairingId, out var pairingId) ? pairingId.ToString("D") : string.Empty;
        OwnerId = DadAutoPartyConfiguration.NormalizeIdentifier(OwnerId);
        IslandId = DadAutoPartyConfiguration.NormalizeIdentifier(IslandId);
        HomeGuildScope = DadAutoPartyConfiguration.NormalizeIdentifier(HomeGuildScope);
        var localAlias = DadAutoPartyConfiguration.NormalizeAlias(LocalAlias);
        LocalAlias = string.IsNullOrWhiteSpace(localAlias) ? null : localAlias;
        PublicKeyFingerprint = DadAutoPartyConfiguration.NormalizeFingerprint(PublicKeyFingerprint);
        LocalFingerprint = DadAutoPartyConfiguration.NormalizeFingerprint(LocalFingerprint);
        TranscriptHash = DadAutoPartyConfiguration.NormalizeFingerprint(TranscriptHash);
        LocalSharePolicy = (LocalSharePolicy ?? new DadAutoPartySharePolicy()).Normalize();
        PeerSharePolicy = (PeerSharePolicy ?? new DadAutoPartySharePolicy()).Normalize();
        SigningPublicKey = DadAutoPartyConfiguration.NormalizePublicKey(SigningPublicKey);
        AgreementPublicKey = DadAutoPartyConfiguration.NormalizePublicKey(AgreementPublicKey);
        PeerEndpointAlias = DadAutoPartyConfiguration.NormalizeAlias(PeerEndpointAlias);
        KeyGeneration = Math.Max(1, KeyGeneration);
        return this;
    }

    internal string TakeLegacyLocalAlias()
    {
        var alias = DadAutoPartyConfiguration.NormalizeAlias(LocalAlias);
        LocalAlias = null;
        return alias;
    }

    public bool ShouldSerializeLocalAlias() => false;

    public DadAutoPartyPairing Clone()
        => new()
        {
            PairingId = PairingId,
            OwnerId = OwnerId,
            IslandId = IslandId,
            HomeGuildScope = HomeGuildScope,
            PublicKeyFingerprint = PublicKeyFingerprint,
            LocalFingerprint = LocalFingerprint,
            TranscriptHash = TranscriptHash,
            LocalSharePolicy = LocalSharePolicy.Clone(),
            PeerSharePolicy = PeerSharePolicy.Clone(),
            ExpiresAtUtc = ExpiresAtUtc,
            KeyGeneration = KeyGeneration,
            SigningPublicKey = SigningPublicKey,
            AgreementPublicKey = AgreementPublicKey,
            PeerEndpointAlias = PeerEndpointAlias,
            ConfirmedAtUtc = ConfirmedAtUtc,
            RevokedAtUtc = RevokedAtUtc,
        };
}

public enum DadAutoPartyCharacterShareMode
{
    SpecificCharacter = 1,
    CharacterList = 2,
    AllCharactersForPeer = 3,
    PromiscuousAllSameGuild = 4,
}

public sealed class DadAutoPartySharePolicy
{
    public DadAutoPartyCharacterShareMode Mode { get; set; } = DadAutoPartyCharacterShareMode.SpecificCharacter;
    public List<string> CharacterHandles { get; set; } = [];
    public bool Enabled { get; set; }
    public long Revision { get; set; } = 1;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public bool IsValid =>
        Enum.IsDefined(Mode) && Revision >= 1 && UpdatedAtUtc != default &&
        (!Enabled || Mode is DadAutoPartyCharacterShareMode.AllCharactersForPeer or
            DadAutoPartyCharacterShareMode.PromiscuousAllSameGuild || CharacterHandles.Count > 0);

    public DadAutoPartySharePolicy Normalize()
    {
        if (!Enum.IsDefined(Mode))
            Mode = DadAutoPartyCharacterShareMode.SpecificCharacter;
        CharacterHandles = (CharacterHandles ?? [])
            .Select(DadAutoPartyConfiguration.NormalizeIdentifier)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Take(256)
            .ToList();
        if (Mode is DadAutoPartyCharacterShareMode.AllCharactersForPeer or
            DadAutoPartyCharacterShareMode.PromiscuousAllSameGuild)
            CharacterHandles.Clear();
        Revision = Math.Max(1, Revision);
        if (UpdatedAtUtc == default)
            UpdatedAtUtc = DateTime.UtcNow;
        if (Enabled &&
            (Mode is DadAutoPartyCharacterShareMode.SpecificCharacter or DadAutoPartyCharacterShareMode.CharacterList) &&
            CharacterHandles.Count == 0)
            Enabled = false;
        return this;
    }

    public DadAutoPartySharePolicy Clone() => new()
    {
        Mode = Mode,
        CharacterHandles = [.. CharacterHandles],
        Enabled = Enabled,
        Revision = Revision,
        UpdatedAtUtc = UpdatedAtUtc,
    };
}

public sealed class DadAutoPartyDeauthentication
{
    public string DeauthenticationId { get; set; } = string.Empty;
    public string PeerIslandId { get; set; } = string.Empty;
    public string PairingTranscriptHash { get; set; } = string.Empty;
    public long RevocationGeneration { get; set; }
    public string SafeReason { get; set; } = string.Empty;
    public DateTime RevokedAtUtc { get; set; }

    public bool IsValid =>
        Guid.TryParse(DeauthenticationId, out _) &&
        !string.IsNullOrWhiteSpace(PeerIslandId) &&
        !string.IsNullOrWhiteSpace(PairingTranscriptHash) &&
        RevocationGeneration >= 1 &&
        !string.IsNullOrWhiteSpace(SafeReason) &&
        RevokedAtUtc != default;

    public DadAutoPartyDeauthentication Normalize()
    {
        DeauthenticationId = Guid.TryParse(DeauthenticationId, out var id) ? id.ToString("D") : string.Empty;
        PeerIslandId = DadAutoPartyConfiguration.NormalizeIdentifier(PeerIslandId);
        PairingTranscriptHash = DadAutoPartyConfiguration.NormalizeFingerprint(PairingTranscriptHash);
        RevocationGeneration = Math.Max(1, RevocationGeneration);
        SafeReason = DadAutoPartyConfiguration.NormalizeSafeCode(SafeReason);
        return this;
    }

    public DadAutoPartyDeauthentication Clone() => new()
    {
        DeauthenticationId = DeauthenticationId,
        PeerIslandId = PeerIslandId,
        PairingTranscriptHash = PairingTranscriptHash,
        RevocationGeneration = RevocationGeneration,
        SafeReason = SafeReason,
        RevokedAtUtc = RevokedAtUtc,
    };
}

public enum DadAutoPartyPairingHealth
{
    Disabled = 0,
    Unpaired = 1,
    Healthy = 2,
    Stale = 3,
    Revoked = 4,
    Blocked = 5,
}

public sealed record DadAutoPartyLanPresence(
    string RegisteredIslandId = "",
    string EndpointFingerprint = "",
    DadAutoPartyPairingHealth PairingHealth = DadAutoPartyPairingHealth.Disabled);

public sealed record DadAutoPartyGrant
{
    public string GrantId { get; init; } = string.Empty;
    public string ProposalId { get; init; } = string.Empty;
    public string OwnerId { get; init; } = string.Empty;
    public string IslandId { get; init; } = string.Empty;
    public string OpaqueCharacterId { get; init; } = string.Empty;
    public string RequestedJobId { get; init; } = string.Empty;
    public string ActivityId { get; init; } = string.Empty;
    public SessionPermission Permissions { get; init; }
    public DateTime IssuedAtUtc { get; init; }
    public DateTime ExpiresAtUtc { get; init; }
    public int MaximumUses { get; init; }
    public DateTime? ConsumedAtUtc { get; set; }

    public bool IsValid =>
        Guid.TryParse(GrantId, out _) &&
        Guid.TryParse(ProposalId, out _) &&
        !string.IsNullOrWhiteSpace(OwnerId) &&
        !string.IsNullOrWhiteSpace(IslandId) &&
        !string.IsNullOrWhiteSpace(OpaqueCharacterId) &&
        !string.IsNullOrWhiteSpace(RequestedJobId) &&
        !string.IsNullOrWhiteSpace(ActivityId) &&
        Permissions != SessionPermission.None &&
        IssuedAtUtc != default &&
        ExpiresAtUtc > IssuedAtUtc &&
        MaximumUses == 1;
}

internal sealed record DadAutoPartyPrivateIdentityPackage(
    string OwnerId,
    string IslandId,
    long KeyGeneration,
    string SigningPrivateKey,
    string EncryptionPrivateKey);

public sealed class DadAutoPartyListing
{
    public string ListingId { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public string SharingIslandId { get; set; } = string.Empty;
    public string SharingEndpointAlias { get; set; } = string.Empty;
    public DadAutoPartyCharacterShareMode EffectiveShareMode { get; set; } =
        DadAutoPartyCharacterShareMode.SpecificCharacter;
    public string EffectivePolicyHash { get; set; } = string.Empty;
    public string OpaqueCharacterId { get; set; } = string.Empty;
    public string DisplayLabel { get; set; } = string.Empty;

    [JsonIgnore]
    public string OpaqueDisplayLabel { get; set; } = string.Empty;

    public List<string> AllowedJobIds { get; set; } = [];
    public List<string> AllowedActivityIds { get; set; } = [];
    public bool Available { get; set; } = true;
    public long Revision { get; set; } = 1;
    public DateTime ExpiresAtUtc { get; set; }

    [JsonIgnore]
    public DateTime? TransientRouteExpiresAtUtc { get; set; }

    [JsonIgnore]
    public bool HasCurrentTransientRoute =>
        TransientRouteExpiresAtUtc is { } expiresAt && expiresAt > DateTime.UtcNow;

    public bool IsValid =>
        Guid.TryParse(ListingId, out _) &&
        !string.IsNullOrWhiteSpace(OwnerId) &&
        !string.IsNullOrWhiteSpace(SharingIslandId) &&
        Enum.IsDefined(EffectiveShareMode) &&
        !string.IsNullOrWhiteSpace(OpaqueCharacterId) &&
        !string.IsNullOrWhiteSpace(DisplayLabel) &&
        AllowedJobIds.Count > 0 &&
        AllowedActivityIds.Count > 0 &&
        Revision >= 1 &&
        ExpiresAtUtc != default;

    public DadAutoPartyListing Normalize()
    {
        ListingId = Guid.TryParse(ListingId, out var id) ? id.ToString("D") : string.Empty;
        OwnerId = DadAutoPartyConfiguration.NormalizeIdentifier(OwnerId);
        SharingIslandId = DadAutoPartyConfiguration.NormalizeIdentifier(SharingIslandId);
        SharingEndpointAlias = DadAutoPartyConfiguration.NormalizeAlias(SharingEndpointAlias);
        if (!Enum.IsDefined(EffectiveShareMode))
            EffectiveShareMode = DadAutoPartyCharacterShareMode.SpecificCharacter;
        EffectivePolicyHash = DadAutoPartyConfiguration.NormalizeIdentifier(EffectivePolicyHash);
        OpaqueCharacterId = DadAutoPartyConfiguration.NormalizeIdentifier(OpaqueCharacterId);
        DisplayLabel = (DisplayLabel ?? string.Empty).Trim();
        if (DisplayLabel.Length > 96)
            DisplayLabel = string.Empty;
        AllowedJobIds = NormalizeValues(AllowedJobIds);
        AllowedActivityIds = NormalizeValues(AllowedActivityIds);
        Revision = Math.Max(1, Revision);
        return this;
    }

    public DadAutoPartyListing Clone()
        => new()
        {
            ListingId = ListingId,
            OwnerId = OwnerId,
            SharingIslandId = SharingIslandId,
            SharingEndpointAlias = SharingEndpointAlias,
            EffectiveShareMode = EffectiveShareMode,
            EffectivePolicyHash = EffectivePolicyHash,
            OpaqueCharacterId = OpaqueCharacterId,
            DisplayLabel = DisplayLabel,
            OpaqueDisplayLabel = OpaqueDisplayLabel,
            AllowedJobIds = [.. AllowedJobIds],
            AllowedActivityIds = [.. AllowedActivityIds],
            Available = Available,
            Revision = Revision,
            ExpiresAtUtc = ExpiresAtUtc,
            TransientRouteExpiresAtUtc = TransientRouteExpiresAtUtc,
        };

    private static List<string> NormalizeValues(IEnumerable<string>? values)
        => (values ?? [])
            .Select(DadAutoPartyConfiguration.NormalizeIdentifier)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Take(64)
            .ToList();
}

public enum DadAutoPartyComponentState
{
    Disabled = 0,
    NotReady = 1,
    Ready = 2,
    Waiting = 3,
    Denied = 4,
    Faulted = 5,
}

public sealed record DadAutoPartyComponentStatus(
    DadAutoPartyComponentState State,
    string SafeCode,
    DateTime ObservedAtUtc);

public sealed record DadAutoPartyStatus(
    DadAutoPartyComponentStatus Transport,
    DadAutoPartyComponentStatus Policy,
    DadAutoPartyComponentStatus Execution,
    string RegisteredIslandId,
    int ListingCount,
    int GrantCount,
    int ActiveSessionCount);

public enum DadAutoPartyAuthorizationState
{
    NotRequired = 0,
    Waiting = 1,
    Authorized = 2,
    Denied = 3,
}

public sealed record DadAutoPartyAuthorizationDecision(
    DadAutoPartyAuthorizationState State,
    string SafeCode,
    Guid ProposalId)
{
    public bool Required => State != DadAutoPartyAuthorizationState.NotRequired;
    public bool Authorized => State == DadAutoPartyAuthorizationState.Authorized;
}

public sealed record DadAutoPartyPolicyDecision(
    bool Allowed,
    string SafeCode,
    long StateGeneration);

public enum DadAutoPartySessionMode
{
    Local = 0,
    Hybrid = 1,
    MultiOwner = 2,
}

public sealed record DadAutoPartySessionSnapshot(
    Guid ProposalId,
    string IslandId,
    string OwnerId,
    DadAutoPartySessionMode Mode,
    DateTime LeaseExpiresAtUtc,
    long StateGeneration,
    bool Revoked);

public sealed record DadAutoPartyRegistrationImport(
    string OwnerId,
    string IslandId,
    string PublicKeyFingerprint,
    long KeyGeneration,
    byte[] ProtectedIdentityMaterial);

public sealed record DadAutoPartyPublicIdentity(
    string Schema,
    string Alias,
    string OwnerId,
    string IslandId,
    long KeyGeneration,
    string SigningPublicKey,
    string EncryptionPublicKey,
    string Fingerprint,
    DateTime GeneratedAtUtc);

public sealed record DadAutoPartyIdentityOperationResult(
    bool Succeeded,
    string SafeCode,
    string OutputPath = "");

public sealed record DadAutoPartyObservedPartyReceipt(
    int MemberCount,
    ImmutableArray<ulong> ContentIds,
    string ObservedStateHash,
    DateTime ObservedAtUtc);

public sealed record DadAutoPartyExecutionResult(
    Guid OperationId,
    Guid ProposalId,
    ExecutionOperationKind Kind,
    ExecutionOutcome Outcome,
    DadRunPhase Phase,
    string SafeCode,
    long ObservedStateGeneration,
    DadAutoPartyObservedPartyReceipt? PartyReceipt = null,
    bool ProfileRestored = false);

public sealed record DadAutoPartyPrivacyResult(
    bool Purged,
    bool IdentityDeleted,
    string SafeCode);

public sealed record DadAutoPartyWebhookCredential(
    string WebhookId,
    string WebhookToken,
    string ChannelId)
{
    public CourierEpochDescriptor? UplinkEpoch { get; init; }
    public CourierEpochDescriptor? DownlinkEpoch { get; init; }
    public EndpointPublicKeys? RelayPublicKeys { get; init; }

    public bool IsValid =>
        DadAutoPartyConfiguration.NormalizeSnowflake(WebhookId) == WebhookId &&
        DadAutoPartyConfiguration.NormalizeSnowflake(ChannelId) == ChannelId &&
        WebhookToken.Length is >= 32 and <= 256 &&
        WebhookToken.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    public bool HasProvisionedMailbox =>
        IsValid &&
        IsEpochValid(UplinkEpoch, CourierDirection.Uplink) &&
        IsEpochValid(DownlinkEpoch, CourierDirection.Downlink) &&
        UplinkEpoch!.IslandId == DownlinkEpoch!.IslandId &&
        UplinkEpoch.EpochId != DownlinkEpoch.EpochId &&
        RelayPublicKeys is { KeyVersion: >= 1 } &&
        !RelayPublicKeys.Ed25519PublicKey.IsDefault &&
        !RelayPublicKeys.X25519PublicKey.IsDefault &&
        RelayPublicKeys.Ed25519PublicKey.Length == AutoPartyProtocol.Ed25519PublicKeyBytes &&
        RelayPublicKeys.X25519PublicKey.Length == AutoPartyProtocol.X25519KeyBytes;

    public override string ToString() => "DadAutoPartyWebhookCredential([redacted])";

    private static bool IsEpochValid(CourierEpochDescriptor? epoch, CourierDirection direction) =>
        epoch != null &&
        epoch.EpochId != Guid.Empty &&
        epoch.PageCount is > 0 and <= AutoPartyProtocol.MaximumCourierPages &&
        epoch.EpochGeneration >= 1 &&
        !epoch.PageReferences.IsDefault &&
        epoch.Direction == direction &&
        epoch.StartsAt.Offset == TimeSpan.Zero &&
        epoch.RotatesAt > epoch.StartsAt &&
        epoch.OverlapEndsAt > epoch.RotatesAt &&
        epoch.PageReferences.Length == epoch.PageCount &&
        epoch.PageReferences
            .OrderBy(static page => page.PageNumber)
            .Select(static page => page.PageNumber)
            .SequenceEqual(Enumerable.Range(1, epoch.PageCount)) &&
        epoch.PageReferences.All(page =>
            DadAutoPartyConfiguration.NormalizeSnowflake(page.MessageReference) == page.MessageReference);
}

public sealed record DadAutoPartyBootstrapImport(
    Guid RegistrationId,
    string OwnerId,
    string IslandId,
    string EndpointFingerprint,
    string CentralBotApplicationId,
    string HomeGuildScope,
    string RouteId,
    DadAutoPartyWebhookCredential Mailbox,
    CourierEpochDescriptor UplinkEpoch,
    CourierEpochDescriptor DownlinkEpoch,
    EndpointPublicKeys RelayPublicKeys,
    DateTime BootstrapExpiresAtUtc);

public enum DadAutoPartyEndpointConnectionState
{
    Disabled = 0,
    NotRegistered = 1,
    Connecting = 2,
    Ready = 3,
    Degraded = 4,
    Quarantined = 5,
}

public sealed record DadAutoPartyEndpointSnapshot(
    DadAutoPartyEndpointConnectionState State,
    string SafeCode,
    DateTime ObservedAtUtc,
    DateTime? LastSuccessfulExchangeAtUtc,
    int PendingOutboundCount,
    int PendingAcknowledgementCount,
    int BufferedInboundCount,
    long EpochGeneration)
{
    public static DadAutoPartyEndpointSnapshot Disabled(string safeCode = "dad-autoparty-disabled") =>
        new(DadAutoPartyEndpointConnectionState.Disabled, safeCode, DateTime.UtcNow, null, 0, 0, 0, 0);
}

public sealed record DadAutoPartyDirectorySnapshot(
    long StateGeneration,
    IReadOnlyList<DadAutoPartyListing> Listings,
    IReadOnlySet<string> OnlineIslandIds);
