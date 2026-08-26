using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using dad.Models;

namespace dad.Services;

public sealed class DadCharacterIntelligenceService
{
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions PoolJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ConfigManager configManager;
    private readonly DadXadbClient xadbClient;
    private readonly DadTransportService transportService;
    private readonly IPluginLog log;
    private readonly DadSemanticRevisionTracker<global::dad.DadPlannerRosterSemantic>
        plannerSemanticRevisionTracker = new();
    // B5: fires once per distinct (active job, level) change of the local character, never on first capture.
    private readonly DadLocalLevelChangeDetector levelChangeDetector = new();
    private DateTime nextAutoRefreshUtc = DateTime.MinValue;
    private string lastRuntimeIdentitySignature = string.Empty;
    private bool runtimeIdentityInitialized;

    public DadCharacterIntelligenceService(
        ConfigManager configManager,
        DadXadbClient xadbClient,
        DadTransportService transportService,
        IPluginLog log)
    {
        this.configManager = configManager;
        this.xadbClient = xadbClient;
        this.transportService = transportService;
        this.log = log;

        CurrentPool = new DadCharacterPool
        {
            PeerTransport = transportService.CurrentTransport,
        };
        AdvancePlannerSemanticRevision();
    }

    public DadCharacterPool CurrentPool { get; private set; }
    internal long PlannerSemanticRevision => plannerSemanticRevisionTracker.Revision;

    public void Update()
    {
        var runtimeIdentitySignature = CaptureRuntimeIdentitySignature();
        var identityChanged = runtimeIdentityInitialized &&
                              !string.Equals(
                                  lastRuntimeIdentitySignature,
                                  runtimeIdentitySignature,
                                  StringComparison.Ordinal);
        lastRuntimeIdentitySignature = runtimeIdentitySignature;
        runtimeIdentityInitialized = true;

        if (!identityChanged && DateTime.UtcNow < nextAutoRefreshUtc)
            return;

        RefreshLocalCharacterPool("framework", logRefresh: false);
    }

    public DadCharacterPool RefreshLocalCharacterPool(string trigger = "manual", bool logRefresh = true)
    {
        lastRuntimeIdentitySignature = CaptureRuntimeIdentitySignature();
        runtimeIdentityInitialized = true;
        var xadbStatus = xadbClient.Inspect();
        CurrentPool = BuildPool(xadbStatus, transportService.CurrentTransport);
        AdvancePlannerSemanticRevision();
        nextAutoRefreshUtc = DateTime.UtcNow + AutoRefreshInterval;

        if (logRefresh)
        {
            log.Information(
                "[dad] Refreshed character pool via {Trigger}: {RowCount} row(s), XADB {XadbAvailability}, peers {PeerAvailability}.",
                trigger,
                CurrentPool.Characters.Count,
                CurrentPool.XadbStatus.Availability,
                CurrentPool.PeerTransport.Availability);
        }

        return CurrentPool;
    }

    private static string CaptureRuntimeIdentitySignature()
    {
        try
        {
            if (!Plugin.ClientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer == null)
                return "OFFLINE";

            var player = Plugin.ObjectTable.LocalPlayer;
            return string.Join(
                '|',
                Plugin.PlayerState.ContentId,
                player.Name.ToString().Trim().ToUpperInvariant(),
                player.HomeWorld.RowId);
        }
        catch
        {
            return "UNAVAILABLE";
        }
    }

    public DadCharacterPool SaveLocalToXadb()
    {
        var xadbStatus = xadbClient.Save();
        CurrentPool = BuildPool(xadbStatus, transportService.CurrentTransport);
        AdvancePlannerSemanticRevision();
        nextAutoRefreshUtc = DateTime.UtcNow + AutoRefreshInterval;
        log.Information(
            "[dad] Saved local snapshot to XADB: {RowCount} row(s), status {Status}.",
            CurrentPool.Characters.Count,
            CurrentPool.XadbStatus.LastStatus);
        return CurrentPool;
    }

