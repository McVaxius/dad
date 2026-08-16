using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using dad.Models;
using dad.Services;

namespace dad.Windows;

public sealed class DadAutoPartyWindow : Window
{
    private readonly Plugin plugin;
    private string endpointAlias = string.Empty;
    private string challengeCopy = string.Empty;
    private string bootstrapCopy = string.Empty;
    private string directorySearch = string.Empty;
    private string peerIslandId = string.Empty;
    private string pairingFingerprint = string.Empty;
    private string pairingCode = string.Empty;
    private DadAutoPartyCrewShareScope pairingShareScope = DadAutoPartyCrewShareScope.SpecificCharacters;
    private DadAutoPartyCrewShareScope communityShareScope = DadAutoPartyCrewShareScope.SpecificCharacters;
    private bool includePromiscuous = true;
    private List<DadAcquiredCharacter> localCandidates = [];
    private List<DadAcquiredCharacter> localShareCandidates = [];
    private List<DadAutoPartyCrewCandidate> shareCandidates = [];
    private readonly HashSet<string> pairingShareHandles = new(StringComparer.Ordinal);
    private readonly HashSet<string> communityShareHandles = new(StringComparer.Ordinal);
    private string pairingSelectionId = string.Empty;
    private readonly List<string> freeformSelectionOrder = [];
    private readonly Dictionary<string, uint> remoteRequestedJobs = new(StringComparer.Ordinal);
    private string status = "AutoParty is disabled.";
    private Task<UiOperationResult>? operationTask;
    private bool forgetLostIdentityConfirmed;

    private sealed record UiOperationResult(
        string SafeCode,
        string ChallengeCopy = "");

    public DadAutoPartyWindow(Plugin plugin)
        : base("DAD AutoParty###DadAutoParty", ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;
        RespectCloseHotkey = false;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(680f, 560f),
            MaximumSize = new Vector2(1200f, 1000f),
        };
        Size = new Vector2(820f, 760f);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void OnOpen()
    {
        endpointAlias = plugin.Configuration.AutoParty.EndpointAlias;
        bootstrapCopy = string.Empty;
        peerIslandId = string.Empty;
        pairingCode = string.Empty;
        pairingSelectionId = string.Empty;
        forgetLostIdentityConfirmed = false;
        pairingShareHandles.Clear();
        communityShareHandles.Clear();
        if (plugin.Configuration.AutoParty.StandingSharePolicy.Mode ==
            DadAutoPartyCharacterShareMode.CharacterList)
        {
            communityShareHandles.UnionWith(
                plugin.Configuration.AutoParty.StandingSharePolicy.CharacterHandles);
        }
        communityShareScope = plugin.Configuration.AutoParty.StandingShareScope;
        RefreshLocalCandidates();
    }

