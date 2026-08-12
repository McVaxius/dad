using System.Security.Cryptography;
using System.Text;
using AutoParty.Contracts;
using dad.Models;

namespace dad.Services;

public sealed class DadAutoPartyService : IDisposable
{
    private readonly DadAutoPartyConfiguration configuration;
    private readonly IDadAutoPartyEndpointIdentityStore identityStore;
    private readonly IDadAutoPartyWebhookCredentialStore? credentialStore;
    private readonly Action saveConfiguration;
    private Action<string>? ownerStop;
    private DateTime nextMaintenanceUtc = DateTime.MinValue;
    private bool disposed;

    public DadAutoPartyService(
        DadAutoPartyConfiguration configuration,
        IDadAutoPartyEndpointIdentityStore identityStore,
        Func<bool> dadEnabled,
        Action saveConfiguration,
        Func<bool>? localSafetyAllowsExecution = null,
        IDadAutoPartyWebhookCredentialStore? credentialStore = null)
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.identityStore = identityStore ?? throw new ArgumentNullException(nameof(identityStore));
        this.credentialStore = credentialStore;
        this.saveConfiguration = saveConfiguration ?? throw new ArgumentNullException(nameof(saveConfiguration));
        Connector = new DadDiscordCourierConnector(configuration, dadEnabled);
        Policy = new DadAutoPartyPolicyFacade(configuration, dadEnabled, localSafetyAllowsExecution, saveConfiguration);
        Execution = DadAutoPartyRuntimeExecutionFacade.CreateUnavailable(Policy);
        IdentityPackages = new DadAutoPartyIdentityPackageService(configuration, identityStore, saveConfiguration);
    }

    public DadDiscordCourierConnector Connector { get; }
    public DadAutoPartyPolicyFacade Policy { get; }
    public IAutoPartyExecutionFacade Execution { get; private set; }
    public DadAutoPartyIdentityPackageService IdentityPackages { get; }

    public void AttachVerifiedCourier(IAutoPartyTransportAdapter adapter)
    {
        ThrowIfDisposed();
        Connector.AttachVerifiedAdapter(adapter);
    }

    public void ConfigureExecutionFacade(IAutoPartyExecutionFacade facade)
    {
        ThrowIfDisposed();
        Execution = facade ?? throw new ArgumentNullException(nameof(facade));
    }

    public void ConfigureOwnerStop(Action<string> stop)
    {
        ThrowIfDisposed();
        ownerStop = stop ?? throw new ArgumentNullException(nameof(stop));
    }

    public void SetEnabled(bool enabled)
    {
        ThrowIfDisposed();
        if (configuration.Enabled == enabled)
            return;
        configuration.Enabled = enabled;
        if (enabled)
            Policy.ClearStopAfterExplicitEnable();
        else
            StopAll("dad-autoparty-disabled-by-owner");
        configuration.StateGeneration++;
        saveConfiguration();
    }

    public DadAutoPartyPolicyDecision ReceivePairingNotice(
        Guid pairingId,
        string peerOwnerId,
        string peerIslandId,
        string peerHomeGuildScope,
        EndpointPublicKeys peerPublicKeys,
        string peerFingerprint,
        string transcriptHash,
        string confirmationCodeHash,
        DateTime expiresAtUtc)
    {
        ThrowIfDisposed();
        var ownerId = DadAutoPartyConfiguration.NormalizeIdentifier(peerOwnerId);
        var islandId = DadAutoPartyConfiguration.NormalizeIdentifier(peerIslandId);
        var guildScope = DadAutoPartyConfiguration.NormalizeIdentifier(peerHomeGuildScope);
        var fingerprint = DadAutoPartyConfiguration.NormalizeFingerprint(peerFingerprint);
        var transcript = DadAutoPartyConfiguration.NormalizeFingerprint(transcriptHash);
        var codeHash = DadAutoPartyConfiguration.NormalizeFingerprint(confirmationCodeHash);
        var hasPeerKeys = peerPublicKeys != null &&
            peerPublicKeys.KeyVersion >= 1 &&
            !string.IsNullOrWhiteSpace(DadAutoPartyConfiguration.NormalizeIdentifier(peerPublicKeys.SigningKeyId)) &&
            !peerPublicKeys.Ed25519PublicKey.IsDefault &&
            peerPublicKeys.Ed25519PublicKey.Length == AutoPartyProtocol.Ed25519PublicKeyBytes &&
            !string.IsNullOrWhiteSpace(DadAutoPartyConfiguration.NormalizeIdentifier(peerPublicKeys.AgreementKeyId)) &&
            !peerPublicKeys.X25519PublicKey.IsDefault &&
            peerPublicKeys.X25519PublicKey.Length == AutoPartyProtocol.X25519KeyBytes;
        var signingPublicKey = hasPeerKeys
            ? DadAutoPartyConfiguration.NormalizePublicKey(
                Convert.ToBase64String(peerPublicKeys!.Ed25519PublicKey.AsSpan()))
            : string.Empty;
        var agreementPublicKey = hasPeerKeys
            ? DadAutoPartyConfiguration.NormalizePublicKey(
                Convert.ToBase64String(peerPublicKeys!.X25519PublicKey.AsSpan()))
            : string.Empty;
        var expectedFingerprint = hasPeerKeys
            ? DadAutoPartyIdentityPackageService.BuildFingerprint(
                ownerId,
                islandId,
                peerPublicKeys!.KeyVersion,
                peerPublicKeys.Ed25519PublicKey.ToArray(),
                peerPublicKeys.X25519PublicKey.ToArray())
            : string.Empty;
        if (!configuration.IsRegistrationActive || pairingId == Guid.Empty ||
            string.IsNullOrWhiteSpace(ownerId) || string.IsNullOrWhiteSpace(islandId) ||
            string.Equals(islandId, configuration.RegisteredIslandId, StringComparison.Ordinal) ||
            !hasPeerKeys || string.IsNullOrWhiteSpace(signingPublicKey) ||
            string.IsNullOrWhiteSpace(agreementPublicKey) || !FixedEquals(fingerprint, expectedFingerprint) ||
            string.IsNullOrWhiteSpace(fingerprint) || string.IsNullOrWhiteSpace(transcript) ||
            string.IsNullOrWhiteSpace(codeHash) || expiresAtUtc <= DateTime.UtcNow ||
            expiresAtUtc > DateTime.UtcNow + TimeSpan.FromMinutes(15) ||
            configuration.Deauthentications.Any(item =>
                string.Equals(item.PeerIslandId, islandId, StringComparison.Ordinal) &&
                item.RevocationGeneration >= configuration.RevocationGeneration))
            return Decision(false, "dad-pairing-notice-invalid");

        var pairingIdText = pairingId.ToString("D");
        var existing = configuration.PendingPairings
            .Concat(configuration.Pairings)
            .FirstOrDefault(item =>
                string.Equals(item.PairingId, pairingIdText, StringComparison.Ordinal) ||
                string.Equals(item.IslandId, islandId, StringComparison.Ordinal));
        if (existing != null && (existing.LocalApproved || existing.PeerApproved || existing.IsActive))
        {
            var unchanged = string.Equals(existing.PairingId, pairingIdText, StringComparison.Ordinal) &&
                            string.Equals(existing.OwnerId, ownerId, StringComparison.Ordinal) &&
                            string.Equals(existing.IslandId, islandId, StringComparison.Ordinal) &&
                            string.Equals(existing.HomeGuildScope, guildScope, StringComparison.Ordinal) &&
                            FixedEquals(existing.PublicKeyFingerprint, fingerprint) &&
                            FixedEquals(existing.TranscriptHash, transcript) &&
                            FixedEquals(existing.ConfirmationCodeHash, codeHash) &&
                            existing.KeyGeneration == peerPublicKeys!.KeyVersion &&
                            string.Equals(existing.SigningPublicKey, signingPublicKey, StringComparison.Ordinal) &&
                            string.Equals(existing.AgreementPublicKey, agreementPublicKey, StringComparison.Ordinal);
            return Decision(unchanged, unchanged
                ? "dad-pairing-notice-idempotent"
                : "dad-pairing-notice-conflict");
        }

        configuration.PendingPairings.RemoveAll(item =>
            string.Equals(item.IslandId, islandId, StringComparison.Ordinal) ||
            string.Equals(item.PairingId, pairingIdText, StringComparison.Ordinal));
        configuration.PendingPairings.Add(new DadAutoPartyPairing
        {
            PairingId = pairingIdText,
            OwnerId = ownerId,
            IslandId = islandId,
            HomeGuildScope = guildScope,
            PublicKeyFingerprint = fingerprint,
            LocalFingerprint = configuration.RegistrationFingerprint,
            TranscriptHash = transcript,
            ConfirmationCodeHash = codeHash,
            ExpiresAtUtc = expiresAtUtc,
            KeyGeneration = peerPublicKeys!.KeyVersion,
            SigningPublicKey = signingPublicKey,
            AgreementPublicKey = agreementPublicKey,
        });
        configuration.StateGeneration++;
        saveConfiguration();
        return Decision(true, "dad-pairing-notice-pending");
    }

    public DadAutoPartyPolicyDecision ApprovePairing(
        Guid pairingId,
        string displayedPeerFingerprint,
        string confirmationCode,
        DadAutoPartySharePolicy localSharePolicy)
    {
        ThrowIfDisposed();
        var pairingIdText = pairingId.ToString("D");
        var pending = configuration.PendingPairings.FirstOrDefault(item =>
            string.Equals(item.PairingId, pairingIdText, StringComparison.Ordinal));
        var pairing = pending ?? configuration.Pairings.FirstOrDefault(item =>
            string.Equals(item.PairingId, pairingIdText, StringComparison.Ordinal));
        var policy = localSharePolicy?.Clone().Normalize();
        if (pairing == null || (pending != null && pairing.ExpiresAtUtc <= DateTime.UtcNow) ||
            !string.Equals(
                pairing.PublicKeyFingerprint,
                DadAutoPartyConfiguration.NormalizeFingerprint(displayedPeerFingerprint),
                StringComparison.Ordinal) ||
            !FixedEquals(pairing.ConfirmationCodeHash, HashConfirmationCode(confirmationCode)) ||
            policy is not { IsValid: true })
            return Decision(false, "dad-pairing-approval-mismatch");
        var messageId = DerivePairingApprovalMessageId(pairingId, configuration.RegisteredIslandId);
        if (pairing.LocalApproved)
        {
            var unchanged = SamePolicy(pairing.LocalSharePolicy, policy) &&
                            Guid.TryParse(pairing.LocalApprovalRelayMessageId, out var storedMessageId) &&
                            storedMessageId == messageId;
            return Decision(unchanged, unchanged
                ? pairing.LocalApprovalRelayAcceptedAtUtc == null
                    ? "dad-pairing-approval-idempotent"
                    : "dad-pairing-approval-relayed"
                : "dad-pairing-approval-conflict");
        }
        pairing.LocalApproved = true;
        pairing.LocalSharePolicy = policy;
        pairing.LocalApprovalRelayMessageId = messageId.ToString("D");
        pairing.LocalApprovalRelayAcceptedAtUtc = null;
        ActivateIfComplete(pairing);
        configuration.StateGeneration++;
        saveConfiguration();
        return Decision(true, "dad-pairing-local-approved");
    }

    public DadAutoPartyPolicyDecision ConfirmPeerApproval(
        Guid pairingId,
        string transcriptHash,
        string confirmationCodeHash,
        string approvingFingerprint,
        string peerObservedLocalFingerprint,
        DadAutoPartySharePolicy peerSharePolicy)
    {
        ThrowIfDisposed();
        var pairingIdText = pairingId.ToString("D");
        var pending = configuration.PendingPairings.FirstOrDefault(item =>
            string.Equals(item.PairingId, pairingIdText, StringComparison.Ordinal));
        var pairing = pending ?? configuration.Pairings.FirstOrDefault(item =>
            string.Equals(item.PairingId, pairingIdText, StringComparison.Ordinal));
        var policy = peerSharePolicy?.Clone().Normalize();
        if (pairing == null || (pending != null && pairing.ExpiresAtUtc <= DateTime.UtcNow) ||
            !FixedEquals(pairing.TranscriptHash, DadAutoPartyConfiguration.NormalizeFingerprint(transcriptHash)) ||
            !FixedEquals(pairing.ConfirmationCodeHash, DadAutoPartyConfiguration.NormalizeFingerprint(confirmationCodeHash)) ||
            !FixedEquals(pairing.PublicKeyFingerprint, DadAutoPartyConfiguration.NormalizeFingerprint(approvingFingerprint)) ||
            !FixedEquals(pairing.LocalFingerprint, DadAutoPartyConfiguration.NormalizeFingerprint(peerObservedLocalFingerprint)) ||
            policy is not { IsValid: true })
            return Decision(false, "dad-pairing-peer-approval-mismatch");
        if (pairing.PeerApproved)
            return Decision(SamePolicy(pairing.PeerSharePolicy, policy),
                SamePolicy(pairing.PeerSharePolicy, policy)
                    ? "dad-pairing-peer-approval-idempotent"
                    : "dad-pairing-peer-approval-conflict");
        pairing.PeerApproved = true;
        pairing.PeerSharePolicy = policy;
        ActivateIfComplete(pairing);
        configuration.StateGeneration++;
        saveConfiguration();
        return Decision(true, pairing.IsActive ? "dad-pairing-active" : "dad-pairing-peer-approved");
    }

    internal bool TryApplyPairingApprovalRelayReceipt(
        Guid relatedMessageId,
        bool accepted,
        out DadAutoPartyPolicyDecision decision)
    {
        ThrowIfDisposed();
        var matches = configuration.PendingPairings
            .Concat(configuration.Pairings)
            .Where(item =>
                Guid.TryParse(item.LocalApprovalRelayMessageId, out var messageId) &&
                messageId == relatedMessageId)
            .Take(2)
            .ToList();
        if (matches.Count != 1)
        {
            decision = Decision(false, "dad-pairing-approval-relay-unmatched");
            return false;
        }
        var pairing = matches[0];
        if (!accepted)
        {
            decision = Decision(true, "dad-pairing-approval-relay-rejected");
            return true;
        }
        if (pairing.LocalApprovalRelayAcceptedAtUtc != null)
        {
            decision = Decision(true, "dad-pairing-approval-relay-idempotent");
            return true;
        }
        if (!pairing.LocalApproved || pairing.ExpiresAtUtc <= DateTime.UtcNow)
        {
            decision = Decision(false, "dad-pairing-approval-relay-invalid");
            return true;
        }
        pairing.LocalApprovalRelayAcceptedAtUtc = DateTime.UtcNow;
        ActivateIfComplete(pairing);
        configuration.StateGeneration++;
        saveConfiguration();
        decision = Decision(true, pairing.IsActive
            ? "dad-pairing-active"
            : "dad-pairing-approval-relayed");
        return true;
    }

    internal static Guid DerivePairingApprovalMessageId(Guid pairingId, string localIslandId)
    {
        var bytes = Encoding.UTF8.GetBytes(
            $"dad.autoparty.pairing-approval/v1|{pairingId:N}|{DadAutoPartyConfiguration.NormalizeIdentifier(localIslandId)}");
        try
        {
            var digest = SHA256.HashData(bytes);
            try
            {
                return new Guid(digest.AsSpan(0, 16));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public DadAutoPartyPolicyDecision SetSharePolicy(
        string peerIslandId,
        DadAutoPartySharePolicy sharePolicy)
    {
        ThrowIfDisposed();
        var pairing = configuration.Pairings.FirstOrDefault(item =>
            item.IsActive && string.Equals(item.IslandId, peerIslandId?.Trim(), StringComparison.Ordinal));
        var normalized = sharePolicy?.Clone().Normalize();
        if (pairing == null || normalized is not { IsValid: true })
            return Decision(false, "dad-share-policy-invalid");
        pairing.LocalSharePolicy = normalized;
        configuration.StateGeneration++;
        saveConfiguration();
        return Decision(true, "dad-share-policy-updated");
    }

    public DadAutoPartyPolicyDecision Deauthenticate(string peerIslandId, string safeReason)
    {
        ThrowIfDisposed();
        var islandId = DadAutoPartyConfiguration.NormalizeIdentifier(peerIslandId);
        var reason = DadAutoPartyConfiguration.NormalizeSafeCode(safeReason);
        var pairing = configuration.Pairings.FirstOrDefault(item =>
            item.RevokedAtUtc == null && string.Equals(item.IslandId, islandId, StringComparison.Ordinal));
        if (pairing == null)
            return Decision(true, "dad-deauthentication-already-applied");
        if (string.IsNullOrWhiteSpace(reason))
            reason = "dad-owner-deauthenticated";
        pairing.RevokedAtUtc = DateTime.UtcNow;
        configuration.RevocationGeneration++;
        configuration.Deauthentications.RemoveAll(item =>
            string.Equals(item.PeerIslandId, islandId, StringComparison.Ordinal));
        configuration.Deauthentications.Add(new DadAutoPartyDeauthentication
        {
            DeauthenticationId = Guid.NewGuid().ToString("D"),
            PeerIslandId = islandId,
            PairingTranscriptHash = pairing.TranscriptHash,
            RevocationGeneration = configuration.RevocationGeneration,
            SafeReason = reason,
            RevokedAtUtc = DateTime.UtcNow,
        });
        configuration.PendingPairings.RemoveAll(item =>
            string.Equals(item.IslandId, islandId, StringComparison.Ordinal));
        configuration.Listings.RemoveAll(item =>
            string.Equals(item.SharingIslandId, islandId, StringComparison.Ordinal));
        configuration.RemoteBindings.RemoveAll(item =>
            string.Equals(item.IslandId, islandId, StringComparison.Ordinal));
        configuration.Grants.RemoveAll(item =>
            string.Equals(item.IslandId, islandId, StringComparison.Ordinal));
        Policy.SetOwnerVeto(new OwnerId(pairing.OwnerId), true, reason);
        configuration.StateGeneration++;
        saveConfiguration();
        return Decision(true, "dad-deauthentication-applied");
    }

    public DadAutoPartyPolicyDecision AddListing(DadAutoPartyListing listing)
    {
        ThrowIfDisposed();
        var normalized = listing?.Clone();
        if (normalized != null && string.IsNullOrWhiteSpace(normalized.SharingIslandId))
            normalized.SharingIslandId = configuration.RegisteredIslandId;
        normalized?.Normalize();
        if (!configuration.Enabled || !configuration.IsRegistrationActive ||
            normalized is not { IsValid: true } ||
            !string.Equals(normalized.SharingIslandId, configuration.RegisteredIslandId, StringComparison.Ordinal) ||
            normalized.ExpiresAtUtc <= DateTime.UtcNow ||
            normalized.ExpiresAtUtc > DateTime.UtcNow + TimeSpan.FromHours(24))
            return Decision(false, "dad-listing-invalid");
        configuration.Listings.RemoveAll(candidate =>
            string.Equals(candidate.ListingId, normalized.ListingId, StringComparison.Ordinal));
        configuration.Listings.Add(normalized);
        configuration.StateGeneration++;
        saveConfiguration();
        return Decision(true, "dad-listing-saved");
    }

    public DadAutoPartyPolicyDecision ApplyRemoteListings(
        string sharingIslandId,
        string sharingHomeGuildScope,
        DadAutoPartySharePolicy standingPolicy,
        IEnumerable<DadAutoPartyListing> listings,
        bool registeredRequesterAttested)
    {
        ThrowIfDisposed();
        var islandId = DadAutoPartyConfiguration.NormalizeIdentifier(sharingIslandId);
        var guildScope = DadAutoPartyConfiguration.NormalizeIdentifier(sharingHomeGuildScope);
        var policy = standingPolicy?.Clone().Normalize();
        var pairing = configuration.Pairings.FirstOrDefault(item =>
            item.IsActive && string.Equals(item.IslandId, islandId, StringComparison.Ordinal));
        var paired = pairing != null;
        var sameGuild = !string.IsNullOrWhiteSpace(guildScope) &&
            string.Equals(guildScope, configuration.HomeGuildScope, StringComparison.Ordinal);
        if (policy is not { Enabled: true, IsValid: true } ||
            (!paired && (policy.Mode != DadAutoPartyCharacterShareMode.PromiscuousAllSameGuild ||
                         !registeredRequesterAttested || !sameGuild)))
            return Decision(false, "dad-listing-share-denied");

        var now = DateTime.UtcNow;
        var accepted = (listings ?? [])
            .Select(static item => item?.Clone().Normalize())
            .Where(item => item is { IsValid: true, Available: true } &&
                           item.ExpiresAtUtc > now && item.ExpiresAtUtc <= now + TimeSpan.FromHours(24) &&
                           DadAutoPartyShareRules.Allows(policy, item.OpaqueCharacterId, paired, sameGuild))
            .Select(item =>
            {
                item!.SharingIslandId = islandId;
                return item;
            })
            .Take(256)
            .ToList();
        configuration.Listings.RemoveAll(item =>
            string.Equals(item.SharingIslandId, islandId, StringComparison.Ordinal));
        configuration.Listings.AddRange(accepted!);
        configuration.StateGeneration++;
        saveConfiguration();
        return Decision(true, "dad-listings-applied");
    }

    public DadAutoPartyDirectorySnapshot GetDirectorySnapshot()
    {
        ThrowIfDisposed();
        var now = DateTime.UtcNow;
        return new(
            configuration.StateGeneration,
            configuration.Listings
                .Where(item => item.Available && item.ExpiresAtUtc > now)
                .Select(static item => item.Clone())
                .OrderBy(static item => item.DisplayLabel, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static item => item.OpaqueCharacterId, StringComparer.Ordinal)
                .ToList());
    }

    public void Update(bool dadPluginEnabled)
    {
        ThrowIfDisposed();
        if (!dadPluginEnabled || !configuration.Enabled)
            return;
        var now = DateTime.UtcNow;
        if (now < nextMaintenanceUtc)
            return;
        nextMaintenanceUtc = now + TimeSpan.FromMinutes(1);
        var changed = configuration.Grants.RemoveAll(grant => grant.ExpiresAtUtc <= now) > 0;
        changed |= configuration.Listings.RemoveAll(listing => listing.ExpiresAtUtc <= now) > 0;
        changed |= configuration.PendingPairings.RemoveAll(pairing => pairing.ExpiresAtUtc <= now) > 0;
        if (changed)
        {
            configuration.StateGeneration++;
            saveConfiguration();
        }
    }

    public DadAutoPartyPolicyDecision AddImmutableGrant(CapabilityGrant grant)
    {
        ThrowIfDisposed();
        if (!configuration.Enabled || !configuration.IsRegistrationActive ||
            grant.GrantId == Guid.Empty || grant.ProposalId == Guid.Empty ||
            grant.ValidFrom >= grant.ValidUntil || grant.ValidUntil <= DateTimeOffset.UtcNow ||
            grant.ValidUntil > DateTimeOffset.UtcNow + TimeSpan.FromDays(30) ||
            grant.Scope.Permissions == SessionPermission.None || grant.Scope.MaximumUses != 1 ||
            string.IsNullOrWhiteSpace(grant.Scope.CharacterId.Value) ||
            string.IsNullOrWhiteSpace(grant.Scope.RequestedJob.Value) ||
            string.IsNullOrWhiteSpace(grant.Scope.ActivityId.Value) ||
            (!string.IsNullOrWhiteSpace(configuration.RegisteredOwnerId) &&
             !string.Equals(grant.GranteeOwnerId.Value, configuration.RegisteredOwnerId, StringComparison.Ordinal)))
            return Decision(false, "dad-grant-invalid");
        var grantId = grant.GrantId.ToString("D");
        if (configuration.Grants.Any(candidate => string.Equals(candidate.GrantId, grantId, StringComparison.Ordinal)))
            return Decision(true, "dad-grant-already-present");
        configuration.Grants.Add(new DadAutoPartyGrant
        {
            GrantId = grantId,
            ProposalId = grant.ProposalId.ToString("D"),
            OwnerId = grant.OwnerId.Value,
            IslandId = grant.Header.SenderIslandId.Value,
            OpaqueCharacterId = grant.Scope.CharacterId.Value,
            RequestedJobId = grant.Scope.RequestedJob.Value,
            ActivityId = grant.Scope.ActivityId.Value,
            Permissions = grant.Scope.Permissions,
            IssuedAtUtc = grant.ValidFrom.UtcDateTime,
            ExpiresAtUtc = grant.ValidUntil.UtcDateTime,
            MaximumUses = grant.Scope.MaximumUses,
        });
        configuration.StateGeneration++;
        saveConfiguration();
        return Decision(true, "dad-grant-saved");
    }

    public DadAutoPartyPolicyDecision AcceptProposal(
        RunProposal proposal,
        SessionPermission requiredPermissions) =>
        Policy.AcceptProposal(proposal, requiredPermissions);

    public DadAutoPartyPolicyDecision AcceptOwnedProposal(
        RunProposal proposal,
        IReadOnlyList<ParticipantRequest> ownedParticipants,
        SessionPermission requiredPermissions) =>
        Policy.AcceptOwnedProposal(proposal, ownedParticipants, requiredPermissions);

    public DadAutoPartyPolicyDecision Reserve(Reservation reservation, DadAutoPartySessionMode mode) =>
        Policy.Reserve(reservation, mode);

    public DadAutoPartyPolicyDecision VerifyPreflight(PreflightResult preflight) =>
        Policy.VerifyPreflight(preflight);

    public DadAutoPartyPolicyDecision AcquireLease(SessionLease lease) =>
        Policy.AcquireLease(lease);

    internal DadAutoPartyPolicyDecision RestoreOwnedProposalSession(
        DadAutoPartyInboundProposalState state,
        SessionPermission requiredPermissions)
    {
        if (!state.AdmissionReady || state.Preflight == null || state.Lease == null)
            return new(false, "dad-owned-session-restore-invalid", Math.Max(1, configuration.StateGeneration));
        return Policy.RestoreOwnedProposalSession(
            state.Proposal,
            state.OwnedParticipants,
            state.Reservations,
            state.Preflight,
            state.Lease,
            requiredPermissions);
    }

    public DadAutoPartyPolicyDecision Revoke(Revocation revocation) =>
        Policy.Revoke(revocation);

    public DadAutoPartyAuthorizationDecision EvaluateSchedulerAuthorization(DadRunRequest request) =>
        DadAutoPartySchedulerAuthorizationRules.Evaluate(request, Policy.GetProposalAuthorization);

    public async ValueTask<DadAutoPartyStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var transportHealth = await Connector.GetHealthAsync(cancellationToken).ConfigureAwait(false);
        var transportState = transportHealth.State switch
        {
            AutoPartyTransportHealthState.Disabled => DadAutoPartyComponentState.Disabled,
            AutoPartyTransportHealthState.NotReady => DadAutoPartyComponentState.NotReady,
            AutoPartyTransportHealthState.Ready => DadAutoPartyComponentState.Ready,
            _ => DadAutoPartyComponentState.Faulted,
        };
        var now = DateTime.UtcNow;
        var policyState = !configuration.Enabled
            ? DadAutoPartyComponentState.Disabled
            : configuration.IsRegistrationActive
                ? DadAutoPartyComponentState.Ready
                : DadAutoPartyComponentState.NotReady;
        return new(
            new(transportState, transportHealth.SafeCode, transportHealth.ObservedAt.UtcDateTime),
            new(policyState, policyState == DadAutoPartyComponentState.Ready ? "dad-policy-ready" : "dad-policy-not-ready", now),
            new(policyState, policyState == DadAutoPartyComponentState.Ready ? "dad-execution-ready" : "dad-execution-not-ready", now),
            configuration.RegisteredIslandId,
            configuration.Listings.Count,
            configuration.Grants.Count,
            Policy.ActiveSessionCount);
    }

    public async ValueTask<DadAutoPartyPrivacyResult> DeregisterAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        StopAll("dad-deregistered");
        var reference = configuration.WebhookCredentialReference;
        if (credentialStore != null && !string.IsNullOrWhiteSpace(reference))
        {
            try
            {
                await credentialStore.DeleteAsync(reference, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new(false, false, "dad-deregister-credential-cleanup-retry");
            }
        }
        ClearRegistrationAndTrust();
        configuration.Enabled = false;
        configuration.StateGeneration++;
        saveConfiguration();
        return new(true, false, "dad-deregistered");
    }

    public async ValueTask<DadAutoPartyPrivacyResult> PurgeAsync(
        bool deleteEndpointIdentity,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var deregistered = await DeregisterAsync(cancellationToken).ConfigureAwait(false);
        if (!deregistered.Purged)
            return deregistered;
        var identityDeleted = false;
        if (deleteEndpointIdentity && !string.IsNullOrWhiteSpace(configuration.EndpointIdentityReference))
            identityDeleted = await identityStore.DeleteAsync(
                configuration.EndpointIdentityReference,
                cancellationToken).ConfigureAwait(false);
        if (deleteEndpointIdentity)
        {
            configuration.EndpointIdentityReference = string.Empty;
            configuration.RegisteredOwnerId = string.Empty;
            configuration.RegisteredIslandId = string.Empty;
            configuration.RegistrationFingerprint = string.Empty;
            configuration.EndpointAlias = string.Empty;
            configuration.SigningPublicKey = string.Empty;
            configuration.EncryptionPublicKey = string.Empty;
            configuration.EndpointKeyGeneration = 1;
            configuration.StateGeneration++;
            saveConfiguration();
        }
        return new(true, identityDeleted, "dad-autoparty-purged");
    }

    public void StopAll(string safeReason)
    {
        Policy.StopAll(safeReason);
        Execution.StopAll(safeReason);
        ownerStop?.Invoke(safeReason);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        StopAll("dad-autoparty-disposed");
        Connector.DisposeAsync().AsTask().GetAwaiter().GetResult();
        disposed = true;
    }

    private void ActivateIfComplete(DadAutoPartyPairing pending)
    {
        if (!pending.LocalApproved || !pending.PeerApproved ||
            pending.LocalApprovalRelayAcceptedAtUtc == null)
            return;
        pending.ConfirmedAtUtc = DateTime.UtcNow;
        pending.RevokedAtUtc = null;
        configuration.Pairings.RemoveAll(item =>
            string.Equals(item.IslandId, pending.IslandId, StringComparison.Ordinal));
        configuration.Pairings.Add(pending.Clone());
        configuration.PendingPairings.Remove(pending);
    }

    private void ClearRegistrationAndTrust()
    {
        configuration.RegistrationState = DadAutoPartyRegistrationState.Unregistered;
        configuration.RegistrationId = string.Empty;
        configuration.RouteId = string.Empty;
        configuration.CentralBotApplicationId = string.Empty;
        configuration.HomeGuildScope = string.Empty;
        configuration.WebhookCredentialReference = string.Empty;
        configuration.UplinkEpochId = string.Empty;
        configuration.DownlinkEpochId = string.Empty;
        configuration.MailboxEpochGeneration = 0;
        configuration.RelayKeyGeneration = 1;
        configuration.RelaySigningPublicKey = string.Empty;
        configuration.RelayAgreementPublicKey = string.Empty;
        configuration.BootstrapExpiresAtUtc = default;
        configuration.Pairings.Clear();
        configuration.PendingPairings.Clear();
        configuration.Grants.Clear();
        configuration.Listings.Clear();
        configuration.RemoteBindings.Clear();
        configuration.Deauthentications.Clear();
    }

    private DadAutoPartyPolicyDecision Decision(bool allowed, string safeCode) =>
        new(allowed, safeCode, Math.Max(1, configuration.StateGeneration));

    private static string HashConfirmationCode(string? confirmationCode)
    {
        var normalized = (confirmationCode ?? string.Empty).Trim();
        if (normalized.Length is < 4 or > 32)
            return string.Empty;
        var bytes = Encoding.UTF8.GetBytes(normalized);
        try
        {
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool FixedEquals(string expected, string observed)
    {
        if (expected.Length != observed.Length || expected.Length == 0)
            return false;
        var left = Encoding.ASCII.GetBytes(expected);
        var right = Encoding.ASCII.GetBytes(observed);
        try
        {
            return CryptographicOperations.FixedTimeEquals(left, right);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(left);
            CryptographicOperations.ZeroMemory(right);
        }
    }

    internal static bool SamePolicy(DadAutoPartySharePolicy left, DadAutoPartySharePolicy right)
        => left.Enabled == right.Enabled && left.Mode == right.Mode && left.Revision == right.Revision &&
           left.CharacterHandles.SequenceEqual(right.CharacterHandles, StringComparer.Ordinal);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
}

public static class DadAutoPartyShareRules
{
    public static bool Allows(
        DadAutoPartySharePolicy policy,
        string opaqueCharacterHandle,
        bool paired,
        bool sameHomeGuild)
    {
        if (policy is not { Enabled: true, IsValid: true } ||
            string.IsNullOrWhiteSpace(DadAutoPartyConfiguration.NormalizeIdentifier(opaqueCharacterHandle)))
            return false;
        return policy.Mode switch
        {
            DadAutoPartyCharacterShareMode.SpecificCharacter =>
                paired && policy.CharacterHandles.Count == 1 &&
                policy.CharacterHandles.Contains(opaqueCharacterHandle, StringComparer.Ordinal),
            DadAutoPartyCharacterShareMode.CharacterList =>
                paired && policy.CharacterHandles.Contains(opaqueCharacterHandle, StringComparer.Ordinal),
            DadAutoPartyCharacterShareMode.AllCharactersForPeer => paired,
            DadAutoPartyCharacterShareMode.PromiscuousAllSameGuild => sameHomeGuild,
            _ => false,
        };
    }
}
