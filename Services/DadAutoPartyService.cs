using AutoParty.Contracts;
using dad.Models;

namespace dad.Services;

public sealed class DadAutoPartyService : IDisposable
{
    private readonly object directoryPresenceGate = new();
    private readonly DadAutoPartyConfiguration configuration;
    private readonly IDadAutoPartyEndpointIdentityStore identityStore;
    private readonly IDadAutoPartyWebhookCredentialStore? credentialStore;
    private readonly Action saveConfiguration;
    private Action<string>? ownerStop;
    private DateTime nextMaintenanceUtc = DateTime.MinValue;
    private readonly HashSet<string> onlineDirectoryIslands = new(StringComparer.Ordinal);
    private readonly Dictionary<PairedLabelKey, PairedLabelOverlay> pairedLabelOverlay = [];
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

    public DadAutoPartyPolicyDecision AcceptPairingEstablished(PairingEstablished established)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(established);
        var localAttemptId = Guid.TryParse(configuration.PairingAttemptId, out var configuredAttempt)
            ? configuredAttempt
            : Guid.Empty;
        var expectedPeerAttemptId = Guid.TryParse(
            configuration.PairingPeerAttemptId,
            out var configuredPeerAttempt)
            ? configuredPeerAttempt
            : Guid.Empty;
        var localPolicy = ToDadPolicy(established.LocalSharePolicy).Normalize();
        var peerPolicy = ToDadPolicy(established.PeerSharePolicy).Normalize();
        var peerFingerprint = DadAutoPartyIdentityPackageService.BuildFingerprint(
            established.PeerOwnerId.Value,
            established.PeerIslandId.Value,
            established.PeerPublicKeys.KeyVersion,
            established.PeerPublicKeys.Ed25519PublicKey.ToArray(),
            established.PeerPublicKeys.X25519PublicKey.ToArray());
        if (!configuration.IsRegistrationActive ||
            established.PairingId == Guid.Empty ||
            established.LocalAttemptId != localAttemptId ||
            established.PeerAttemptId != expectedPeerAttemptId ||
            established.Header.RecipientIslandId.Value != configuration.RegisteredIslandId ||
            established.PeerIslandId.Value == configuration.RegisteredIslandId ||
            !string.Equals(
                established.PeerFingerprint,
                peerFingerprint,
                StringComparison.Ordinal) ||
            !SamePolicy(localPolicy, configuration.PairingAttemptSharePolicy) ||
            !localPolicy.IsValid ||
            !peerPolicy.IsValid)
        {
            return Decision(false, "dad-pairing-established-invalid");
        }

        var pairing = new DadAutoPartyPairing
        {
            PairingId = established.PairingId.ToString("D"),
            OwnerId = established.PeerOwnerId.Value,
            IslandId = established.PeerIslandId.Value,
            PeerEndpointAlias = established.PeerEndpointAlias,
            PublicKeyFingerprint = established.PeerFingerprint,
            LocalFingerprint = configuration.RegistrationFingerprint,
            TranscriptHash = established.TranscriptHash,
            LocalSharePolicy = localPolicy,
            PeerSharePolicy = peerPolicy,
            ExpiresAtUtc = established.Header.ExpiresAt.UtcDateTime,
            KeyGeneration = established.PeerPublicKeys.KeyVersion,
            SigningPublicKey = Convert.ToBase64String(
                established.PeerPublicKeys.Ed25519PublicKey.AsSpan()),
            AgreementPublicKey = Convert.ToBase64String(
                established.PeerPublicKeys.X25519PublicKey.AsSpan()),
            ConfirmedAtUtc = established.EstablishedAt.UtcDateTime,
        }.Normalize();
        if (!pairing.IsActive)
        {
            return Decision(false, "dad-pairing-established-invalid");
        }

        configuration.Pairings.RemoveAll(item =>
            string.Equals(item.IslandId, pairing.IslandId, StringComparison.Ordinal));
        configuration.Pairings.Add(pairing);
        configuration.PendingPairings.Clear();
        configuration.ClearPairingAttempt();
        configuration.StateGeneration++;
        saveConfiguration();
        return Decision(true, "dad-pairing-active");
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

