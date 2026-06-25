namespace dad.Models;

public sealed class DadProfileCatalog
{
    public int SchemaVersion { get; set; } = 1;
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public string OwnerClientInstanceId { get; set; } = string.Empty;
    public DadWorkerSessionId OwnerWorkerSessionId { get; set; } = new(string.Empty);
    // Legacy serialized field. Routing uses OwnerWorkerSessionId only.
    public string OwnerEndpoint { get; set; } = string.Empty;
    public bool OwnerOnline { get; set; }
    public bool ReadOnly { get; set; }
    public List<DadAccountProfileRecord> Accounts { get; set; } = [];

    public DadProfileCatalog Clone()
        => new()
        {
            SchemaVersion = SchemaVersion,
            GeneratedAtUtc = GeneratedAtUtc,
            OwnerClientInstanceId = OwnerClientInstanceId,
            OwnerWorkerSessionId = OwnerWorkerSessionId,
            OwnerEndpoint = OwnerEndpoint,
            OwnerOnline = OwnerOnline,
            ReadOnly = ReadOnly,
            Accounts = Accounts.Select(static account => account.Clone()).ToList(),
        };
}

public sealed class DadAccountProfileRecord
{
    public DadAccountKey AccountKey { get; set; } = new(string.Empty);
    public string AccountAlias { get; set; } = string.Empty;
    public long Revision { get; set; }
    public string PrimaryLaunchProfileId { get; set; } = string.Empty;
    public CharacterConfig DefaultProfile { get; set; } = new();
    public List<DadCharacterProfileRecord> Characters { get; set; } = [];

    public DadAccountProfileRecord Clone()
        => new()
        {
            AccountKey = AccountKey,
            AccountAlias = AccountAlias,
            Revision = Revision,
            PrimaryLaunchProfileId = PrimaryLaunchProfileId,
            DefaultProfile = DefaultProfile.Clone(),
            Characters = Characters.Select(static character => character.Clone()).ToList(),
        };
}

public sealed class DadCharacterProfileRecord
{
    public DadCharacterKey CharacterKey { get; set; } = new(string.Empty);
    public long Revision { get; set; }
    public CharacterConfig Profile { get; set; } = new();

    public DadCharacterProfileRecord Clone()
        => new()
        {
            CharacterKey = CharacterKey,
            Revision = Revision,
            Profile = Profile.Clone(),
        };
}

public sealed class DadProfileCatalogResponse
{
    public string RequestId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Summary { get; set; } = string.Empty;
    public DadProfileCatalog Catalog { get; set; } = new();
}

public sealed class DadProfileUpdateRequest
{
    public int SchemaVersion { get; set; } = 1;
    public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
    public DadAccountKey AccountKey { get; set; } = new(string.Empty);
    public DadCharacterKey CharacterKey { get; set; } = new(string.Empty);
    public bool UpdateAccountDefault { get; set; }
    public bool UpdatePrimaryLaunchProfile { get; set; }
    public string PrimaryLaunchProfileId { get; set; } = string.Empty;
    public long ExpectedAccountRevision { get; set; }
    public long ExpectedProfileRevision { get; set; }
    public CharacterConfig Profile { get; set; } = new();
}

public sealed class DadProfileUpdateAck
{
    public int SchemaVersion { get; set; } = 1;
    public string RequestId { get; set; } = string.Empty;
    public bool Accepted { get; set; }
    public bool RevisionConflict { get; set; }
    public long AccountRevision { get; set; }
    public long ProfileRevision { get; set; }
    public string Summary { get; set; } = string.Empty;
    public DadAccountProfileRecord? Account { get; set; }
}

public sealed class DadLaunchProfileUpdateRequest
{
    public int SchemaVersion { get; set; } = 1;
    public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
    public long ExpectedRevision { get; set; }
    public DadLaunchProfile Profile { get; set; } = new();
}

public sealed class DadLaunchProfileUpdateAck
{
    public int SchemaVersion { get; set; } = 1;
    public string RequestId { get; set; } = string.Empty;
    public bool Accepted { get; set; }
    public bool RevisionConflict { get; set; }
    public string Summary { get; set; } = string.Empty;
    public DadLaunchProfile? Profile { get; set; }
}
