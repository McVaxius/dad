using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using dad.Models;

namespace dad.Services;

public sealed class DadAutoPartyPairingProtocol
{
    public const string Schema = "dad.pairing/v1";
    public const int MaximumEnvelopeCharacters = 1900;
    public static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(3);
    private const int MaximumReplayEntries = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object gate = new();
    private readonly Dictionary<string, DateTime> observedNonces = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, string> identityByApplication = [];

    public async ValueTask<DadAutoPartyPairingEnvelope> CreateAsync(
        DadAutoPartyPairingMessageKind kind,
        DadAutoPartyRole role,
        DadAutoPartyConfiguration configuration,
        DadAutoPartySigningService signing,
        ulong targetApplicationId = 0,
        string targetDadIdentity = "",
        CancellationToken cancellationToken = default)
    {
        if (configuration.DiscordApplicationId == 0 || configuration.DiscordBotUserId == 0 ||
            string.IsNullOrWhiteSpace(configuration.RegisteredIslandId) ||
            string.IsNullOrWhiteSpace(configuration.RegistrationFingerprint) ||
            string.IsNullOrWhiteSpace(configuration.SigningPublicKey))
            throw new InvalidOperationException("The Discord/DAD identity binding is incomplete.");
        var envelope = new DadAutoPartyPairingEnvelope
        {
            Kind = kind,
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Nonce = Guid.NewGuid().ToString("N"),
            KeyGeneration = Math.Max(1, configuration.DiscordBinding.KeyGeneration),
            ApplicationId = configuration.DiscordApplicationId,
            BotUserId = configuration.DiscordBotUserId,
            Role = role,
            DadIdentity = configuration.RegisteredIslandId,
            EndpointFingerprint = configuration.RegistrationFingerprint,
            SigningPublicKey = configuration.SigningPublicKey,
            TargetApplicationId = targetApplicationId,
            TargetDadIdentity = DadAutoPartyConfiguration.NormalizeIdentifier(targetDadIdentity),
        };
        var payload = BuildSigningPayload(envelope);
        try
        {
            envelope.Signature = Convert.ToBase64String(
                await signing.SignAsync(payload, cancellationToken).ConfigureAwait(false));
            if (Serialize(envelope).Length > MaximumEnvelopeCharacters)
                throw new InvalidOperationException("The pairing envelope exceeds the Discord message bound.");
            return envelope;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    public DadAutoPartyPolicyDecision Validate(
        DadAutoPartyPairingEnvelope? envelope,
        ulong messageAuthorId,
        DateTime utcNow,
        DadAutoPartyRole localRole)
    {
        if (envelope == null || !string.Equals(envelope.Schema, Schema, StringComparison.Ordinal) ||
            envelope.ApplicationId == 0 || envelope.BotUserId == 0 || envelope.BotUserId != messageAuthorId ||
            envelope.KeyGeneration < 1 || !Guid.TryParseExact(envelope.Nonce, "N", out _) ||
            string.IsNullOrWhiteSpace(DadAutoPartyConfiguration.NormalizeIdentifier(envelope.DadIdentity)) ||
            string.IsNullOrWhiteSpace(DadAutoPartyConfiguration.NormalizeFingerprint(envelope.EndpointFingerprint)) ||
            string.IsNullOrWhiteSpace(DadAutoPartyConfiguration.NormalizePublicKey(envelope.SigningPublicKey)))
            return Denied("dad-discord-envelope-invalid");

        DateTime observedAt;
        try
        {
            observedAt = DateTimeOffset.FromUnixTimeMilliseconds(envelope.TimestampUnixMs).UtcDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return Denied("dad-discord-envelope-timestamp-invalid");
        }
        if (observedAt > utcNow + TimeSpan.FromSeconds(30) || utcNow - observedAt > MaximumAge)
            return Denied("dad-discord-envelope-stale");

        if (localRole == DadAutoPartyRole.Client &&
            envelope.Kind is DadAutoPartyPairingMessageKind.PairRequest or DadAutoPartyPairingMessageKind.PairAccept &&
            envelope.Role != DadAutoPartyRole.Coordinator)
            return Denied("dad-discord-coordinator-star-required");
        if (localRole == DadAutoPartyRole.Coordinator && envelope.Kind == DadAutoPartyPairingMessageKind.PairRequest &&
            envelope.Role != DadAutoPartyRole.Client)
            return Denied("dad-discord-client-request-required");

        byte[]? publicKey = null;
        byte[]? signature = null;
        var payload = BuildSigningPayload(envelope);
        try
        {
            publicKey = Convert.FromBase64String(envelope.SigningPublicKey);
            signature = Convert.FromBase64String(envelope.Signature);
            if (!DadAutoPartySigningService.Verify(publicKey, payload, signature))
                return Denied("dad-discord-envelope-signature-invalid");
        }
        catch (FormatException)
        {
            return Denied("dad-discord-envelope-signature-invalid");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            if (publicKey != null) CryptographicOperations.ZeroMemory(publicKey);
            if (signature != null) CryptographicOperations.ZeroMemory(signature);
        }

        lock (gate)
        {
            foreach (var nonce in observedNonces.Where(pair => utcNow - pair.Value > MaximumAge).Select(static p => p.Key).ToList())
                observedNonces.Remove(nonce);
            if (observedNonces.ContainsKey(envelope.Nonce))
                return Denied("dad-discord-envelope-replay");
            if (observedNonces.Count >= MaximumReplayEntries)
                observedNonces.Remove(observedNonces.MinBy(static pair => pair.Value).Key);
            observedNonces[envelope.Nonce] = observedAt;

            if (identityByApplication.TryGetValue(envelope.ApplicationId, out var priorIdentity) &&
                !string.Equals(priorIdentity, envelope.DadIdentity, StringComparison.Ordinal))
                return Denied("dad-discord-application-identity-conflict");
            identityByApplication[envelope.ApplicationId] = envelope.DadIdentity;
        }
        return new DadAutoPartyPolicyDecision(true, "dad-discord-envelope-verified", envelope.KeyGeneration);
    }

    public static string Serialize(DadAutoPartyPairingEnvelope envelope)
        => JsonSerializer.Serialize(envelope, JsonOptions);

    public static DadAutoPartyPairingEnvelope? Deserialize(string content)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length > MaximumEnvelopeCharacters)
            return null;
        try { return JsonSerializer.Deserialize<DadAutoPartyPairingEnvelope>(content, JsonOptions); }
        catch (JsonException) { return null; }
    }

    public static byte[] BuildSigningPayload(DadAutoPartyPairingEnvelope envelope)
        => Encoding.UTF8.GetBytes(string.Join('\n',
            Schema,
            ((int)envelope.Kind).ToString(CultureInfo.InvariantCulture),
            envelope.TimestampUnixMs.ToString(CultureInfo.InvariantCulture),
            envelope.Nonce,
            envelope.KeyGeneration.ToString(CultureInfo.InvariantCulture),
            envelope.ApplicationId.ToString(CultureInfo.InvariantCulture),
            envelope.BotUserId.ToString(CultureInfo.InvariantCulture),
            ((int)envelope.Role).ToString(CultureInfo.InvariantCulture),
            envelope.DadIdentity,
            envelope.EndpointFingerprint,
            envelope.SigningPublicKey,
            envelope.TargetApplicationId.ToString(CultureInfo.InvariantCulture),
            envelope.TargetDadIdentity));

    private static DadAutoPartyPolicyDecision Denied(string safeCode) => new(false, safeCode, 1);
}
