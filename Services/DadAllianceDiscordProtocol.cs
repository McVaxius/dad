using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using dad.Models;

namespace dad.Services;

public sealed class DadAllianceDiscordProtocol
{
    public const string Schema = "dad.alliance-pf/v1";
    public const int MaximumEnvelopeCharacters = 1900;
    public static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(3);
    private const int MaximumReplayEntries = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object gate = new();
    private readonly Dictionary<string, DateTime> observedNonces = new(StringComparer.Ordinal);

    public async ValueTask<DadAllianceDiscordEnvelope> CreateAsync(
        DadAllianceRecruitmentInstructionDto instruction,
        DadAutoPartyConfiguration configuration,
        DadAutoPartySigningService signing,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        var blocker = DadAlliancePartyFinderRules.ValidateInstruction(instruction);
        if (!string.IsNullOrWhiteSpace(blocker))
            throw new InvalidOperationException(blocker);
        if (!configuration.DiscordBinding.IsComplete ||
            configuration.DiscordApplicationId == 0 ||
            configuration.DiscordBotUserId == 0 ||
            string.IsNullOrWhiteSpace(configuration.RegisteredIslandId) ||
            string.IsNullOrWhiteSpace(configuration.RegistrationFingerprint))
        {
            throw new InvalidOperationException("The Discord coordinator identity binding is incomplete.");
        }
        if (instruction.TargetApplicationId == 0)
            throw new InvalidOperationException("The exact target Discord application is missing.");
        if (!string.Equals(
                instruction.CoordinatorIdentity.Trim(),
                configuration.RegisteredIslandId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The Discord coordinator identity contradicts the hub instruction.");
        }

        var envelope = new DadAllianceDiscordEnvelope
        {
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Nonce = Guid.NewGuid().ToString("N"),
            KeyGeneration = Math.Max(1, configuration.EndpointKeyGeneration),
            ApplicationId = configuration.DiscordApplicationId,
            BotUserId = configuration.DiscordBotUserId,
            Role = DadAutoPartyRole.Coordinator,
            CoordinatorIdentity = instruction.CoordinatorIdentity.Trim(),
            CoordinatorWorkerSessionId = instruction.CoordinatorWorkerSessionId,
            EndpointFingerprint = configuration.RegistrationFingerprint,
            TargetApplicationId = instruction.TargetApplicationId,
            TargetWorkerSessionId = instruction.TargetWorkerSessionId,
            RecruitmentId = instruction.RecruitmentId,
            TargetCharacterKey = instruction.TargetCharacterKey,
            TargetCharacterName = instruction.TargetCharacterName,
            TargetCharacterWorld = instruction.TargetCharacterWorld,
            TargetContentId = instruction.TargetContentId,
            LeaderName = instruction.LeaderName,
            LeaderWorld = instruction.LeaderWorld,
            Passcode = instruction.Passcode,
            AssignedAlliance = instruction.AssignedAlliance,
            Attempt = instruction.Attempt,
            State = instruction.State,
            StopGeneration = instruction.StopGeneration,
        };

        var payload = BuildSigningPayload(envelope);
        try
        {
            envelope.Signature = Convert.ToBase64String(
                await signing.SignAsync(payload, cancellationToken).ConfigureAwait(false));
            if (Serialize(envelope).Length > MaximumEnvelopeCharacters)
                throw new InvalidOperationException("The alliance PF envelope exceeds the Discord message bound.");
            return envelope;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    public DadAutoPartyPolicyDecision Validate(
        DadAllianceDiscordEnvelope? envelope,
        DadAllianceDiscordValidationContext context)
    {
        if (envelope == null ||
            !string.Equals(envelope.Schema, Schema, StringComparison.Ordinal) ||
            envelope.ApplicationId == 0 ||
            envelope.BotUserId == 0 ||
            envelope.BotUserId != context.MessageAuthorId ||
            envelope.Role != DadAutoPartyRole.Coordinator ||
            envelope.KeyGeneration < 1 ||
            envelope.TargetApplicationId == 0 ||
            envelope.TargetApplicationId != context.LocalApplicationId ||
            envelope.CoordinatorWorkerSessionId.IsEmpty ||
            envelope.TargetWorkerSessionId.IsEmpty ||
            !Guid.TryParseExact(envelope.RecruitmentId, "N", out _) ||
            !Guid.TryParseExact(envelope.Nonce, "N", out _) ||
            envelope.Passcode is < 1000 or > 9999 ||
            !DadAlliancePartyFinderRules.IsConcreteAssignment(envelope.AssignedAlliance) ||
            !Enum.IsDefined(typeof(DadAllianceRecruitmentState), envelope.State) ||
            envelope.Attempt < 0 ||
            envelope.StopGeneration < 0 ||
            envelope.TargetCharacterKey.IsEmpty ||
            string.IsNullOrWhiteSpace(envelope.TargetCharacterName) ||
            string.IsNullOrWhiteSpace(envelope.TargetCharacterWorld) ||
            string.IsNullOrWhiteSpace(envelope.LeaderName) ||
            string.IsNullOrWhiteSpace(envelope.LeaderWorld) ||
            !string.Equals(
                envelope.TargetCharacterKey.Value.Trim(),
                context.LocalCharacterKey.Value.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            return Denied("dad-alliance-discord-envelope-invalid");
        }

        if (string.IsNullOrWhiteSpace(envelope.Signature))
            return Denied("dad-alliance-discord-signature-missing");

        var pairing = context.CoordinatorPairing;
        if (pairing == null || !pairing.IsValid)
            return Denied("dad-alliance-discord-unpaired");
        if (pairing.RevokedAtUtc.HasValue)
            return Denied("dad-alliance-discord-pairing-revoked");
        if (!pairing.OperatorFingerprintConfirmedAtUtc.HasValue ||
            pairing.Role != DadAutoPartyRole.Coordinator ||
            pairing.ApplicationId != envelope.ApplicationId ||
            pairing.BotUserId != envelope.BotUserId ||
            pairing.KeyGeneration != envelope.KeyGeneration ||
            !string.Equals(pairing.IslandId, envelope.CoordinatorIdentity, StringComparison.Ordinal) ||
            !string.Equals(pairing.PublicKeyFingerprint, envelope.EndpointFingerprint, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(pairing.SigningKeyFingerprint) ||
            !string.Equals(
                pairing.SigningKeyFingerprint,
                DadAutoPartyDiscordPairingRules.ComputeSigningKeyFingerprint(pairing.SigningPublicKey),
                StringComparison.Ordinal))
        {
            return Denied("dad-alliance-discord-paired-identity-changed");
        }

        DateTime observedAt;
        try
        {
            observedAt = DateTimeOffset.FromUnixTimeMilliseconds(envelope.TimestampUnixMs).UtcDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return Denied("dad-alliance-discord-timestamp-invalid");
        }
        if (observedAt > context.UtcNow + TimeSpan.FromSeconds(30) ||
            context.UtcNow - observedAt > MaximumAge)
        {
            return Denied("dad-alliance-discord-envelope-stale");
        }

        byte[]? publicKey = null;
        byte[]? signature = null;
        var payload = BuildSigningPayload(envelope);
        try
        {
            publicKey = Convert.FromBase64String(pairing.SigningPublicKey);
            signature = Convert.FromBase64String(envelope.Signature);
            if (signature.Length != 64)
                return Denied("dad-alliance-discord-signature-malformed");
            if (!DadAutoPartySigningService.Verify(publicKey, payload, signature))
                return Denied("dad-alliance-discord-signature-invalid");
        }
        catch (FormatException)
        {
            return Denied("dad-alliance-discord-signature-malformed");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            if (publicKey != null)
                CryptographicOperations.ZeroMemory(publicKey);
            if (signature != null)
                CryptographicOperations.ZeroMemory(signature);
        }

        lock (gate)
        {
            foreach (var nonce in observedNonces
                         .Where(pair => context.UtcNow - pair.Value > MaximumAge)
                         .Select(static pair => pair.Key)
                         .ToList())
            {
                observedNonces.Remove(nonce);
            }

            if (observedNonces.ContainsKey(envelope.Nonce))
                return Denied("dad-alliance-discord-envelope-replay");
            if (observedNonces.Count >= MaximumReplayEntries)
                observedNonces.Remove(observedNonces.MinBy(static pair => pair.Value).Key);
            observedNonces[envelope.Nonce] = observedAt;
        }

        return new DadAutoPartyPolicyDecision(
            true,
            "dad-alliance-discord-envelope-verified",
            envelope.KeyGeneration);
    }

    public static string Serialize(DadAllianceDiscordEnvelope envelope)
        => JsonSerializer.Serialize(envelope, JsonOptions);

    public static DadAllianceDiscordEnvelope? Deserialize(string content)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length > MaximumEnvelopeCharacters)
            return null;
        try
        {
            return JsonSerializer.Deserialize<DadAllianceDiscordEnvelope>(content, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static byte[] BuildSigningPayload(DadAllianceDiscordEnvelope envelope)
        => Encoding.UTF8.GetBytes(string.Join('\n',
            Schema,
            envelope.TimestampUnixMs.ToString(CultureInfo.InvariantCulture),
            envelope.Nonce,
            envelope.KeyGeneration.ToString(CultureInfo.InvariantCulture),
            envelope.ApplicationId.ToString(CultureInfo.InvariantCulture),
            envelope.BotUserId.ToString(CultureInfo.InvariantCulture),
            ((int)envelope.Role).ToString(CultureInfo.InvariantCulture),
            envelope.CoordinatorIdentity,
            envelope.CoordinatorWorkerSessionId.Value,
            envelope.EndpointFingerprint,
            envelope.TargetApplicationId.ToString(CultureInfo.InvariantCulture),
            envelope.TargetWorkerSessionId.Value,
            envelope.RecruitmentId,
            envelope.TargetCharacterKey.Value,
            envelope.TargetCharacterName,
            envelope.TargetCharacterWorld,
            envelope.TargetContentId.ToString(CultureInfo.InvariantCulture),
            envelope.LeaderName,
            envelope.LeaderWorld,
            envelope.Passcode.ToString(CultureInfo.InvariantCulture),
            ((int)envelope.AssignedAlliance).ToString(CultureInfo.InvariantCulture),
            envelope.Attempt.ToString(CultureInfo.InvariantCulture),
            ((int)envelope.State).ToString(CultureInfo.InvariantCulture),
            envelope.StopGeneration.ToString(CultureInfo.InvariantCulture)));

    private static DadAutoPartyPolicyDecision Denied(string safeCode)
        => new(false, safeCode, 1);
}
