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
    string PairingId,
    DadAutoPartyProgressState PeerIdValidated,
    DadAutoPartyProgressState NoticeQueued,
    bool NoticeAcceptedAndPendingPairingReceived,
    bool LocalApprovalSaved,
    bool LocalApprovalAcceptedByCentral,
    bool PeerApprovalReceived,
    bool PairingActive,
    bool ExpiredOrRejected,
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
        DadAutoPartyPairingChallenge? challenge,
        DadAutoPartyPairingAttemptResult? attempt,
        string? requestedPeerIslandId,
        string? operationSafeCode,
        DateTime utcNow,
        string? challengedPeerIslandId = null,
        DadAutoPartyAdapterTransferSnapshot? transfer = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(endpoint);

        var normalizedPeer = DadAutoPartyConfiguration.NormalizeIdentifier(requestedPeerIslandId);
        var requestedPeerValid = IsGeneratedIslandId(normalizedPeer) &&
            !string.Equals(normalizedPeer, configuration.RegisteredIslandId, StringComparison.Ordinal);
        var normalizedChallengedPeer = DadAutoPartyConfiguration.NormalizeIdentifier(challengedPeerIslandId);
        var challengedPeerValid = IsGeneratedIslandId(normalizedChallengedPeer) &&
            !string.Equals(normalizedChallengedPeer, configuration.RegisteredIslandId, StringComparison.Ordinal);
        var normalizedAttemptPeer = DadAutoPartyConfiguration.NormalizeIdentifier(attempt?.PeerIslandId);
        var attemptPeerValid = IsGeneratedIslandId(normalizedAttemptPeer) &&
            !string.Equals(normalizedAttemptPeer, configuration.RegisteredIslandId, StringComparison.Ordinal);
        var targetPeers = new[]
            {
                requestedPeerValid ? normalizedPeer : string.Empty,
                challengedPeerValid ? normalizedChallengedPeer : string.Empty,
                attemptPeerValid ? normalizedAttemptPeer : string.Empty,
            }
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var hasProcessLocalAttempt = challenge != null || attempt != null;
        var pendingPairings = configuration.PendingPairings
            .Where(item => !hasProcessLocalAttempt || item.ExpiresAtUtc > utcNow);
        var pairing = pendingPairings
            .Where(item => targetPeers.Length == 0 || targetPeers.Contains(item.IslandId, StringComparer.Ordinal))
            .OrderBy(static item => item.ExpiresAtUtc)
            .FirstOrDefault() ??
            pendingPairings.OrderBy(static item => item.ExpiresAtUtc).FirstOrDefault();
        if (pairing == null)
        {
            pairing = configuration.Pairings
                .Where(item => item.IsActive &&
                               (targetPeers.Length == 0 || targetPeers.Contains(item.IslandId, StringComparer.Ordinal)))
                .OrderByDescending(static item => item.ConfirmedAtUtc)
                .FirstOrDefault();
        }

        var selectedPairingId = pairing?.PairingId ??
            challenge?.ChallengeId.ToString("D") ??
            attempt?.PairingId.ToString("D") ??
            string.Empty;
        var selectedChallenge = challenge != null &&
            string.Equals(challenge.ChallengeId.ToString("D"), selectedPairingId, StringComparison.Ordinal)
                ? challenge
                : null;
        var selectedAttempt = attempt != null &&
            string.Equals(attempt.PairingId.ToString("D"), selectedPairingId, StringComparison.Ordinal)
                ? attempt
                : null;
        var inPendingPairings = pairing != null && configuration.PendingPairings.Any(item =>
            string.Equals(item.PairingId, selectedPairingId, StringComparison.Ordinal));
        var inActivePairings = pairing != null && configuration.Pairings.Any(item =>
            string.Equals(item.PairingId, selectedPairingId, StringComparison.Ordinal));
        var initiatorSide = selectedChallenge != null || selectedAttempt != null;
        var peerIdValidated = pairing != null || requestedPeerValid || challengedPeerValid || attemptPeerValid
            ? DadAutoPartyProgressState.Complete
            : string.IsNullOrWhiteSpace(normalizedPeer)
                ? DadAutoPartyProgressState.Pending
                : DadAutoPartyProgressState.Blocked;
        var noticeQueued = initiatorSide
            ? DadAutoPartyProgressState.Complete
            : pairing != null
                ? DadAutoPartyProgressState.NotRequired
                : DadAutoPartyProgressState.Pending;
        var localApprovalSaved = pairing?.LocalApproved == true &&
            Guid.TryParse(pairing.LocalApprovalRelayMessageId, out _) &&
            pairing.LocalSharePolicy is { IsValid: true };
        var pairingActive = inActivePairings && pairing?.IsActive == true;
        var expired = !pairingActive &&
            (inPendingPairings && pairing!.ExpiresAtUtc <= utcNow ||
             selectedChallenge != null && selectedChallenge.ExpiresAtUtc <= utcNow);
        var rejected = selectedAttempt != null &&
            !string.Equals(selectedAttempt.SafeCode, "dad-pairing-notice-queued", StringComparison.Ordinal);
        var pairingOperationCode = DadAutoPartyConfiguration.NormalizeSafeCode(operationSafeCode);
        if (!pairingOperationCode.StartsWith("dad-pairing-", StringComparison.Ordinal))
            pairingOperationCode = string.Empty;
        var activeTransfer = transfer ?? DadAutoPartyAdapterTransferSnapshot.Idle;
        var queuedBehindActivePayload = pairing == null && selectedChallenge != null && !activeTransfer.IsIdle &&
            !string.Equals(
                activeTransfer.PayloadType,
                ProtocolContractRegistry.GetTypeId<PairingNotice>(),
                StringComparison.Ordinal);

        string safeCode;
        if (pairingActive)
            safeCode = "dad-pairing-active";
        else if (rejected)
            safeCode = selectedAttempt!.SafeCode;
        else if (expired)
            safeCode = "dad-pairing-expired";
        else if (!configuration.IsRegistrationActive)
            safeCode = "dad-pairing-registration-not-active";
        else if (string.Equals(endpoint.SafeCode, "dad-webhook-refreshing", StringComparison.Ordinal))
            safeCode = "dad-pairing-mailbox-refreshing";
        else if (endpoint.State != DadAutoPartyEndpointConnectionState.Ready)
            safeCode = "dad-pairing-mailbox-not-ready";
        else if (pairing?.LocalApprovalRelayAcceptedAtUtc != null)
            safeCode = "dad-pairing-approval-relayed";
        else if (localApprovalSaved)
            safeCode = "dad-pairing-local-approved";
        else if (pairing != null)
            safeCode = "dad-pairing-notice-pending";
        else if (selectedChallenge != null)
            safeCode = "dad-pairing-notice-queued";
        else if (!string.IsNullOrWhiteSpace(pairingOperationCode))
            safeCode = pairingOperationCode;
        else
            safeCode = "dad-pairing-idle";

        string nextAction;
        if (rejected || expired)
            nextAction = "This attempt ended; start an explicit new pairing attempt.";
        else if (!configuration.IsRegistrationActive)
            nextAction = "Complete registration activation before pairing.";
        else if (endpoint.State != DadAutoPartyEndpointConnectionState.Ready)
            nextAction = "Wait for the current mailbox to become Ready.";
        else if (pairingActive)
            nextAction = "Pairing is Active.";
        else if (peerIdValidated == DadAutoPartyProgressState.Blocked)
            nextAction = "Enter an island ID in the form island- plus 32 lowercase hexadecimal characters.";
        else if (pairing == null && selectedChallenge == null)
            nextAction = "Enter a peer island ID and initiate pairing.";
        else if (queuedBehindActivePayload)
            nextAction =
                $"Pairing notice is queued behind {FriendlyPayloadName(activeTransfer.PayloadType).ToLowerInvariant()}: " +
                $"accepted fragments {activeTransfer.AcceptedFragmentCount} / {activeTransfer.TotalFragmentCount}; " +
                $"current fragment {activeTransfer.CurrentFragmentNumber} / {activeTransfer.TotalFragmentCount}, " +
                (activeTransfer.AwaitingCentralAcknowledgement
                    ? "waiting for central acknowledgement."
                    : "ready to publish.");
        else if (pairing == null)
            nextAction = "Wait for central acceptance and the pending pairing notice.";
        else if (!localApprovalSaved)
            nextAction = "Verify the fingerprint and code, choose a share scope, and approve locally.";
        else if (pairing.LocalApprovalRelayAcceptedAtUtc == null)
            nextAction = "Wait for central to accept the local approval.";
        else if (!pairing.PeerApproved)
            nextAction = "Wait for the peer owner's approval.";
        else
            nextAction = "Wait for the pairing to become Active.";

        return new(
            configuration.IsRegistrationActive,
            endpoint.State == DadAutoPartyEndpointConnectionState.Ready,
            selectedPairingId,
            peerIdValidated,
            noticeQueued,
            pairing != null,
            localApprovalSaved,
            pairing?.LocalApprovalRelayAcceptedAtUtc != null,
            pairing?.PeerApproved == true,
            pairingActive,
            expired || rejected,
            safeCode,
            nextAction);
    }

    internal static string FriendlyPayloadName(string payloadType)
    {
        if (string.Equals(payloadType, ProtocolContractRegistry.GetTypeId<RegistrationHello>(), StringComparison.Ordinal))
            return "Registration hello";
        if (string.Equals(payloadType, ProtocolContractRegistry.GetTypeId<PrivateListingUpdate>(), StringComparison.Ordinal))
            return "Listing update";
        if (string.Equals(payloadType, ProtocolContractRegistry.GetTypeId<PairingNotice>(), StringComparison.Ordinal))
            return "Pairing notice";
        if (string.Equals(payloadType, ProtocolContractRegistry.GetTypeId<PairingApproval>(), StringComparison.Ordinal))
            return "Pairing approval";
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

    private static bool IsGeneratedIslandId(string value) =>
        value.Length == 39 &&
        value.StartsWith("island-", StringComparison.Ordinal) &&
        value[7..].All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