    public override void Draw()
    {
        ObserveTask();
        var configuration = plugin.Configuration.AutoParty;
        var endpoint = plugin.AutoPartyEndpointService.Snapshot;

        DadUi.Heading(
            "Central AutoParty bridge",
            "One centrally operated Discord bot; this DAD uses only its private encrypted webhook mailbox.");
        ImGui.TextWrapped(
            "Enable bot DMs before registering. Submit this DAD's APR1 challenge in the guild, then paste the raw APB1 " +
            "token or exact wrapper from the bot's single DM here. The guild shows only safe registration feedback; " +
            "transport-channel traffic is private machine traffic. Pairing and fingerprint approval remain local.");
        if (!string.IsNullOrWhiteSpace(configuration.LegacyDiscordTokenCleanupWarning))
            ImGui.TextColored(
                new Vector4(1f, .7f, .2f, 1f),
                $"Security cleanup warning: {configuration.LegacyDiscordTokenCleanupWarning}. DAD will retry.");
        ImGui.Separator();

        var enabled = configuration.Enabled;
        if (ImGui.Checkbox("Enable AutoParty", ref enabled))
            plugin.AutoPartyService.SetEnabled(enabled);
        var relayStatus = plugin.AutoPartyEndpointService.RelayStatus;
        var registrationProgress = DadAutoPartyProgressProjection.Registration(
            configuration,
            endpoint,
            DateTime.UtcNow);
        var mailboxActivity = DadAutoPartyProgressProjection.MailboxActivity(
            endpoint,
            relayStatus,
            plugin.AutoPartyEndpointService.TransferSnapshot);
        DrawRegistrationProgressCard(registrationProgress);
        DrawMailboxActivityCard(mailboxActivity, endpoint.LastSuccessfulExchangeAtUtc);
        var activationPending = configuration.RegistrationState == DadAutoPartyRegistrationState.BootstrapImported &&
            configuration.BootstrapExpiresAtUtc > DateTime.UtcNow;

        var identityLost = configuration.RegistrationRecoveryState ==
            DadAutoPartyRegistrationRecoveryState.IdentityLost;
        if (identityLost)
        {
            ImGui.TextColored(
                new Vector4(1f, .45f, .3f, 1f),
                "The protected endpoint identity is missing or no longer matches this DAD. Trust cannot be transferred to a replacement identity.");
            ImGui.TextWrapped(
                $"First run /autoparty deregister island:{configuration.RegisteredIslandId} confirm:true as the Discord owner.");
            ImGui.Checkbox("I confirm the owner deregistration completed", ref forgetLostIdentityConfirmed);
            ImGui.BeginDisabled(!forgetLostIdentityConfirmed || operationTask is { IsCompleted: false });
            if (ImGui.Button("Forget old identity and register as new"))
            {
                Start(async () =>
                {
                    var result = await plugin.AutoPartyService
                        .PurgeAsync(deleteEndpointIdentity: true)
                        .ConfigureAwait(false);
                    return new UiOperationResult(result.SafeCode);
                });
            }
            ImGui.EndDisabled();
        }

        var registrationLocked = identityLost ||
            activationPending ||
            operationTask is { IsCompleted: false };
        ImGui.BeginDisabled(registrationLocked);
        ImGui.SetNextItemWidth(300f);
        ImGui.InputText("DAD endpoint alias", ref endpointAlias, 48);
        var challengeButtonLabel = configuration.RegistrationRecoveryState ==
            DadAutoPartyRegistrationRecoveryState.RecoveryAvailable ||
            configuration.RegistrationState is DadAutoPartyRegistrationState.Active or
                DadAutoPartyRegistrationState.BootstrapImported
                ? "Recover registration"
                : "Generate registration challenge";
        if (ImGui.Button(challengeButtonLabel))
        {
            Start(async () =>
            {
                var result = await plugin.AutoPartyService.IdentityPackages
                    .GenerateChallengeAsync(endpointAlias)
                    .ConfigureAwait(false);
                return new UiOperationResult(
                    result.SafeCode,
                    result.Succeeded ? result.OutputPath : string.Empty);
            });
        }
        ImGui.InputTextMultiline(
            "Encrypted registration challenge",
            ref challengeCopy,
            4096,
            new Vector2(-1f, 90f),
            ImGuiInputTextFlags.ReadOnly);
        if (!string.IsNullOrWhiteSpace(challengeCopy) && ImGui.Button("Copy challenge"))
            ImGui.SetClipboardText(challengeCopy);

        ImGui.InputTextMultiline(
            "Encrypted bootstrap DM",
            ref bootstrapCopy,
            4096,
            new Vector2(-1f, 90f));
        if (ImGui.Button("Import bootstrap"))
        {
            var copy = bootstrapCopy;
            bootstrapCopy = string.Empty;
            Start(async () =>
            {
                var result = await plugin.AutoPartyEndpointService
                    .ImportBootstrapCopyPasteAsync(copy)
                    .ConfigureAwait(false);
                return new UiOperationResult(result.SafeCode);
            });
        }
        ImGui.EndDisabled();

        ImGui.Separator();
        DadUi.Heading(
            "Pairing and sharing",
            "Pairing is bilateral, but initiation is one-sided. Only one DAD initiates a pairing. The peer waits for the pending notice; both owners then verify the same code and fingerprints and approve their own private character scope.");
        var pairingChallenge = plugin.AutoPartyEndpointService.LastPairingChallenge;
        var pairingChallengePeerIslandId = plugin.AutoPartyEndpointService.LastPairingChallengePeerIslandId;
        var pairingProgress = DadAutoPartyProgressProjection.Pairing(
            configuration,
            endpoint,
            pairingChallenge,
            plugin.AutoPartyEndpointService.LastPairingAttemptResult,
            pairingChallenge != null ? pairingChallengePeerIslandId : peerIslandId,
            status,
            DateTime.UtcNow,
            pairingChallengePeerIslandId,
            plugin.AutoPartyEndpointService.TransferSnapshot);
        DrawPairingProgressCard(pairingProgress);
        var localIslandId = configuration.RegisteredIslandId;
        ImGui.TextUnformatted($"This DAD island ID: {(string.IsNullOrWhiteSpace(localIslandId) ? "not registered" : localIslandId)}");
        ImGui.SameLine();
        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(localIslandId));
        if (ImGui.SmallButton("Copy island ID"))
            ImGui.SetClipboardText(localIslandId);
        ImGui.EndDisabled();

        var registrationReady = configuration.IsRegistrationActive &&
            endpoint.State == DadAutoPartyEndpointConnectionState.Ready;
        var pairingInitiationPending = pairingChallenge?.ExpiresAtUtc > DateTime.UtcNow ||
            configuration.PendingPairings.Any(item => item.ExpiresAtUtc > DateTime.UtcNow);
        ImGui.BeginDisabled(!registrationReady);
        ImGui.BeginDisabled(pairingInitiationPending);
        ImGui.InputText("Peer island ID", ref peerIslandId, 128);
        if (ImGui.Button("Initiate bilateral pairing by island ID"))
        {
            var requestedPeerIslandId = peerIslandId.Trim();
            Start(async () =>
            {
                var result = await plugin.AutoPartyEndpointService
                    .InitiatePairingAsync(requestedPeerIslandId)
                    .ConfigureAwait(false);
                return new UiOperationResult(result.SafeCode);
            });
        }
        ImGui.EndDisabled();
        if (pairingInitiationPending)
            ImGui.TextDisabled("A pairing attempt is already pending; do not initiate from the peer DAD.");

