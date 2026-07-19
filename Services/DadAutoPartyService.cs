using System.Security.Cryptography;
using AutoParty.Contracts;
using dad.Models;

namespace dad.Services;

public sealed class DadAutoPartyService : IDisposable
{
    private readonly DadAutoPartyConfiguration configuration;
    private readonly IDadAutoPartyEndpointIdentityStore identityStore;
    private readonly Action saveConfiguration;
    private readonly Dictionary<Guid, DadAutoPartyPairingChallenge> pairingChallenges = [];
    private readonly CancellationTokenSource courierPumpCancellation = new();
    private Action<string>? ownerStop;
    private DateTime nextMaintenanceUtc = DateTime.MinValue;
    private DateTime nextCourierPumpUtc = DateTime.MinValue;
    private Task<bool>? courierPumpTask;
    private bool disposed;

    public DadAutoPartyService(
        DadAutoPartyConfiguration configuration,
        IDadAutoPartyEndpointIdentityStore identityStore,
        Func<bool> dadEnabled,
        Action saveConfiguration,
        Func<bool>? localSafetyAllowsExecution = null)
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.identityStore = identityStore ?? throw new ArgumentNullException(nameof(identityStore));
        this.saveConfiguration = saveConfiguration ?? throw new ArgumentNullException(nameof(saveConfiguration));
        Connector = new DadDiscordCourierConnector(configuration, dadEnabled);
        Policy = new DadAutoPartyPolicyFacade(configuration, dadEnabled, localSafetyAllowsExecution);
        Execution = new DadAutoPartyFakeExecutionFacade(Policy);
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

    public DadAutoPartyPolicyDecision SetPairingEnabled(bool enabled)
    {
        ThrowIfDisposed();
        if (enabled && (!configuration.OwnerAcceptanceConfirmed ||
                        string.IsNullOrWhiteSpace(configuration.EnrollmentReceiptId) ||
                        string.IsNullOrWhiteSpace(configuration.PilotArtifactSha256)))
            return Decision(false, "dad-pairing-registration-pending");
        if (configuration.PairingEnabled == enabled)
            return Decision(true, enabled ? "dad-pairing-enabled" : "dad-pairing-disabled");
        configuration.PairingEnabled = enabled;
        if (!enabled)
        {
            configuration.ExecutionEnabled = false;
            StopAll("dad-pairing-disabled-by-owner");
        }
        configuration.StateGeneration++;
        saveConfiguration();
        return Decision(true, enabled ? "dad-pairing-enabled" : "dad-pairing-disabled");
    }

    public DadAutoPartyPolicyDecision SetExecutionEnabled(bool enabled)
    {
        ThrowIfDisposed();
        if (enabled && (!configuration.Enabled || !configuration.PairingEnabled ||
                        !configuration.OwnerAcceptanceConfirmed ||
                        configuration.Pairings.Count == 0 ||
                        string.IsNullOrWhiteSpace(configuration.PilotPlannerGroupId) ||
                        string.IsNullOrWhiteSpace(configuration.PilotQueueAuthorityFingerprint) ||
                        !configuration.PilotCourierProbeVerified))
            return Decision(false, "dad-execution-prerequisites-pending");
        if (configuration.ExecutionEnabled == enabled)
            return Decision(true, enabled ? "dad-execution-enabled" : "dad-execution-disabled");
        configuration.ExecutionEnabled = enabled;
        if (!enabled)
            StopAll("dad-execution-disabled-by-owner");
        configuration.StateGeneration++;
        saveConfiguration();
        return Decision(true, enabled ? "dad-execution-enabled" : "dad-execution-disabled");
    }

