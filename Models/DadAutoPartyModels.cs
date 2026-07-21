using AutoParty.Contracts;

namespace dad.Models;

public sealed class DadAutoPartyConfiguration
{
    public const string DefaultPilotExchangeRoot = @"Z:\autopartypilot";

    public bool Enabled { get; set; }
    public bool PairingEnabled { get; set; }
    public bool ExecutionEnabled { get; set; }
    public bool DiscordEnabled { get; set; }
    public string DiscordTokenReference { get; set; } = string.Empty;
    public ulong DiscordGuildId { get; set; }
    public ulong DiscordChannelId { get; set; }
    public ulong DiscordApplicationId { get; set; }
    public ulong DiscordBotUserId { get; set; }
    public ulong DiscordPresenceMessageId { get; set; }
    public DadAutoPartyDiscordBinding DiscordBinding { get; set; } = new();
    public DadMeasuredPilotCampaign MeasuredPilot { get; set; } = new();
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
    public string PilotExchangeRoot { get; set; } = DefaultPilotExchangeRoot;
    public string CourierRootPath { get; set; } = @"Z:\autopartypilot\pilot-courier";
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
        DiscordTokenReference = NormalizeTokenReference(DiscordTokenReference);
        if (DiscordGuildId == 0 || DiscordChannelId == 0)
            DiscordEnabled = false;
        DiscordBinding = (DiscordBinding ?? new DadAutoPartyDiscordBinding()).Normalize();
        MeasuredPilot = (MeasuredPilot ?? new DadMeasuredPilotCampaign()).Normalize();
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
        PilotExchangeRoot = NormalizePilotExchangeRoot(PilotExchangeRoot);
        CourierRootPath = Path.Combine(PilotExchangeRoot, "pilot-courier");
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
            DiscordEnabled = DiscordEnabled,
            DiscordTokenReference = DiscordTokenReference,
            DiscordGuildId = DiscordGuildId,
            DiscordChannelId = DiscordChannelId,
            DiscordApplicationId = DiscordApplicationId,
            DiscordBotUserId = DiscordBotUserId,
            DiscordPresenceMessageId = DiscordPresenceMessageId,
            DiscordBinding = DiscordBinding.Clone(),
            MeasuredPilot = MeasuredPilot.Clone(),
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
            PilotExchangeRoot = PilotExchangeRoot,
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

    internal static string NormalizeTokenReference(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized.Length == 46 &&
               normalized.StartsWith("discord-token-", StringComparison.Ordinal) &&
               normalized[14..].All(char.IsAsciiHexDigit)
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

    public string GetPilotInputRoot() => Path.Combine(PilotExchangeRoot, "pilot-input");

    public string GetPilotReceiptRoot() => Path.Combine(PilotExchangeRoot, "pilot-receipts");

    public string GetPilotFixturePath() => Path.Combine(GetPilotInputRoot(), "pilot-fixture.json");

    public string GetPilotCourierRoot() => Path.Combine(PilotExchangeRoot, "pilot-courier");

    public string GetPilotPluginRoot() => Path.Combine(PilotExchangeRoot, "plugin");

    public static bool TryNormalizePilotExchangeRoot(string? value, out string normalized)
    {
        var candidate = (value ?? string.Empty).Trim();
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate))
            return false;
        try
        {
            var fullPath = Path.GetFullPath(candidate).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            var pathRoot = Path.GetPathRoot(fullPath)?.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(fullPath) ||
                string.IsNullOrWhiteSpace(pathRoot) ||
                string.Equals(fullPath, pathRoot, StringComparison.OrdinalIgnoreCase))
                return false;
            normalized = fullPath;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string NormalizePilotExchangeRoot(string? value)
        => TryNormalizePilotExchangeRoot(value, out var normalized)
            ? normalized
            : DefaultPilotExchangeRoot;
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
    public ulong ApplicationId { get; set; }
    public ulong BotUserId { get; set; }
    public string SigningPublicKey { get; set; } = string.Empty;
    public DadAutoPartyRole Role { get; set; }
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
        SigningPublicKey = DadAutoPartyConfiguration.NormalizePublicKey(SigningPublicKey);
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
            ApplicationId = ApplicationId,
            BotUserId = BotUserId,
            SigningPublicKey = SigningPublicKey,
            Role = Role,
            ConfirmedAtUtc = ConfirmedAtUtc,
            RevokedAtUtc = RevokedAtUtc,
        };
}

public enum DadAutoPartyRole
{
    Client = 0,
    Coordinator = 1,
}

public enum DadAutoPartyDiscordConnectionState
{
    Disabled = 0,
    Connecting = 1,
    Ready = 2,
    Stale = 3,
    Disconnected = 4,
    Blocked = 5,
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

public sealed class DadAutoPartyDiscordBinding
{
    public ulong ApplicationId { get; set; }
    public ulong BotUserId { get; set; }
    public string DadIdentity { get; set; } = string.Empty;
    public string EndpointFingerprint { get; set; } = string.Empty;
    public long KeyGeneration { get; set; } = 1;