        DadUi.Heading(
            "Community Available",
            "Choose which Crew characters this home-guild Community availability may expose. The default is none.");
        DrawShareScopeSelector("Community scope", ref communityShareScope);
        if (communityShareScope == DadAutoPartyCrewShareScope.SpecificCharacters)
            DrawShareCharacterSelector("community", communityShareHandles, singleSelection: false);
        if (ImGui.Button("Save Community Available characters"))
        {
            var availableHandles = shareCandidates
                .Select(static candidate => candidate.Identity.OpaqueCharacterId)
                .ToHashSet(StringComparer.Ordinal);
            communityShareHandles.IntersectWith(availableHandles);
            var policy = DadAutoPartyCrewSharingRules.BuildCommunityPolicy(
                communityShareScope,
                shareCandidates,
                communityShareHandles,
                DateTime.UtcNow);
            policy.Revision = Math.Max(1, configuration.StandingSharePolicy.Revision + 1);
            status = plugin.AutoPartyEndpointService
                .SetStandingSharePolicy(communityShareScope, policy).SafeCode;
        }
        ImGui.TextDisabled("Community availability never widens an active private pairing policy.");
        var initiated = pairingChallenge != null && string.Equals(
            pairingProgress.PairingId,
            pairingChallenge.ChallengeId.ToString("D"),
            StringComparison.Ordinal)
                ? pairingChallenge
                : null;
        if (initiated != null && initiated.ExpiresAtUtc > DateTime.UtcNow)
        {
            ImGui.TextUnformatted($"Pairing initiated for: {pairingChallengePeerIslandId}");
            ImGui.TextUnformatted($"Local fingerprint: {initiated.PublicKeyFingerprint}");
            ImGui.TextUnformatted($"Confirmation code: {initiated.ConfirmationCode}");
            ImGui.TextDisabled($"Expires {initiated.ExpiresAtUtc:u}");
            if (ImGui.Button("Copy pairing code"))
                ImGui.SetClipboardText(initiated.ConfirmationCode);
        }
        var pending = configuration.PendingPairings.OrderBy(static item => item.ExpiresAtUtc).FirstOrDefault();
        var currentPairingSelectionId = pending?.PairingId ?? string.Empty;
        if (!string.Equals(pairingSelectionId, currentPairingSelectionId, StringComparison.Ordinal))
        {
            pairingSelectionId = currentPairingSelectionId;
            pairingShareHandles.Clear();
            pairingShareScope = DadAutoPartyCrewShareScope.SpecificCharacters;
        }
        if (pending == null)
        {
            ImGui.TextDisabled("No pairing approval is pending.");
        }
        else
        {
            ImGui.TextUnformatted($"Peer island: {pending.IslandId}");
            ImGui.TextUnformatted($"Peer fingerprint: {pending.PublicKeyFingerprint}");
            ImGui.TextDisabled($"Expires {pending.ExpiresAtUtc:u}");
            if (pending.LocalApproved)
            {
                ImGui.TextDisabled(pending.LocalApprovalRelayAcceptedAtUtc == null
                    ? "Local approval is saved; waiting for central to accept it."
                    : "Local approval is accepted; waiting for the peer owner.");
            }
            else
            {
                ImGui.InputText("Confirmed peer fingerprint", ref pairingFingerprint, 128);
                ImGui.InputText("Pairing code", ref pairingCode, 32);
                DrawShareScopeSelector("What this endpoint shares", ref pairingShareScope);
                if (pairingShareScope == DadAutoPartyCrewShareScope.SpecificCharacters)
                    DrawShareCharacterSelector(
                        "pairing",
                        pairingShareHandles,
                        singleSelection: false);
                var pairingSelectionValid = DadAutoPartyCrewSharingRules.TryBuildPrivatePolicy(
                    pairingShareScope,
                    shareCandidates,
                    pairingShareHandles,
                    DateTime.UtcNow,
                    out var policy);
                ImGui.BeginDisabled(!pairingSelectionValid);
                if (ImGui.Button("Approve pairing locally"))
                {
                    var decision = plugin.AutoPartyEndpointService.ApprovePairing(
                        Guid.Parse(pending.PairingId),
                        pairingFingerprint,
                        pairingCode,
                        policy);
                    status = decision.SafeCode;
                    pairingCode = string.Empty;
                    if (decision.Allowed)
                        pairingShareHandles.Clear();
                }
                ImGui.EndDisabled();
            }
        }

