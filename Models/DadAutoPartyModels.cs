using AutoParty.Contracts;

namespace dad.Models;

public sealed class DadAutoPartyConfiguration
{
    public bool Enabled { get; set; }
    public bool PairingEnabled { get; set; }
    public bool ExecutionEnabled { get; set; }
    public string EndpointIdentityReference { get; set; } = string.Empty;
    public string RegisteredOwnerId { get; set; } = string.Empty;
    public string RegisteredIslandId { get; set; } = string.Empty;
    public string RegistrationFingerprint { get; set; } = string.Empty;
    public string EndpointAlias { get; set; } = string.Empty;
    public string SigningPublicKey { get; set; } = string.Empty;
    public string EncryptionPublicKey { get; set; } = string.Empty;
    public string EnrollmentReceiptId { get; set; } = string.Empty;
    public string PilotArtifactSha256 { get; set; } = string.Empty;
    public bool OwnerAcceptanceConfirmed { get; set; }
    public string CourierRootPath { get; set; } = @"D:\AutoParty-LiveGate\pilot-courier";
    public string PilotPlannerGroupId { get; set; } = string.Empty;
    public string PilotQueueAuthorityFingerprint { get; set; } = string.Empty;
    public bool PilotCourierProbeVerified { get; set; }
    public long StateGeneration { get; set; } = 1;
    public List<DadAutoPartyPairing> Pairings { get; set; } = [];
    public List<DadAutoPartyGrant> Grants { get; set; } = [];
    public List<DadAutoPartyListing> Listings { get; set; } = [];
    public List<DadAutoPartyRemoteBinding> RemoteBindings { get; set; } = [];
    public List<DadAutoPartyPairing> PendingPairings { get; set; } = [];

    public DadAutoPartyConfiguration Normalize()
    {
        EndpointIdentityReference = NormalizeIdentifier(EndpointIdentityReference);
        RegisteredOwnerId = NormalizeIdentifier(RegisteredOwnerId);
        RegisteredIslandId = NormalizeIdentifier(RegisteredIslandId);
        RegistrationFingerprint = NormalizeFingerprint(RegistrationFingerprint);
        EndpointAlias = NormalizeAlias(EndpointAlias);
        SigningPublicKey = NormalizePublicKey(SigningPublicKey);
        EncryptionPublicKey = NormalizePublicKey(EncryptionPublicKey);
        EnrollmentReceiptId = Guid.TryParse(EnrollmentReceiptId, out var receiptId)
            ? receiptId.ToString("D")
            : string.Empty;
        PilotArtifactSha256 = NormalizeSha256(PilotArtifactSha256);
        CourierRootPath = NormalizeCourierRoot(CourierRootPath);
        PilotPlannerGroupId = NormalizeIdentifier(PilotPlannerGroupId);
        PilotQueueAuthorityFingerprint = NormalizeFingerprint(PilotQueueAuthorityFingerprint);
        StateGeneration = Math.Max(1, StateGeneration);
        Pairings = (Pairings ?? [])
            .Where(static pairing => pairing != null)
            .Select(static pairing => pairing!.Normalize())
            .Where(static pairing => pairing.IsValid)
            .DistinctBy(static pairing => pairing.IslandId, StringComparer.Ordinal)
            .Take(256)
            .ToList();
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
        PendingPairings = (PendingPairings ?? [])
            .Where(static pairing => pairing != null)
            .Select(static pairing => pairing!.Normalize())
            .Where(static pairing => pairing.IsValid)
            .DistinctBy(static pairing => pairing.IslandId, StringComparer.Ordinal)
            .Take(16)
            .ToList();
        return this;
    }

