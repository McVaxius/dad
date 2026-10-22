using System.Text.Json;
using dad.Models;
using dad.Windows;
using Xunit;

namespace dad.Tests;

public sealed class DadRosterEnabledDraftsTests
{
    private static readonly DadWorkerSessionId LocalOwner = new("local-worker");

    [Fact]
    public void LoggedOutStoredCharacterResolvesExactLocalProfileAndKeepsVisibilitySeparate()
    {
        var catalog = Catalog("local-worker", "account-a", "character-a");
        var remote = Catalog("remote-worker", "account-a", "character-a");
        var row = Row(" ACCOUNT-A ", "CHARACTER-A");
        row.IsCurrent = false;
        row.Visibility = DadRosterVisibility.Hidden;

        var target = DadRosterEnabledDrafts.Resolve(row, [remote, catalog], LocalOwner);

        Assert.True(target.Writable);
        Assert.Same(catalog, target.Catalog);
        Assert.Same(catalog.Accounts[0].Characters[0], target.Character);
        Assert.False(row.IsCurrent);
        Assert.Equal(DadRosterVisibility.Hidden, row.Visibility);
    }

    [Theory]
    [InlineData(false, false, "remote-worker", false)]
    [InlineData(true, true, "remote-worker", false)]
    [InlineData(true, false, "", false)]
    [InlineData(true, false, "remote-worker", true)]
    public void RemoteProfileRequiresReachableWritableOwner(bool online, bool readOnly, string owner, bool writable)
    {
        var catalog = Catalog(owner, "account-a", "character-a");
        catalog.OwnerOnline = online;
        catalog.ReadOnly = readOnly;

        var target = DadRosterEnabledDrafts.Resolve(Row("account-a", "character-a"), [catalog], LocalOwner);

        Assert.Equal(writable, target.Writable);
        Assert.Equal(writable, string.IsNullOrEmpty(target.Reason));
    }

    [Fact]
    public void MissingAmbiguousAndWrongCharacterProfilesNeverResolveByDisplayText()
    {
        var row = Row("account-a", "character-a");
        var first = Catalog("remote-a", "account-a", "character-a");
        var second = Catalog("remote-b", "account-a", "character-a");
        Assert.False(DadRosterEnabledDrafts.Resolve(row, [first, second], LocalOwner).Writable);
        Assert.False(DadRosterEnabledDrafts.Resolve(row, [], LocalOwner).Writable);
        var wrongCharacter = Catalog("local-worker", "account-a", "different-character");
        Assert.False(DadRosterEnabledDrafts.Resolve(row, [wrongCharacter], LocalOwner).Writable);
        var wrongAccount = Catalog("local-worker", "different-account", "character-a");
        wrongAccount.Accounts[0].AccountAlias = row.AccountAlias;
        Assert.False(DadRosterEnabledDrafts.Resolve(row, [wrongAccount], LocalOwner).Writable);
        row.AccountKey = new("");
        Assert.False(DadRosterEnabledDrafts.Resolve(row, [first], LocalOwner).Writable);
    }

