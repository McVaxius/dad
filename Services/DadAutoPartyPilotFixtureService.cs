using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using dad.Models;

namespace dad.Services;

public sealed class DadAutoPartyPilotFixtureService
{
    public const string FixtureSchema = "dad.autoparty.pilot-fixture/v1";
    private const int MaximumFixtureBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Configuration configuration;
    private readonly Action saveConfiguration;

    public DadAutoPartyPilotFixtureService(Configuration configuration, Action saveConfiguration)
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.saveConfiguration = saveConfiguration ?? throw new ArgumentNullException(nameof(saveConfiguration));
    }

    public async ValueTask<DadAutoPartyIdentityOperationResult> ImportAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path))
            return Failure("dad-pilot-fixture-path-invalid");
        var file = new FileInfo(path);
        if (file.Length is <= 0 or > MaximumFixtureBytes)
            return Failure("dad-pilot-fixture-size-invalid");

        DadAutoPartyPilotFixture? fixture;
        try
        {
            await using var stream = File.OpenRead(path);
            fixture = await JsonSerializer.DeserializeAsync<DadAutoPartyPilotFixture>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Failure("dad-pilot-fixture-invalid");
        }

        var autoParty = configuration.AutoParty;
        var queueAuthorityFingerprint = DadAutoPartyConfiguration.NormalizeFingerprint(
            fixture?.QueueAuthorityFingerprint);
        if (fixture == null || !string.Equals(fixture.Schema, FixtureSchema, StringComparison.Ordinal) ||
            !fixture.FormationOnly || fixture.ContentFinderConditionId == 0 ||
            string.IsNullOrWhiteSpace(autoParty.PilotArtifactSha256) ||
            !autoParty.OwnerAcceptanceConfirmed || autoParty.PendingPairings.Count != 0 ||
            !string.Equals(
                DadAutoPartyConfiguration.NormalizeSha256(fixture.PilotArtifactSha256),
                autoParty.PilotArtifactSha256,
                StringComparison.Ordinal) ||
            fixture.Participants == null || fixture.Participants.Count is < 2 or > 8 ||
            fixture.Participants.Count(participant => participant.OwnsQueueAuthority) != 1 ||
            fixture.Participants.Any(participant =>
                !participant.OwnerConsentConfirmed ||
                !string.Equals(
                    participant.IdentityFingerprint,
                    DadAutoPartyConfiguration.NormalizeFingerprint(participant.IdentityFingerprint),
                    StringComparison.Ordinal) ||
                !uint.TryParse(participant.RequestedJobId, NumberStyles.None, CultureInfo.InvariantCulture, out var jobId) ||
                jobId is 0 or > 1000) ||
            fixture.Participants.Select(participant => participant.IdentityFingerprint)
                .Distinct(StringComparer.Ordinal).Count() != fixture.Participants.Count ||
            string.IsNullOrWhiteSpace(queueAuthorityFingerprint) ||
            fixture.Participants.Single(participant => participant.OwnsQueueAuthority).IdentityFingerprint !=
            queueAuthorityFingerprint)
            return Failure("dad-pilot-fixture-mismatch");

        var activePairings = autoParty.Pairings
            .Where(pairing => pairing.RevokedAtUtc == null)
            .ToList();
        var knownFingerprints = activePairings
            .Select(pairing => pairing.PublicKeyFingerprint)
            .Append(autoParty.RegistrationFingerprint)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (fixture.Participants.Count != activePairings.Count + 1 ||
            !fixture.Participants.Any(participant =>
                string.Equals(
                    participant.IdentityFingerprint,
                    autoParty.RegistrationFingerprint,
                    StringComparison.Ordinal)) ||
            fixture.Participants.Any(participant => !knownFingerprints.Contains(participant.IdentityFingerprint)))
            return Failure("dad-pilot-fixture-identity-not-enrolled");

        var material = Encoding.UTF8.GetBytes(string.Join('|',
            FixtureSchema,
            autoParty.PilotArtifactSha256,
            fixture.ContentFinderConditionId.ToString(CultureInfo.InvariantCulture),
            string.Join(',', fixture.Participants.Select(participant => participant.IdentityFingerprint))));
        string groupId;
        string proposalId;
        try
        {
            var hash = SHA256.HashData(material);
            groupId = Convert.ToHexString(hash)[..32].ToLowerInvariant();
            proposalId = new Guid(hash.AsSpan(0, 16)).ToString("D");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }

        var slots = fixture.Participants.Select((participant, index) => new DadPlannerGroupSlot
        {
            SlotId = DadPlannerSlotRules.FormatSlotId(index + 1),
            RequiredRole = DadPartyRole.Any,
            RequiredAccountKey = new DadAccountKey(string.Empty),
            RequiredCharacterKey = new DadCharacterKey(string.Empty),
            RequiredJobId = uint.Parse(participant.RequestedJobId, CultureInfo.InvariantCulture),
            WakePolicy = DadSchedulerWakePolicy.AlreadyOnlineOnly,
            CharacterLoadInstruction = new DadCharacterLoadInstruction { Enabled = false, DryRun = true },
            SharedIdentity = new DadSharedIdentityPlaceholder
            {
                IdentityToken = participant.IdentityFingerprint,
                CharacterLabel = $"AutoParty island {index + 1}",
                RequiresCharacter = true,
            },
            AllowSubstitution = false,
        }).ToList();
        var group = new DadPlannerGroup
        {
            GroupId = groupId,
            DisplayName = "AutoParty P1203 formation-only",
            RunFamily = DadPlannerRunFamily.DutyFinder,
            ActivityMode = DadPlannerActivityMode.DutyPremade,
            OperatorMode = DadPlannerOperatorMode.RemotePartyPlan,
            ConnectedOnly = true,
            SameDatacenterOnly = true,
            TransportOwner = DadTransportOwner.DadDirect,
            QueueAuthority = DadQueueAuthority.LocalOnly,
            InviteAuthority = DadInviteAuthority.PresetLeader,
            DutyContentFinderConditionId = fixture.ContentFinderConditionId,
            DutyDisplayName = $"P1203 safe duty #{fixture.ContentFinderConditionId}",
            DutyUnsynced = false,
            DutyExpectedPartySize = slots.Count,
            Slots = slots,
            AutoPartyProposalId = proposalId,
            AutoPartyFormationOnly = true,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        };

        var collision = configuration.PlannerGroups.Any(existing =>
            string.Equals(existing.GroupId, groupId, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(existing.GroupId, autoParty.PilotPlannerGroupId, StringComparison.OrdinalIgnoreCase));
        if (collision)
            return Failure("dad-pilot-fixture-plan-collision");
        if (!string.IsNullOrWhiteSpace(autoParty.PilotPlannerGroupId))
            configuration.PlannerGroups.RemoveAll(existing =>
                string.Equals(existing.GroupId, autoParty.PilotPlannerGroupId, StringComparison.OrdinalIgnoreCase));
        configuration.PlannerGroups.Add(group);
        autoParty.PilotPlannerGroupId = groupId;
        autoParty.PilotQueueAuthorityFingerprint = queueAuthorityFingerprint;
        autoParty.RemoteBindings = fixture.Participants.Select((participant, index) =>
        {
            var pairing = activePairings.FirstOrDefault(candidate =>
                string.Equals(candidate.PublicKeyFingerprint, participant.IdentityFingerprint, StringComparison.OrdinalIgnoreCase));
            var isLocal = string.Equals(
                autoParty.RegistrationFingerprint,
                participant.IdentityFingerprint,
                StringComparison.OrdinalIgnoreCase);
            return new DadAutoPartyRemoteBinding
            {
                FleetRowId = $"autoparty-{index + 1}",
                OpaqueCharacterId = participant.IdentityFingerprint,
                OwnerId = isLocal ? autoParty.RegisteredOwnerId : pairing?.OwnerId ?? string.Empty,
                IslandId = isLocal ? autoParty.RegisteredIslandId : pairing?.IslandId ?? string.Empty,
                RequestedJobId = participant.RequestedJobId,
                OwnsQueueAuthority = participant.OwnsQueueAuthority,
                OwnerConsentConfirmed = participant.OwnerConsentConfirmed,
            }.Normalize();
        }).ToList();
        autoParty.StateGeneration++;
        saveConfiguration();
        return new(true, "dad-pilot-formation-fixture-imported", path);
    }

    private static DadAutoPartyIdentityOperationResult Failure(string code) => new(false, code);
}
