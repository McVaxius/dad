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
    private string pairingFingerprint = string.Empty;
    private string pairingCode = string.Empty;
    private string shareHandles = string.Empty;
    private DadAutoPartyCharacterShareMode shareMode = DadAutoPartyCharacterShareMode.SpecificCharacter;
    private bool includePromiscuous = true;
    private string initiatedPeerIslandId = string.Empty;
    private List<DadAcquiredCharacter> localCandidates = [];
    private readonly List<string> freeformSelectionOrder = [];
    private readonly Dictionary<string, uint> remoteRequestedJobs = new(StringComparer.Ordinal);
    private string status = "AutoParty is disabled.";
    private Task<UiOperationResult>? operationTask;

    private sealed record UiOperationResult(
        string SafeCode,
        string ChallengeCopy = "",
        string InitiatedPeerIslandId = "");

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
        pairingCode = string.Empty;
        RefreshLocalCandidates();
    }

    public override void Draw()
    {
        ObserveTask();
        var configuration = plugin.Configuration.AutoParty;
        var endpoint = plugin.AutoPartyEndpointService.Snapshot;

        DadUi.Heading(
            "Central AutoParty bridge",
            "One centrally operated Discord bot; this DAD uses only its encrypted webhook mailbox.");
        ImGui.TextWrapped(
            "No bot token or Application ID is entered here. Registration is one encrypted copy/paste; " +
            "pairing and fingerprint approval remain local.");
        if (!string.IsNullOrWhiteSpace(configuration.LegacyDiscordTokenCleanupWarning))
            ImGui.TextColored(
                new Vector4(1f, .7f, .2f, 1f),
                $"Security cleanup warning: {configuration.LegacyDiscordTokenCleanupWarning}. DAD will retry.");
        ImGui.Separator();

        var enabled = configuration.Enabled;
        if (ImGui.Checkbox("Enable AutoParty", ref enabled))
            plugin.AutoPartyService.SetEnabled(enabled);
        ImGui.SameLine();
        ImGui.TextDisabled(
            $"Registration {configuration.RegistrationState} | connection {endpoint.State} | {endpoint.SafeCode}");
        if (endpoint.LastSuccessfulExchangeAtUtc.HasValue)
            ImGui.TextDisabled($"Last mailbox exchange: {endpoint.LastSuccessfulExchangeAtUtc.Value:u}");
        ImGui.TextDisabled(
            $"Mailbox queues: outbound {endpoint.PendingOutboundCount}, acknowledgements {endpoint.PendingAcknowledgementCount}, " +
            $"inbound {endpoint.BufferedInboundCount}, epoch {endpoint.EpochGeneration}.");

        ImGui.SetNextItemWidth(300f);
        ImGui.InputText("DAD endpoint alias", ref endpointAlias, 48);
        if (ImGui.Button("Generate registration challenge"))
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
            "Encrypted bootstrap response",
            ref bootstrapCopy,
            4096,
            new Vector2(-1f, 90f),
            ImGuiInputTextFlags.Password);
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

        ImGui.Separator();
        DadUi.Heading("Pairing and sharing", "Both endpoints approve the same transcript, code, and fingerprints.");
        var standingShareEnabled = configuration.StandingSharePolicy is
        {
            Enabled: true,
            Mode: DadAutoPartyCharacterShareMode.PromiscuousAllSameGuild,
        };
        if (ImGui.Checkbox("Share all local characters with attested same-guild requesters", ref standingShareEnabled))
        {
            status = plugin.AutoPartyEndpointService.SetStandingSharePolicy(new DadAutoPartySharePolicy
            {
                Mode = DadAutoPartyCharacterShareMode.PromiscuousAllSameGuild,
                Enabled = standingShareEnabled,
                Revision = Math.Max(1, configuration.StandingSharePolicy.Revision + 1),
                UpdatedAtUtc = DateTime.UtcNow,
            }).SafeCode;
        }
        ImGui.TextDisabled("This endpoint-wide listing policy is independent of every pairing policy.");
        var initiated = plugin.AutoPartyEndpointService.LastPairingChallenge;
        if (initiated != null && initiated.ExpiresAtUtc > DateTime.UtcNow)
        {
            ImGui.TextUnformatted($"Pairing initiated for: {initiatedPeerIslandId}");
            ImGui.TextUnformatted($"Local fingerprint: {initiated.PublicKeyFingerprint}");
            ImGui.TextUnformatted($"Confirmation code: {initiated.ConfirmationCode}");
            ImGui.TextDisabled($"Expires {initiated.ExpiresAtUtc:u}");
            if (ImGui.Button("Copy pairing code"))
                ImGui.SetClipboardText(initiated.ConfirmationCode);
        }
        var pending = configuration.PendingPairings.OrderBy(static item => item.ExpiresAtUtc).FirstOrDefault();
        if (pending == null)
        {
            ImGui.TextDisabled("No pairing approval is pending.");
        }
        else
        {
            ImGui.TextUnformatted($"Peer island: {pending.IslandId}");
            ImGui.TextUnformatted($"Peer fingerprint: {pending.PublicKeyFingerprint}");
            ImGui.TextDisabled($"Expires {pending.ExpiresAtUtc:u}");
            ImGui.InputText("Confirmed peer fingerprint", ref pairingFingerprint, 128);
            ImGui.InputText("Pairing code", ref pairingCode, 32, ImGuiInputTextFlags.Password);
            var selectedMode = (int)shareMode - 1;
            if (ImGui.Combo(
                    "What this endpoint shares",
                    ref selectedMode,
                    "Specific character\0Character list\0All characters for peer\0Promiscuous all (same guild only)\0"))
                shareMode = (DadAutoPartyCharacterShareMode)(selectedMode + 1);
            if (shareMode is DadAutoPartyCharacterShareMode.SpecificCharacter or
                DadAutoPartyCharacterShareMode.CharacterList)
                ImGui.InputText("Opaque handles (comma-separated)", ref shareHandles, 2048);
            if (ImGui.Button("Approve pairing locally"))
            {
                var policy = BuildSharePolicy();
                status = plugin.AutoPartyEndpointService.ApprovePairing(
                    Guid.Parse(pending.PairingId),
                    pairingFingerprint,
                    pairingCode,
                    policy).SafeCode;
                pairingCode = string.Empty;
            }
        }

        foreach (var pairing in configuration.Pairings.OrderBy(static item => item.IslandId))
        {
            ImGui.TextUnformatted(
                $"{pairing.IslandId}: {(pairing.IsActive ? "active" : "revoked")} | " +
                $"share {pairing.LocalSharePolicy.Mode}");
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
        ImGui.Checkbox("Include same-guild promiscuous listings", ref includePromiscuous);
        var directory = plugin.AutoPartyService.GetDirectorySnapshot();
        DrawDirectory(configuration, directory);

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

    private void DrawDirectory(
        DadAutoPartyConfiguration configuration,
        DadAutoPartyDirectorySnapshot directory)
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
                if (ImGui.SmallButton("Initiate pairing"))
                {
                    var peerIslandId = island.Key;
                    Start(async () =>
                    {
                        var result = await plugin.AutoPartyEndpointService
                            .InitiatePairingAsync(peerIslandId)
                            .ConfigureAwait(false);
                        return new UiOperationResult(
                            result.SafeCode,
                            InitiatedPeerIslandId: result.Allowed ? peerIslandId : string.Empty);
                    });
                }
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
                    listing.EffectiveShareMode == DadAutoPartyCharacterShareMode.PromiscuousAllSameGuild &&
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
                listing.EffectiveShareMode == DadAutoPartyCharacterShareMode.PromiscuousAllSameGuild &&
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
        localCandidates = plugin.BuildPlannerPool().Characters
            .Where(static character => character.IsLiveConnected &&
                                       character.ContentId != 0 &&
                                       !string.IsNullOrWhiteSpace(character.CharacterKey) &&
                                       character.CurrentJobId.HasValue &&
                                       DadRosterCharacterMerge.IsCombatJob(character.CurrentJobId.Value))
            .DistinctBy(DadRosterIdentity.BuildKey, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static character => character.CharacterName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static character => character.WorldName, StringComparer.OrdinalIgnoreCase)
            .Take(64)
            .Select(static character => character.Clone())
            .ToList();
        var currentKeys = localCandidates.Select(LocalSelectionKey).ToHashSet(StringComparer.Ordinal);
        freeformSelectionOrder.RemoveAll(key =>
            key.StartsWith("local:", StringComparison.Ordinal) && !currentKeys.Contains(key));
    }

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

    private DadAutoPartySharePolicy BuildSharePolicy() => new DadAutoPartySharePolicy
    {
        Mode = shareMode,
        CharacterHandles = shareMode is DadAutoPartyCharacterShareMode.AllCharactersForPeer or
            DadAutoPartyCharacterShareMode.PromiscuousAllSameGuild
                ? []
                : shareHandles
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .ToList(),
        Enabled = true,
        Revision = DateTime.UtcNow.Ticks,
        UpdatedAtUtc = DateTime.UtcNow,
    }.Normalize();

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
            if (!string.IsNullOrWhiteSpace(result.InitiatedPeerIslandId))
                initiatedPeerIslandId = result.InitiatedPeerIslandId;
        }
        else
        {
            status = operationTask.IsCanceled
                ? "dad-autoparty-operation-cancelled"
                : "dad-autoparty-operation-failed";
        }
        endpointAlias = plugin.Configuration.AutoParty.EndpointAlias;
        operationTask = null;
    }
}