    [Fact]
    public void DraftCancelAndSaveRequestPreserveStoredProfileAndAllUnrelatedFields()
    {
        var catalog = Catalog("local-worker", "account-a", "character-a");
        var target = Resolve(catalog, "character-a");
        var stored = target.Character!.Profile;
        stored.AllowIpcStarts = false;
        stored.TargetNotes = "Synthetic notes";
        stored.BlundervilleEmoteCommand = "/wave";
        var original = JsonSerializer.Serialize(stored);
        var drafts = new DadRosterEnabledDrafts();
        drafts.Stage(target, false);
        var draft = Assert.Single(drafts.Entries);
        Assert.NotSame(stored, draft.Profile);
        Assert.Equal(original, JsonSerializer.Serialize(stored));
        Assert.True(stored.Enabled);

        var request = DadRosterEnabledDrafts.CreateRequest(draft, target);

        Assert.NotNull(request);
        Assert.Equal(target.Account!.AccountKey, request.AccountKey);
        Assert.Equal(target.Character.CharacterKey, request.CharacterKey);
        Assert.Equal(10, request.ExpectedAccountRevision);
        Assert.Equal(4, request.ExpectedProfileRevision);
        Assert.False(request.UpdateAccountDefault);
        Assert.False(request.UpdatePrimaryLaunchProfile);
        var expected = stored.Clone();
        expected.Enabled = false;
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(request.Profile));
        Assert.NotSame(draft.Profile, request.Profile);
        Assert.Equal(original, JsonSerializer.Serialize(stored));
        drafts.Cancel(target);
        Assert.Empty(drafts.Entries);
        Assert.Equal(original, JsonSerializer.Serialize(stored));
        drafts.Stage(target, false);
        drafts.Stage(target, true);
        Assert.Empty(drafts.Entries);
    }

    [Fact]
    public void RequestRechecksProfileRevisionOwnerAndWritabilityButUsesCurrentAccountRevision()
    {
        var catalog = Catalog("local-worker", "account-a", "character-a");
        var target = Resolve(catalog, "character-a");
        var drafts = new DadRosterEnabledDrafts();
        drafts.Stage(target, false);
        var draft = Assert.Single(drafts.Entries);
        var current = catalog.Clone();
        current.Accounts[0].Revision = 11;
        Assert.Equal(11, DadRosterEnabledDrafts.CreateRequest(draft, Resolve(current, "character-a"))!.ExpectedAccountRevision);
        current.Accounts[0].Characters[0].Revision++;
        Assert.Null(DadRosterEnabledDrafts.CreateRequest(draft, Resolve(current, "character-a")));
        Assert.Null(DadRosterEnabledDrafts.CreateRequest(draft, target with { Writable = false }));
        current = catalog.Clone();
        current.OwnerWorkerSessionId = new("different-owner");
        Assert.Null(DadRosterEnabledDrafts.CreateRequest(draft, Resolve(current, "character-a")));
    }

    [Fact]
    public void ExplicitVisibleSelectionStagesAndCancelsOnlyWritableTargets()
    {
        var catalog = Catalog("local-worker", "account-a", "visible", "filtered-out");
        var visible = Resolve(catalog, "visible");
        var hidden = Resolve(catalog, "filtered-out");
        var remote = Catalog("remote-worker", "account-b", "read-only");
        remote.OwnerOnline = false;
        var readOnly = Resolve(remote, "read-only");
        var drafts = new DadRosterEnabledDrafts();
        drafts.Stage(hidden, false);

        foreach (var selected in new[] { visible, readOnly })
            drafts.Stage(selected, false);

        Assert.Equal(2, drafts.Entries.Count);
        Assert.Null(drafts.Get(readOnly));
        drafts.Cancel(visible);
        Assert.Same(drafts.Get(hidden), Assert.Single(drafts.Entries));
        Assert.All(catalog.Accounts[0].Characters, character => Assert.True(character.Profile.Enabled));
        Assert.True(remote.Accounts[0].Characters[0].Profile.Enabled);
    }

    [Fact]
    public void SynchronousAcknowledgementsSequenceCurrentAccountRevisionsAndSaveOnlyExplicitSelection()
    {
        var catalog = Catalog("local-worker", "account-a", "first", "second", "filtered-out");
        var drafts = new DadRosterEnabledDrafts();
        foreach (var character in catalog.Accounts[0].Characters)
            drafts.Stage(Resolve(catalog, character.CharacterKey.Value), false);
        var selected = drafts.Entries.Where(draft => draft.Target.Character!.CharacterKey.Value != "filtered-out").ToList();
        var requests = new List<DadProfileUpdateRequest>();

        drafts.Save(selected, draft => Resolve(catalog, draft.Target.Character!.CharacterKey.Value), (request, complete) =>
        {
            requests.Add(request);
            Assert.Equal(catalog.Accounts[0].Revision, request.ExpectedAccountRevision);
            catalog.Accounts[0].Revision++;
            complete(Ack(request, accepted: true));
            return Ack(request, accepted: true);
        });

        Assert.Equal(new long[] { 10, 11 }, requests.Select(request => request.ExpectedAccountRevision));
        Assert.Equal("filtered-out", Assert.Single(drafts.Entries).Target.Character!.CharacterKey.Value);
        Assert.False(drafts.IsAccountSaving(Resolve(catalog, "first")));
        Assert.Equal("Saved", drafts.GetStatus(Resolve(catalog, "first")));
        Assert.All(catalog.Accounts[0].Characters, character => Assert.True(character.Profile.Enabled));
    }

    [Fact]
    public void PendingAcknowledgementBlocksSameAccountEditsAndRequiresMatchingCompletionBeforeNextSave()
    {
        var catalog = Catalog("local-worker", "account-a", "first", "second");
        var drafts = new DadRosterEnabledDrafts();
        var first = Resolve(catalog, "first");
        var second = Resolve(catalog, "second");
        drafts.Stage(first, false);
        drafts.Stage(second, false);
        var requests = new List<DadProfileUpdateRequest>();
        var callbacks = new List<Action<DadProfileUpdateAck>>();
        DadProfileUpdateAck Dispatch(DadProfileUpdateRequest request, Action<DadProfileUpdateAck> complete)
        {
            requests.Add(request);
            callbacks.Add(complete);
            return new DadProfileUpdateAck { RequestId = request.RequestId, Summary = "Queued" };
        }

        drafts.Save(drafts.Entries.ToList(), draft => Resolve(catalog, draft.Target.Character!.CharacterKey.Value), Dispatch);

        Assert.Single(requests);
        Assert.True(drafts.Get(first)!.Submitting);
        Assert.Equal("Awaiting acknowledgement", drafts.GetStatus(first));
        Assert.True(drafts.IsAccountSaving(second));
        drafts.Cancel(first);
        drafts.Stage(second, true);
        Assert.Equal(2, drafts.Entries.Count);
        Assert.False(drafts.Get(second)!.Enabled);
        callbacks[0](new DadProfileUpdateAck { RequestId = "unrelated-request", Accepted = true });
        Assert.Single(requests);
        Assert.Equal(2, drafts.Entries.Count);
        catalog.Accounts[0].Revision = 11;
        callbacks[0](Ack(requests[0], accepted: true));
        Assert.Equal(2, requests.Count);
        Assert.Equal(11, requests[1].ExpectedAccountRevision);
        Assert.Single(drafts.Entries);
        callbacks[0](Ack(requests[0], accepted: true));
        Assert.Single(drafts.Entries);
        callbacks[1](Ack(requests[1], accepted: true));
        Assert.Empty(drafts.Entries);
        Assert.False(drafts.IsAccountSaving(first));
    }

    [Fact]
    public void RejectionForOneAccountDoesNotBlockAnotherAccountFromSaving()
    {
        var first = Catalog("remote-a", "account-a", "character-a");
        var second = Catalog("remote-b", "account-b", "character-b");
        var drafts = new DadRosterEnabledDrafts();
        drafts.Stage(Resolve(first, "character-a"), false);
        drafts.Stage(Resolve(second, "character-b"), false);
        var calls = new List<DadAccountKey>();

        drafts.Save(drafts.Entries.ToList(), draft => Resolve(
            draft.Target.Account!.AccountKey.Value == "account-a" ? first : second,
            draft.Target.Character!.CharacterKey.Value), (request, complete) =>
        {
            calls.Add(request.AccountKey);
            var ack = Ack(request, accepted: request.AccountKey.Value == "account-b");
            complete(ack);
            return ack;
        });

        Assert.Equal(2, calls.Count);
        Assert.Equal("account-a", Assert.Single(drafts.Entries).Target.Account!.AccountKey.Value);
        Assert.Equal("Save rejected", drafts.GetStatus(Resolve(first, "character-a")));
        Assert.Equal("Saved", drafts.GetStatus(Resolve(second, "character-b")));
    }

    [Fact]
    public void PendingSaveRetainsAccountLockWhenOwnerSessionChanges()
    {
        var oldOwner = Catalog("remote-old", "account-a", "first");
        var newOwner = Catalog("remote-new", " ACCOUNT-A ", "first", "second");
        var drafts = new DadRosterEnabledDrafts();
        var oldTarget = Resolve(oldOwner, "first");
        var newTarget = Resolve(newOwner, "first");
        drafts.Stage(oldTarget, false);
        drafts.Stage(newTarget, false);
        var oldDraft = drafts.Get(oldTarget)!;
        var newDraft = drafts.Get(newTarget)!;
        var calls = 0;
        DadProfileUpdateAck Dispatch(DadProfileUpdateRequest request, Action<DadProfileUpdateAck> complete)
        {
            calls++;
            return new DadProfileUpdateAck { RequestId = request.RequestId, Summary = "Queued" };
        }
        drafts.Save([oldDraft], _ => oldTarget, Dispatch);

        Assert.True(drafts.IsAccountSaving(newTarget));
        drafts.Stage(Resolve(newOwner, "second"), false);
        drafts.Cancel(newTarget);
        drafts.Save([newDraft], _ => newTarget, Dispatch);

        Assert.Equal(1, calls);
        Assert.Equal(2, drafts.Entries.Count);
        Assert.Same(newDraft, drafts.Get(newTarget));
        Assert.Null(drafts.Get(Resolve(newOwner, "second")));
        Assert.True(oldDraft.Submitting);
        Assert.False(newDraft.Submitting);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectedAcknowledgementRetainsDraftsAndStopsThatAccountWithoutRetry(bool revisionConflict)
    {
        var catalog = Catalog("local-worker", "account-a", "first", "second");
        var drafts = new DadRosterEnabledDrafts();
        drafts.Stage(Resolve(catalog, "first"), false);
        drafts.Stage(Resolve(catalog, "second"), false);
        var calls = 0;
        drafts.Save(drafts.Entries.ToList(), draft => Resolve(catalog, draft.Target.Character!.CharacterKey.Value), (request, complete) =>
        {
            calls++;
            var ack = Ack(request, accepted: false);
            ack.RevisionConflict = revisionConflict;
            complete(ack);
            return ack;
        });

        Assert.Equal(1, calls);
        Assert.Equal(2, drafts.Entries.Count);
        var rejected = drafts.Get(Resolve(catalog, "first"))!;
        Assert.False(rejected.Submitting);
        Assert.Equal(revisionConflict ? "Revision conflict; cancel and edit again." : "Save rejected", rejected.Status);
        Assert.False(drafts.IsAccountSaving(rejected.Target));
    }

    private static DadProfileUpdateAck Ack(DadProfileUpdateRequest request, bool accepted)
        => new() { RequestId = request.RequestId, Accepted = accepted };

    private static DadRosterCharacter Row(string account, string character)
        => new() { AccountKey = new(account), AccountAlias = "Synthetic crew", CharacterKey = new(character), CharacterName = "Synthetic character" };

    private static DadProfileCatalog Catalog(string owner, string account, params string[] characters)
        => new()
        {
            OwnerWorkerSessionId = new(owner), OwnerOnline = true,
            Accounts = [new DadAccountProfileRecord
            {
                AccountKey = new(account), Revision = 10,
                Characters = characters.Select(character => new DadCharacterProfileRecord
                {
                    CharacterKey = new(character), Revision = 4, Profile = new CharacterConfig { Revision = 4 },
                }).ToList(),
            }],
        };

    private static DadRosterProfileTarget Resolve(DadProfileCatalog catalog, string character)
        => DadRosterEnabledDrafts.Resolve(Row(catalog.Accounts[0].AccountKey.Value, character), [catalog], LocalOwner);
}
