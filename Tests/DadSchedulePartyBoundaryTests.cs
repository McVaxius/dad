using dad.Models;
using Xunit;

namespace dad.Tests;

public sealed class DadSchedulePartyBoundaryTests
{
    [Fact]
    public void VisitedWorldRetentionDefaultsOffAndSurvivesScheduleCloneAndPersistence()
    {
        Assert.False(new DadScheduleDefinition().StayOnVisitedWorldBetweenPresets);
        var schedule = new DadScheduleDefinition { StayOnVisitedWorldBetweenPresets = true };
        Assert.True(schedule.Clone().StayOnVisitedWorldBetweenPresets);
        var restored = System.Text.Json.JsonSerializer.Deserialize<DadScheduleDefinition>(
            System.Text.Json.JsonSerializer.Serialize(schedule));
        Assert.True(restored!.StayOnVisitedWorldBetweenPresets);
    }

    [Theory]
    [InlineData("same", true)]
    [InlineData("character", false)]
    [InlineData("account", false)]
    [InlineData("slot", false)]
    [InlineData("substitute", false)]
    [InlineData("online-only", false)]
    [InlineData("not-ready", false)]
    [InlineData("cancelled", false)]
    [InlineData("unacknowledged", false)]
    [InlineData("unknown-worker", false)]
    [InlineData("missing-token", false)]
    [InlineData("registered-island", false)]
    public void RetentionRequiresTheExactReadyTakeoverForTheNextPrimaryCharacter(string change, bool expected)
    {
        var current = new DadSchedulerSlotState
        {
            SlotId = "Slot1", RequiredAccountKey = new("synthetic-account"),
            RequiredCharacterKey = new("Synthetic Character@Home"), MatchedWorkerSessionId = new("synthetic-worker"),
            OperationToken = "exact-old-operation", WakePolicy = DadSchedulerWakePolicy.LaunchIfOffline,
            Ready = true, TakeoverPhase = DadWakeTakeoverPhase.Ready,
            AcknowledgementState = DadWakeAcknowledgementState.Executed,
        };
        var next = new DadPlannerGroupSlot
        {
            SlotId = current.SlotId, RequiredAccountKey = current.RequiredAccountKey,
            RequiredCharacterKey = current.RequiredCharacterKey, WakePolicy = current.WakePolicy,
        };
        switch (change)
        {
            case "character": next.RequiredCharacterKey = new("Another Character@Home"); break;
            case "account": next.RequiredAccountKey = new("other-account"); break;
            case "slot": next.SlotId = "Slot2"; break;
            case "substitute": next.IsSubstitute = true; break;
            case "online-only": next.WakePolicy = DadSchedulerWakePolicy.AlreadyOnlineOnly; break;
            case "not-ready": current.Ready = false; break;
            case "cancelled": current.TakeoverPhase = DadWakeTakeoverPhase.Cancelled; break;
            case "unacknowledged": current.AcknowledgementState = DadWakeAcknowledgementState.Pending; break;
            case "unknown-worker": current.MatchedWorkerSessionId = new(""); break;
            case "missing-token": current.OperationToken = ""; break;
            case "registered-island": current.IsRegisteredIsland = true; break;
        }
        Assert.Equal(expected, DadScheduleRules.CanRetainVisitedWorldTakeover(current, next));
    }

    [Fact]
    public void StandalonePresetRequiresPartyTeardown()
    {
        var boundary = DadScheduleRules.ResolveRepeatBoundary(
            string.Empty,
            string.Empty,
            repeatIteration: 0,
            entries: null);

        Assert.False(boundary.IsScheduleRun);
        Assert.False(boundary.PreservePartyAfterCompletion);
        Assert.True(boundary.RequiresPartyTeardown);
    }

    [Fact]
    public void SchedulePreservesPartyOnlyBeforeAnotherRepeatInTheSamePresetRow()
    {
        var entries = new List<DadScheduleEntry>
        {
            new() { EntryId = "row-a", GroupId = "preset-a", RepeatCount = 2 },
            new() { EntryId = "row-b", GroupId = "preset-b", RepeatCount = 1 },
        };

        var firstRepeat = DadScheduleRules.ResolveRepeatBoundary("run", "row-a", 1, entries);
        var finalRepeat = DadScheduleRules.ResolveRepeatBoundary("run", "row-a", 2, entries);
        var nextRow = DadScheduleRules.ResolveRepeatBoundary("run", "row-b", 1, entries);

        Assert.True(firstRepeat.PreservePartyAfterCompletion);
        Assert.False(firstRepeat.RequiresPartyTeardown);
        Assert.False(finalRepeat.PreservePartyAfterCompletion);
        Assert.True(finalRepeat.RequiresPartyTeardown);
        Assert.False(nextRow.PreservePartyAfterCompletion);
        Assert.True(nextRow.RequiresPartyTeardown);
    }

    [Fact]
    public void MissingScheduleRowMetadataFailsSafeToPartyTeardown()
    {
        var boundary = DadScheduleRules.ResolveRepeatBoundary(
            "run",
            "missing-row",
            repeatIteration: 1,
            [new DadScheduleEntry { EntryId = "other-row", RepeatCount = 2 }]);

        Assert.True(boundary.RequiresPartyTeardown);
    }
}
