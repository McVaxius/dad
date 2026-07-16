using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using dad.Models;

namespace dad.Services;

public sealed class ConfigManager
{
    private readonly IPluginLog log;
    private readonly string configDirectory;
    private readonly Dictionary<string, AccountConfig> accounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> deletedAccountIds = new(StringComparer.OrdinalIgnoreCase);
    private long profileCatalogRevision = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public ConfigManager(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.log = log;
        configDirectory = pluginInterface.GetPluginConfigDirectory();
        Directory.CreateDirectory(configDirectory);
        LoadAllAccounts();
    }

    public string CurrentAccountId { get; set; } = string.Empty;
    public string SelectedCharacterKey { get; set; } = string.Empty;
    public long ProfileCatalogRevision => Interlocked.Read(ref profileCatalogRevision);

    public AccountConfig? GetCurrentAccount()
        => string.IsNullOrWhiteSpace(CurrentAccountId) ? null : accounts.GetValueOrDefault(CurrentAccountId);

    public string GetCurrentAccountKey()
        => GetCurrentAccount()?.AccountId ?? CurrentAccountId;

    public string GetCurrentAccountAlias()
        => GetCurrentAccount()?.AccountAlias ?? "(Account)";

    public IReadOnlyList<string> GetKnownCharacterKeysForCurrentAccount()
    {
        var account = GetCurrentAccount();
        if (account == null)
            return [];

        var keys = account.Characters.Keys
            .OrderBy(static key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return keys;
    }

    public IReadOnlyList<AccountConfig> GetAllAccounts()
        => accounts.Values
            .Select(CloneAccount)
            .OrderBy(static account => account.AccountAlias, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static account => account.AccountId, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public AccountConfig? GetAccount(DadAccountKey accountKey)
    {
        var account = ResolveAccount(accountKey);
        return account == null ? null : CloneAccount(account);
    }

    public bool UpdateAccountAlias(DadAccountKey accountKey, string alias)
    {
        var account = ResolveAccount(accountKey);
        if (account == null)
            return false;

        NormalizeAccount(account);
        account.AccountAlias = string.IsNullOrWhiteSpace(alias) ? "Account" : alias.Trim();
        account.Revision++;
        SaveAccount(account.AccountId);
        return true;
    }

    public DadProfileCatalog BuildLocalProfileCatalog(
        string ownerClientInstanceId,
        DadWorkerSessionId ownerWorkerSessionId,
        string ownerEndpoint,
        bool ownerOnline = true)
        => new()
        {
            GeneratedAtUtc = DateTime.UtcNow,
            OwnerClientInstanceId = ownerClientInstanceId,
            OwnerWorkerSessionId = ownerWorkerSessionId,
            OwnerEndpoint = ownerEndpoint,
            OwnerOnline = ownerOnline,
            ReadOnly = !ownerOnline,
            Accounts = accounts.Values
                .Select(BuildAccountProfileRecord)
                .OrderBy(static account => account.AccountAlias, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static account => account.AccountKey.Value, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };

    public DadProfileUpdateAck ApplyProfileUpdate(DadProfileUpdateRequest request)
    {
        var account = ResolveAccount(request.AccountKey);
        if (account == null)
        {
            return new DadProfileUpdateAck
            {
                RequestId = request.RequestId,
                Summary = $"Account {request.AccountKey} is not owned by this Client Dad.",
            };
        }

        NormalizeAccount(account);
        if (request.ExpectedAccountRevision != account.Revision)
        {
            return new DadProfileUpdateAck
            {
                RequestId = request.RequestId,
                RevisionConflict = true,
                AccountRevision = account.Revision,
                ProfileRevision = account.DefaultConfig.Revision,
                Summary = $"Profile revision conflict for {account.AccountAlias}; refresh before saving.",
                Account = BuildAccountProfileRecord(account),
            };
        }

        if (request.UpdatePrimaryLaunchProfile)
        {
            account.PrimaryLaunchProfileId = request.PrimaryLaunchProfileId?.Trim() ?? string.Empty;
            account.Revision++;
            SaveAccount(account.AccountId);
            return new DadProfileUpdateAck
            {
                RequestId = request.RequestId,
                Accepted = true,
                AccountRevision = account.Revision,
                ProfileRevision = account.DefaultConfig.Revision,
                Summary = $"Saved primary launch profile for {account.AccountAlias}.",
                Account = BuildAccountProfileRecord(account),
            };
        }

        var target = request.UpdateAccountDefault
            ? account.DefaultConfig
            : ResolveCharacterProfile(account, request.CharacterKey);
        if (target == null)
        {
            return new DadProfileUpdateAck
            {
                RequestId = request.RequestId,
                AccountRevision = account.Revision,
                Summary = $"Character profile {request.CharacterKey} is not owned by account {account.AccountId}.",
                Account = BuildAccountProfileRecord(account),
            };
        }

        if (request.ExpectedProfileRevision != target.Revision)
        {
            return new DadProfileUpdateAck
            {
                RequestId = request.RequestId,
                RevisionConflict = true,
                AccountRevision = account.Revision,
                ProfileRevision = target.Revision,
                Summary = $"Profile revision conflict for {account.AccountAlias}; refresh before saving.",
                Account = BuildAccountProfileRecord(account),
            };
        }

        var replacement = request.Profile?.Clone() ?? new CharacterConfig();
        replacement.Revision = target.Revision + 1;
        NormalizeProfile(replacement);
        if (request.UpdateAccountDefault)
        {
            account.DefaultConfig = replacement;
        }
        else
        {
            var key = account.Characters.Keys.First(existing =>
                string.Equals(existing, request.CharacterKey.Value, StringComparison.OrdinalIgnoreCase));
            account.Characters[key] = replacement;
        }

        account.Revision++;
        SaveAccount(account.AccountId);
        return new DadProfileUpdateAck
        {
            RequestId = request.RequestId,
            Accepted = true,
            AccountRevision = account.Revision,
            ProfileRevision = replacement.Revision,
            Summary = $"Saved profile for {(request.UpdateAccountDefault ? "account default" : request.CharacterKey.Value)}.",
            Account = BuildAccountProfileRecord(account),
        };
    }

    public bool UpdatePrimaryLaunchProfile(DadAccountKey accountKey, string profileId)
    {
        var account = ResolveAccount(accountKey);
        if (account == null)
            return false;

        NormalizeAccount(account);
        account.PrimaryLaunchProfileId = profileId?.Trim() ?? string.Empty;
        account.Revision++;
        SaveAccount(account.AccountId);
        return true;
    }

    public bool DeleteAccount(DadAccountKey accountKey)
    {
        var account = ResolveAccount(accountKey);
        if (account == null)
            return false;

        NormalizeAccount(account);
        var accountId = account.AccountId;
        if (string.IsNullOrWhiteSpace(accountId))
            return false;

        try
        {
            var path = Path.Combine(configDirectory, $"{accountId}_dad.json");
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[dad] Failed to delete account config {AccountId}.", accountId);
            return false;
        }

        var existingKey = accounts.Keys.FirstOrDefault(key =>
            string.Equals(key, accountId, StringComparison.OrdinalIgnoreCase));
        if (existingKey != null)
            accounts.Remove(existingKey);
        deletedAccountIds.Add(accountId);

        if (string.Equals(CurrentAccountId, accountId, StringComparison.OrdinalIgnoreCase))
        {
            CurrentAccountId = string.Empty;
            SelectedCharacterKey = string.Empty;
        }

        Interlocked.Increment(ref profileCatalogRevision);

        return true;
    }

    public DadAccountDataClearResult ClearAllAccounts()
    {
        var result = new DadAccountDataClearResult
        {
            AccountConfigsCleared = accounts.Count,
        };

        foreach (var accountId in accounts.Keys)
        {
            if (!string.IsNullOrWhiteSpace(accountId))
                deletedAccountIds.Add(accountId);
        }

        try
        {
            foreach (var path in Directory.GetFiles(configDirectory, "*_dad.json"))
            {
                var accountId = ResolveAccountIdFromConfigPath(path);
                if (!string.IsNullOrWhiteSpace(accountId))
                    deletedAccountIds.Add(accountId);

                try
                {
                    File.Delete(path);
                    result.AccountConfigFilesDeleted++;
                }
                catch (Exception ex)
                {
                    result.AccountConfigDeleteFailures++;
                    log.Error(ex, "[dad] Failed to delete account config {ConfigPath}.", path);
                }
            }
        }
        catch (Exception ex)
        {
            result.AccountConfigDeleteFailures++;
            log.Error(ex, "[dad] Failed to enumerate Dad account configs.");
        }

        accounts.Clear();
        CurrentAccountId = string.Empty;
        SelectedCharacterKey = string.Empty;
        if (result.AccountConfigsCleared > 0 || result.AccountConfigFilesDeleted > 0)
            Interlocked.Increment(ref profileCatalogRevision);
        return result;
    }

    public bool MergeAccountInto(DadAccountKey sourceKey, DadAccountKey targetKey)
    {
        var source = ResolveAccount(sourceKey);
        var target = ResolveAccount(targetKey);
        if (source == null || target == null)
            return false;

        NormalizeAccount(source);
        NormalizeAccount(target);
        if (string.Equals(source.AccountId, target.AccountId, StringComparison.OrdinalIgnoreCase))
            return false;

        var changed = false;
        foreach (var pair in source.Characters)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || target.Characters.ContainsKey(pair.Key))
                continue;

            target.Characters[pair.Key] = pair.Value.Clone();
            changed = true;
        }

        if (changed)
        {
            target.Revision++;
            SaveAccount(target.AccountId);
        }

        var sourceWasCurrent = string.Equals(CurrentAccountId, source.AccountId, StringComparison.OrdinalIgnoreCase);
        var selectedBeforeDelete = SelectedCharacterKey;
        var deleted = DeleteAccount(new DadAccountKey(source.AccountId));
        if (!deleted)
            return false;

        if (sourceWasCurrent || string.IsNullOrWhiteSpace(CurrentAccountId))
            CurrentAccountId = target.AccountId;
        if (sourceWasCurrent && !string.IsNullOrWhiteSpace(selectedBeforeDelete) && target.Characters.ContainsKey(selectedBeforeDelete))
            SelectedCharacterKey = selectedBeforeDelete;

        SaveAccount(target.AccountId);
        return true;
    }

    public bool EnsureCharacterForAccount(
        DadAccountKey accountKey,
        string characterKey,
        string characterName,
        string worldName)
    {
        var account = ResolveAccount(accountKey);
        if (account == null)
            return false;

        NormalizeAccount(account);
        var resolvedKey = ResolveCharacterKey(characterKey, characterName, worldName);
        if (string.IsNullOrWhiteSpace(resolvedKey))
            return false;

        if (account.Characters.ContainsKey(resolvedKey))
            return true;

        account.Characters[resolvedKey] = account.DefaultConfig.Clone();
        account.Revision++;
        SaveAccount(account.AccountId);
        return true;
    }

    public bool RemoveCharacterFromAccount(DadAccountKey accountKey, DadCharacterKey characterKey)
    {
        var account = ResolveAccount(accountKey);
        if (account == null || characterKey.IsEmpty)
            return false;

        NormalizeAccount(account);
        var existingKey = account.Characters.Keys.FirstOrDefault(key =>
            string.Equals(key, characterKey.Value, StringComparison.OrdinalIgnoreCase));
        if (existingKey == null)
            return false;

        account.Characters.Remove(existingKey);
        account.Revision++;
        if (string.Equals(CurrentAccountId, account.AccountId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(SelectedCharacterKey, existingKey, StringComparison.OrdinalIgnoreCase))
        {
            SelectedCharacterKey = string.Empty;
        }

        SaveAccount(account.AccountId);
        return true;
    }

    public bool HasCharacterInAccount(DadAccountKey accountKey, DadCharacterKey characterKey)
    {
        var account = ResolveAccount(accountKey);
        if (account == null || characterKey.IsEmpty)
            return false;

        NormalizeAccount(account);
        return account.Characters.Keys.Any(key =>
            string.Equals(key, characterKey.Value, StringComparison.OrdinalIgnoreCase));
    }

    public CharacterConfig GetActiveConfig()
    {
        var account = GetCurrentAccount();
        if (account == null)
            return new CharacterConfig();

        if (string.IsNullOrWhiteSpace(SelectedCharacterKey))
            return account.DefaultConfig;

        return account.Characters.TryGetValue(SelectedCharacterKey, out var config)
            ? config
            : account.DefaultConfig;
    }

    public IEnumerable<string> GetSortedCharacterKeys()
        => GetCurrentAccount()?.Characters.Keys.OrderBy(static key => key, StringComparer.OrdinalIgnoreCase)
           ?? Enumerable.Empty<string>();

    public bool EnsureRuntimeIdentity(string characterName, string worldName, string? preferredAccountId = null)
    {
        var characterKey = ResolveCharacterKey(string.Empty, characterName, worldName);
        if (string.IsNullOrWhiteSpace(characterKey))
            return false;

        var preferredKey = preferredAccountId?.Trim() ?? string.Empty;
        var account = !string.IsNullOrWhiteSpace(preferredKey)
            ? ResolveAccountById(preferredKey) ?? CreateAccount(preferredKey, characterName)
            : FindAccountContainingCharacter(characterKey)
              ?? ResolveAccount(new DadAccountKey(CurrentAccountId))
              ?? ResolveSingleAccount()
              ?? ResolveFirstAccount();
        if (account == null)
            return false;

        NormalizeAccount(account);
        CurrentAccountId = account.AccountId;
        SelectedCharacterKey = characterKey;

        if (!account.Characters.ContainsKey(characterKey))
        {
            account.Characters[characterKey] = account.DefaultConfig.Clone();
            account.Revision++;
            SaveAccount(account.AccountId);
        }

        return true;
    }

    public void EnsureAccountSelected(string? preferredAccountId = null, string? aliasHint = null)
    {
        var preferredKey = preferredAccountId?.Trim() ?? string.Empty;
        var account = !string.IsNullOrWhiteSpace(preferredKey)
            ? ResolveAccountById(preferredKey) ?? CreateAccount(preferredKey, aliasHint)
            : ResolveAccount(new DadAccountKey(CurrentAccountId))
                      ?? ResolveSingleAccount()
                      ?? ResolveFirstAccount();
        if (account == null)
            return;

        NormalizeAccount(account);
        CurrentAccountId = account.AccountId;
        SaveAccount(account.AccountId);
    }

    public void EnsureCharacterExists(string name, string world)
    {
        var account = GetCurrentAccount();
        if (account == null || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(world))
            return;

        var key = $"{name}@{world}";
        if (!account.Characters.ContainsKey(key))
        {
            account.Characters[key] = account.DefaultConfig.Clone();
            account.Revision++;
        }

        SelectedCharacterKey = key;
        SaveCurrentAccount();
    }

    public void SaveCurrentAccount()
    {
        if (string.IsNullOrWhiteSpace(CurrentAccountId) ||
            !accounts.TryGetValue(CurrentAccountId, out var account))
        {
            return;
        }

        NormalizeAccount(account);
        account.Revision++;
        var profile = string.IsNullOrWhiteSpace(SelectedCharacterKey)
            ? account.DefaultConfig
            : ResolveCharacterProfile(account, new DadCharacterKey(SelectedCharacterKey));
        if (profile != null)
            profile.Revision++;
        SaveAccount(CurrentAccountId);
    }

    private void LoadAllAccounts()
    {
        try
        {
            foreach (var path in Directory.GetFiles(configDirectory, "*_dad.json"))
            {
                var account = JsonSerializer.Deserialize<AccountConfig>(File.ReadAllText(path), JsonOptions);
                if (account != null && !string.IsNullOrWhiteSpace(account.AccountId))
                {
                    NormalizeAccount(account);
                    accounts[account.AccountId] = account;
                }
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "[dad] Failed to load account configs.");
        }
    }

    private void SaveAccount(string accountId)
    {
        if (!accounts.TryGetValue(accountId, out var account))
            return;

        try
        {
            var path = Path.Combine(configDirectory, $"{accountId}_dad.json");
            File.WriteAllText(path, JsonSerializer.Serialize(account, JsonOptions));
            Interlocked.Increment(ref profileCatalogRevision);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[dad] Failed to save account config.");
        }
    }

    private static string ResolveAccountIdFromConfigPath(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(fileName))
            return string.Empty;

        return fileName.EndsWith("_dad", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^4]
            : fileName;
    }

    private AccountConfig? ResolveAccount(DadAccountKey accountKey)
    {
        var value = accountKey.Value.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (accounts.TryGetValue(value, out var exact))
            return exact;

        return accounts.Values.FirstOrDefault(account =>
            string.Equals(account.AccountId, value, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(account.AccountAlias, value, StringComparison.OrdinalIgnoreCase));
    }

    private AccountConfig? ResolveAccountById(string accountId)
    {
        var value = accountId.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return accounts.GetValueOrDefault(value);
    }

    private AccountConfig? FindAccountContainingCharacter(string characterKey)
        => accounts.Values.FirstOrDefault(account =>
        {
            NormalizeAccount(account);
            return account.Characters.ContainsKey(characterKey);
        });

    private AccountConfig? ResolveSingleAccount()
        => accounts.Count == 1 ? accounts.Values.First() : null;

    private AccountConfig? ResolveFirstAccount()
        => accounts.Values
            .OrderBy(static account => account.AccountAlias, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static account => account.AccountId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    private AccountConfig? CreateAccount(string accountId, string? aliasHint)
    {
        var normalizedAccountId = accountId.Trim();
        if (string.IsNullOrWhiteSpace(normalizedAccountId))
            return null;
        if (accounts.TryGetValue(normalizedAccountId, out var existing))
            return existing;

        var account = new AccountConfig
        {
            AccountId = normalizedAccountId,
            AccountAlias = string.IsNullOrWhiteSpace(aliasHint) ? "Account" : aliasHint.Trim(),
        };
        NormalizeAccount(account);
        deletedAccountIds.Remove(account.AccountId);
        accounts[account.AccountId] = account;
        SaveAccount(account.AccountId);
        return account;
    }

    private static string ResolveCharacterKey(string characterKey, string characterName, string worldName)
    {
        if (!string.IsNullOrWhiteSpace(characterKey))
            return characterKey.Trim();

        if (string.IsNullOrWhiteSpace(characterName) || string.IsNullOrWhiteSpace(worldName))
            return string.Empty;

        return $"{characterName.Trim()}@{worldName.Trim()}";
    }

    private static void NormalizeAccount(AccountConfig account)
    {
        account.SchemaVersion = Math.Max(2, account.SchemaVersion);
        account.Revision = Math.Max(1, account.Revision);
        account.AccountId = account.AccountId?.Trim() ?? string.Empty;
        account.AccountAlias = string.IsNullOrWhiteSpace(account.AccountAlias) ? "Account" : account.AccountAlias.Trim();
        account.PrimaryLaunchProfileId = account.PrimaryLaunchProfileId?.Trim() ?? string.Empty;
        account.DefaultConfig ??= new CharacterConfig();
        NormalizeProfile(account.DefaultConfig);
        var normalizedCharacters = new Dictionary<string, CharacterConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in account.Characters ?? new Dictionary<string, CharacterConfig>())
        {
            var key = pair.Key?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key) || normalizedCharacters.ContainsKey(key))
                continue;

            var profile = pair.Value?.Clone() ?? new CharacterConfig();
            NormalizeProfile(profile);
            normalizedCharacters[key] = profile;
        }

        account.Characters = normalizedCharacters;
    }

