using System.Text.Json;
using dad.Models;

namespace dad.Services;

internal static class DadAccountConfigPersistence
{
    public static IReadOnlyList<AccountConfig> LoadAll(
        string configDirectory,
        JsonSerializerOptions jsonOptions,
        Action<string, Exception> failure)
    {
        string[] paths;
        try
        {
            paths = Directory.GetFiles(configDirectory, "*_dad.json");
        }
        catch (Exception exception)
        {
            failure(string.Empty, exception);
            return [];
        }

        var accounts = new List<AccountConfig>();
        foreach (var path in paths)
        {
            try
            {
                var account = JsonSerializer.Deserialize<AccountConfig>(File.ReadAllText(path), jsonOptions);
                if (account == null || string.IsNullOrWhiteSpace(account.AccountId))
                    continue;

                Normalize(account);
                accounts.Add(account);
            }
            catch (Exception exception)
            {
                failure(path, exception);
            }
        }

        return accounts;
    }

    public static bool TryApply(
        AccountConfig account,
        Action mutation,
        Func<bool> persist)
    {
        var before = Clone(account);
        try
        {
            mutation();
            Normalize(account);
            if (persist())
                return true;
        }
        catch
        {
            Restore(account, before);
            throw;
        }

        Restore(account, before);
        return false;
    }

    public static void Normalize(AccountConfig account)
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

    public static AccountConfig Clone(AccountConfig account)
    {
        Normalize(account);
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

    private static void Restore(AccountConfig target, AccountConfig snapshot)
    {
        target.SchemaVersion = snapshot.SchemaVersion;
        target.Revision = snapshot.Revision;
        target.AccountId = snapshot.AccountId;
        target.AccountAlias = snapshot.AccountAlias;
        target.PrimaryLaunchProfileId = snapshot.PrimaryLaunchProfileId;
        target.DefaultConfig = snapshot.DefaultConfig.Clone();
        target.Characters = snapshot.Characters.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.Clone(),
            StringComparer.OrdinalIgnoreCase);
    }

    public static void NormalizeProfile(CharacterConfig profile)
    {
        profile.Revision = Math.Max(1, profile.Revision);
        profile.TargetNotes = profile.TargetNotes?.Trim() ?? string.Empty;
        profile.BlundervilleEmoteCommand = profile.BlundervilleEmoteCommand?.Trim() ?? string.Empty;
    }
}
