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
    private DateTime nextAutoRefreshUtc = DateTime.MinValue;

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
    }

    public DadCharacterPool CurrentPool { get; private set; }

    public void Update()
    {
        if (DateTime.UtcNow < nextAutoRefreshUtc)
            return;

        RefreshLocalCharacterPool("framework", logRefresh: false);
    }

    public DadCharacterPool RefreshLocalCharacterPool(string trigger = "manual", bool logRefresh = true)
    {
        var xadbStatus = xadbClient.Inspect();
        CurrentPool = BuildPool(xadbStatus, transportService.CurrentTransport);
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

    public DadCharacterPool SaveLocalToXadb()
    {
        var xadbStatus = xadbClient.Save();
        CurrentPool = BuildPool(xadbStatus, transportService.CurrentTransport);
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
        nextAutoRefreshUtc = DateTime.UtcNow + AutoRefreshInterval;
        return CurrentPool;
    }

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
            MergeXadb(localCharacter, xadbStatus);
            UpsertCharacter(characters, localCharacter);
        }
        else if (HasXadbIdentity(xadbStatus))
        {
            UpsertCharacter(characters, BuildXadbOnlyCharacter(xadbStatus));
        }

        foreach (var response in peerTransport.LastResponses)
        {
            var character = response.Participant.Character.Clone();
            character.Source = DadCharacterSource.PeerRuntime;
            character.Freshness = ResolvePeerFreshness(response);
            character.Readiness = ResolvePeerReadiness(response.Participant, character.Readiness);

            if (character.Blockers.Count == 0)
                character.Blockers.AddRange(response.Warnings);
            else
                character.Blockers.AddRange(response.Warnings.Where(warning =>
                    character.Blockers.All(existing => !string.Equals(existing, warning, StringComparison.OrdinalIgnoreCase))));

            if (!string.IsNullOrWhiteSpace(response.Participant.StatusText) &&
                character.Blockers.All(existing => !string.Equals(existing, response.Participant.StatusText, StringComparison.OrdinalIgnoreCase)))
            {
                character.Blockers.Add(response.Participant.StatusText);
            }

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

    private static void MergeXadb(DadAcquiredCharacter character, DadXadbStatus xadbStatus)
    {
        character.XadbReady = xadbStatus.IsReady;
        character.XadbSnapshotUtc = xadbStatus.SnapshotUtc;
        character.SnapshotVersion = xadbStatus.SnapshotVersion;
        character.SnapshotQuality = xadbStatus.SnapshotQuality;

        if (!xadbStatus.IsReady)
        {
            AddBlocker(character, "XADB unavailable.");
            return;
        }

        if (character.ContentId == 0 && xadbStatus.ContentId != 0)
            character.ContentId = xadbStatus.ContentId;

        if (character.WorldId == 0 && xadbStatus.WorldId.HasValue)
            character.WorldId = xadbStatus.WorldId.Value;

        if (string.IsNullOrWhiteSpace(character.WorldName))
            character.WorldName = xadbStatus.WorldName;

        if (character.DataCenterId == null && xadbStatus.DataCenterId.HasValue)
            character.DataCenterId = xadbStatus.DataCenterId;

        if (string.IsNullOrWhiteSpace(character.DataCenterName))
            character.DataCenterName = xadbStatus.DataCenterName;

        if (character.CurrentJobId == null && xadbStatus.CurrentJobId.HasValue)
            character.CurrentJobId = xadbStatus.CurrentJobId.Value;

        if (string.IsNullOrWhiteSpace(character.CurrentJobAbbrev))
            character.CurrentJobAbbrev = xadbStatus.CurrentJobAbbrev;

        if (character.CurrentLevel == null && xadbStatus.CurrentLevel.HasValue)
            character.CurrentLevel = xadbStatus.CurrentLevel.Value;

        foreach (var pair in xadbStatus.JobLevels)
            character.JobLevels[pair.Key] = pair.Value;

        if (character.JobLevels.Count == 0)
            AddBlocker(character, "Missing XADB job levels.");

        if (!string.IsNullOrWhiteSpace(xadbStatus.SnapshotQuality) &&
            xadbStatus.SnapshotQuality.Contains("partial", StringComparison.OrdinalIgnoreCase))
        {
            AddBlocker(character, $"XADB snapshot quality {xadbStatus.SnapshotQuality}.");
        }
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

    private static DadReadinessState ResolvePeerReadiness(DadParticipantSnapshot participant, DadReadinessState fallback)
    {
        if (participant.State == DadParticipantState.Stale)
            return DadReadinessState.Stale;

        if (!participant.IsAvailable ||
            !participant.IsEligibleForRun ||
            participant.AuthorityMode == DadAuthorityMode.LocalOnly)
        {
            return DadReadinessState.Unavailable;
        }

        return fallback;
    }

    private static bool HasXadbIdentity(DadXadbStatus xadbStatus)
        => xadbStatus.ContentId != 0
           || !string.IsNullOrWhiteSpace(xadbStatus.CharacterName)
           || !string.IsNullOrWhiteSpace(xadbStatus.WorldName);

    private static void UpsertCharacter(List<DadAcquiredCharacter> characters, DadAcquiredCharacter candidate)
    {
        var existing = characters.FindIndex(existingCharacter =>
            existingCharacter.ContentId != 0
                ? existingCharacter.ContentId == candidate.ContentId
                : string.Equals(existingCharacter.CharacterKey, candidate.CharacterKey, StringComparison.OrdinalIgnoreCase));

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