    public bool IsComplete =>
        ApplicationId != 0 && BotUserId != 0 &&
        !string.IsNullOrWhiteSpace(DadIdentity) &&
        !string.IsNullOrWhiteSpace(EndpointFingerprint);

    public DadAutoPartyDiscordBinding Normalize()
    {
        DadIdentity = DadAutoPartyConfiguration.NormalizeIdentifier(DadIdentity);
        EndpointFingerprint = DadAutoPartyConfiguration.NormalizeFingerprint(EndpointFingerprint);
        KeyGeneration = Math.Max(1, KeyGeneration);
        return this;
    }

    public DadAutoPartyDiscordBinding Clone() => new()
    {
        ApplicationId = ApplicationId,
        BotUserId = BotUserId,
        DadIdentity = DadIdentity,
        EndpointFingerprint = EndpointFingerprint,
        KeyGeneration = KeyGeneration,
    };
}

public sealed record DadAutoPartyDiscordHealth(
    DadAutoPartyDiscordConnectionState State,
    string SafeCode,
    DateTime ObservedAtUtc,
    DateTime? LastPresenceAtUtc,
    ulong ApplicationId,
    ulong BotUserId,
    bool PermissionsValid)
{
    public bool IsHealthy =>
        State == DadAutoPartyDiscordConnectionState.Ready && PermissionsValid &&
        LastPresenceAtUtc.HasValue && DateTime.UtcNow - LastPresenceAtUtc.Value <= TimeSpan.FromMinutes(3);
}

public sealed record DadAutoPartyDiscoveredClient(
    ulong ApplicationId,
    ulong BotUserId,
    string DadIdentity,
    string EndpointFingerprint,
    string SigningPublicKey,
    long KeyGeneration,
    DadAutoPartyRole Role,
    DateTime LastSeenUtc,
    DadAutoPartyPairingHealth PairingHealth,
    string Blocker);

public sealed record DadAutoPartyLanPresence(
    ulong ApplicationId = 0,
    string EndpointFingerprint = "",
    DadAutoPartyPairingHealth PairingHealth = DadAutoPartyPairingHealth.Disabled);

public enum DadAutoPartyPairingMessageKind
{
    Presence = 0,
    PairRequest = 1,
    PairAccept = 2,
    PairReject = 3,
    Revoke = 4,
}

public sealed class DadAutoPartyPairingEnvelope
{
    public string Schema { get; set; } = "dad.pairing/v1";
    public DadAutoPartyPairingMessageKind Kind { get; set; }
    public long TimestampUnixMs { get; set; }
    public string Nonce { get; set; } = string.Empty;
    public long KeyGeneration { get; set; } = 1;
    public ulong ApplicationId { get; set; }
    public ulong BotUserId { get; set; }
    public DadAutoPartyRole Role { get; set; }
    public string DadIdentity { get; set; } = string.Empty;
    public string EndpointFingerprint { get; set; } = string.Empty;
    public string SigningPublicKey { get; set; } = string.Empty;
    public ulong TargetApplicationId { get; set; }
    public string TargetDadIdentity { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
}

public enum DadMeasuredPilotState
{
    NotStarted = 0,
    Active = 1,
    EvaluationIncomplete = 2,
    Passed = 3,
    HardFailed = 4,
}

public enum DadMeasuredPilotOrigin
{
    Unknown = 0,
    Plans = 1,
    Schedules = 2,
}

public enum DadMeasuredPilotEventKind
{
    CampaignStarted = 0,
    CampaignResumed = 1,
    RunStarted = 2,
    RunTerminal = 3,
    StopAll = 4,
    DiscordHealth = 5,
    PairingRevoked = 6,
    PairingRestored = 7,
    ReceiptWritten = 8,
    SafetyViolation = 9,
}

public sealed class DadMeasuredPilotEvent
{
    public string EventId { get; set; } = Guid.NewGuid().ToString("N");
    public DadMeasuredPilotEventKind Kind { get; set; }
    public DateTime ObservedAtUtc { get; set; } = DateTime.UtcNow;
    public string RunId { get; set; } = string.Empty;
    public string SafeCode { get; set; } = string.Empty;
}

public sealed class DadMeasuredPilotRunEvidence
{
    public string RunId { get; set; } = string.Empty;
    public DadMeasuredPilotOrigin Origin { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime CompletedAtUtc { get; set; }
    public bool Terminal { get; set; }
    public bool DryRun { get; set; }
    public bool Successful { get; set; }
    public int ParticipantCount { get; set; }
    public List<ulong> HealthyApplicationIds { get; set; } = [];
    public bool FormationVerified { get; set; }
    public bool ReadinessBeforeQueueVerified { get; set; }
    public bool RequestedJobRun { get; set; }
    public bool RequestedJobMatched { get; set; }
    public bool RequestedJobSwitched { get; set; }
    public bool LeaseCleanupVerified { get; set; }
    public bool ClaimCleanupVerified { get; set; }
    public bool SchedulerCleanupVerified { get; set; }
    public string ProfileRestoration { get; set; } = "not-applicable";
    public string FailureCode { get; set; } = string.Empty;

    public bool Qualifies => Terminal && !DryRun && Successful && ParticipantCount >= 2 &&
        HealthyApplicationIds.Distinct().Count() >= 2 && FormationVerified &&
        ReadinessBeforeQueueVerified && (!RequestedJobRun || RequestedJobMatched) &&
        LeaseCleanupVerified && ClaimCleanupVerified && SchedulerCleanupVerified;
}

public sealed class DadMeasuredPilotCampaign
{
    public string CampaignId { get; set; } = string.Empty;
    public DadMeasuredPilotState State { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? StoppedAtUtc { get; set; }
    public string CoordinatorIdentity { get; set; } = string.Empty;
    public string AssemblySha256 { get; set; } = string.Empty;
    public List<DadMeasuredPilotRunEvidence> Runs { get; set; } = [];
    public List<DadMeasuredPilotEvent> Events { get; set; } = [];
    public List<string> SafetyViolations { get; set; } = [];
    public bool StopAllVerified { get; set; }
    public bool RecoveryRunVerified { get; set; }
    public bool RecoveryRunRequired { get; set; }
    public bool DiscordReconnectCycleVerified { get; set; }
    public bool RevokeExclusionVerified { get; set; }
    public bool RePairVerified { get; set; }
    public string ReceiptPath { get; set; } = string.Empty;

    public DadMeasuredPilotCampaign Normalize()
    {
        CampaignId = Guid.TryParse(CampaignId, out var id) ? id.ToString("D") : string.Empty;
        CoordinatorIdentity = DadAutoPartyConfiguration.NormalizeIdentifier(CoordinatorIdentity);
        AssemblySha256 = DadAutoPartyConfiguration.NormalizeSha256(AssemblySha256);
        Runs = (Runs ?? []).Where(static run => !string.IsNullOrWhiteSpace(run.RunId))
            .DistinctBy(static run => run.RunId, StringComparer.Ordinal).TakeLast(256).ToList();
        Events = (Events ?? []).TakeLast(2048).ToList();
        SafetyViolations = (SafetyViolations ?? []).Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal).Take(32).ToList();
        ReceiptPath = ReceiptPath?.Trim() ?? string.Empty;
        return this;
    }