    public DadAutoPartyConfiguration Clone()
        => new()
        {
            Enabled = Enabled,
            PairingEnabled = PairingEnabled,
            ExecutionEnabled = ExecutionEnabled,
            EndpointIdentityReference = EndpointIdentityReference,
            RegisteredOwnerId = RegisteredOwnerId,
            RegisteredIslandId = RegisteredIslandId,
            RegistrationFingerprint = RegistrationFingerprint,
            EndpointAlias = EndpointAlias,
            SigningPublicKey = SigningPublicKey,
            EncryptionPublicKey = EncryptionPublicKey,
            EnrollmentReceiptId = EnrollmentReceiptId,
            PilotArtifactSha256 = PilotArtifactSha256,
            OwnerAcceptanceConfirmed = OwnerAcceptanceConfirmed,
            CourierRootPath = CourierRootPath,
            PilotPlannerGroupId = PilotPlannerGroupId,
            PilotQueueAuthorityFingerprint = PilotQueueAuthorityFingerprint,
            PilotCourierProbeVerified = PilotCourierProbeVerified,
            StateGeneration = StateGeneration,
            Pairings = Pairings.Select(static pairing => pairing.Clone()).ToList(),
            Grants = [.. Grants],
            Listings = Listings.Select(static listing => listing.Clone()).ToList(),
            RemoteBindings = RemoteBindings.Select(static binding => binding.Clone()).ToList(),
            PendingPairings = PendingPairings.Select(static pairing => pairing.Clone()).ToList(),
        };

    internal static string NormalizeIdentifier(string? value)
        => (value ?? string.Empty).Trim() is { Length: <= 128 } normalized
            ? normalized
            : string.Empty;

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

    private static string NormalizeCourierRoot(string? value)
    {
        var candidate = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate))
            return @"D:\AutoParty-LiveGate\pilot-courier";
        try
        {
            return Path.GetFullPath(candidate);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return @"D:\AutoParty-LiveGate\pilot-courier";
        }
    }
}

public sealed class DadAutoPartyRemoteBinding
{
    public string FleetRowId { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public string IslandId { get; set; } = string.Empty;
    public string RequestedJobId { get; set; } = string.Empty;
    public bool OwnsQueueAuthority { get; set; }
    public bool OwnerConsentConfirmed { get; set; }

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(FleetRowId) &&
        !string.IsNullOrWhiteSpace(OwnerId) &&
        !string.IsNullOrWhiteSpace(IslandId) &&
        !string.IsNullOrWhiteSpace(RequestedJobId) &&
        OwnerConsentConfirmed;

    public DadAutoPartyRemoteBinding Normalize()
    {
        FleetRowId = DadAutoPartyConfiguration.NormalizeIdentifier(FleetRowId);
        OwnerId = DadAutoPartyConfiguration.NormalizeIdentifier(OwnerId);
        IslandId = DadAutoPartyConfiguration.NormalizeIdentifier(IslandId);
        RequestedJobId = DadAutoPartyConfiguration.NormalizeIdentifier(RequestedJobId);
        return this;
    }

    public DadAutoPartyRemoteBinding Clone() => new()
    {
        FleetRowId = FleetRowId,
        OwnerId = OwnerId,
        IslandId = IslandId,
        RequestedJobId = RequestedJobId,
        OwnsQueueAuthority = OwnsQueueAuthority,
        OwnerConsentConfirmed = OwnerConsentConfirmed,
    };
}

public sealed class DadAutoPartyPairing
{
    public string OwnerId { get; set; } = string.Empty;
    public string IslandId { get; set; } = string.Empty;
    public string PublicKeyFingerprint { get; set; } = string.Empty;
    public long KeyGeneration { get; set; } = 1;
    public DateTime ConfirmedAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(OwnerId) &&
        !string.IsNullOrWhiteSpace(IslandId) &&
        !string.IsNullOrWhiteSpace(PublicKeyFingerprint) &&
        ConfirmedAtUtc != default;

    public DadAutoPartyPairing Normalize()
    {
        OwnerId = DadAutoPartyConfiguration.NormalizeIdentifier(OwnerId);
        IslandId = DadAutoPartyConfiguration.NormalizeIdentifier(IslandId);
        PublicKeyFingerprint = DadAutoPartyConfiguration.NormalizeFingerprint(PublicKeyFingerprint);
        KeyGeneration = Math.Max(1, KeyGeneration);
        return this;
    }

    public DadAutoPartyPairing Clone()
        => new()
        {
            OwnerId = OwnerId,
            IslandId = IslandId,
            PublicKeyFingerprint = PublicKeyFingerprint,
            KeyGeneration = KeyGeneration,
            ConfirmedAtUtc = ConfirmedAtUtc,
            RevokedAtUtc = RevokedAtUtc,
        };
}

public sealed record DadAutoPartyGrant
{
    public string GrantId { get; init; } = string.Empty;
    public string OwnerId { get; init; } = string.Empty;
    public string IslandId { get; init; } = string.Empty;
    public string OpaqueCharacterId { get; init; } = string.Empty;
    public string RequestedJobId { get; init; } = string.Empty;
    public string ActivityId { get; init; } = string.Empty;
    public SessionPermission Permissions { get; init; }
    public DateTime IssuedAtUtc { get; init; }
    public DateTime ExpiresAtUtc { get; init; }