    public DadCharacterPool RequestPeerSnapshots()
    {
        var request = new DadPeerSnapshotRequest();
        var peerTransport = transportService.RequestSnapshots(request);
        var xadbStatus = CurrentPool.XadbStatus.IsReady ? CurrentPool.XadbStatus : xadbClient.Inspect();
        CurrentPool = BuildPool(xadbStatus, peerTransport);
        AdvancePlannerSemanticRevision();
        nextAutoRefreshUtc = DateTime.UtcNow + AutoRefreshInterval;
        return CurrentPool;
    }

    private void AdvancePlannerSemanticRevision()
        => plannerSemanticRevisionTracker.Observe(new global::dad.DadPlannerRosterSemantic(
            CurrentPool.XadbStatus.IsReady,
            CurrentPool.XadbStatus.SnapshotVersion,
            new DadOrderedSemantic<global::dad.DadPlannerCharacterSemantic>(CurrentPool.Characters
                .OrderBy(static character => character.CharacterKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static character => character.ContentId)
                .ThenBy(static character => character.AccountId, StringComparer.OrdinalIgnoreCase)
                .Select(static character => new global::dad.DadPlannerCharacterSemantic(
                    character.CharacterKey,
                    character.ContentId,
                    character.CharacterName,
                    character.WorldId,
                    character.WorldName,
                    character.DataCenterId,
                    character.DataCenterName,
                    character.AccountId,
                    character.AccountAlias,
                    character.Source,
                    character.Freshness,
                    character.CurrentJobId,
                    character.CurrentJobAbbrev,
                    character.CurrentLevel,
                    new DadOrderedSemantic<KeyValuePair<uint, int>>(character.JobLevels
                        .OrderBy(static job => job.Key)),
                    character.TerritoryId,
                    character.TerritoryName,
                    character.PartyRosterCount,
                    character.VisiblePartyCount,
                    character.Readiness,
                    new DadOrderedSemantic<string>(character.Blockers),
                    character.SnapshotQuality,
                    character.SnapshotVersion,
                    character.XadbReady,
                    character.RosterVisibility,
                    character.NeedsRosterUpdate,
                    character.MapEligible)))));

    public string GetCharacterPoolJson()
        => JsonSerializer.Serialize(CurrentPool, PoolJsonOptions);

