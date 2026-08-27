using AutoParty.Contracts;
using dad.Models;

namespace dad.Services;

internal enum DadAutoPartyProgressState
{
    Pending,
    Complete,
    NotRequired,
    Blocked,
}

internal sealed record DadAutoPartyRegistrationProgress(
    bool EndpointIdentityReady,
    bool ChallengeGenerated,
    bool BootstrapImportedAndProtected,
    DadAutoPartyProgressState ActivationReceipt,
    bool RegistrationActive,
    bool MailboxReady,
    string NextAction,
    string SafeCode);

internal sealed record DadAutoPartyMailboxActivityProgress(
    bool Idle,
    string FriendlyPayloadName,
    string RawPayloadType,
    int AcceptedFragmentCount,
    int CurrentFragmentNumber,
    int TotalFragmentCount,
    bool AwaitingCentralAcknowledgement,
    int RelayPendingCount,
    int RelayAwaitingCount,
    string RawSafeCode);

internal sealed record DadAutoPartyPairingProgress(
    bool RegistrationActive,
    bool MailboxReady,
    bool LocalInviteCurrent,
    bool PeerInviteValid,
    bool IntentSubmitted,
    bool AttemptExpired,
    string SafeCode,
    string NextAction);

internal static class DadAutoPartyProgressProjection
{
    internal static DadAutoPartyRegistrationProgress Registration(
        DadAutoPartyConfiguration configuration,
        DadAutoPartyEndpointSnapshot endpoint,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(endpoint);

        var identityLost = configuration.RegistrationRecoveryState ==
            DadAutoPartyRegistrationRecoveryState.IdentityLost;
        var identityReady = !identityLost && HasCompleteIdentity(configuration);
        var challengeGenerated = identityReady && Guid.TryParse(configuration.RegistrationId, out _);
        var bootstrapImported = identityReady && configuration.HasImportedBootstrap;
        var registrationActive = configuration.IsRegistrationActive;
        var mailboxReady = endpoint.State == DadAutoPartyEndpointConnectionState.Ready;
        var activeRecovery = registrationActive &&
            string.Equals(endpoint.SafeCode, "dad-webhook-refreshing", StringComparison.Ordinal);
        var activationReceipt = activeRecovery
            ? DadAutoPartyProgressState.NotRequired
            : registrationActive
                ? DadAutoPartyProgressState.Complete
                : identityLost
                    ? DadAutoPartyProgressState.Blocked
                    : DadAutoPartyProgressState.Pending;
        var bootstrapExpired = configuration.RegistrationState == DadAutoPartyRegistrationState.BootstrapImported &&
            configuration.BootstrapExpiresAtUtc != default &&
            configuration.BootstrapExpiresAtUtc <= utcNow;

        string nextAction;
        if (identityLost)
            nextAction = "Owner deregistration is required before this DAD can forget the lost identity.";
        else if (!configuration.Enabled)
            nextAction = "Enable AutoParty to continue registration or connect the current mailbox.";
        else if (!identityReady)
            nextAction = "Enter an endpoint alias and generate the registration challenge.";
        else if (!challengeGenerated)
            nextAction = "Generate the registration challenge.";
        else if (bootstrapExpired ||
                 configuration.RegistrationRecoveryState == DadAutoPartyRegistrationRecoveryState.RecoveryAvailable)
            nextAction = "Recover this registration, then import the replacement bootstrap DM.";
        else if (!bootstrapImported)
            nextAction = "Copy or regenerate the challenge, submit it with /autoparty register, then import the bootstrap DM.";
        else if (!registrationActive)
            nextAction = "Wait for the activation receipt; pairing remains locked.";
        else if (activeRecovery)
            nextAction = "Wait for the replacement mailbox to become Ready.";
        else if (!mailboxReady)
            nextAction = $"Current mailbox blocker: {endpoint.SafeCode}.";
        else
            nextAction = "Registration is Active and the current mailbox is Ready.";

        return new(
            identityReady,
            challengeGenerated,
            bootstrapImported,
            activationReceipt,
            registrationActive,
            mailboxReady,
            nextAction,
            endpoint.SafeCode);
    }

