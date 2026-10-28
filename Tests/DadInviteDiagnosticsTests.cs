using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadInviteDiagnosticsTests
{
    private static readonly DateTime Start = new(2026, 10, 28, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RuntimePreparationCannotReintroduceJobsThroughSchedulerPreviewBinding()
    {
        var saved = new DadPlannerGroup
        {
            ActivityMode = DadPlannerActivityMode.DailyRoulette,
            Slots = [new DadPlannerGroupSlot
            {
                SlotId = "Slot1", RequiredAccountKey = new("test-account"),
                RequiredCharacterKey = new("Test Character@World"), RequiredJobId = 26,
                CharacterLoadInstruction = new() { Enabled = true, CommandTemplate = "/synthetic" },
            }],
        };
        var runtime = DadCrewToolsRules.BuildRuntimeFormationGroup(saved);
        var preview = new DadPlannerRunRequestPreview
        {
            Request = new DadRunRequest
            {
                DailyMsq = new(),
                Orchestration = new()
                {
                    AutoPartyFormationOnly = true,
                    RequiredRosterCharacters = [new() { ContentId = 100, RequiredJobId = 26 }],
                    PreferredRosterCharacters = [new() { ContentId = 100, RequiredJobId = 26 }],
                },
            },
            PlannerPreview = new()
            {
                SelectedCharacters = [new()
                {
                    SlotId = "Slot1", CharacterKey = "Test Character@World",
                    RequiredAccountKey = new("test-account"), RequiredJobId = 26,
                }],
            },
        };
        var alliance = new DadActivityPreset { SelectedCharacters = [new() { SlotId = "Slot1", RequiredJobId = 26 }] };
        DadInviteDiagnostics.PrepareRuntimeCrew(runtime, preview, alliance);
        var bound = DadEffectivePlannerGroupProjection.BindResolvedSchedulerSlots(
            DadEffectivePlannerGroupProjection.Project(runtime, DadPlannerActivityMode.DailyRoulette, 4),
            preview.PlannerPreview.SelectedCharacters);
        Assert.Null(Assert.Single(bound.Slots).RequiredJobId);
        Assert.Equal(DadSchedulerWakePolicy.AlreadyOnlineOnly, bound.Slots[0].WakePolicy);
        Assert.False(bound.Slots[0].CharacterLoadInstruction.Enabled);
        Assert.Null(preview.Request.Orchestration.RequiredRosterCharacters[0].RequiredJobId);
        Assert.Null(preview.Request.Orchestration.PreferredRosterCharacters[0].RequiredJobId);
        Assert.Null(alliance.SelectedCharacters[0].RequiredJobId);
        Assert.Equal(DadModuleId.None, preview.Request.Orchestration.ModuleTarget);
        Assert.False(preview.Request.RefreshRecommendedGear);
        Assert.Equal(26u, saved.Slots[0].RequiredJobId);
        Assert.True(saved.Slots[0].CharacterLoadInstruction.Enabled);
        Assert.False(saved.AutoPartyFormationOnly);
    }

    [Fact]
    public void RuntimePreparationRejectsOrdinaryRunWithoutChangingIt()
    {
        var group = new DadPlannerGroup { Slots = [new() { RequiredJobId = 26 }] };
        var preview = new DadPlannerRunRequestPreview { Request = new() };
        Assert.Throws<InvalidOperationException>(() => DadInviteDiagnostics.PrepareRuntimeCrew(group, preview, new()));
        Assert.Equal(26u, group.Slots[0].RequiredJobId);
    }

    [Theory]
    [InlineData("Mixed", null)]
    [InlineData("Cross", DadNativePartyInviteType.CrossWorldContentId)]
    [InlineData("Serial", DadNativePartyInviteType.CrossWorldContentId)]
    [InlineData("Reverse", DadNativePartyInviteType.CrossWorldContentId)]
    public void ExactScopeChoosesModeWithoutChangingOtherRuns(string modeName, DadNativePartyInviteType? expected)
    {
        var mode = Enum.Parse<DadInviteTestMode>(modeName);
        var d = New(mode);
        var first = mode == DadInviteTestMode.Reverse ? 400ul : 200ul;
        Assert.True(d.Allows("run", first, [100], Start, true, out var type));
        Assert.Equal(expected, type);
        Assert.True(d.Allows("unrelated", 999, [], Start, false, out type));
        Assert.Null(type);
        Assert.True(d.Active);
    }

    [Theory]
    [InlineData("Serial", 200ul, 300ul, 400ul)]
    [InlineData("Reverse", 400ul, 300ul, 200ul)]
    public void SequentialDispatchWaitsForActualMembership(string modeName, ulong first, ulong second, ulong third)
    {
        var mode = Enum.Parse<DadInviteTestMode>(modeName);
        var d = New(mode);
        Assert.False(d.Allows("run", second, [100], Start, true, out _));
        Assert.False(d.Allows("run", third, [100], Start, true, out _));
        Assert.True(d.Allows("run", first, [100], Start, true, out _));
        Assert.False(d.Allows("run", second, [100], Start.AddSeconds(30), true, out _));
        Assert.True(d.Allows("run", second, [100, first], Start.AddSeconds(31), true, out _));
        Assert.True(d.Allows("run", third, [100, first, second], Start.AddSeconds(32), true, out _));
        Assert.Contains("membership-observed recipient=1", d.Report());
    }

    [Theory]
    [InlineData("Mixed")]
    [InlineData("Cross")]
    public void ParallelModesAllowAllExactRecipients(string modeName)
    {
        var mode = Enum.Parse<DadInviteTestMode>(modeName);
        var d = New(mode);
        foreach (var id in new ulong[] { 200, 300, 400 }) Assert.True(d.Allows("run", id, [100], Start, true, out _));
        Assert.False(d.Allows("run", 999, [100], Start, true, out _));
    }

    [Theory]
    [InlineData(true, 120)]
    [InlineData(false, 1)]
    public void DeadlineAndDebugOffRevokeExactRequest(bool enabled, int seconds)
    {
        var d = New();
        Assert.False(d.Allows("run", 200, [100], Start.AddSeconds(seconds), enabled, out _));
        Assert.False(d.Active);
        Assert.False(d.Allows("run", 200, [100], Start.AddSeconds(seconds + 1), true, out _));
        Assert.True(d.Allows("new-run", 200, [100], Start.AddSeconds(seconds + 1), true, out var type));
        Assert.Null(type);
    }

    [Fact]
    public void StopIsIdempotentAndRetainsClosedScope()
    {
        var d = New();
        d.Finish(Start.AddSeconds(3), "stopped");
        var report = d.Report();
        d.Finish(Start.AddSeconds(5), "duplicate");
        Assert.Equal(report, d.Report());
        Assert.False(d.Allows("run", 200, [], Start.AddSeconds(6), true, out _));
        Assert.True(d.Allows("other", 200, [], Start.AddSeconds(6), true, out _));
    }

    [Fact]
    public void JournalIsBoundedAndIndependentObservationChannelsDoNotSpam()
    {
        var d = New();
        for (var i = 0; i < 100; i++)
        {
            d.Observe(Start, "raw pending", "raw");
            d.Observe(Start, "not-armed", "decision");
        }
        Assert.Equal(4, d.Report().Split('\n').Length); // Summary, start, two observations.
        for (var i = 0; i < 500; i++) d.Record(Start, "event " + i);
        Assert.Equal(DadInviteDiagnostics.Capacity + 1, d.Report().Split('\n').Length);
    }

    [Fact]
    public void NewTestClearsPreviousModeAndFreezesItsOwnRoster()
    {
        var ids = new ulong[] { 200, 300 };
        var d = new DadInviteDiagnostics();
        d.Start("old", DadInviteTestMode.Reverse, ids, Start);
        ids[1] = 999;
        Assert.True(d.Allows("old", 300, [], Start, true, out _));
        d.Finish(Start, "done");
        d.Start("new", DadInviteTestMode.Mixed, [500], Start);
        Assert.True(d.Allows("new", 500, [], Start, true, out var type));
        Assert.Null(type);
        Assert.DoesNotContain("request=old", d.Report());
    }

    [Fact]
    public void InvalidRosterAndActiveReplacementAreRejected()
    {
        var d = new DadInviteDiagnostics();
        Assert.Throws<ArgumentException>(() => d.Start("", DadInviteTestMode.Cross, [200], Start));
        Assert.Throws<ArgumentException>(() => d.Start("run", DadInviteTestMode.Cross, [0], Start));
        Assert.Throws<ArgumentException>(() => d.Start("run", DadInviteTestMode.Cross, [200, 200], Start));
        Assert.Throws<ArgumentException>(() => d.Start("run", DadInviteTestMode.Cross, [], Start));
        d.Start("run", DadInviteTestMode.Mixed, [200], Start);
        Assert.Throws<InvalidOperationException>(() => d.Start("new", DadInviteTestMode.Cross, [200], Start));
    }

    private static DadInviteDiagnostics New(DadInviteTestMode mode = DadInviteTestMode.Serial)
    {
        var d = new DadInviteDiagnostics();
        d.Start("run", mode, [200, 300, 400], Start);
        return d;
    }
}