        var directory = plugin.AutoPartyService.GetDirectorySnapshot();
        foreach (var pairing in configuration.Pairings.OrderBy(static item => item.IslandId))
        {
            var pairingState = pairing.IsActive
                ? directory.OnlineIslandIds.Contains(pairing.IslandId) ? "active, online" : "active, offline"
                : "revoked";
            ImGui.TextUnformatted(
                $"{(string.IsNullOrWhiteSpace(pairing.LocalAlias) ? pairing.IslandId : pairing.LocalAlias)} ({pairing.IslandId}): {pairingState} | " +
                $"share {pairing.LocalSharePolicy.Mode}");
            if (pairing.IsActive)
            {
                var alias = pairing.LocalAlias;
                ImGui.SetNextItemWidth(180f);
                if (ImGui.InputText($"Paired DAD island alias##{pairing.PairingId}", ref alias, 48) &&
                    string.Equals(alias, pairing.LocalAlias, StringComparison.Ordinal))
                {
                    alias = pairing.LocalAlias;
                }
                ImGui.SameLine();
                if (ImGui.SmallButton($"Save alias##{pairing.PairingId}"))
                    status = plugin.AutoPartyEndpointService.SetPairingAlias(pairing.IslandId, alias).SafeCode;
            }
            ImGui.SameLine();
            if (pairing.IsActive && ImGui.SmallButton($"Deauthenticate##{pairing.PairingId}"))
                status = plugin.AutoPartyEndpointService.Deauthenticate(
                    pairing.IslandId,
                    "dad-owner-deauthenticated").SafeCode;
        }

        ImGui.Separator();
        DadUi.Heading("Private directory", "Only opaque handles and bounded display labels are retained.");
        ImGui.InputText("Search", ref directorySearch, 96);
        ImGui.SameLine();
        if (ImGui.Button("Search directory"))
        {
            var search = directorySearch;
            var include = includePromiscuous;
            Start(async () =>
            {
                var result = await plugin.AutoPartyEndpointService
                    .RequestDirectoryAsync(search, include)
                    .ConfigureAwait(false);
                return new UiOperationResult(result.SafeCode);
            });
        }
        ImGui.Checkbox("Include same-guild Community Available listings", ref includePromiscuous);
        DrawDirectory(configuration, directory, pairingInitiationPending);
        ImGui.EndDisabled();

        ImGui.Separator();
        DadUi.Heading(
            "Freeform party",
            "Select two to eight current LAN characters or authorized private listings. Selection order fixes Slot 1.");
        if (ImGui.Button("Refresh local characters"))
            RefreshLocalCandidates();
        DrawLocalCandidates();
        DrawFreeformSelectionOrder(directory);

        var formation = plugin.SchedulerService.GetCrewFormationStatus();
        if (DadAutoPartyFreeformRules.IsFreeformGroupId(formation.SourceGroupId))
            ImGui.TextWrapped($"Active AutoParty formation: {formation.Phase} | {formation.Summary}");
        ImGui.BeginDisabled(freeformSelectionOrder.Count is < 2 or > DadAutoPartyFreeformRules.MaximumParticipants);
        if (ImGui.Button("Create party"))
            status = CreateFreeformParty(directory);
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Disband party"))
            status = plugin.RequestAutoPartyFormationDisband();

