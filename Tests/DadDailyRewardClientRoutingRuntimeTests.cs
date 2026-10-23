extern alias DadRuntime;
extern alias DalamudApi;

using System.Reflection;
using System.Runtime.CompilerServices;
using DalamudApi::Dalamud.Plugin.Services;
using Xunit;
using Scheduler = DadRuntime::dad.Services.DadSchedulerService;
using M = DadRuntime::dad.Models;

namespace dad.Tests;

public sealed class DadDailyRewardClientRoutingRuntimeTests
{
    [Theory]
    [InlineData(0, 2, false, false)]
    [InlineData(1, 1, false, false)]
    [InlineData(2, 1, false, false)]
    [InlineData(3, 0, true, false)]
    [InlineData(3, 2, false, true)]
    public void DurableDailyReceiptsAreConsumedBeforeAnyWakeAndOnlyForCheckedExactCharacters(int receiptMask, int missing, bool skipped, bool legacy)
    {
        var configuration = new DadRuntime::dad.Configuration { RunAsServerDad = true };
        var now = DateTime.UtcNow;
        var target = new M.DadQueueTarget { Kind = M.DadQueueTargetKind.Roulette, RouletteId = 1, Key = "ContentRoulette:1" };
        for (var index = 0; index < 2; index++)
        {
            if ((receiptMask & (1 << index)) == 0) continue;
            var slot = index == 0 ? 2 : 4;
            var request = new M.DadRouletteRewardProbeRequestDto
            {
                OperationId = $"receipt-{slot}", SchedulerRunId = "previous-scheduler", SlotId = $"Slot{slot}",
                RouteWorkerSessionId = new($"old-worker-{slot}"), AccountKey = new($"account-{slot}"),
                CharacterKey = new($"character-{slot}"), CharacterContentId = (ulong)slot,
                RouletteId = 1, RouletteKey = target.Key, RequestedAtUtc = now,
            };
            var response = M.DadRouletteRewardProbeResultDto.FromRequest(request, M.DadRouletteRewardProbeOutcome.Received, "", now, 1, 1);
            response.RewardTextProofVersion = M.DadRouletteRewardProbeIdentityRules.CurrentRewardTextProofVersion;
            Assert.True(M.DadDailyRouletteCompletionRules.RecordReward(configuration.DailyRouletteCompletions, request, response, now));
        }
        // Reload round-trip: runtime state, old worker/session and schedule IDs are not required to reuse exact receipts.
        if (legacy)
            foreach (var row in configuration.DailyRouletteCompletions) row.RewardTextProofVersion = 0;
        configuration = System.Text.Json.JsonSerializer.Deserialize<DadRuntime::dad.Configuration>(
            System.Text.Json.JsonSerializer.Serialize(configuration))!;
        var scheduler = new Scheduler(configuration, null!, null!, null!, null!, null!, null!, null!,
            DispatchProxy.Create<IPluginLog, DadScheduleDutyExitRetryRuntimeTests.QuietLog>());
        var state = new M.DadSchedulerPresetState
        {
            SchedulerRunId = "new-scheduler", JobId = "new-job", Phase = M.DadSchedulerPresetPhase.Resolving,
            StartedAtUtc = now,
            Slots = Enumerable.Range(1, 4).Select(i => new M.DadSchedulerSlotState
            {
                SlotId = $"Slot{i}", RequiredAccountKey = new($"account-{i}"), RequiredCharacterKey = new($"character-{i}"),
            }).ToList(),
        };
        Set(scheduler, "currentState", state);
        Set(scheduler, "frozenPlannerRequest", new M.DadRunRequest
        {
            DailyMsq = new() { QueueTarget = target },
            Orchestration = new() { RequiredRosterCharacters = Enumerable.Range(1, 4).Select(i => new M.DadRosterCharacterRef
                { AccountKey = new($"account-{i}"), CharacterKey = new($"character-{i}"), ContentId = (ulong)i }).ToList() },
        });
        var group = new M.DadPlannerGroup
        {
            ActivityMode = M.DadPlannerActivityMode.DailyRoulette,
            Slots = Enumerable.Range(1, 4).Select(i => new M.DadPlannerGroupSlot
                { SlotId = $"Slot{i}", SkipIfDailyRouletteRewardReceived = i is 2 or 4 }).ToList(),
        };
        Assert.True((bool)typeof(Scheduler).GetMethod("TryBeginDailyRewardPreflight", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(scheduler, [group])!);
        Assert.Equal(missing, state.Slots.Count);
        Assert.Equal(skipped ? M.DadSchedulerPresetPhase.Skipped : M.DadSchedulerPresetPhase.DailyRewardPreflight, state.Phase);
        if (skipped)
        {
            Assert.Equal(M.DadSchedulerSkipKind.DailyRouletteReward, state.SkipKind);
            Assert.Single(configuration.SchedulerHistory);
            Assert.Contains("persisted exact-character receipt", state.Summary);
        }
        Assert.All(state.Slots, slot => Assert.Contains(slot.SlotId, new[] { "Slot2", "Slot4" }));
    }

    [Theory]
    [InlineData(false, false, 2, false)]
    [InlineData(true, false, 2, false)]
    [InlineData(true, true, 2, false)]
    [InlineData(false, false, 10, false)]
    [InlineData(true, false, 10, false)]
    [InlineData(true, true, 10, false)]
    [InlineData(false, false, 2, true)]
    [InlineData(true, false, 2, true)]
    [InlineData(true, true, 2, true)]
    [InlineData(false, false, 10, true)]
    [InlineData(true, false, 10, true)]
    [InlineData(true, true, 10, true)]
    public void ActualSchedulerChecksOnlySelectedRemoteSlotsNotTheCoordinator(bool scheduled, bool dailyReset, int checkedMask, bool hasLevelTargets)
    {
        var scheduler = (Scheduler)RuntimeHelpers.GetUninitializedObject(typeof(Scheduler));
        Set(scheduler, "configuration", new DadRuntime::dad.Configuration());
        Set(scheduler, "log", DispatchProxy.Create<IPluginLog, DadScheduleDutyExitRetryRuntimeTests.QuietLog>());
        var target = new M.DadQueueTarget { Kind = M.DadQueueTargetKind.Roulette, RouletteId = 1, Key = "ContentRoulette:1" };
        Set(scheduler, "frozenPlannerRequest", new M.DadRunRequest
        {
            DailyMsq = new() { QueueTarget = target },
            Orchestration = new()
            {
                RequiredRosterCharacters = Enumerable.Range(1, 4).Select(i => new M.DadRosterCharacterRef
                { AccountKey = new($"account-{i}"), CharacterKey = new($"character-{i}"), ContentId = (ulong)i }).ToList(),
            },
        });
        var assignmentsField = typeof(Scheduler).GetField("frozenEarlyAssignments", BindingFlags.Instance | BindingFlags.NonPublic)!;
        assignmentsField.SetValue(scheduler, Activator.CreateInstance(assignmentsField.FieldType));
        Set(scheduler, "activeJob", new M.DadScheduledCrewJob
        {
            ScheduleCadence = dailyReset ? M.DadScheduleCadence.DailyReset : M.DadScheduleCadence.Manual,
            ScheduleId = scheduled ? "schedule" : "", ScheduleRunId = scheduled ? "run" : "",
            ScheduleEntryId = scheduled ? "entry" : "",
        });
        var state = new M.DadSchedulerPresetState
        {
            SchedulerRunId = "scheduler-job", StartedAtUtc = DateTime.UtcNow,
            Slots = Enumerable.Range(1, 4).Select(i => new M.DadSchedulerSlotState
            {
                SlotId = $"Slot{i}", MatchedWorkerSessionId = new($"worker-{i}"),
                RequiredAccountKey = new($"account-{i}"), RequiredCharacterKey = new($"character-{i}"),
            }).ToList(),
        };
        Set(scheduler, "currentState", state);
        var group = new M.DadPlannerGroup
        {
            ActivityMode = M.DadPlannerActivityMode.DailyRoulette,
            Slots = Enumerable.Range(1, 4).Select(i => new M.DadPlannerGroupSlot
            {
                SlotId = $"Slot{i}", SkipIfDailyRouletteRewardReceived = (checkedMask & (1 << (i - 1))) != 0,
                LevelSeekTarget = hasLevelTargets ? 70 : null,
            }).ToList(),
        };
        if (hasLevelTargets)
            Set(scheduler, "frozenLevelSeekGroup", group);
        var begun = (bool)typeof(Scheduler).GetMethod("TryBeginDailyRewardPreflight", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(scheduler, [group])!;
        Assert.True(begun);
        Assert.Equal(M.DadSchedulerPresetPhase.DailyRewardPreflight, state.Phase);
        var checkedIndices = Enumerable.Range(1, 4).Where(i => (checkedMask & (1 << (i - 1))) != 0).ToList();
        Assert.Equal(checkedIndices.Select(i => $"Slot{i}"), state.Slots.Select(slot => slot.SlotId));
        Assert.Equal(checkedIndices.Select(i => $"worker-{i}"), state.Slots.Select(slot => slot.MatchedWorkerSessionId.Value));
        var ordinary = (List<M.DadSchedulerSlotState>)typeof(Scheduler)
            .GetField("frozenOrdinarySlots", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scheduler)!;
        Assert.Equal(4, ordinary.Count);
        Assert.False((bool)typeof(Scheduler).GetField("dependencyGateCommitted", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scheduler)!);
        Assert.Equal(hasLevelTargets, typeof(Scheduler).GetField("frozenLevelSeekGroup", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scheduler) != null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SchedulerValidatesRewardRepliesAgainstTimeCapturedAfterDispatch(bool cancellation)
    {
        var source = SchedulerSource();
        var dispatch = cancellation
            ? "var result = transportService.SendRouletteRewardProbe(participant, pending.Request);"
            : "var response = transportService.SendRouletteRewardProbe(participant, request);";
        var start = source.IndexOf(dispatch, StringComparison.Ordinal);
        Assert.True(start >= 0);
        var section = source[start..source.IndexOf("\n    }", start, StringComparison.Ordinal)];
        var captured = section.IndexOf("var responseReceivedAtUtc = DadClock.UtcNow;", StringComparison.Ordinal);
        var validated = section.IndexOf(cancellation
            ? "IsRewardProbeCancellationAcknowledged(pending.Request, result, responseReceivedAtUtc)"
            : "TryValidateResponse(request, response, responseReceivedAtUtc, out", StringComparison.Ordinal);
        Assert.True(captured > 0 && validated > captured,
            "A synchronous local reply must be validated against the receive clock, not the pre-dispatch clock.");
    }

    [Fact]
    public void EarlyPreflightReturnsBeforeUncheckedWakesOrPartyAndQueueDispatch()
    {
        var source = SchedulerSource();
        var update = source[source.IndexOf("private void UpdateCore(", StringComparison.Ordinal)..];
        var preflight = update.IndexOf("if (currentState.Phase == DadSchedulerPresetPhase.DailyRewardPreflight)", StringComparison.Ordinal);
        var early = update[preflight..update.IndexOf("if (frozenPlannerRequest == null)", preflight, StringComparison.Ordinal)];
        Assert.Contains("UpdateDailyRewardPreflight();", early);
        Assert.Contains("return;", early);
        Assert.True(preflight < update.IndexOf("DeliverEarlyJobAssignments(", StringComparison.Ordinal));
        Assert.True(preflight < update.IndexOf("AdvanceWakeTakeoverPipelines(currentState.Slots", StringComparison.Ordinal));
        Assert.True(preflight < update.IndexOf("autoPartyAuthorizationGate?.Invoke", StringComparison.Ordinal));
        Assert.True(preflight < update.IndexOf("startPlannerRequest(", StringComparison.Ordinal));
        var initialPreflight = source.IndexOf("if (TryBeginDailyRewardPreflight(effectiveGroup))", StringComparison.Ordinal);
        Assert.True(initialPreflight >= 0);
        var start = source[initialPreflight..];
        Assert.Contains("return CurrentState;", start[..start.IndexOf("var transientRuntimeReadiness", StringComparison.Ordinal)]);
        Assert.DoesNotContain("frozenLevelSeekGroup == null",
            source[..source.IndexOf("private DadSchedulerPresetState StartLevelingOperation(", StringComparison.Ordinal)]);
    }

    [Fact]
    public void RealSchedulerAcceptsImmediateLocalProbeCancellationWithoutLeavingCleanupBlocked()
    {
        var config = new DadRuntime::dad.Configuration { PluginEnabled = true, LocalOnlyModeEnabled = false };
        var worker = new M.DadWorkerSessionId("local-worker");
        var presence = (DadRuntime::dad.Services.DadPresenceService)RuntimeHelpers.GetUninitializedObject(typeof(DadRuntime::dad.Services.DadPresenceService));
        Set(presence, "<WorkerSessionId>k__BackingField", worker);
        var probe = (DadRuntime::dad.Services.DadRouletteRewardProbeService)RuntimeHelpers.GetUninitializedObject(typeof(DadRuntime::dad.Services.DadRouletteRewardProbeService));
        var transport = (DadRuntime::dad.Services.DadTransportService)RuntimeHelpers.GetUninitializedObject(typeof(DadRuntime::dad.Services.DadTransportService));
        Set(transport, "configuration", config);
        Set(transport, "presenceService", presence);
        Set(transport, "rouletteRewardProbeService", probe);
        var scheduler = (Scheduler)RuntimeHelpers.GetUninitializedObject(typeof(Scheduler));
        Set(scheduler, "transportService", transport);
        Set(scheduler, "log", DispatchProxy.Create<IPluginLog, DadScheduleDutyExitRetryRuntimeTests.QuietLog>());
        var field = typeof(Scheduler).GetField("pendingRewardProbeCancellations", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var pending = (System.Collections.IDictionary)Activator.CreateInstance(field.FieldType)!;
        field.SetValue(scheduler, pending);
        var entry = Activator.CreateInstance(field.FieldType.GetGenericArguments()[1], nonPublic: true)!;
        var request = new M.DadRouletteRewardProbeRequestDto
        {
            OperationId = "cancel-probe", Operation = M.DadRouletteRewardProbeOperation.Cancel,
            SchedulerRunId = "scheduler", SlotId = "Slot1", RouteWorkerSessionId = worker,
            AccountKey = new("account"), CharacterKey = new("character"), CharacterContentId = 123,
            RouletteId = 1, RouletteKey = "ContentRoulette:1", RequestedAtUtc = DateTime.UtcNow.AddSeconds(-2),
        };
        entry.GetType().GetProperty("Request")!.SetValue(entry, request);
        entry.GetType().GetProperty("CancellationDeadlineUtc")!.SetValue(entry, DateTime.UtcNow.AddMinutes(1));
        pending.Add(request.OperationId, entry);
        scheduler.UpdatePendingRewardProbeCancellations();
        Assert.Empty(pending);
    }

    private static string SchedulerSource()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "dad.csproj"))) root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "Services", "DadSchedulerService.cs"));
    }

    [Theory]
    [InlineData(0, "Slot1,Slot2,Slot4")]
    [InlineData(1, "Slot2,Slot3,Slot4")]
    [InlineData(2, "Slot3,Slot4")]
    [InlineData(3, "Slot4")]
    [InlineData(-1, "")]
    [InlineData(4, "")]
    public void DailyWakeBatchOverlapsDistinctWorkersButNeverTwoRowsOnOneWorker(int cursor, string expected)
    {
        var slots = new List<M.DadSchedulerSlotState>
        {
            new() { SlotId = "Slot1", MatchedWorkerSessionId = new("worker-A"), Ready = true },
            new() { SlotId = "Slot2", MatchedWorkerSessionId = new("worker-B") },
            new() { SlotId = "Slot3", MatchedWorkerSessionId = new("WORKER-a") },
            new() { SlotId = "Slot4", MatchedWorkerSessionId = new("") },
        };
        var method = typeof(Scheduler).GetMethod("SelectDailyRewardWakeSlots", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var batch = (IReadOnlyList<M.DadSchedulerSlotState>)method!.Invoke(null, [slots, cursor])!;
        Assert.Equal(expected.Length == 0 ? [] : expected.Split(','), batch.Select(s => s.SlotId));
        Assert.Equal(4, slots.Count);
        foreach (var slot in batch) Assert.Contains(slot, slots);
    }

    [Fact]
    public void IndependentUnresolvedRoutesAreNotAccidentallySerializedUnderAnEmptyWorkerKey()
    {
        var slots = new List<M.DadSchedulerSlotState>
        {
            new() { SlotId = "Slot1", MatchedWorkerSessionId = new("") },
            new() { SlotId = "Slot2", MatchedWorkerSessionId = new("") },
        };
        var method = typeof(Scheduler).GetMethod("SelectDailyRewardWakeSlots", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var batch = (IReadOnlyList<M.DadSchedulerSlotState>)method!.Invoke(null, [slots, 0])!;
        Assert.Equal(new[] { "Slot1", "Slot2" }, batch.Select(s => s.SlotId));
    }

    [Theory]
    [InlineData("Daily unclaimed")]
    [InlineData("Daily unknown")]
    [InlineData("Daily timed out")]
    public void RealFailOpenContinuationRetainsAlreadyIssuedParallelWakeOperations(string reason)
    {
        var scheduler = (Scheduler)RuntimeHelpers.GetUninitializedObject(typeof(Scheduler));
        Set(scheduler, "configuration", new DadRuntime::dad.Configuration());
        Set(scheduler, "log", DispatchProxy.Create<IPluginLog, DadScheduleDutyExitRetryRuntimeTests.QuietLog>());
        var assignments = typeof(Scheduler).GetField("frozenEarlyAssignments", BindingFlags.Instance | BindingFlags.NonPublic)!;
        assignments.SetValue(scheduler, Activator.CreateInstance(assignments.FieldType));
        var originals = Enumerable.Range(1, 4).Select(i => new M.DadSchedulerSlotState
        {
            SlotId = $"Slot{i}", RequiredAccountKey = new($"account-{i}"),
            RequiredCharacterKey = new($"character-{i}"), RequiredJobId = i < 3 ? 26u : null,
            MatchedWorkerSessionId = new($"old-worker-{i}"),
        }).ToList();
        Set(scheduler, "frozenOrdinarySlots", originals);
        Set(scheduler, "frozenPlannerRequest", new M.DadRunRequest
        {
            Orchestration = new()
            {
                RequiredRosterCharacters = originals.Select((s, i) => new M.DadRosterCharacterRef
                { AccountKey = s.RequiredAccountKey, CharacterKey = s.RequiredCharacterKey, ContentId = (ulong)i + 1 }).ToList(),
            },
        });
        var checkedRows = originals.Take(2).Select(s => s.Clone()).ToList();
        var issuedAt = DateTime.UtcNow.AddSeconds(-10);
        foreach (var row in checkedRows)
        {
            row.MatchedWorkerSessionId = new($"live-worker-{row.SlotId}");
            row.OperationToken = "same-scheduler-operation";
            row.TakeoverPhase = M.DadWakeTakeoverPhase.RelogCommitted;
            row.RelogIssued = true;
            row.RelogIssuedUtc = issuedAt;
            row.RelogExecutionUtc = issuedAt;
            row.CommitKind = M.DadWakeCommitKind.Relog;
            row.AcknowledgementState = M.DadWakeAcknowledgementState.Accepted;
            row.LevelSeekTarget = 99; // Mutable runtime projection must not replace frozen plan settings.
        }
        var state = new M.DadSchedulerPresetState { SchedulerRunId = "same-scheduler-operation", Slots = checkedRows };
        Set(scheduler, "currentState", state);
        typeof(Scheduler).GetMethod("ContinueAfterDailyRewardPreflight", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(scheduler, [reason]);
        Assert.Equal(M.DadSchedulerPresetPhase.Resolving, state.Phase);
        Assert.Equal(4, state.Slots.Count);
        Assert.Equal(reason, state.Summary);
        foreach (var row in state.Slots.Take(2))
        {
            Assert.Equal("same-scheduler-operation", row.OperationToken);
            Assert.Equal(M.DadWakeTakeoverPhase.RelogCommitted, row.TakeoverPhase);
            Assert.Equal(M.DadWakeCommitKind.Relog, row.CommitKind);
            Assert.Equal(M.DadWakeAcknowledgementState.Accepted, row.AcknowledgementState);
            Assert.Equal(issuedAt, row.RelogExecutionUtc);
            Assert.Equal($"live-worker-{row.SlotId}", row.MatchedWorkerSessionId.Value);
            Assert.True(row.RelogIssued);
            Assert.Null(row.LevelSeekTarget);
        }
        Assert.All(state.Slots.Skip(2), row => Assert.Empty(row.OperationToken));
        Assert.All(originals, row => Assert.Empty(row.OperationToken));
        Assert.NotSame(checkedRows[0], state.Slots[0]);
        Assert.NotSame(originals[2], state.Slots[2]);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("character")]
    [InlineData("job")]
    [InlineData("wake")]
    [InlineData("launch")]
    [InlineData("operation")]
    [InlineData("island")]
    public void FailOpenNeverCarriesWakeCommandsFromAContradictoryFrozenIdentity(string mismatch)
    {
        var scheduler = (Scheduler)RuntimeHelpers.GetUninitializedObject(typeof(Scheduler));
        Set(scheduler, "log", DispatchProxy.Create<IPluginLog, DadScheduleDutyExitRetryRuntimeTests.QuietLog>());
        var assignments = typeof(Scheduler).GetField("frozenEarlyAssignments", BindingFlags.Instance | BindingFlags.NonPublic)!;
        assignments.SetValue(scheduler, Activator.CreateInstance(assignments.FieldType));
        var original = new M.DadSchedulerSlotState
        {
            SlotId = "Slot1", RequiredAccountKey = new("account"), RequiredCharacterKey = new("character"),
            RequiredJobId = 26, LaunchProfileId = "launch",
        };
        var projected = original.Clone();
        projected.OperationToken = "scheduler";
        projected.RelogIssued = true;
        projected.TakeoverPhase = M.DadWakeTakeoverPhase.RelogCommitted;
        switch (mismatch)
        {
            case "account": projected.RequiredAccountKey = new("other-account"); break;
            case "character": projected.RequiredCharacterKey = new("other-character"); break;
            case "job": projected.RequiredJobId = 27; break;
            case "wake": projected.WakePolicy = M.DadSchedulerWakePolicy.AlreadyOnlineOnly; break;
            case "launch": projected.LaunchProfileId = "other-launch"; break;
            case "operation": projected.OperationToken = "old-scheduler"; break;
            case "island": projected.SharedIdentityToken = "other-island"; break;
        }
        var state = new M.DadSchedulerPresetState { SchedulerRunId = "scheduler", Slots = [projected] };
        Set(scheduler, "currentState", state);
        Set(scheduler, "frozenOrdinarySlots", new List<M.DadSchedulerSlotState> { original });
        Set(scheduler, "frozenPlannerRequest", new M.DadRunRequest
        {
            Orchestration = new()
            {
                RequiredRosterCharacters = [new M.DadRosterCharacterRef
                { AccountKey = original.RequiredAccountKey, CharacterKey = original.RequiredCharacterKey, ContentId = 123 }],
            },
        });
        typeof(Scheduler).GetMethod("ContinueAfterDailyRewardPreflight", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(scheduler, ["unknown"]);
        var restored = Assert.Single(state.Slots);
        Assert.Empty(restored.OperationToken);
        Assert.False(restored.RelogIssued);
        Assert.Equal(26u, restored.RequiredJobId);
        Assert.Equal("account", restored.RequiredAccountKey.Value);
        Assert.Equal("character", restored.RequiredCharacterKey.Value);
    }

    [Fact]
    public void DailyUpdateAdvancesTheParallelCheckedBatchBeforeWaitingForCurrentProbeReadiness()
    {
        var source = SchedulerSource();
        var start = source.IndexOf("private void UpdateDailyRewardPreflight()", StringComparison.Ordinal);
        var end = source.IndexOf("private void ContinueAfterDailyRewardPreflight", start, StringComparison.Ordinal);
        var update = source[start..end];
        var selection = update.IndexOf("SelectDailyRewardWakeSlots(currentState.Slots, active.CurrentSlotIndex)", StringComparison.Ordinal);
        var advance = update.IndexOf("AdvanceWakeTakeoverPipelines(wakeSlots, out", StringComparison.Ordinal);
        var wait = update.IndexOf("if (!slot.Ready)", StringComparison.Ordinal);
        Assert.True(selection >= 0 && advance > selection && wait > advance);
        Assert.DoesNotContain("AdvanceWakeTakeoverPipelines([slot]", update);
        Assert.DoesNotContain("frozenOrdinarySlots", update);
    }

    [Fact]
    public void ProductionDispatchRoutesEachProbeToItsExactWorkerAndValidatesTheResponse()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "dad.csproj"))) root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root!.FullName, "Services", "DadSchedulerService.cs"));
        Assert.Contains("RouteWorkerSessionId = participant.WorkerSessionId", source);
        Assert.Contains("transportService.SendRouletteRewardProbe(participant, request)", source);
        Assert.Contains("DadRouletteRewardProbeIdentityRules.TryValidateResponse(request, response", source);
        var proof = source.IndexOf("!DadRouletteRewardProbeIdentityRules.HasCurrentReceivedTextProof(response)", StringComparison.Ordinal);
        var consensus = source.IndexOf("var receivedCount = active.ReceivedSlotCount", proof, StringComparison.Ordinal);
        Assert.True(proof > 0 && consensus > proof);
        var transport = File.ReadAllText(Path.Combine(root.FullName, "Services", "DadTransportService.cs"));
        Assert.Contains("SameWorker(request.RouteWorkerSessionId, presenceService.WorkerSessionId)", transport);
        Assert.Contains("!IsAuthenticatedCoordinator(context)", transport);
    }

    private static void Set(object instance, string field, object value)
        => instance.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
}