    public DadMeasuredPilotCampaign Clone() => new()
    {
        CampaignId = CampaignId,
        State = State,
        StartedAtUtc = StartedAtUtc,
        StoppedAtUtc = StoppedAtUtc,
        CoordinatorIdentity = CoordinatorIdentity,
        AssemblySha256 = AssemblySha256,
        Runs = Runs.Select(static run => new DadMeasuredPilotRunEvidence
        {
            RunId = run.RunId,
            Origin = run.Origin,
            StartedAtUtc = run.StartedAtUtc,
            CompletedAtUtc = run.CompletedAtUtc,
            Terminal = run.Terminal,
            DryRun = run.DryRun,
            Successful = run.Successful,
            ParticipantCount = run.ParticipantCount,
            HealthyApplicationIds = [.. run.HealthyApplicationIds],
            FormationVerified = run.FormationVerified,
            ReadinessBeforeQueueVerified = run.ReadinessBeforeQueueVerified,
            RequestedJobRun = run.RequestedJobRun,
            RequestedJobMatched = run.RequestedJobMatched,
            RequestedJobSwitched = run.RequestedJobSwitched,
            LeaseCleanupVerified = run.LeaseCleanupVerified,
            ClaimCleanupVerified = run.ClaimCleanupVerified,
            SchedulerCleanupVerified = run.SchedulerCleanupVerified,
            ProfileRestoration = run.ProfileRestoration,
            FailureCode = run.FailureCode,
        }).ToList(),
        Events = Events.Select(static e => new DadMeasuredPilotEvent
        {
            EventId = e.EventId,
            Kind = e.Kind,
            ObservedAtUtc = e.ObservedAtUtc,
            RunId = e.RunId,
            SafeCode = e.SafeCode,
        }).ToList(),
        SafetyViolations = [.. SafetyViolations],
        StopAllVerified = StopAllVerified,
        RecoveryRunVerified = RecoveryRunVerified,
        RecoveryRunRequired = RecoveryRunRequired,
        DiscordReconnectCycleVerified = DiscordReconnectCycleVerified,
        RevokeExclusionVerified = RevokeExclusionVerified,
        RePairVerified = RePairVerified,
        ReceiptPath = ReceiptPath,
    };
}

public sealed record DadMeasuredPilotEvaluation(
    DadMeasuredPilotState State,
    int QualifyingSuccesses,
    int PlanSuccesses,
    int ScheduleSuccesses,
    int RequestedJobSuccesses,
    int RequestedJobSwitches,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> SafetyViolations)
{
    public bool Passed => State == DadMeasuredPilotState.Passed;
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
