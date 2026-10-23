extern alias DadRuntime;

using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;
using M = DadRuntime::dad.Models;
using S = DadRuntime::dad.Services;

namespace dad.Tests;

public sealed class DadDailyCompletionNativeRoutingTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void RealParticipantCompletionReaderUsesItsOwnCapturedTerritoryNotTheLeaders(bool participantEntered, bool expected)
    {
        var queue = (S.DadLocalDutyQueueService)RuntimeHelpers.GetUninitializedObject(typeof(S.DadLocalDutyQueueService));
        var started = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        Set(queue, "lastDutyCompletedTerritoryId", 1036u);
        Set(queue, "lastDutyCompletedUtc", started.AddMinutes(8));
        var leader = new S.DadRouletteTerritoryEvidenceGate();
        leader.ObserveEntryEvidence();
        Assert.True(leader.TryCapture(true, 1036));
        var participant = new S.DadRouletteTerritoryEvidenceGate();
        if (participantEntered)
        {
            participant.ObserveEntryEvidence();
            Assert.True(participant.TryCapture(true, 1036));
        }
        Set(queue, "rouletteTerritoryGate", leader);
        Set(queue, "participantRouletteTerritoryGate", participant);
        var content = new S.DadLocalDutyResolvedContent { TargetKind = M.DadQueueTargetKind.Roulette };
        Assert.True(queue.HasDutyCompleted(content, started));
        Assert.Equal(expected, queue.HasParticipantDutyCompleted(content, started));
        Set(queue, "lastDutyCompletedTerritoryId", 999u);
        Assert.False(queue.HasParticipantDutyCompleted(content, started));
        Set(queue, "lastDutyCompletedTerritoryId", 1036u);
        Set(queue, "lastDutyCompletedUtc", started.AddSeconds(-1));
        Assert.False(queue.HasParticipantDutyCompleted(content, started));
    }

    [Fact]
    public void ProductionWiringCapturesPerWorkerWitnessBeforeExitAndPersistsOnlyAfterSuccessfulModuleSettlement()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "dad.csproj"))) root = root.Parent;
        Assert.NotNull(root);
        string Read(string file) => File.ReadAllText(Path.Combine(root!.FullName, "Services", file));
        var worker = Read("DadWorkerExecutionService.cs");
        var capture = worker.IndexOf("queueExecutionService.HasParticipantDutyCompleted(participantQueueContent, startedAtUtc)", StringComparison.Ordinal);
        var exit = worker.IndexOf("if (enteredDuty && !condition[ConditionFlag.BoundByDuty])", capture, StringComparison.Ordinal);
        Assert.True(capture > 0 && exit > capture);
        Assert.Contains("DutyCompletedAtUtc = participantDutyCompletedAtUtc", worker);
        Assert.Contains("status.DutyCompletedAtUtc = now", Read("DadPremadeDutyExecutor.cs"));
        var coordinator = Read("DadCoordinatorService.cs");
        var success = coordinator.IndexOf("statuses.All(static worker => worker.IsTerminal && worker.Success)", StringComparison.Ordinal);
        var settlement = coordinator.IndexOf("TrySettleAutoPartyModule", success, StringComparison.Ordinal);
        var persistence = coordinator.IndexOf("RecordDailyRouletteCompletions(module)", success, StringComparison.Ordinal);
        Assert.True(success > 0 && settlement > success && persistence > settlement);
        Assert.Contains("configuration.TryPersistNow()", coordinator);
        var scheduler = Read("DadSchedulerService.cs");
        Assert.Contains("active.ResetBoundaryUtc != DadScheduleRules.GetDailyResetBoundaryUtc(responseReceivedAtUtc)", scheduler);
    }

    private static void Set(object instance, string field, object value)
        => instance.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
}
