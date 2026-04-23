using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using dad.Models;

namespace dad.Services;

public sealed class ConfigManager
{
    private readonly IPluginLog log;
    private readonly string configDirectory;
    private readonly Dictionary<string, AccountConfig> accounts = new();

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

    public void EnsureAccountSelected(ulong contentId, string? aliasHint = null)
    {
        var accountId = contentId == 0
            ? Guid.NewGuid().ToString("N")[..8]
            : contentId.ToString("X");

        if (!accounts.ContainsKey(accountId))
        {
            accounts[accountId] = new AccountConfig
            {
                AccountId = accountId,
                AccountAlias = string.IsNullOrWhiteSpace(aliasHint) ? "Account" : aliasHint,
            };
        }

        CurrentAccountId = accountId;
        SaveCurrentAccount();
    }

    public void EnsureCharacterExists(string name, string world)
    {
        var account = GetCurrentAccount();
        if (account == null || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(world))
            return;

        var key = $"{name}@{world}";
        if (!account.Characters.ContainsKey(key))
            account.Characters[key] = account.DefaultConfig.Clone();

        SelectedCharacterKey = key;
        SaveCurrentAccount();
    }

    public void SaveCurrentAccount()
    {
        if (!string.IsNullOrWhiteSpace(CurrentAccountId))
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
                    accounts[account.AccountId] = account;
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
        }
        catch (Exception ex)
        {
            log.Error(ex, "[dad] Failed to save account config.");
        }
    }
}
