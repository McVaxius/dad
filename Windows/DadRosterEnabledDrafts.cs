using dad.Models;

namespace dad.Windows;

public sealed record DadRosterProfileTarget(
    DadProfileCatalog? Catalog,
    DadAccountProfileRecord? Account,
    DadCharacterProfileRecord? Character,
    bool Writable,
    string Reason)
{
    public string Key => Catalog == null || Account == null || Character == null ? string.Empty
        : $"{Catalog.OwnerWorkerSessionId.Value}|{Account.AccountKey.Value.Trim()}|{Character.CharacterKey.Value}";
    public string AccountIdentity => Catalog == null || Account == null ? string.Empty
        : $"{Catalog.OwnerWorkerSessionId.Value}|{Account.AccountKey.Value.Trim()}";
}

public sealed class DadRosterEnabledDraft
{
    public required DadRosterProfileTarget Target { get; init; }
    public required CharacterConfig Profile { get; init; }
    public required long ProfileRevision { get; init; }
    public bool Enabled { get; set; }
    public bool Submitting { get; internal set; }
    public string Status { get; internal set; } = string.Empty;
}

// Window-session drafts only. A Save action owns its fixed selection until its existing acknowledgements complete.
public sealed class DadRosterEnabledDrafts
{
    private readonly Dictionary<string, DadRosterEnabledDraft> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> results = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> savingAccounts = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyCollection<DadRosterEnabledDraft> Entries => entries.Values;

    public static DadRosterProfileTarget Resolve(DadRosterCharacter row, IReadOnlyList<DadProfileCatalog> catalogs, DadWorkerSessionId localOwner)
    {
        if (row.AccountKey.IsEmpty || row.CharacterKey.IsEmpty)
            return new(null, null, null, false, "Account or character identity is missing.");
        var owners = catalogs.SelectMany(catalog => catalog.Accounts
            .Where(account => DadRosterIdentity.SameAccount(account.AccountKey, row.AccountKey))
            .Select(account => (Catalog: catalog, Account: account))).ToList();
        var local = owners.Where(owner => !localOwner.IsEmpty && string.Equals(
            owner.Catalog.OwnerWorkerSessionId.Value, localOwner.Value, StringComparison.OrdinalIgnoreCase)).ToList();
        if (local.Count > 0)
            owners = local;
        if (owners.Count != 1)
            return new(null, null, null, false, owners.Count == 0 ? "No owned stored profile is available." : "Multiple profile owners; review ownership.");
        var owner = owners[0];
        var characters = owner.Account.Characters.Where(character => string.Equals(
            character.CharacterKey.Value, row.CharacterKey.Value, StringComparison.OrdinalIgnoreCase)).ToList();
        if (characters.Count != 1)
            return new(owner.Catalog, owner.Account, null, false, "No exact stored character profile is available.");
        var writable = local.Count == 1 || (!owner.Catalog.OwnerWorkerSessionId.IsEmpty && owner.Catalog.OwnerOnline && !owner.Catalog.ReadOnly);
        return new(owner.Catalog, owner.Account, characters[0], writable, writable ? string.Empty : "Offline remote profiles are read-only.");
    }

    public DadRosterEnabledDraft? Get(DadRosterProfileTarget target)
        => entries.GetValueOrDefault(target.Key);

    public string GetStatus(DadRosterProfileTarget target) => Get(target)?.Status ?? results.GetValueOrDefault(target.Key, string.Empty);

    public bool IsAccountSaving(DadRosterProfileTarget target)
        => savingAccounts.Contains(target.AccountIdentity) || (target.Account != null && entries.Values.Any(draft => draft.Submitting
            && draft.Target.Account != null && DadRosterIdentity.SameAccount(draft.Target.Account.AccountKey, target.Account.AccountKey)));

    public void Stage(DadRosterProfileTarget target, bool enabled)
    {
        if (!target.Writable || target.Character == null || IsAccountSaving(target))
            return;
        results.Remove(target.Key);
        var draft = Get(target);
        if (draft == null)
        {
            if (target.Character.Profile.Enabled == enabled)
                return;
            draft = new DadRosterEnabledDraft
            {
                Target = target, Profile = target.Character.Profile.Clone(), ProfileRevision = target.Character.Revision,
                Enabled = enabled,
            };
            entries[target.Key] = draft;
        }
        else
        {
            draft.Enabled = enabled;
            draft.Status = string.Empty;
            if (draft.Profile.Enabled == enabled)
                entries.Remove(target.Key);
        }
    }

    public void Cancel(DadRosterProfileTarget target)
    {
        if (!IsAccountSaving(target))
        {
            entries.Remove(target.Key);
            results.Remove(target.Key);
        }
    }

    public static DadProfileUpdateRequest? CreateRequest(DadRosterEnabledDraft draft, DadRosterProfileTarget current)
    {
        if (!current.Writable || current.Account == null || current.Character == null
            || !string.Equals(current.Key, draft.Target.Key, StringComparison.OrdinalIgnoreCase)
            || current.Character.Revision != draft.ProfileRevision)
            return null;
        var profile = draft.Profile.Clone();
        profile.Enabled = draft.Enabled;
        return new DadProfileUpdateRequest
        {
            AccountKey = current.Account.AccountKey, CharacterKey = current.Character.CharacterKey,
            ExpectedAccountRevision = current.Account.Revision, ExpectedProfileRevision = draft.ProfileRevision,
            Profile = profile,
        };
    }

    public void Save(
        IEnumerable<DadRosterEnabledDraft> selected,
        Func<DadRosterEnabledDraft, DadRosterProfileTarget> resolve,
        Func<DadProfileUpdateRequest, Action<DadProfileUpdateAck>, DadProfileUpdateAck> dispatch)
    {
        foreach (var group in selected.Distinct().Where(draft => !draft.Submitting)
                     .GroupBy(draft => draft.Target.AccountIdentity, StringComparer.OrdinalIgnoreCase).ToList())
        {
            if (group.Any(draft => IsAccountSaving(draft.Target)) || !savingAccounts.Add(group.Key))
                continue;
            var action = group.ToList();
            var index = 0;
            var pumping = false;
            var waiting = false;
            var stopped = false;
            void Pump()
            {
                if (pumping)
                    return;
                pumping = true;
                try
                {
                    while (!waiting && !stopped && index < action.Count)
                    {
                        var draft = action[index++];
                        var current = resolve(draft);
                        var request = CreateRequest(draft, current);
                        if (request == null)
                        {
                            draft.Status = current.Writable ? "Profile changed; cancel and edit again." : current.Reason;
                            stopped = true;
                            break;
                        }
                        waiting = true;
                        draft.Submitting = true;
                        draft.Status = "Awaiting acknowledgement";
                        dispatch(request, ack =>
                        {
                            if (!waiting || !draft.Submitting || !string.Equals(ack.RequestId, request.RequestId, StringComparison.OrdinalIgnoreCase))
                                return;
                            waiting = false;
                            draft.Submitting = false;
                            if (ack.Accepted)
                            {
                                entries.Remove(draft.Target.Key);
                                results[draft.Target.Key] = "Saved";
                            }
                            else
                            {
                                draft.Status = ack.RevisionConflict ? "Revision conflict; cancel and edit again." : "Save rejected";
                                stopped = true;
                            }
                            Pump();
                        });
                    }
                    if (!waiting && (stopped || index >= action.Count))
                        savingAccounts.Remove(group.Key);
                }
                finally { pumping = false; }
            }
            Pump();
        }
    }
}