    private DadCharacterPool BuildPool(DadXadbStatus xadbStatus, DadPeerTransportSnapshot peerTransport)
    {
        var pool = new DadCharacterPool
        {
            LastUpdatedUtc = DateTime.UtcNow,
            XadbStatus = xadbStatus,
            PeerTransport = peerTransport,
        };

        var characters = new List<DadAcquiredCharacter>();
        var localCharacter = CaptureLocalCharacter();

        if (localCharacter != null)
        {
            DadCharacterXadbMergeRules.Merge(localCharacter, xadbStatus);
            UpsertCharacter(characters, localCharacter);
        }
        else if (HasXadbIdentity(xadbStatus))
        {
            UpsertCharacter(characters, BuildXadbOnlyCharacter(xadbStatus));
        }

        foreach (var response in peerTransport.LastResponses)
        {
            if (response.Participant.IsLocalClient ||
                string.Equals(
                    response.Participant.WorkerSessionId.Value,
                    peerTransport.LocalWorkerSessionId.Value,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var character = response.Participant.Character.Clone();
            character.Source = DadCharacterSource.PeerRuntime;
            character.Freshness = ResolvePeerFreshness(response);
            var runtimeProjection = DadPeerRuntimeProjectionRules.Evaluate(response.Participant, character);
            character.Readiness = runtimeProjection.Readiness;
            character.Blockers = runtimeProjection.Blockers;

            // StatusText and response warnings describe what the peer said over time. They stay on
            // the transport response for diagnostics and never become planner readiness blockers.

            UpsertCharacter(characters, character);
        }

        pool.Characters = characters
            .OrderByDescending(static character => character.Source == DadCharacterSource.LocalRuntime)
            .ThenBy(static character => character.Source)
            .ThenBy(static character => character.CharacterKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

        pool.LastSummary = pool.Characters.Count == 0
            ? "No Dad characters captured yet."
            : $"{pool.Characters.Count} Dad character row(s) | XADB {xadbStatus.Availability} | peers {peerTransport.ConnectedPeerCount}.";

        return pool;
    }

    private DadAcquiredCharacter? CaptureLocalCharacter()
    {
        try
        {
            if (!Plugin.ClientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer == null)
                return null;

            var player = Plugin.ObjectTable.LocalPlayer;
            var now = DateTime.UtcNow;
            var name = player.Name.ToString();
            var worldName = player.HomeWorld.Value.Name.ToString();
            var worldId = (uint)player.HomeWorld.RowId;
            var currentJobId = player.ClassJob.IsValid ? (uint?)player.ClassJob.RowId : null;
            var currentJobAbbrev = player.ClassJob.IsValid ? player.ClassJob.Value.Abbreviation.ToString() : string.Empty;
            var account = configManager.GetCurrentAccount();
            var territoryId = Plugin.ClientState.TerritoryType;
            var partyCount = Plugin.PartyList.Length > 0 ? Plugin.PartyList.Length : 1;

            var character = new DadAcquiredCharacter
            {
                CharacterKey = BuildCharacterKey(name, worldName),
                ContentId = Plugin.PlayerState.ContentId,
                CharacterName = name,
                WorldId = worldId,
                WorldName = worldName,
                AccountId = configManager.CurrentAccountId,
                AccountAlias = account?.AccountAlias ?? "(Account)",
                Source = DadCharacterSource.LocalRuntime,
                Freshness = DadSnapshotFreshness.Live,
                LastSeenUtc = now,
                CurrentJobId = currentJobId,
                CurrentJobAbbrev = currentJobAbbrev,
                CurrentLevel = player.Level,
                TerritoryId = territoryId,
                TerritoryName = ResolveTerritoryName(territoryId),
                PartyRosterCount = partyCount,
                VisiblePartyCount = partyCount == 1 ? 1 : null,
                Readiness = DadReadinessState.Ready,
            };

            if (currentJobId.HasValue && character.CurrentLevel.HasValue)
                character.JobLevels[currentJobId.Value] = character.CurrentLevel.Value;

            PopulateDataCenter(character);

            if (character.ContentId == 0)
            {
                character.Readiness = DadReadinessState.Blocked;
                AddBlocker(character, "Content ID unavailable.");
            }

            if (string.IsNullOrWhiteSpace(character.DataCenterName))
                AddBlocker(character, "Datacenter unresolved from world.");

            // B5: on a real level / active-job-level change (not the initial login capture), nudge the
            // transport to republish/refresh so peers see the new level without waiting a full reconcile.
            // The detector coalesces multi-level gains to a single (job, level) transition per capture.
            if (levelChangeDetector.Register(character.CurrentJobId ?? 0, character.CurrentLevel ?? 0))
            {
                transportService.NotifyLocalRosterChanged(
                    $"Local character {character.CurrentJobAbbrev} reached level {character.CurrentLevel ?? 0}.");
            }

            return character;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[dad] Failed to capture local Dad character snapshot.");
            return null;
        }
    }

    private void PopulateDataCenter(DadAcquiredCharacter character)
    {
        try
        {
            var worldSheet = Plugin.DataManager.GetExcelSheet<World>();
            if (worldSheet == null || !worldSheet.TryGetRow(character.WorldId, out var world))
                return;

            if (world.DataCenter.RowId == 0)
                return;

            character.DataCenterId = world.DataCenter.RowId;
            character.DataCenterName = world.DataCenter.Value.Name.ToString().Trim();
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[dad] Failed to resolve datacenter for {CharacterKey}.", character.CharacterKey);
        }
    }

    private static string ResolveTerritoryName(uint territoryId)
    {
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<TerritoryType>();
            if (sheet != null && sheet.TryGetRow(territoryId, out var territory))
            {
                var placeName = territory.PlaceName.Value.Name.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(placeName))
                    return placeName;
            }
        }
        catch
        {
            // Ignore territory lookup errors in the scaffold layer.
        }

        return territoryId == 0 ? "Unknown" : $"Territory {territoryId}";
    }

    private static DadAcquiredCharacter BuildXadbOnlyCharacter(DadXadbStatus xadbStatus)
    {
        var character = new DadAcquiredCharacter
        {
            CharacterKey = BuildCharacterKey(xadbStatus.CharacterName, xadbStatus.WorldName),
            ContentId = xadbStatus.ContentId,
            CharacterName = xadbStatus.CharacterName,
            WorldId = xadbStatus.WorldId ?? 0,
            WorldName = xadbStatus.WorldName,
            DataCenterId = xadbStatus.DataCenterId,
            DataCenterName = xadbStatus.DataCenterName,
            Source = DadCharacterSource.XadbOnly,
            Freshness = ResolveFreshness(xadbStatus.SnapshotUtc),
            LastSeenUtc = xadbStatus.SnapshotUtc,
            XadbSnapshotUtc = xadbStatus.SnapshotUtc,
            CurrentJobId = xadbStatus.CurrentJobId,
            CurrentJobAbbrev = xadbStatus.CurrentJobAbbrev,
            CurrentLevel = xadbStatus.CurrentLevel,
            JobLevels = new Dictionary<uint, int>(xadbStatus.JobLevels),
            TerritoryName = "stored only",
            Readiness = DadReadinessState.Unavailable,
            SnapshotQuality = xadbStatus.SnapshotQuality,
            SnapshotVersion = xadbStatus.SnapshotVersion,
            XadbReady = xadbStatus.IsReady,
            Blockers = ["No live peer connection."],
        };

        return character;
    }

    private static DadSnapshotFreshness ResolveFreshness(DateTime? snapshotUtc)
    {
        if (!snapshotUtc.HasValue)
            return DadSnapshotFreshness.Unknown;

        var age = DateTime.UtcNow - snapshotUtc.Value;
        if (age <= TimeSpan.FromMinutes(1))
            return DadSnapshotFreshness.Live;
        if (age <= TimeSpan.FromMinutes(15))
            return DadSnapshotFreshness.Recent;

        return DadSnapshotFreshness.Stale;
    }

    private static DadSnapshotFreshness ResolvePeerFreshness(DadPeerSnapshotResponse response)
    {
        if (response.Participant.State == DadParticipantState.Stale)
            return DadSnapshotFreshness.Stale;

        var age = DateTime.UtcNow - response.RespondedAtUtc;
        if (age <= TimeSpan.FromMinutes(1))
            return DadSnapshotFreshness.Live;

        return age <= TimeSpan.FromMinutes(15)
            ? DadSnapshotFreshness.Recent
            : DadSnapshotFreshness.Stale;
    }

    private static bool HasXadbIdentity(DadXadbStatus xadbStatus)
        => xadbStatus.ContentId != 0
           || !string.IsNullOrWhiteSpace(xadbStatus.CharacterName)
           || !string.IsNullOrWhiteSpace(xadbStatus.WorldName);

    private static void UpsertCharacter(List<DadAcquiredCharacter> characters, DadAcquiredCharacter candidate)
    {
        var existing = characters.FindIndex(existingCharacter =>
            DadRosterIdentity.BuildKey(existingCharacter)
                .Equals(DadRosterIdentity.BuildKey(candidate), StringComparison.OrdinalIgnoreCase));

        if (existing >= 0)
            characters[existing] = candidate;
        else
            characters.Add(candidate);
    }

    private static string BuildCharacterKey(string? name, string? worldName)
    {
        var cleanName = string.IsNullOrWhiteSpace(name) ? "Unknown" : name.Trim();
        var cleanWorld = string.IsNullOrWhiteSpace(worldName) ? "Unknown" : worldName.Trim();
        return $"{cleanName}@{cleanWorld}";
    }

    private static void AddBlocker(DadAcquiredCharacter character, string blocker)
    {
        if (character.Blockers.Any(existing => string.Equals(existing, blocker, StringComparison.OrdinalIgnoreCase)))
            return;

        character.Blockers.Add(blocker);
    }
}