    public DadAutoPartyPolicyDecision SetPairingAlias(string peerIslandId, string localAlias)
    {
        ThrowIfDisposed();
        var islandId = DadAutoPartyConfiguration.NormalizeIdentifier(peerIslandId);
        var pairing = configuration.Pairings.FirstOrDefault(item =>
            item.IsActive && string.Equals(item.IslandId, islandId, StringComparison.Ordinal));
        var alias = DadAutoPartyConfiguration.NormalizeAlias(localAlias);
        if (pairing == null)
            return Decision(false, "dad-pairing-alias-invalid");
        if (!string.IsNullOrWhiteSpace(localAlias) && string.IsNullOrWhiteSpace(alias))
            return Decision(false, "dad-pairing-alias-invalid");
        configuration.PairedDadAliases ??= new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(alias))
        {
            if (!configuration.PairedDadAliases.Remove(islandId))
                return Decision(true, "dad-pairing-alias-unchanged");
        }
        else
        {
            if (configuration.PairedDadAliases.TryGetValue(islandId, out var currentAlias) &&
                string.Equals(currentAlias, alias, StringComparison.Ordinal))
                return Decision(true, "dad-pairing-alias-unchanged");
            if (!configuration.PairedDadAliases.ContainsKey(islandId) &&
                configuration.PairedDadAliases.Count >= 256)
                return Decision(false, "dad-pairing-alias-limit");
            configuration.PairedDadAliases[islandId] = alias;
        }
        configuration.StateGeneration++;
        saveConfiguration();
        return Decision(true, "dad-pairing-alias-updated");
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
        lock (directoryPresenceGate)
        {
            onlineDirectoryIslands.Remove(islandId);
            RemovePairedLabelsLocked(islandId);
        }
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
            (!paired && (policy.Mode != DadAutoPartyCharacterShareMode.CharacterList ||
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
        lock (directoryPresenceGate)
            PrunePairedLabelsLocked(now);
        configuration.StateGeneration++;
        saveConfiguration();
        return Decision(true, "dad-listings-applied");
    }

    internal void ApplyDirectoryPresence(string sharingIslandId, bool online)
    {
        ThrowIfDisposed();
        var islandId = DadAutoPartyConfiguration.NormalizeIdentifier(sharingIslandId);
        if (string.IsNullOrWhiteSpace(islandId) ||
            string.Equals(islandId, configuration.RegisteredIslandId, StringComparison.Ordinal))
            return;

        lock (directoryPresenceGate)
        {
            if (online)
                onlineDirectoryIslands.Add(islandId);
            else
            {
                onlineDirectoryIslands.Remove(islandId);
                RemovePairedLabelsLocked(islandId);
            }
        }

        if (online || configuration.Listings.RemoveAll(item =>
                string.Equals(item.SharingIslandId, islandId, StringComparison.Ordinal)) == 0)
            return;
        configuration.StateGeneration++;
        saveConfiguration();
    }

    public DadAutoPartyDirectorySnapshot GetDirectorySnapshot()
    {
        ThrowIfDisposed();
        var now = DateTime.UtcNow;
        HashSet<string> onlineIslands;
        Dictionary<PairedLabelKey, PairedLabelOverlay> labels;
        lock (directoryPresenceGate)
        {
            PrunePairedLabelsLocked(now);
            onlineIslands = new HashSet<string>(onlineDirectoryIslands, StringComparer.Ordinal);
            labels = new Dictionary<PairedLabelKey, PairedLabelOverlay>(pairedLabelOverlay);
        }
        var listings = configuration.Listings
            .Where(item => item.Available && item.ExpiresAtUtc > now &&
                           (string.Equals(
                                item.SharingIslandId,
                                configuration.RegisteredIslandId,
                                StringComparison.Ordinal) ||
                            onlineIslands.Contains(item.SharingIslandId)))
            .Select(static item => item.Clone())
            .ToList();
        foreach (var listing in listings)
        {
            listing.OpaqueDisplayLabel = listing.DisplayLabel;
            if (labels.TryGetValue(
                    new PairedLabelKey(listing.SharingIslandId, listing.OpaqueCharacterId),
                    out var overlay) &&
                overlay.ListingRevision == listing.Revision &&
                overlay.ListingExpiresAtUtc == listing.ExpiresAtUtc)
            {
                listing.DisplayLabel = overlay.DisplayLabel;
            }
        }
        return new(
            configuration.StateGeneration,
            listings
                .OrderBy(static item => item.DisplayLabel, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static item => item.OpaqueCharacterId, StringComparer.Ordinal)
                .ToList(),
            onlineIslands);
    }

    internal bool ApplyPairedListingLabels(
        string peerIslandId,
        Guid pairingId,
        string pairingTranscriptHash,
        IReadOnlyCollection<string> requestedHandles,
        IEnumerable<PairedListingLabel> labels,
        DateTimeOffset observedAt)
    {
        ThrowIfDisposed();
        var islandId = DadAutoPartyConfiguration.NormalizeIdentifier(peerIslandId);
        var transcriptHash = DadAutoPartyConfiguration.NormalizeIdentifier(pairingTranscriptHash);
        var pairing = configuration.Pairings.SingleOrDefault(item =>
            item.IsActive && Guid.TryParse(item.PairingId, out var configuredPairingId) &&
            configuredPairingId == pairingId &&
            string.Equals(item.IslandId, islandId, StringComparison.Ordinal) &&
            string.Equals(item.TranscriptHash, transcriptHash, StringComparison.Ordinal));
        if (pairing == null)
            return false;

        var requested = (requestedHandles ?? [])
            .Select(DadAutoPartyConfiguration.NormalizeIdentifier)
            .Where(static handle => handle.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Take(AutoPartyProtocol.MaximumPairedListingLabelBatchSize)
            .ToHashSet(StringComparer.Ordinal);
        var supplied = (labels ?? [])
            .Where(label => label != null && requested.Contains(label.CharacterHandle.Value))
            .DistinctBy(static label => label.CharacterHandle.Value, StringComparer.Ordinal)
            .ToList();
        var now = observedAt.UtcDateTime;
        lock (directoryPresenceGate)
        {
            if (!onlineDirectoryIslands.Contains(islandId))
                return false;
            foreach (var handle in requested)
                pairedLabelOverlay.Remove(new PairedLabelKey(islandId, handle));
            foreach (var label in supplied)
            {
                var listing = configuration.Listings.SingleOrDefault(item =>
                    item.Available && item.ExpiresAtUtc > now &&
                    string.Equals(item.OwnerId, pairing.OwnerId, StringComparison.Ordinal) &&
                    string.Equals(item.SharingIslandId, islandId, StringComparison.Ordinal) &&
                    string.Equals(item.OpaqueCharacterId, label.CharacterHandle.Value, StringComparison.Ordinal));
                if (listing == null)
                    continue;
                pairedLabelOverlay[new PairedLabelKey(islandId, listing.OpaqueCharacterId)] = new(
                    pairingId,
                    transcriptHash,
                    listing.Revision,
                    listing.ExpiresAtUtc,
                    label.DisplayLabel);
            }
            PrunePairedLabelsLocked(now);
        }
        return true;
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
        lock (directoryPresenceGate)
            PrunePairedLabelsLocked(now);
        changed |= configuration.PendingPairings.RemoveAll(pairing => pairing.ExpiresAtUtc <= now) > 0;
        if (configuration.PairingAttemptExpiresAtUtc != default &&
            configuration.PairingAttemptExpiresAtUtc <= now)
        {
            configuration.ClearPairingAttempt();
            changed = true;
        }
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

    public DadAutoPartyPolicyDecision RenewOwnedProposal(
        Guid proposalId,
        DateTimeOffset previousExpiresAt,
        DateTimeOffset newExpiresAt) =>
        Policy.RenewOwnedProposal(proposalId, previousExpiresAt, newExpiresAt);

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

    public DadAutoPartyPolicyDecision Revoke(Revocation revocation)
    {
        var decision = Policy.Revoke(revocation);
        if (decision.Allowed && revocation.TargetKind == RevocationTargetKind.Identity)
        {
            var islands = configuration.Pairings
                .Where(pairing => string.Equals(pairing.OwnerId, revocation.TargetId, StringComparison.Ordinal))
                .Select(static pairing => pairing.IslandId)
                .ToList();
            lock (directoryPresenceGate)
            {
                foreach (var islandId in islands)
                    RemovePairedLabelsLocked(islandId);
            }
        }
        return decision;
    }

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
        lock (directoryPresenceGate)
        {
            onlineDirectoryIslands.Clear();
            pairedLabelOverlay.Clear();
        }
        Connector.DisposeAsync().AsTask().GetAwaiter().GetResult();
        disposed = true;
    }

    private void ClearRegistrationAndTrust()
    {
        configuration.ClearRegistrationAndTrust();
        lock (directoryPresenceGate)
        {
            onlineDirectoryIslands.Clear();
            pairedLabelOverlay.Clear();
        }
    }

    private void PrunePairedLabelsLocked(DateTime now)
    {
        foreach (var key in pairedLabelOverlay.Keys.ToList())
        {
            var overlay = pairedLabelOverlay[key];
            var pairing = configuration.Pairings.SingleOrDefault(item =>
                item.IsActive && Guid.TryParse(item.PairingId, out var pairingId) &&
                pairingId == overlay.PairingId &&
                string.Equals(item.IslandId, key.IslandId, StringComparison.Ordinal) &&
                string.Equals(item.TranscriptHash, overlay.PairingTranscriptHash, StringComparison.Ordinal));
            var listing = configuration.Listings.SingleOrDefault(item =>
                item.Available && item.ExpiresAtUtc > now &&
                string.Equals(item.SharingIslandId, key.IslandId, StringComparison.Ordinal) &&
                string.Equals(item.OpaqueCharacterId, key.OpaqueCharacterId, StringComparison.Ordinal));
            if (pairing == null || listing == null || !onlineDirectoryIslands.Contains(key.IslandId) ||
                listing.Revision != overlay.ListingRevision ||
                listing.ExpiresAtUtc != overlay.ListingExpiresAtUtc ||
                overlay.ListingExpiresAtUtc <= now)
            {
                pairedLabelOverlay.Remove(key);
            }
        }
    }

    private void RemovePairedLabelsLocked(string islandId)
    {
        foreach (var key in pairedLabelOverlay.Keys
                     .Where(key => string.Equals(key.IslandId, islandId, StringComparison.Ordinal))
                     .ToList())
            pairedLabelOverlay.Remove(key);
    }

    private DadAutoPartyPolicyDecision Decision(bool allowed, string safeCode) =>
        new(allowed, safeCode, Math.Max(1, configuration.StateGeneration));

    internal static bool SamePolicy(DadAutoPartySharePolicy left, DadAutoPartySharePolicy right)
        => left.Enabled == right.Enabled && left.Mode == right.Mode && left.Revision == right.Revision &&
        left.CharacterHandles.SequenceEqual(right.CharacterHandles, StringComparer.Ordinal);

    private static DadAutoPartySharePolicy ToDadPolicy(CharacterSharePolicy policy)
        => new()
        {
            Mode = (DadAutoPartyCharacterShareMode)(int)policy.Mode,
            CharacterHandles = policy.CharacterHandles.Select(static value => value.Value).ToList(),
            Enabled = policy.Enabled,
            Revision = policy.Revision,
            UpdatedAtUtc = policy.UpdatedAt.UtcDateTime,
        };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    private readonly record struct PairedLabelKey(string IslandId, string OpaqueCharacterId);

    private readonly record struct PairedLabelOverlay(
        Guid PairingId,
        string PairingTranscriptHash,
        long ListingRevision,
        DateTime ListingExpiresAtUtc,
        string DisplayLabel);
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
                (paired || sameHomeGuild) &&
                policy.CharacterHandles.Contains(opaqueCharacterHandle, StringComparer.Ordinal),
            DadAutoPartyCharacterShareMode.AllCharactersForPeer => paired,
            DadAutoPartyCharacterShareMode.PromiscuousAllSameGuild => paired && sameHomeGuild,
            _ => false,
        };
    }
}
