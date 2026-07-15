namespace dad.Models;

public sealed class DadWorldLocationObservation
{
    public uint WorldId { get; set; }
    public string WorldName { get; set; } = string.Empty;
    public uint DataCenterId { get; set; }
    public string DataCenterName { get; set; } = string.Empty;
    public uint RegionId { get; set; }
    public string RegionName { get; set; } = string.Empty;
    public DateTime ObservedAtUtc { get; set; } = DateTime.UtcNow;

    public bool IsComplete =>
        WorldId != 0 &&
        !string.IsNullOrWhiteSpace(WorldName) &&
        DataCenterId != 0 &&
        !string.IsNullOrWhiteSpace(DataCenterName) &&
        RegionId != 0 &&
        !string.IsNullOrWhiteSpace(RegionName);

    public DadWorldLocationObservation Clone()
        => new()
        {
            WorldId = WorldId,
            WorldName = WorldName,
            DataCenterId = DataCenterId,
            DataCenterName = DataCenterName,
            RegionId = RegionId,
            RegionName = RegionName,
            ObservedAtUtc = ObservedAtUtc,
        };
}

public sealed class DadCoordinatorTravelTarget
{
    public string RunId { get; set; } = string.Empty;
    public DadWorkerSessionId CoordinatorWorkerSessionId { get; set; } = new(string.Empty);
    public DadAccountKey CoordinatorAccountKey { get; set; } = new(string.Empty);
    public DadCharacterKey CoordinatorCharacterKey { get; set; } = new(string.Empty);
    public ulong CoordinatorContentId { get; set; }
    public uint WorldId { get; set; }
    public string WorldName { get; set; } = string.Empty;
    public uint DataCenterId { get; set; }
    public string DataCenterName { get; set; } = string.Empty;
    public uint RegionId { get; set; }
    public string RegionName { get; set; } = string.Empty;
    public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(RunId) &&
        !CoordinatorWorkerSessionId.IsEmpty &&
        !CoordinatorAccountKey.IsEmpty &&
        !CoordinatorCharacterKey.IsEmpty &&
        CoordinatorContentId != 0 &&
        WorldId != 0 &&
        !string.IsNullOrWhiteSpace(WorldName) &&
        DataCenterId != 0 &&
        !string.IsNullOrWhiteSpace(DataCenterName) &&
        RegionId != 0 &&
        !string.IsNullOrWhiteSpace(RegionName);

    public DadCoordinatorTravelTarget Clone()
        => new()
        {
            RunId = RunId,
            CoordinatorWorkerSessionId = CoordinatorWorkerSessionId,
            CoordinatorAccountKey = CoordinatorAccountKey,
            CoordinatorCharacterKey = CoordinatorCharacterKey,
            CoordinatorContentId = CoordinatorContentId,
            WorldId = WorldId,
            WorldName = WorldName,
            DataCenterId = DataCenterId,
            DataCenterName = DataCenterName,
            RegionId = RegionId,
            RegionName = RegionName,
            CapturedAtUtc = CapturedAtUtc,
        };
}

public sealed class DadOceRosterCharacterProof
{
    public DadAccountKey AccountKey { get; set; } = new(string.Empty);
    public DadCharacterKey CharacterKey { get; set; } = new(string.Empty);
    public ulong ContentId { get; set; }
    public uint HomeWorldId { get; set; }
    public string HomeWorldName { get; set; } = string.Empty;
    public uint HomeRegionId { get; set; }
    public string HomeRegionName { get; set; } = string.Empty;

    public DadOceRosterCharacterProof Clone()
        => new()
        {
            AccountKey = AccountKey,
            CharacterKey = CharacterKey,
            ContentId = ContentId,
            HomeWorldId = HomeWorldId,
            HomeWorldName = HomeWorldName,
            HomeRegionId = HomeRegionId,
            HomeRegionName = HomeRegionName,
        };
}

public sealed class DadOceTravelCapacityProof
{
    public DadAccountKey AccountKey { get; set; } = new(string.Empty);
    public bool IsFullRosterAvailable { get; set; }
    public bool IsComplete { get; set; }
    public int? XadbContractVersion { get; set; }
    public int AdvertisedCharacterCount { get; set; }
    public int AttributedCharacterCount { get; set; }
    public DateTime ObservedAtUtc { get; set; } = DateTime.UtcNow;
    public List<DadOceRosterCharacterProof> Characters { get; set; } = [];
    public string Summary { get; set; } = string.Empty;

    public DadOceTravelCapacityProof Clone()
        => new()
        {
            AccountKey = AccountKey,
            IsFullRosterAvailable = IsFullRosterAvailable,
            IsComplete = IsComplete,
            XadbContractVersion = XadbContractVersion,
            AdvertisedCharacterCount = AdvertisedCharacterCount,
            AttributedCharacterCount = AttributedCharacterCount,
            ObservedAtUtc = ObservedAtUtc,
            Characters = Characters.Select(static character => character.Clone()).ToList(),
            Summary = Summary,
        };
}

public sealed class DadClientTravelSafetyEvidence
{
    public bool VermaxionSafe { get; set; }
    public bool AutoRetainerAvailable { get; set; }
    public bool AutoRetainerBusy { get; set; }
    public bool AutoRetainerMultiModeEnabled { get; set; }
    public bool LifestreamAvailable { get; set; }
    public bool LifestreamBusy { get; set; }
}

public sealed class DadClientTravelContext
{
    public DadWakeRequestDto Assignment { get; set; } = new();
    public DadParticipantSnapshot Participant { get; set; } = new();
    public uint HomeRegionId { get; set; }
    public string HomeRegionName { get; set; } = string.Empty;
    public DadOceTravelCapacityProof? OceCapacityProof { get; set; }
    public DadClientTravelSafetyEvidence Safety { get; set; } = new();
}

public enum DadClientTravelAction
{
    Ready,
    Wait,
    InvokeLifestream,
    Reject,
}

public sealed class DadClientTravelDecision
{
    public DadClientTravelAction Action { get; init; }
    public int AttemptNumber { get; init; }
    public string DestinationWorldName { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
}

public enum DadLifestreamChangeWorldOutcome
{
    Accepted,
    ExplicitFalse,
    Uncertain,
}

public readonly record struct DadLifestreamChangeWorldResult(
    DadLifestreamChangeWorldOutcome Outcome,
    string Summary);

public sealed class DadCoordinatorTravelProofResult
{
    public bool Ready { get; init; }
    public bool ImmutableTargetChanged { get; init; }
    public string Summary { get; init; } = string.Empty;
}
