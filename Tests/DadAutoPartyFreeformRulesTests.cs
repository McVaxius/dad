using dad.Models;
using dad.Services;
using System.Text.Json;
using Xunit;

namespace dad.Tests;

public sealed class DadAutoPartyFreeformRulesTests
{
    [Fact]
    public void MixedSelectionBuildsOneFormationOnlyRuntimeGroup()
    {
        var built = DadAutoPartyFreeformRules.TryBuild(
            [Local("local-one", 19), Remote("remote-one", 24)],
            out var formation,
            out var blocker);

        Assert.True(built, blocker);
        Assert.True(DadAutoPartyFreeformRules.IsFreeformGroupId(formation.Group.GroupId));
        Assert.True(formation.Group.AutoPartyFormationOnly);
        Assert.False(formation.Group.ScheduleEnabled);
        Assert.Equal(DadQueueAuthority.Leader, formation.Group.QueueAuthority);
        Assert.Equal(DadInviteAuthority.PresetLeader, formation.Group.InviteAuthority);
        Assert.Equal(2, formation.Group.Slots.Count);
        Assert.Null(formation.Group.Slots[0].SharedIdentity);
        Assert.Equal("opaque-remote-one", formation.Group.Slots[1].SharedIdentity?.IdentityToken);
        var binding = Assert.Single(formation.RemoteBindings);
        Assert.False(binding.OwnsQueueAuthority);
        Assert.True(binding.OwnerConsentConfirmed);
    }

    [Fact]
    public void RegisteredIslandSlotOneRetainsQueueAndInviteAuthority()
    {
        Assert.True(DadAutoPartyFreeformRules.TryBuild(
            [Remote("remote-leader", 19), Local("local-member", 24)],
            out var formation,
            out var blocker), blocker);

        var binding = Assert.Single(formation.RemoteBindings);
        Assert.True(binding.OwnsQueueAuthority);
        Assert.Equal("opaque-remote-leader", formation.Group.Slots[0].SharedIdentity?.IdentityToken);
        Assert.Equal(DadQueueAuthority.Leader, formation.Group.QueueAuthority);
        Assert.Equal(DadInviteAuthority.PresetLeader, formation.Group.InviteAuthority);
    }

    [Fact]
    public void InvalidCountsDuplicateRoutesAndNonCombatJobsFailClosed()
    {
        Assert.False(DadAutoPartyFreeformRules.TryBuild([Local("one", 19)], out _, out _));
        Assert.False(DadAutoPartyFreeformRules.TryBuild(
            Enumerable.Range(1, DadAutoPartyFreeformRules.MaximumParticipants + 1)
                .Select(index => Local($"local-{index}", 19))
                .ToList(),
            out _,
            out _));
        Assert.False(DadAutoPartyFreeformRules.TryBuild(
            [Local("duplicate", 19), Local("duplicate", 19)],
            out _,
            out _));
        Assert.False(DadAutoPartyFreeformRules.TryBuild(
            [Local("one", 19), Remote("remote", 0)],
            out _,
            out _));
    }

    [Fact]
    public void RuntimeAdmissionCloneKeepsOpaqueRoutesOutOfSerializedRequests()
    {
        var source = new DadRunRequest
        {
            Orchestration = new DadOrchestrationIntent
            {
                AutoPartyProposalId = Guid.NewGuid().ToString("D"),
                AutoPartyFormationOnly = true,
                RequiredRosterCharacters =
                [
                    new DadRosterCharacterRef
                    {
                        SharedIdentityToken = "opaque-runtime-only",
                        RequiredJobId = 19,
                    },
                ],
            },
        };

        var clone = DadAutoPartyRuntimeRequestRules.CloneForAdmission(source);
        var json = JsonSerializer.Serialize(clone);

        Assert.Equal(string.Empty, clone.Orchestration.AutoPartyProposalId);
        Assert.True(clone.Orchestration.AutoPartyFormationOnly);
        Assert.Equal("opaque-runtime-only", Assert.Single(clone.Orchestration.RequiredRosterCharacters).SharedIdentityToken);
        Assert.DoesNotContain("opaque-runtime-only", json, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(DadOrchestrationIntent.AutoPartyProposalId), json, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(DadOrchestrationIntent.AutoPartyFormationOnly), json, StringComparison.Ordinal);
        Assert.Equal(Guid.Empty, DadAutoPartyRuntimeRequestRules.BindNewProposal(new DadRunRequest()));
        Assert.NotEqual(Guid.Empty, DadAutoPartyRuntimeRequestRules.BindNewProposal(clone));
    }

    private static DadAutoPartyFreeformParticipant Local(string key, uint jobId)
        => new()
        {
            SelectionKey = key,
            DisplayLabel = key,
            Kind = DadAutoPartyFreeformParticipantKind.Local,
            AccountKey = new DadAccountKey($"account-{key}"),
            CharacterKey = new DadCharacterKey($"Character {key}@World"),
            ContentId = (ulong)key.Length + 1,
            RequestedJobId = jobId,
        };

    private static DadAutoPartyFreeformParticipant Remote(string key, uint jobId)
        => new()
        {
            SelectionKey = key,
            DisplayLabel = key,
            Kind = DadAutoPartyFreeformParticipantKind.RegisteredIsland,
            OwnerId = $"owner-{key}",
            IslandId = $"island-{key}",
            OpaqueCharacterId = $"opaque-{key}",
            RequestedJobId = jobId,
        };
}