    internal static DadAutoPartyMailboxActivityProgress MailboxActivity(
        DadAutoPartyEndpointSnapshot endpoint,
        DadAutoPartyRelayStatus relay,
        DadAutoPartyAdapterTransferSnapshot transfer)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(relay);
        ArgumentNullException.ThrowIfNull(transfer);
        return new(
            transfer.IsIdle,
            transfer.IsIdle ? "No active mailbox transfer" : FriendlyPayloadName(transfer.PayloadType),
            transfer.PayloadType,
            transfer.AcceptedFragmentCount,
            transfer.CurrentFragmentNumber,
            transfer.TotalFragmentCount,
            transfer.AwaitingCentralAcknowledgement,
            relay.PendingOutboundCount,
            relay.AwaitingRelayReceiptCount,
            endpoint.SafeCode);
    }

    internal static DadAutoPartyPairingProgress Pairing(
        DadAutoPartyConfiguration configuration,
        DadAutoPartyEndpointSnapshot endpoint,
        bool peerInviteValid,
        string? operationSafeCode,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(endpoint);
        var attemptPresent = Guid.TryParse(configuration.PairingAttemptId, out _) &&
            !string.IsNullOrWhiteSpace(configuration.PairingInviteToken);
        var expired = attemptPresent && configuration.PairingAttemptExpiresAtUtc <= utcNow;
        var localInviteCurrent = attemptPresent && !expired;
        var pairingOperationCode = DadAutoPartyConfiguration.NormalizeSafeCode(operationSafeCode);
        if (!pairingOperationCode.StartsWith("dad-pairing-", StringComparison.Ordinal))
            pairingOperationCode = string.Empty;

        string safeCode;
        if (expired)
            safeCode = "dad-pairing-expired";
        else if (!configuration.IsRegistrationActive)
            safeCode = "dad-pairing-registration-not-active";
        else if (string.Equals(endpoint.SafeCode, "dad-webhook-refreshing", StringComparison.Ordinal))
            safeCode = "dad-pairing-mailbox-refreshing";
        else if (endpoint.State != DadAutoPartyEndpointConnectionState.Ready)
            safeCode = "dad-pairing-mailbox-not-ready";
        else if (configuration.PairingAttemptSubmitted)
            safeCode = "dad-pairing-intent-submitted";
        else if (!string.IsNullOrWhiteSpace(pairingOperationCode))
            safeCode = pairingOperationCode;
        else if (localInviteCurrent)
            safeCode = "dad-pairing-invite-current";
        else
            safeCode = "dad-pairing-idle";

        string nextAction;
        if (!configuration.IsRegistrationActive)
            nextAction = "Complete registration activation before pairing.";
        else if (endpoint.State != DadAutoPartyEndpointConnectionState.Ready)
            nextAction = "Wait for the current mailbox to become Ready.";
        else if (expired || !localInviteCurrent)
            nextAction = "Generate a current pairing fingerprint.";
        else if (configuration.PairingAttemptSubmitted)
            nextAction = "Wait for the peer owner to submit the reciprocal fingerprint and sharing choice.";
        else if (!peerInviteValid)
            nextAction = "Paste the peer DAD's current APP1 pairing fingerprint.";
        else
            nextAction = "Choose what this DAD shares, then submit pairing.";

        return new(
            configuration.IsRegistrationActive,
            endpoint.State == DadAutoPartyEndpointConnectionState.Ready,
            localInviteCurrent,
            peerInviteValid,
            configuration.PairingAttemptSubmitted,
            expired,
            safeCode,
            nextAction);
    }

    internal static string FriendlyPayloadName(string payloadType)
    {
        if (string.Equals(payloadType, ProtocolContractRegistry.GetTypeId<RegistrationHello>(), StringComparison.Ordinal))
            return "Registration hello";
        if (string.Equals(payloadType, ProtocolContractRegistry.GetTypeId<PrivateListingUpdate>(), StringComparison.Ordinal))
            return "Listing update";
        if (string.Equals(payloadType, ProtocolContractRegistry.GetTypeId<PairingIntent>(), StringComparison.Ordinal))
            return "Pairing intent";
        if (string.Equals(payloadType, ProtocolContractRegistry.GetTypeId<PairingAttemptCancellation>(), StringComparison.Ordinal))
            return "Pairing cancellation";
        return "Mailbox message";
    }

    private static bool HasCompleteIdentity(DadAutoPartyConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration.EndpointIdentityReference) &&
        !string.IsNullOrWhiteSpace(configuration.RegisteredOwnerId) &&
        !string.IsNullOrWhiteSpace(configuration.RegisteredIslandId) &&
        !string.IsNullOrWhiteSpace(configuration.RegistrationFingerprint) &&
        !string.IsNullOrWhiteSpace(configuration.EndpointAlias) &&
        !string.IsNullOrWhiteSpace(configuration.SigningPublicKey) &&
        !string.IsNullOrWhiteSpace(configuration.EncryptionPublicKey) &&
        configuration.EndpointKeyGeneration >= 1;
}