    public DadAutoPartyPolicyDecision ApplyPilotExchangeRoot(string? requestedRoot)
    {
        ThrowIfDisposed();
        if (configuration.Enabled || configuration.PairingEnabled || configuration.ExecutionEnabled)
            return Decision(false, "dad-pilot-exchange-root-gates-enabled");
        if (!DadAutoPartyConfiguration.TryNormalizePilotExchangeRoot(requestedRoot, out var normalizedRoot))
            return Decision(false, "dad-pilot-exchange-root-invalid");

        var probePath = Path.Combine(normalizedRoot, $".dad-write-probe-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(normalizedRoot);
            foreach (var managedFolder in new[] { "pilot-input", "pilot-receipts", "pilot-courier", "plugin" })
                Directory.CreateDirectory(Path.Combine(normalizedRoot, managedFolder));
            File.WriteAllBytes(probePath, []);
            File.Delete(probePath);
        }
        catch (UnauthorizedAccessException)
        {
            return Decision(false, "dad-pilot-exchange-root-unwritable");
        }
        catch (System.Security.SecurityException)
        {
            return Decision(false, "dad-pilot-exchange-root-unwritable");
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException)
        {
            return Decision(false, "dad-pilot-exchange-root-unavailable");
        }
        finally
        {
            try
            {
                if (File.Exists(probePath))
                    File.Delete(probePath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
            }
        }

        configuration.PilotExchangeRoot = normalizedRoot;
        configuration.CourierRootPath = Path.Combine(normalizedRoot, "pilot-courier");
        configuration.StateGeneration++;
        saveConfiguration();
        return Decision(true, "dad-pilot-exchange-root-applied");
    }

    public DadAutoPartyPolicyDecision ConfirmEnrollmentPairings()
    {
        ThrowIfDisposed();
        if (!configuration.PairingEnabled || !configuration.OwnerAcceptanceConfirmed ||
            configuration.PendingPairings.Count == 0)
            return Decision(false, "dad-pairing-confirmation-pending");
        foreach (var pairing in configuration.PendingPairings)
        {
            configuration.Pairings.RemoveAll(existing =>
                string.Equals(existing.IslandId, pairing.IslandId, StringComparison.Ordinal));
            configuration.Pairings.Add(WithConfirmedNow(pairing));
        }
        configuration.PendingPairings.Clear();
        configuration.StateGeneration++;
        saveConfiguration();
        return Decision(true, "dad-pairing-locally-confirmed");
    }

    public void Update(bool dadPluginEnabled)
    {
        ThrowIfDisposed();
        if (!dadPluginEnabled || !configuration.Enabled)
            return;
        ObserveCourierPump();
        var now = DateTime.UtcNow;
        if (now < nextMaintenanceUtc)
            return;
        nextMaintenanceUtc = now + TimeSpan.FromMinutes(1);

        var changed = false;
        changed |= configuration.Grants.RemoveAll(grant => grant.ExpiresAtUtc <= now) > 0;
        changed |= configuration.Listings.RemoveAll(listing => listing.ExpiresAtUtc <= now) > 0;
        foreach (var challengeId in pairingChallenges
                     .Where(pair => pair.Value.ExpiresAtUtc <= now)
                     .Select(static pair => pair.Key)
                     .ToList())
            pairingChallenges.Remove(challengeId);
        if (changed)
        {
            configuration.StateGeneration++;
            saveConfiguration();
        }
    }

    public async ValueTask<DadAutoPartyIdentityOperationResult> SendPilotCourierProbeAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var peer = configuration.Pairings.FirstOrDefault(pairing => pairing.RevokedAtUtc == null);
        if (!configuration.Enabled || !configuration.PairingEnabled ||
            string.IsNullOrWhiteSpace(configuration.RegisteredIslandId) || peer == null)
            return new(false, "dad-pilot-courier-probe-not-ready");

        var payload = RandomNumberGenerator.GetBytes(32);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var envelope = OpaqueEnvelope.Create(
                AutoPartyProtocol.CurrentVersion,
                Guid.NewGuid(),
                new IslandId(configuration.RegisteredIslandId),
                new IslandId(peer.IslandId),
                now,
                now.AddMinutes(10),
                Math.Max(1, configuration.StateGeneration),
                "dad.pilot.probe/v1",
                payload);
            var result = await Connector.SendAsync(envelope, cancellationToken).ConfigureAwait(false);
            return new(result.Accepted, result.SafeCode);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    public async ValueTask<DadAutoPartyPolicyDecision> ImportRegistrationAsync(
        DadAutoPartyRegistrationImport registration,
        bool confirmReplacement,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!configuration.PairingEnabled || !configuration.OwnerAcceptanceConfirmed ||
            string.IsNullOrWhiteSpace(configuration.EnrollmentReceiptId))
            return Decision(false, "dad-registration-disabled-pending-review");
        var ownerId = DadAutoPartyConfiguration.NormalizeIdentifier(registration.OwnerId);
        var islandId = DadAutoPartyConfiguration.NormalizeIdentifier(registration.IslandId);
        var fingerprint = DadAutoPartyConfiguration.NormalizeFingerprint(registration.PublicKeyFingerprint);
        if (string.IsNullOrWhiteSpace(ownerId) || string.IsNullOrWhiteSpace(islandId) ||
            string.IsNullOrWhiteSpace(fingerprint) || registration.KeyGeneration < 1 ||
            registration.ProtectedIdentityMaterial is not { Length: > 0 and <= AutoPartyProtocol.PreallocationDefensiveCeilingBytes })
            return Decision(false, "dad-registration-import-invalid");
        if (!string.IsNullOrWhiteSpace(configuration.EndpointIdentityReference) && !confirmReplacement)
            return Decision(false, "dad-registration-replacement-confirmation-required");

        var oldReference = configuration.EndpointIdentityReference;
        var newReference = await identityStore.StoreAsync(
            registration.ProtectedIdentityMaterial,
            cancellationToken).ConfigureAwait(false);
        configuration.EndpointIdentityReference = newReference;
        configuration.RegisteredOwnerId = ownerId;
        configuration.RegisteredIslandId = islandId;
        configuration.RegistrationFingerprint = fingerprint;
        configuration.StateGeneration++;
        saveConfiguration();
        if (!string.IsNullOrWhiteSpace(oldReference))
            await identityStore.DeleteAsync(oldReference, cancellationToken).ConfigureAwait(false);
        return Decision(true, "dad-registration-imported");
    }