    private static AccountConfig CloneAccount(AccountConfig account)
    {
        NormalizeAccount(account);
        return new AccountConfig
        {
            SchemaVersion = account.SchemaVersion,
            Revision = account.Revision,
            AccountId = account.AccountId,
            AccountAlias = account.AccountAlias,
            PrimaryLaunchProfileId = account.PrimaryLaunchProfileId,
            DefaultConfig = account.DefaultConfig.Clone(),
            Characters = account.Characters.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value.Clone(),
                StringComparer.OrdinalIgnoreCase),
        };
    }

    private static CharacterConfig? ResolveCharacterProfile(AccountConfig account, DadCharacterKey characterKey)
    {
        if (characterKey.IsEmpty)
            return null;

        var key = account.Characters.Keys.FirstOrDefault(existing =>
            string.Equals(existing, characterKey.Value, StringComparison.OrdinalIgnoreCase));
        return key == null ? null : account.Characters[key];
    }

    private static DadAccountProfileRecord BuildAccountProfileRecord(AccountConfig account)
    {
        NormalizeAccount(account);
        return new DadAccountProfileRecord
        {
            AccountKey = new DadAccountKey(account.AccountId),
            AccountAlias = account.AccountAlias,
            Revision = account.Revision,
            PrimaryLaunchProfileId = account.PrimaryLaunchProfileId,
            DefaultProfile = account.DefaultConfig.Clone(),
            Characters = account.Characters
                .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static pair => new DadCharacterProfileRecord
                {
                    CharacterKey = new DadCharacterKey(pair.Key),
                    Revision = pair.Value.Revision,
                    Profile = pair.Value.Clone(),
                })
                .ToList(),
        };
    }

    private static void NormalizeProfile(CharacterConfig profile)
    {
        profile.Revision = Math.Max(1, profile.Revision);
        profile.TargetNotes = profile.TargetNotes?.Trim() ?? string.Empty;
        profile.BlundervilleEmoteCommand = profile.BlundervilleEmoteCommand?.Trim() ?? string.Empty;
    }
}