        ImGui.Separator();
        if (ImGui.Button("Deregister this island"))
            status = plugin.AutoPartyEndpointService.BeginDeregistration().SafeCode;
        ImGui.SameLine();
        if (ImGui.Button("Owner Stop"))
        {
            plugin.AutoPartyService.StopAll("dad-owner-stop-button");
            status = "dad-owner-stop-active";
        }
        ImGui.TextWrapped($"Status: {status}");
    }

    private static void DrawRegistrationProgressCard(DadAutoPartyRegistrationProgress progress)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(1f, 1f, 1f, .03f));
        if (ImGui.BeginChild("dad-registration-progress-card", new Vector2(0f, 205f), true))
        {
            ImGui.TextUnformatted("Registration & mailbox");
            DrawChecklistRow(
                "Endpoint identity ready",
                progress.EndpointIdentityReady
                    ? DadAutoPartyProgressState.Complete
                    : progress.ActivationReceipt == DadAutoPartyProgressState.Blocked
                        ? DadAutoPartyProgressState.Blocked
                        : DadAutoPartyProgressState.Pending);
            DrawChecklistRow("Challenge generated", CompleteOrPending(progress.ChallengeGenerated));
            DrawChecklistRow(
                "Bootstrap imported and protected",
                CompleteOrPending(progress.BootstrapImportedAndProtected));
            DrawChecklistRow(
                progress.ActivationReceipt == DadAutoPartyProgressState.NotRequired
                    ? "Activation receipt not required - Active recovery"
                    : progress.RegistrationActive
                        ? "Registration already Active"
                        : "Activation receipt received",
                progress.ActivationReceipt);
            DrawChecklistRow("Registration Active", CompleteOrPending(progress.RegistrationActive));
            DrawChecklistRow("Current mailbox Ready", CompleteOrPending(progress.MailboxReady));
            ImGui.TextWrapped($"Next: {progress.NextAction}");
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    private static void DrawMailboxActivityCard(
        DadAutoPartyMailboxActivityProgress activity,
        DateTime? lastSuccessfulExchangeAtUtc)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(1f, 1f, 1f, .03f));
        if (ImGui.BeginChild("dad-mailbox-activity-card", new Vector2(0f, 145f), true))
        {
            ImGui.TextUnformatted("Mailbox activity");
            ImGui.TextUnformatted($"Payload: {activity.FriendlyPayloadName}");
            if (!activity.Idle)
            {
                ImGui.TextUnformatted(
                    $"Accepted fragments: {activity.AcceptedFragmentCount} / {activity.TotalFragmentCount}");
                ImGui.TextUnformatted(
                    $"Current fragment: {activity.CurrentFragmentNumber} / {activity.TotalFragmentCount} - " +
                    (activity.AwaitingCentralAcknowledgement
                        ? "waiting for central acknowledgement"
                        : "ready to publish"));
            }
            ImGui.TextDisabled(
                $"Relay: pending {activity.RelayPendingCount}, awaiting semantic receipt {activity.RelayAwaitingCount}.");
            if (lastSuccessfulExchangeAtUtc.HasValue)
                ImGui.TextDisabled($"Last mailbox exchange: {lastSuccessfulExchangeAtUtc.Value:u}");
            ImGui.TextDisabled($"Raw safe code: {activity.RawSafeCode}");
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    private static void DrawPairingProgressCard(DadAutoPartyPairingProgress progress)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(1f, 1f, 1f, .03f));
        if (ImGui.BeginChild("dad-pairing-progress-card", new Vector2(0f, 250f), true))
        {
            ImGui.TextUnformatted("Island pairing");
            if (!string.IsNullOrWhiteSpace(progress.PairingId))
                ImGui.TextDisabled($"Pairing ID: {progress.PairingId}");
            DrawChecklistRow("Registration Active prerequisite", CompleteOrPending(progress.RegistrationActive));
            DrawChecklistRow("Current mailbox Ready prerequisite", CompleteOrPending(progress.MailboxReady));
            DrawChecklistRow("Peer island ID validated", progress.PeerIdValidated);
            DrawChecklistRow(
                progress.NoticeQueued == DadAutoPartyProgressState.NotRequired
                    ? "Notice queued - not required, peer initiated"
                    : "Notice queued",
                progress.NoticeQueued);
            DrawChecklistRow(
                "Notice accepted and pending pairing received",
                CompleteOrPending(progress.NoticeAcceptedAndPendingPairingReceived));
            DrawChecklistRow(
                "Local fingerprint, code, and share approval saved",
                CompleteOrPending(progress.LocalApprovalSaved));
            DrawChecklistRow(
                "Local approval accepted by central",
                CompleteOrPending(progress.LocalApprovalAcceptedByCentral));
            DrawChecklistRow("Peer approval received", CompleteOrPending(progress.PeerApprovalReceived));
            DrawChecklistRow("Pairing Active", CompleteOrPending(progress.PairingActive));
            if (progress.ExpiredOrRejected)
                DrawChecklistRow("Attempt expired or rejected", DadAutoPartyProgressState.Blocked);
            ImGui.TextWrapped($"Next: {progress.NextAction}");
            ImGui.TextDisabled($"Safe code: {progress.SafeCode}");
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    private static DadAutoPartyProgressState CompleteOrPending(bool complete) =>
        complete ? DadAutoPartyProgressState.Complete : DadAutoPartyProgressState.Pending;

    private static void DrawChecklistRow(string label, DadAutoPartyProgressState state)
    {
        var marker = state switch
        {
            DadAutoPartyProgressState.Complete => "[x]",
            DadAutoPartyProgressState.NotRequired => "[-]",
            DadAutoPartyProgressState.Blocked => "[!]",
            _ => "[ ]",
        };
        var color = state switch
        {
            DadAutoPartyProgressState.Complete => new Vector4(.45f, .9f, .55f, 1f),
            DadAutoPartyProgressState.NotRequired => new Vector4(.55f, .75f, .95f, 1f),
            DadAutoPartyProgressState.Blocked => new Vector4(1f, .45f, .3f, 1f),
            _ => ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled],
        };
        ImGui.TextColored(color, $"{marker} {label}");
    }

    private void DrawDirectory(
        DadAutoPartyConfiguration configuration,
        DadAutoPartyDirectorySnapshot directory,
        bool pairingInitiationPending)
    {
        var visible = directory.Listings
            .Where(item => string.IsNullOrWhiteSpace(directorySearch) ||
                           item.DisplayLabel.Contains(directorySearch, StringComparison.OrdinalIgnoreCase))
            .Take(128)
            .ToList();
        if (visible.Count == 0)
        {
            ImGui.TextDisabled("No matching private listings are cached.");
            return;
        }

        foreach (var island in visible.GroupBy(static listing => listing.SharingIslandId, StringComparer.Ordinal)
                     .OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            var pairing = configuration.Pairings.FirstOrDefault(item =>
                item.IsActive && string.Equals(item.IslandId, island.Key, StringComparison.Ordinal));
            ImGui.PushID(island.Key);
            ImGui.TextUnformatted($"Island {island.Key} | {(pairing == null ? "not paired" : "paired")}");
            if (pairing == null)
            {
                ImGui.SameLine();
                ImGui.BeginDisabled(pairingInitiationPending);
                if (ImGui.SmallButton("Initiate pairing"))
                {
                    var peerIslandId = island.Key;
                    Start(async () =>
                    {
                        var result = await plugin.AutoPartyEndpointService
                            .InitiatePairingAsync(peerIslandId)
                            .ConfigureAwait(false);
                        return new UiOperationResult(result.SafeCode);
                    });
                }
                ImGui.EndDisabled();
            }

            foreach (var listing in island.OrderBy(static item => item.DisplayLabel, StringComparer.OrdinalIgnoreCase))
            {
                ImGui.PushID(listing.ListingId);
                var jobs = ParsePermittedJobs(listing);
                var formationAllowed = listing.AllowedActivityIds.Contains(
                    DadAutoPartyFreeformRules.FormationActivityId,
                    StringComparer.OrdinalIgnoreCase);
                var ownerAvailable = !string.IsNullOrWhiteSpace(listing.OwnerId);
                var pairedRouteAvailable = ownerAvailable && pairing != null &&
                    string.Equals(pairing.OwnerId, listing.OwnerId, StringComparison.Ordinal);
                var transientRouteAvailable = ownerAvailable &&
                    listing.EffectiveShareMode == DadAutoPartyCharacterShareMode.CharacterList &&
                    listing.HasCurrentTransientRoute;
                var routeAvailable = pairedRouteAvailable || transientRouteAvailable;
                var selectionKey = RemoteSelectionKey(listing);
                var selected = freeformSelectionOrder.Contains(selectionKey, StringComparer.Ordinal);
                ImGui.BeginDisabled(!formationAllowed || jobs.Count == 0 || !routeAvailable);
                if (ImGui.Checkbox($"{listing.DisplayLabel}##select", ref selected))
                    SetFreeformSelection(selectionKey, selected);
                ImGui.SameLine();
                ImGui.TextDisabled(
                    $"jobs {string.Join(", ", jobs)} | activities {string.Join(", ", listing.AllowedActivityIds)}");
                if (jobs.Count > 0)
                {
                    if (!remoteRequestedJobs.TryGetValue(selectionKey, out var requestedJob) || !jobs.Contains(requestedJob))
                        requestedJob = jobs[0];
                    var jobIndex = Math.Max(0, jobs.IndexOf(requestedJob));
                    var jobLabels = jobs.Select(static job => job.ToString()).ToArray();
                    ImGui.SetNextItemWidth(160f);
                    if (ImGui.Combo("Requested job", ref jobIndex, jobLabels, jobLabels.Length))
                        requestedJob = jobs[jobIndex];
                    remoteRequestedJobs[selectionKey] = requestedJob;
                }
                ImGui.EndDisabled();
                if (!formationAllowed)
                    ImGui.TextDisabled("This listing does not permit freeform party formation.");
                else if (!routeAvailable)
                    ImGui.TextDisabled("Pair this island or obtain a same-guild requester attestation before selection.");
                ImGui.PopID();
            }
            ImGui.PopID();
        }
    }

    private void DrawLocalCandidates()
    {
        if (localCandidates.Count == 0)
        {
            ImGui.TextDisabled("No live, ready local/LAN characters with a current combat job are available.");
            return;
        }

        ImGui.TextUnformatted("Current local/LAN characters");
        foreach (var character in localCandidates)
        {
            var selectionKey = LocalSelectionKey(character);
            var selected = freeformSelectionOrder.Contains(selectionKey, StringComparer.Ordinal);
            ImGui.PushID(selectionKey);
            if (ImGui.Checkbox(
                    $"{character.CharacterName}@{character.WorldName} ({character.CurrentJobAbbrev})##select",
                    ref selected))
                SetFreeformSelection(selectionKey, selected);
            ImGui.PopID();
        }
    }

    private void DrawFreeformSelectionOrder(DadAutoPartyDirectorySnapshot directory)
    {
        ImGui.TextUnformatted(
            $"Selected party order ({freeformSelectionOrder.Count}/{DadAutoPartyFreeformRules.MaximumParticipants}); first row is Slot 1.");
        for (var index = 0; index < freeformSelectionOrder.Count; index++)
        {
            var key = freeformSelectionOrder[index];
            ImGui.PushID(key);
            ImGui.TextUnformatted($"Slot {index + 1}: {ResolveSelectionLabel(key, directory)}");
            ImGui.SameLine();
            ImGui.BeginDisabled(index == 0);
            if (ImGui.SmallButton("Up"))
                (freeformSelectionOrder[index - 1], freeformSelectionOrder[index]) =
                    (freeformSelectionOrder[index], freeformSelectionOrder[index - 1]);
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(index == freeformSelectionOrder.Count - 1);
            if (ImGui.SmallButton("Down"))
                (freeformSelectionOrder[index + 1], freeformSelectionOrder[index]) =
                    (freeformSelectionOrder[index], freeformSelectionOrder[index + 1]);
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.SmallButton("Remove"))
            {
                freeformSelectionOrder.RemoveAt(index);
                ImGui.PopID();
                break;
            }
            ImGui.PopID();
        }
    }

    private string CreateFreeformParty(DadAutoPartyDirectorySnapshot directory)
    {
        var participants = new List<DadAutoPartyFreeformParticipant>(freeformSelectionOrder.Count);
        foreach (var key in freeformSelectionOrder)
        {
            var local = localCandidates.SingleOrDefault(character =>
                string.Equals(LocalSelectionKey(character), key, StringComparison.Ordinal));
            if (local != null)
            {
                if (!local.CurrentJobId.HasValue)
                    return "dad-autoparty-freeform-local-job-missing";
                participants.Add(new DadAutoPartyFreeformParticipant
                {
                    SelectionKey = key,
                    DisplayLabel = $"{local.CharacterName}@{local.WorldName}",
                    Kind = DadAutoPartyFreeformParticipantKind.Local,
                    AccountKey = DadRosterIdentity.ResolveAccountKey(local.AccountId, local.AccountAlias),
                    CharacterKey = new DadCharacterKey(local.CharacterKey),
                    ContentId = local.ContentId,
                    RequestedJobId = local.CurrentJobId.Value,
                });
                continue;
            }

            var listing = directory.Listings.SingleOrDefault(item =>
                string.Equals(RemoteSelectionKey(item), key, StringComparison.Ordinal));
            if (listing == null || listing.ExpiresAtUtc <= DateTime.UtcNow || !listing.Available)
                return "dad-autoparty-freeform-listing-stale";
            if (!listing.AllowedActivityIds.Contains(
                    DadAutoPartyFreeformRules.FormationActivityId,
                    StringComparer.OrdinalIgnoreCase))
                return "dad-autoparty-freeform-activity-denied";
            var jobs = ParsePermittedJobs(listing);
            if (!remoteRequestedJobs.TryGetValue(key, out var jobId) || !jobs.Contains(jobId))
                return "dad-autoparty-freeform-job-denied";
            var pairing = plugin.Configuration.AutoParty.Pairings.SingleOrDefault(item =>
                item.IsActive && string.Equals(item.IslandId, listing.SharingIslandId, StringComparison.Ordinal));
            if (string.IsNullOrWhiteSpace(listing.OwnerId))
                return "dad-autoparty-freeform-route-not-authorized";
            var pairedRouteAvailable = pairing != null &&
                string.Equals(pairing.OwnerId, listing.OwnerId, StringComparison.Ordinal);
            var transientRouteAvailable =
                listing.EffectiveShareMode == DadAutoPartyCharacterShareMode.CharacterList &&
                listing.HasCurrentTransientRoute;
            if (!pairedRouteAvailable && !transientRouteAvailable)
                return "dad-autoparty-freeform-route-not-authorized";
            participants.Add(new DadAutoPartyFreeformParticipant
            {
                SelectionKey = key,
                DisplayLabel = listing.DisplayLabel,
                Kind = DadAutoPartyFreeformParticipantKind.RegisteredIsland,
                OwnerId = listing.OwnerId,
                IslandId = listing.SharingIslandId,
                OpaqueCharacterId = listing.OpaqueCharacterId,
                RequestedJobId = jobId,
            });
        }

        if (!DadAutoPartyFreeformRules.TryBuild(participants, out var formation, out var blocker))
            return blocker;
        return plugin.StartAutoPartyFreeformFormation(formation).Summary;
    }

    private void SetFreeformSelection(string key, bool selected)
    {
        if (!selected)
        {
            freeformSelectionOrder.RemoveAll(item => string.Equals(item, key, StringComparison.Ordinal));
            return;
        }
        if (freeformSelectionOrder.Contains(key, StringComparer.Ordinal))
            return;
        if (freeformSelectionOrder.Count >= DadAutoPartyFreeformRules.MaximumParticipants)
        {
            status = "dad-autoparty-freeform-selection-full";
            return;
        }
        freeformSelectionOrder.Add(key);
    }

    private void RefreshLocalCandidates()
    {
        localShareCandidates = plugin.BuildPlannerPool().Characters
            .Where(static character => character.ContentId != 0 &&
                                       !string.IsNullOrWhiteSpace(character.CharacterKey))
            .DistinctBy(DadRosterIdentity.BuildKey, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static character => character.CharacterName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static character => character.WorldName, StringComparer.OrdinalIgnoreCase)
            .Take(256)
            .Select(static character => character.Clone())
            .ToList();
        localCandidates = localShareCandidates
            .Where(static character => character.IsLiveConnected &&
                                       character.CurrentJobId.HasValue &&
                                       DadRosterCharacterMerge.IsCombatJob(character.CurrentJobId.Value))
            .Take(64)
            .Select(static character => character.Clone())
            .ToList();
        var currentKeys = localCandidates.Select(LocalSelectionKey).ToHashSet(StringComparer.Ordinal);
        freeformSelectionOrder.RemoveAll(key =>
            key.StartsWith("local:", StringComparison.Ordinal) && !currentKeys.Contains(key));
        shareCandidates = plugin.GetCurrentAutoPartyCrewCandidates().ToList();
    }

    private static void DrawShareScopeSelector(
        string label,
        ref DadAutoPartyCrewShareScope scope)
    {
        var selected = scope switch
        {
            DadAutoPartyCrewShareScope.CurrentCharacter => 0,
            DadAutoPartyCrewShareScope.SpecificCharacters => 1,
            DadAutoPartyCrewShareScope.AllCharacters => 2,
            _ => 1,
        };
        if (ImGui.Combo(label, ref selected, "This character\0Specific characters\0All characters\0"))
        {
            scope = selected switch
            {
                0 => DadAutoPartyCrewShareScope.CurrentCharacter,
                2 => DadAutoPartyCrewShareScope.AllCharacters,
                _ => DadAutoPartyCrewShareScope.SpecificCharacters,
            };
        }
    }

    private void DrawShareCharacterSelector(
        string selectorId,
        HashSet<string> selectedHandles,
        bool singleSelection)
    {
        var candidates = shareCandidates
            .OrderBy(ResolveLocalShareLabel, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidates.Count == 0)
        {
            ImGui.TextDisabled("No active curated Crew characters are available.");
            return;
        }

        foreach (var candidate in candidates)
        {
            var selected = selectedHandles.Contains(candidate.Identity.OpaqueCharacterId);
            if (!ImGui.Checkbox(
                    $"{ResolveLocalShareLabel(candidate)}##{selectorId}-{candidate.Identity.OpaqueCharacterId}",
                    ref selected))
                continue;
            if (!selected)
            {
                selectedHandles.Remove(candidate.Identity.OpaqueCharacterId);
                continue;
            }
            if (singleSelection)
                selectedHandles.Clear();
            selectedHandles.Add(candidate.Identity.OpaqueCharacterId);
        }
    }

    private static string ResolveLocalShareLabel(DadAutoPartyCrewCandidate candidate)
        => $"{candidate.Character.CharacterName}@{candidate.Character.WorldName}";

    private static List<uint> ParsePermittedJobs(DadAutoPartyListing listing)
        => listing.AllowedJobIds
            .Select(static value => uint.TryParse(value, out var jobId) ? jobId : 0)
            .Where(DadRosterCharacterMerge.IsCombatJob)
            .Distinct()
            .Order()
            .ToList();

    private string ResolveSelectionLabel(string key, DadAutoPartyDirectorySnapshot directory)
    {
        var local = localCandidates.SingleOrDefault(character =>
            string.Equals(LocalSelectionKey(character), key, StringComparison.Ordinal));
        if (local != null)
            return $"{local.CharacterName}@{local.WorldName}";
        return directory.Listings.SingleOrDefault(listing =>
                   string.Equals(RemoteSelectionKey(listing), key, StringComparison.Ordinal))?.DisplayLabel
               ?? "Unavailable selection";
    }

    private static string LocalSelectionKey(DadAcquiredCharacter character)
        => $"local:{DadRosterIdentity.BuildKey(character)}";

    private static string RemoteSelectionKey(DadAutoPartyListing listing)
        => $"remote:{listing.ListingId}";

    private void Start(Func<Task<UiOperationResult>> operation)
    {
        if (operationTask is { IsCompleted: false })
        {
            status = "dad-autoparty-operation-already-running";
            return;
        }
        operationTask = operation();
        status = "dad-autoparty-operation-running";
    }

    private void ObserveTask()
    {
        if (operationTask == null || !operationTask.IsCompleted)
            return;
        if (operationTask.IsCompletedSuccessfully)
        {
            var result = operationTask.Result;
            status = result.SafeCode;
            if (!string.IsNullOrWhiteSpace(result.ChallengeCopy))
                challengeCopy = result.ChallengeCopy;
        }
        else
        {
            status = operationTask.IsCanceled
                ? "dad-autoparty-operation-cancelled"
                : "dad-autoparty-operation-failed";
        }
        if (string.Equals(status, "dad-autoparty-purged", StringComparison.Ordinal))
        {
            challengeCopy = string.Empty;
            bootstrapCopy = string.Empty;
            forgetLostIdentityConfirmed = false;
        }
        endpointAlias = plugin.Configuration.AutoParty.EndpointAlias;
        operationTask = null;
    }
}