    public DadAutoPartyPairingChallenge? BeginPairing(
        OwnerIdentity owner,
        IslandIdentity island)
    {
        ThrowIfDisposed();
        if (!configuration.PairingEnabled || !configuration.OwnerAcceptanceConfirmed ||
            string.IsNullOrWhiteSpace(configuration.EnrollmentReceiptId) || !configuration.Enabled ||
            owner.OwnerId != island.OwnerId || owner.HomeIslandId != island.IslandId ||
            owner.KeyGeneration != island.KeyGeneration || owner.KeyGeneration < 1 ||
            !string.Equals(owner.PublicKeyId, island.PublicKeyId, StringComparison.Ordinal))
            return null;
        var fingerprint = DadAutoPartyConfiguration.NormalizeFingerprint(owner.PublicKeyId);
        if (string.IsNullOrWhiteSpace(fingerprint))
            return null;

        var challenge = new DadAutoPartyPairingChallenge(
            Guid.NewGuid(),
            owner.OwnerId.Value,
            island.IslandId.Value,
            fingerprint,
            island.KeyGeneration,
            RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6"),
            DateTime.UtcNow + TimeSpan.FromMinutes(5));
        pairingChallenges[challenge.ChallengeId] = challenge;
        return challenge;
    }

    public DadAutoPartyPolicyDecision ConfirmPairing(
        Guid challengeId,
        string displayedFingerprint,
        string confirmationCode)
    {
        ThrowIfDisposed();
        if (!configuration.PairingEnabled)
            return Decision(false, "dad-pairing-disabled-pending-review");
        if (!pairingChallenges.Remove(challengeId, out var challenge) || challenge.ExpiresAtUtc <= DateTime.UtcNow)
            return Decision(false, "dad-pairing-challenge-expired");
        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(challenge.ConfirmationCode),
                System.Text.Encoding.UTF8.GetBytes((confirmationCode ?? string.Empty).Trim())) ||
            !string.Equals(
                challenge.PublicKeyFingerprint,
                DadAutoPartyConfiguration.NormalizeFingerprint(displayedFingerprint),
                StringComparison.Ordinal))
            return Decision(false, "dad-pairing-confirmation-mismatch");

        configuration.Pairings.RemoveAll(pairing =>
            string.Equals(pairing.IslandId, challenge.IslandId, StringComparison.Ordinal));
        configuration.Pairings.Add(new DadAutoPartyPairing
        {
            OwnerId = challenge.OwnerId,
            IslandId = challenge.IslandId,
            PublicKeyFingerprint = challenge.PublicKeyFingerprint,
            KeyGeneration = challenge.KeyGeneration,
            ConfirmedAtUtc = DateTime.UtcNow,
        });
        configuration.StateGeneration++;
        saveConfiguration();
        return Decision(true, "dad-pairing-confirmed");
    }

    public DadAutoPartyPolicyDecision Unpair(string islandId)
    {
        ThrowIfDisposed();
        var pairing = configuration.Pairings.FirstOrDefault(candidate =>
            candidate.RevokedAtUtc == null &&
            string.Equals(candidate.IslandId, islandId?.Trim(), StringComparison.Ordinal));
        if (pairing == null)
            return Decision(true, "dad-pairing-already-absent");
        pairing.RevokedAtUtc = DateTime.UtcNow;
        Policy.SetOwnerVeto(new OwnerId(pairing.OwnerId), true, "dad-unpair");
        configuration.StateGeneration++;
        saveConfiguration();
        return Decision(true, "dad-pairing-revoked");
    }

    public DadAutoPartyPolicyDecision AddListing(DadAutoPartyListing listing)
    {
        ThrowIfDisposed();
        var normalized = listing?.Clone().Normalize();
        if (!configuration.Enabled || normalized is not { IsValid: true } ||
            normalized.ExpiresAtUtc <= DateTime.UtcNow ||
            normalized.ExpiresAtUtc > DateTime.UtcNow + TimeSpan.FromDays(30))
            return Decision(false, "dad-listing-invalid");
        configuration.Listings.RemoveAll(candidate =>
            string.Equals(candidate.ListingId, normalized.ListingId, StringComparison.Ordinal));
        configuration.Listings.Add(normalized);
        configuration.StateGeneration++;
        saveConfiguration();
        return Decision(true, "dad-listing-saved");
    }

    public DadAutoPartyPolicyDecision AddImmutableGrant(CapabilityGrant grant)
    {
        ThrowIfDisposed();
        if (!configuration.Enabled || grant.GrantId == Guid.Empty || grant.ProposalId == Guid.Empty ||
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
            OwnerId = grant.OwnerId.Value,
            IslandId = grant.Header.SenderIslandId.Value,
            OpaqueCharacterId = grant.Scope.CharacterId.Value,
            RequestedJobId = grant.Scope.RequestedJob.Value,
            ActivityId = grant.Scope.ActivityId.Value,
            Permissions = grant.Scope.Permissions,
            IssuedAtUtc = grant.ValidFrom.UtcDateTime,
            ExpiresAtUtc = grant.ValidUntil.UtcDateTime,
        });
        configuration.StateGeneration++;
        saveConfiguration();
        return Decision(true, "dad-grant-saved");
    }

    public DadAutoPartyPolicyDecision AcceptProposal(
        RunProposal proposal,
        SessionPermission requiredPermissions)
    {
        ThrowIfDisposed();
        var replay = Policy.VerifyReplay(proposal.Header);
        if (!replay.Allowed)
            return replay;
        return Policy.IntersectGrant(proposal, requiredPermissions);
    }

    public DadAutoPartyPolicyDecision Reserve(Reservation reservation, DadAutoPartySessionMode mode)
        => Policy.Reserve(reservation, mode);

    public DadAutoPartyPolicyDecision VerifyPreflight(PreflightResult preflight)
        => Policy.VerifyPreflight(preflight);

    public DadAutoPartyPolicyDecision AcquireLease(SessionLease lease)
        => Policy.AcquireLease(lease);

    public DadAutoPartyPolicyDecision Revoke(Revocation revocation)
        => Policy.Revoke(revocation);

    public DadAutoPartyAuthorizationDecision EvaluateSchedulerAuthorization(DadRunRequest request)
        => DadAutoPartySchedulerAuthorizationRules.Evaluate(request, Policy.GetProposalAuthorization);

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
            : string.IsNullOrWhiteSpace(configuration.EndpointIdentityReference) ||
              !configuration.OwnerAcceptanceConfirmed ||
              string.IsNullOrWhiteSpace(configuration.EnrollmentReceiptId)
                ? DadAutoPartyComponentState.NotReady
                : DadAutoPartyComponentState.Ready;
        var executionState = !configuration.ExecutionEnabled
            ? DadAutoPartyComponentState.Disabled
            : policyState == DadAutoPartyComponentState.Ready
                ? DadAutoPartyComponentState.Ready
                : DadAutoPartyComponentState.NotReady;
        return new(
            new(transportState, transportHealth.SafeCode, transportHealth.ObservedAt.UtcDateTime),
            new(policyState, policyState == DadAutoPartyComponentState.Ready ? "dad-policy-ready" : "dad-policy-not-ready", now),
            new(executionState, executionState == DadAutoPartyComponentState.Ready ? "dad-execution-ready" : "dad-execution-disabled", now),
            configuration.RegisteredIslandId,
            configuration.Listings.Count,
            configuration.Grants.Count,
            Policy.ActiveSessionCount);
    }

    public async ValueTask<DadAutoPartyPrivacyResult> PurgeAsync(
        bool deleteEndpointIdentity,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        StopAll("dad-privacy-purge");
        configuration.Enabled = false;
        configuration.PairingEnabled = false;
        configuration.ExecutionEnabled = false;
        pairingChallenges.Clear();
        configuration.Pairings.Clear();
        configuration.Grants.Clear();
        configuration.Listings.Clear();
        var identityDeleted = false;
        if (deleteEndpointIdentity && !string.IsNullOrWhiteSpace(configuration.EndpointIdentityReference))
            identityDeleted = await identityStore.DeleteAsync(configuration.EndpointIdentityReference, cancellationToken).ConfigureAwait(false);
        configuration.EndpointIdentityReference = string.Empty;
        configuration.RegisteredOwnerId = string.Empty;
        configuration.RegisteredIslandId = string.Empty;
        configuration.RegistrationFingerprint = string.Empty;
        configuration.EndpointAlias = string.Empty;
        configuration.SigningPublicKey = string.Empty;
        configuration.EncryptionPublicKey = string.Empty;
        configuration.EnrollmentReceiptId = string.Empty;
        configuration.PilotArtifactSha256 = string.Empty;
        configuration.OwnerAcceptanceConfirmed = false;
        configuration.PilotCourierProbeVerified = false;
        configuration.RemoteBindings.Clear();
        configuration.PendingPairings.Clear();
        configuration.StateGeneration++;
        saveConfiguration();
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
        courierPumpCancellation.Cancel();
        try
        {
            courierPumpTask?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        courierPumpCancellation.Dispose();
        Connector.DisposeAsync().AsTask().GetAwaiter().GetResult();
        disposed = true;
    }

    private DadAutoPartyPolicyDecision Decision(bool allowed, string safeCode)
        => new(allowed, safeCode, Math.Max(1, configuration.StateGeneration));

    private static DadAutoPartyPairing WithConfirmedNow(DadAutoPartyPairing pairing)
    {
        var clone = pairing.Clone();
        clone.ConfirmedAtUtc = DateTime.UtcNow;
        clone.RevokedAtUtc = null;
        return clone;
    }

    private void ObserveCourierPump()
    {
        if (courierPumpTask is { IsCompleted: true })
        {
            if (courierPumpTask.IsCompletedSuccessfully && courierPumpTask.Result &&
                !configuration.PilotCourierProbeVerified)
            {
                configuration.PilotCourierProbeVerified = true;
                configuration.StateGeneration++;
                saveConfiguration();
            }
            else if (courierPumpTask.IsFaulted)
            {
                _ = courierPumpTask.Exception;
            }

            courierPumpTask = null;
        }

        var now = DateTime.UtcNow;
        if (courierPumpTask == null && now >= nextCourierPumpUtc)
        {
            nextCourierPumpUtc = now + TimeSpan.FromSeconds(1);
            courierPumpTask = ReceivePilotCourierProbesAsync(courierPumpCancellation.Token);
        }
    }

    private async Task<bool> ReceivePilotCourierProbesAsync(CancellationToken cancellationToken)
    {
        await foreach (var envelope in Connector.ReceiveAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!string.Equals(envelope.PayloadType, "dad.pilot.probe/v1", StringComparison.Ordinal) ||
                !string.Equals(
                    envelope.RecipientIslandId.Value,
                    configuration.RegisteredIslandId,
                    StringComparison.Ordinal) ||
                !configuration.Pairings.Any(pairing =>
                    pairing.RevokedAtUtc == null &&
                    string.Equals(pairing.IslandId, envelope.SenderIslandId.Value, StringComparison.Ordinal)))
                continue;

            await Connector.AcknowledgeAsync(
                new AutoPartyTransportAcknowledgement(envelope.EnvelopeId, "dad-pilot-probe-accepted"),
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(disposed, this);
}
