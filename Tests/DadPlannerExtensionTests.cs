using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadPlannerExtensionTests
{
    [Fact]
    public void SquadronRequestDefaultsToSquadronModule()
    {
        var request = new DadRunRequest
        {
            Squadron = new DadSquadronTask { ContentFinderConditionId = 100, DutyName = "Command Mission" },
        };

        request.ApplyOrchestrationDefaults();

        Assert.Equal(DadModuleId.Squadron, request.Orchestration.ModuleTarget);
        Assert.Equal(1, request.Orchestration.RosterIntent.ExpectedPartySize);
        Assert.False(request.Orchestration.RosterIntent.RequireRemoteParticipants);
    }

    [Fact]
    public void VariantRequestDefaultsPartySizeAndLeaderAuthority()
    {
        var request = new DadRunRequest
        {
            VariantVvd = new DadVariantVvdTask
            {
                ContentFinderConditionId = 200,
                DutyName = "Variant",
                ExpectedPartySize = 3,
            },
        };

        request.ApplyOrchestrationDefaults();

        Assert.Equal(DadModuleId.VariantVvd, request.Orchestration.ModuleTarget);
        Assert.Equal(3, request.Orchestration.RosterIntent.ExpectedPartySize);
        Assert.True(request.Orchestration.RosterIntent.RequireRemoteParticipants);
        Assert.Equal(DadQueueAuthority.Leader, request.Orchestration.QueueAuthority);
    }

    [Fact]
    public void RemotePartyDefaultsInviteAuthorityToPresetLeader()
    {
        var request = new DadRunRequest
        {
            PremadeDuty = new DadPremadeDutyTask
            {
                ContentFinderConditionId = 300,
                DutyName = "Premade",
                ExpectedPartySize = 4,
            },
            Orchestration = new DadOrchestrationIntent
            {
                PreferredLeaderCharacterKey = new DadCharacterKey("Leader@Alpha"),
                RosterIntent = new DadRosterIntent { ExpectedPartySize = 4 },
                InviteAuthority = DadInviteAuthority.NotNeeded,
            },
        };

        request.ApplyOrchestrationDefaults();

        Assert.Equal(DadInviteAuthority.PresetLeader, request.Orchestration.InviteAuthority);
        Assert.Equal("Leader@Alpha", request.Orchestration.PreferredInviterCharacterKey.Value);
    }

    [Fact]
    public void ApplyOrchestrationDefaultsTrimsConfiguredAuthorityKeys()
    {
        var request = new DadRunRequest
        {
            PremadeDuty = new DadPremadeDutyTask
            {
                ContentFinderConditionId = 400,
                DutyName = "Premade",
                ExpectedPartySize = 2,
            },
            Orchestration = new DadOrchestrationIntent
            {
                PreferredLeaderCharacterKey = new DadCharacterKey(" Leader@Alpha "),
                PreferredInviterCharacterKey = new DadCharacterKey(" Inviter@Alpha "),
                InviteAuthority = DadInviteAuthority.PresetLeader,
                RosterIntent = new DadRosterIntent { ExpectedPartySize = 2 },
            },
        };

        request.ApplyOrchestrationDefaults();

        Assert.Equal("Leader@Alpha", request.Orchestration.PreferredLeaderCharacterKey.Value);
        Assert.Equal("Inviter@Alpha", request.Orchestration.PreferredInviterCharacterKey.Value);
    }

    [Fact]
    public void SquadronAndVariantCapabilitiesArePreviewBlocked()
    {
        var registry = new DadModuleRegistry();

        var squadron = registry.GetCapability(DadModuleId.Squadron);
        var variant = registry.GetCapability(DadModuleId.VariantVvd);

        Assert.True(squadron.CanPlan);
        Assert.False(squadron.CanStartQueue);
        Assert.Contains(squadron.Blockers, static blocker => blocker.Capability == "CanStartQueue");
        Assert.True(variant.CanPlan);
        Assert.False(variant.CanStartQueue);
        Assert.Contains(variant.Blockers, static blocker => blocker.Capability == "CanStartQueue");
    }

    [Fact]
    public void DailyRouletteCapabilityIsLiveAndRequeueable()
    {
        var capability = new DadModuleRegistry().GetCapability(DadModuleId.DailyMsq);

        Assert.Equal("Daily Roulette", capability.DisplayName);
        Assert.Equal(4, capability.RequiredPartySize);
        Assert.True(capability.RequiresPeers);
        Assert.True(capability.CanPlan);
        Assert.True(capability.CanStartQueue);
        Assert.True(capability.CanTrackCompletion);
        Assert.True(capability.CanRequeue);
        Assert.True(capability.CanExecuteLiveQueue);
        Assert.DoesNotContain(capability.Blockers, static blocker => blocker.Capability == "CanStartQueue");
    }

    [Fact]
    public void RestedXpStopPolicyNormalizesAndDescribesSafetyCap()
    {
        var policy = new DadRunStopPolicy
        {
            Mode = DadPlannerStopMode.RestedXpDepleted,
            SafetyCap = 0,
        }.Normalize();

        Assert.Equal(DadRunStopPolicy.DefaultSafetyCap, policy.GetSafetyCap());
        Assert.Contains("rested XP", policy.Describe(), StringComparison.OrdinalIgnoreCase);
    }
}
