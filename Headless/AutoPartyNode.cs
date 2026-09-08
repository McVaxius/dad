using System.Text.Json;
using dad.Models;
using Dalamud.Plugin.Services;
using dad.Services;

namespace dad.Headless;

internal sealed class AutoPartyNode : IDisposable
{
    private readonly Configuration configuration;
    private readonly DadAutoPartyRelayPump relay;
    private readonly DadAutoPartyInboundRuntime runtime;
    public DadAutoPartyParticipantBridge Bridge { get; }
    public DadAutoPartyService Service { get; }
    public DadAutoPartyEndpointService Endpoint { get; }

    public AutoPartyNode(Configuration configuration, string directory, Uri provider, Action<string> observe,
        Func<DateTime, DadAutoPartyListingPublication> publication,
        Func<IReadOnlyList<DadAutoPartyCrewCandidate>> crew,
        DadPresenceService presence, DadTransportService transport, DadWakeTakeoverService wake,
        DadClaimService claims, DadWorkerExecutionService worker, InfoProxyPartyInviteGateway party,
        DadCombatRotationService combat, IPluginLog log)
    {
        this.configuration = configuration;
        configuration.AutoParty.Enabled = true;
        var identity = new DadAutoPartyDpapiEndpointIdentityStore(Path.Combine(directory, "identity"));
        var credentials = new DadAutoPartyDpapiWebhookCredentialStore(Path.Combine(directory, "mailbox"));
        Service = new(configuration.AutoParty, identity, () => configuration.PluginEnabled, configuration.Save, credentialStore: credentials);
        Endpoint = new(configuration.AutoParty, credentials,
            new DadAutoPartyDpapiDiscordTokenStore(Path.Combine(directory, "legacy")), Service.Connector, configuration.Save,
            diagnostic: message => observe($"autoparty:{message}"),
            httpClientFactory: () => new HttpClient(new LoopbackHandler(provider)) { Timeout = TimeSpan.FromSeconds(5) },
            identityStore: identity, listingPublicationProvider: publication);
        Service.ConfigureListingPublicationChanged(Endpoint.ScheduleListingPublication);
        var profiles = new DadFrenRiderProfileTransferService(Plugin.PluginInterface, log);
        Bridge = new(configuration.AutoParty, () => configuration.AutoParty.RemoteBindings, crew,
            () => configuration.CombatRotationMode == DadCombatRotationMode.UseFrenRider, profiles.ResolveAndEncode);
        var admission = DadRuntimeHandlers.CreateAutoPartyAdmission(configuration, presence, transport, wake, claims);
        relay = new(configuration.AutoParty, identity, Service.Connector, Service, Bridge,
            new DadAutoPartyFilePendingOperationStore(directory),
            inboundProposalStore: new DadAutoPartyFileInboundProposalStore(directory),
            inboundListingPublicationProvider: publication,
            inboundAdmissionWithPublication: admission.Admit,
            restoreInboundProposal: admission.RestoreProposal,
            renewInboundProposal: (proposal, previous, next) => Service.RenewOwnedProposal(proposal, previous, next).Allowed,
            expiredRuntimeTargetHandler: target => runtime!.ReleaseExpiredInboundFrenRiderProfile(target),
            utcNow: () => DadClock.OffsetUtcNow,
            diagnostic: message => observe($"relay:{message}"), saveConfiguration: configuration.Save);
        runtime = new(configuration, relay, admission, presence, transport, worker, party, profiles, combat, log);
        DadRuntimeHandlers.ConfigureAutoPartyExecution(relay, Service, runtime, worker);
        Bridge.ConfigureDirectoryAuthorityGate(relay.GetRemoteAuthorityBlocker);
        presence.ConfigureAutoPartyPresenceProvider(Endpoint.GetLanPresence);
    }
    public void Attach() => DadRuntimeHandlers.AttachAutoPartyRelayAfterValidatedBootstrap(configuration.AutoParty, Endpoint, relay, Service);
    public object Execute(JsonElement input) => input.GetProperty("action").GetString() switch
    {
        "bind" => Bind(input.GetProperty("characterId").GetString()!, input.GetProperty("jobId").GetUInt32()),
        "proposal-status" => Bridge.GetSnapshot(input.GetProperty("proposalId").GetGuid(), input.GetProperty("slotId").GetString()!, DadClock.OffsetUtcNow)!,
        "challenge" => Service.IdentityPackages.GenerateChallengeAsync(input.GetProperty("alias").GetString()).AsTask().GetAwaiter().GetResult(),
        "bootstrap" => Endpoint.ImportBootstrapCopyPasteAsync(input.GetProperty("package").GetString()!).AsTask().GetAwaiter().GetResult(),
        "pair-invite" => Endpoint.EnsurePairingInviteAsync(input.TryGetProperty("regenerate", out var regenerate) && regenerate.GetBoolean()).AsTask().GetAwaiter().GetResult(),
        "pair-submit" => Endpoint.SubmitPairingAsync(input.GetProperty("peerToken").GetString()!,
            new DadAutoPartySharePolicy { Enabled = true, Mode = DadAutoPartyCharacterShareMode.AllCharactersForPeer, UpdatedAtUtc = DadClock.UtcNow }).AsTask().GetAwaiter().GetResult(),
        "unpair" => Endpoint.Deauthenticate(input.GetProperty("peerIsland").GetString()!, "synthetic-owner-unpair"),
        "publish" => Endpoint.PublishListingsImmediately(),
        "directory" => Endpoint.RequestDirectoryAsync("", false).AsTask().GetAwaiter().GetResult(),
        "deregister" => Endpoint.BeginDeregistration(),
        _ => throw new InvalidOperationException("Unknown AutoParty lab control."),
    };
    private object Bind(string id, uint job)
    {
        var listing = Service.GetDirectorySnapshot().Listings.Single(l => l.OpaqueCharacterId == id);
        var binding = new DadAutoPartyRemoteBinding
        {
            FleetRowId = $"lab-{id}", OpaqueCharacterId = id, OwnerId = listing.OwnerId,
            IslandId = listing.SharingIslandId, RequestedJobId = job.ToString(System.Globalization.CultureInfo.InvariantCulture),
            OwnerConsentConfirmed = true,
        };
        configuration.AutoParty.RemoteBindings.RemoveAll(b => b.OpaqueCharacterId == id);
        configuration.AutoParty.RemoteBindings.Add(binding);
        configuration.Save();
        return binding;
    }
    public object Snapshot() => new
    {
        configuration.AutoParty.RegistrationState, configuration.AutoParty.RegisteredIslandId,
        configuration.AutoParty.RegistrationId, configuration.AutoParty.Pairings,
        configuration.AutoParty.PairingInviteToken,
        endpoint = Endpoint.Snapshot, relay = Endpoint.RelayStatus, pairingAttempt = relay.LastPairingAttemptResult,
        directory = Service.GetDirectorySnapshot(), publication = Endpoint.ListingPublicationSnapshot,
        inboundProposals = relay.InboundProposals.Select(state => new
        {
            state.Proposal.ProposalId, state.AdmissionReady, state.SafeCode,
            proposalExpires = state.Proposal.Header.ExpiresAt, leaseExpires = state.Lease?.LeaseExpiresAt,
            leaseIssued = state.Lease?.Header.IssuedAt, requestedLeaseSeconds = state.Proposal.ExecutionPlan?.LeaseDurationSeconds,
            leaseGeneration = state.Lease?.ObservedStateGeneration,
        }).ToArray(),
    };
    public void Dispose() { runtime.ReleaseAllInboundFrenRiderProfiles(); relay.DisposeAsync().AsTask().GetAwaiter().GetResult(); Endpoint.Dispose(); Service.Dispose(); }

    private sealed class LoopbackHandler(Uri provider) : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (provider.Scheme != "http" || provider.Host != "127.0.0.1" ||
                request.RequestUri is not { Scheme: "https", Host: "discord.com" } original)
                throw new InvalidOperationException("Only the owned synthetic Discord provider is allowed.");
            request.RequestUri = new Uri(provider, original.PathAndQuery);
            return base.SendAsync(request, token);
        }
    }
}