    public bool IsValid =>
        Guid.TryParse(GrantId, out _) &&
        !string.IsNullOrWhiteSpace(OwnerId) &&
        !string.IsNullOrWhiteSpace(IslandId) &&
        !string.IsNullOrWhiteSpace(OpaqueCharacterId) &&
        !string.IsNullOrWhiteSpace(RequestedJobId) &&
        !string.IsNullOrWhiteSpace(ActivityId) &&
        Permissions != SessionPermission.None &&
        IssuedAtUtc != default &&
        ExpiresAtUtc > IssuedAtUtc;
}

public sealed class DadAutoPartyListing
{
    public string ListingId { get; set; } = string.Empty;
    public string OpaqueCharacterId { get; set; } = string.Empty;
    public List<string> AllowedJobIds { get; set; } = [];
    public List<string> AllowedActivityIds { get; set; } = [];
    public DateTime ExpiresAtUtc { get; set; }

    public bool IsValid =>
        Guid.TryParse(ListingId, out _) &&
        !string.IsNullOrWhiteSpace(OpaqueCharacterId) &&
        AllowedJobIds.Count > 0 &&
        AllowedActivityIds.Count > 0 &&
        ExpiresAtUtc != default;

    public DadAutoPartyListing Normalize()
    {
        ListingId = Guid.TryParse(ListingId, out var id) ? id.ToString("D") : string.Empty;
        OpaqueCharacterId = DadAutoPartyConfiguration.NormalizeIdentifier(OpaqueCharacterId);
        AllowedJobIds = NormalizeValues(AllowedJobIds);
        AllowedActivityIds = NormalizeValues(AllowedActivityIds);
        return this;
    }

    public DadAutoPartyListing Clone()
        => new()
        {
            ListingId = ListingId,
            OpaqueCharacterId = OpaqueCharacterId,
            AllowedJobIds = [.. AllowedJobIds],
            AllowedActivityIds = [.. AllowedActivityIds],
            ExpiresAtUtc = ExpiresAtUtc,
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

public sealed record DadAutoPartyEnrollmentReceipt(
    string Schema,
    string ReceiptId,
    string OwnerId,
    string IslandId,
    long KeyGeneration,
    string IdentityFingerprint,
    string PilotArtifactSha256,
    bool OwnerAcceptanceConfirmed,
    DateTime AcceptedAtUtc,
    IReadOnlyList<DadAutoPartyEnrollmentPeer> Peers);

public sealed record DadAutoPartyEnrollmentPeer(
    string OwnerId,
    string IslandId,
    string IdentityFingerprint,
    long KeyGeneration);

public sealed record DadAutoPartyPilotStatusReceipt(
    string Schema,
    string Alias,
    string IdentityFingerprint,
    string PilotArtifactSha256,
    bool TransportEnabled,
    bool PairingEnabled,
    bool ExecutionEnabled,
    bool OwnerAcceptanceConfirmed,
    bool FormationOnlyFixtureReady,
    bool CourierProbeVerified,
    int PairingCount,
    DateTime GeneratedAtUtc);

public sealed record DadAutoPartyPilotFixture(
    string Schema,
    bool FormationOnly,
    uint ContentFinderConditionId,
    string QueueAuthorityFingerprint,
    string PilotArtifactSha256,
    IReadOnlyList<DadAutoPartyPilotParticipant> Participants);

public sealed record DadAutoPartyPilotParticipant(
    string IdentityFingerprint,
    string RequestedJobId,
    bool OwnerConsentConfirmed,
    bool OwnsQueueAuthority);

public sealed record DadAutoPartyIdentityOperationResult(
    bool Succeeded,
    string SafeCode,
    string OutputPath = "");

public sealed record DadAutoPartyPairingChallenge(
    Guid ChallengeId,
    string OwnerId,
    string IslandId,
    string PublicKeyFingerprint,
    long KeyGeneration,
    string ConfirmationCode,
    DateTime ExpiresAtUtc);

public sealed record DadAutoPartyObservedPartyReceipt(
    int MemberCount,
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
