using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using dad.Models;

namespace dad.Services;

public sealed class DadMeasuredPilotService
{
    public const string ReceiptSchema = "dad.pilot-evidence/v1";
    private static readonly TimeSpan ReceiptRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly DadAutoPartyConfiguration configuration;
    private readonly DadAutoPartySigningService signing;
    private readonly Func<bool> isCoordinator;
    private readonly Action saveConfiguration;
    private readonly string assemblyPath;
    private readonly Action<string> diagnostic;
    private bool receiptDirty;
    private Task<DadPilotReceiptWriteResult>? receiptTask;
    private DateTime nextReceiptAttemptUtc = DateTime.MinValue;
    private DadAutoPartyDiscordConnectionState priorDiscordState = DadAutoPartyDiscordConnectionState.Disabled;
    private bool observedDiscordLoss;
    private string lastStopOperationId = string.Empty;

    public DadMeasuredPilotService(
        DadAutoPartyConfiguration configuration,
        DadAutoPartySigningService signing,
        Func<bool> isCoordinator,
        Action saveConfiguration,
        string? assemblyPath = null,
        Action<string>? diagnostic = null)
    {
        this.configuration = configuration;
        this.signing = signing;
        this.isCoordinator = isCoordinator;
        this.saveConfiguration = saveConfiguration;
        this.diagnostic = diagnostic ?? (static _ => { });
        this.assemblyPath = string.IsNullOrWhiteSpace(assemblyPath)
            ? Assembly.GetExecutingAssembly().Location
            : Path.GetFullPath(assemblyPath);
    }

    public DadMeasuredPilotEvaluation CurrentEvaluation => Evaluate(configuration.MeasuredPilot);
    internal bool ReceiptDirty => receiptDirty;
    internal bool ReceiptWriteInFlight => receiptTask is { IsCompleted: false };
    internal DateTime NextReceiptAttemptUtc => nextReceiptAttemptUtc;

    public DadAutoPartyPolicyDecision Start()
    {
        if (!isCoordinator())
            return Denied("dad-pilot-coordinator-required");
        if (!HasSigningIdentity())
            return Denied("dad-pilot-signing-identity-required");
        if (configuration.MeasuredPilot.State == DadMeasuredPilotState.Active)
            return Allowed("dad-pilot-already-active");
        if (!File.Exists(assemblyPath))
            return Denied("dad-pilot-assembly-missing");

        configuration.MeasuredPilot = new DadMeasuredPilotCampaign
        {
            CampaignId = Guid.NewGuid().ToString("D"),
            State = DadMeasuredPilotState.Active,
            StartedAtUtc = DateTime.UtcNow,
            CoordinatorIdentity = configuration.RegisteredIslandId,
            AssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assemblyPath))).ToLowerInvariant(),
        };
        priorDiscordState = DadAutoPartyDiscordConnectionState.Disabled;
        observedDiscordLoss = false;
        lastStopOperationId = string.Empty;
        nextReceiptAttemptUtc = DateTime.MinValue;
        RecordEvent(DadMeasuredPilotEventKind.CampaignStarted, "dad-pilot-started");
        Persist();
        return Allowed("dad-pilot-started");
    }

    public DadAutoPartyPolicyDecision Resume()
    {
        var campaign = configuration.MeasuredPilot;
        if (!isCoordinator())
            return Denied("dad-pilot-coordinator-required");
        if (campaign.State != DadMeasuredPilotState.EvaluationIncomplete)
            return Denied("dad-pilot-resume-not-available");
        campaign.State = DadMeasuredPilotState.Active;
        campaign.StoppedAtUtc = null;
        RecordEvent(DadMeasuredPilotEventKind.CampaignResumed, "dad-pilot-resumed");
        Persist();
        return Allowed("dad-pilot-resumed");
    }

    public DadMeasuredPilotEvaluation StopAndEvaluate()
    {
        var campaign = configuration.MeasuredPilot;
        if (campaign.State != DadMeasuredPilotState.Active)
            return Evaluate(campaign);
        campaign.StoppedAtUtc = DateTime.UtcNow;
        var evaluation = Evaluate(campaign);
        campaign.State = evaluation.State;
        Persist();
        return Evaluate(campaign);
    }

    public void RegisterRun(string runId, DadMeasuredPilotOrigin origin, bool dryRun = false)
    {
        var campaign = configuration.MeasuredPilot;
        if (campaign.State != DadMeasuredPilotState.Active || string.IsNullOrWhiteSpace(runId) ||
            campaign.Runs.Any(run => string.Equals(run.RunId, runId, StringComparison.Ordinal)))
            return;
        campaign.Runs.Add(new DadMeasuredPilotRunEvidence
        {
            RunId = runId.Trim(),
            Origin = origin,
            DryRun = dryRun,
            StartedAtUtc = DateTime.UtcNow,
        });
        RecordEvent(DadMeasuredPilotEventKind.RunStarted, "dad-pilot-run-started", runId);
        Persist();
    }

    public void ObserveRun(
        DadRunResult result,
        bool claimsClear,
        bool schedulerClear,
        IReadOnlyCollection<ulong> healthyApplicationIds)
    {
        var campaign = configuration.MeasuredPilot;
        if (campaign.State != DadMeasuredPilotState.Active || string.IsNullOrWhiteSpace(result.RequestId))
            return;
        var run = campaign.Runs.FirstOrDefault(candidate =>
            string.Equals(candidate.RunId, result.RequestId, StringComparison.Ordinal));
        if (run == null)
        {
            RegisterRun(result.RequestId, DadMeasuredPilotOrigin.Unknown);
            run = campaign.Runs.First(candidate => string.Equals(candidate.RunId, result.RequestId, StringComparison.Ordinal));
        }

        var isGroupReady = result.Phase == DadRunPhase.GroupReady;
        var isQueueOrLater = result.Phase is DadRunPhase.QueuePreparing or DadRunPhase.QueueStarting or
            DadRunPhase.WaitingForQueuePop or DadRunPhase.InDutyOrTask or DadRunPhase.PostRunStabilizing or
            DadRunPhase.RequeueOrComplete;
        if (isGroupReady)
        {
            run.FormationVerified = result.Participants.Count >= 2;
            run.ReadinessBeforeQueueVerified = result.Participants.Count >= 2 &&
                result.Participants.All(static participant => participant.PostArReady);
            if (configuration.Pairings.Any(pairing => pairing.RevokedAtUtc != null &&
                result.Participants.All(participant => participant.DiscordApplicationId != pairing.ApplicationId)))
                campaign.RevokeExclusionVerified = true;
        }
        if (isQueueOrLater && !run.ReadinessBeforeQueueVerified)
            AddSafetyViolation("queue-before-ready", result.RequestId);

        foreach (var participant in result.Participants)
        {
            if (!string.IsNullOrWhiteSpace(participant.DesiredCharacterKey) &&
                !string.Equals(participant.DesiredCharacterKey, participant.ActiveCharacterKey.Value, StringComparison.OrdinalIgnoreCase))
                AddSafetyViolation("wrong-identity", result.RequestId);
            var proof = participant.RequestedJobPreparation;
            if (proof?.Key.RequiredJobId is { } requestedJob)
            {
                run.RequestedJobRun = true;
                var matched = proof.Status is DadRequestedJobPreparationStatus.AlreadyMatched or DadRequestedJobPreparationStatus.Switched &&
                              participant.Character.CurrentJobId == requestedJob;
                run.RequestedJobMatched |= matched;
                run.RequestedJobSwitched |= matched && proof.Status == DadRequestedJobPreparationStatus.Switched;
                if (proof.Status is DadRequestedJobPreparationStatus.AlreadyMatched or DadRequestedJobPreparationStatus.Switched && !matched)
                    AddSafetyViolation("wrong-job", result.RequestId);
            }
            if (participant.DiscordApplicationId != 0 && configuration.Pairings.Any(pairing =>
                    pairing.ApplicationId == participant.DiscordApplicationId && pairing.RevokedAtUtc != null))
                AddSafetyViolation("revoked-client-accepted", result.RequestId);
        }

        if (!result.IsTerminal)
            return;
        var firstTerminalObservation = !run.Terminal;
        run.Terminal = true;
        run.CompletedAtUtc = result.CompletedAtUtc ?? DateTime.UtcNow;
        run.Successful = result.Status == DadRunStatus.Completed;
        run.ParticipantCount = result.Participants.Select(static participant => participant.WorkerSessionId.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        run.HealthyApplicationIds = healthyApplicationIds.Where(static id => id != 0).Distinct().Order().ToList();
        run.LeaseCleanupVerified |= result.Leases.Count == 0;
        run.ClaimCleanupVerified |= claimsClear;
        run.SchedulerCleanupVerified |= schedulerClear;
        run.ProfileRestoration = "not-applicable";
        if (!run.Successful)
            run.FailureCode = $"dad-run-{result.Status.ToString().ToLowerInvariant()}";
        if (run.Qualifies && campaign.RecoveryRunRequired)
        {
            campaign.RecoveryRunRequired = false;
            campaign.RecoveryRunVerified = true;
        }
        if (firstTerminalObservation)
            RecordEvent(DadMeasuredPilotEventKind.RunTerminal,
                run.Qualifies ? "dad-pilot-run-qualified" : "dad-pilot-run-recorded", result.RequestId);
        Persist();
    }

    public void ObserveStopAll(DadStopAllStatus? status, int expectedParticipantCount)
    {
        if (configuration.MeasuredPilot.State != DadMeasuredPilotState.Active || status is not { IsFinal: true } ||
            string.Equals(lastStopOperationId, status.OperationId, StringComparison.Ordinal))
            return;
        lastStopOperationId = status.OperationId;
        var acknowledged = status.Workers.Count(static worker =>
            worker.State == DadStopAllWorkerState.Acknowledged && worker.LocalCleanupCompleted);
        var localAcknowledged = status.LocalResult.State == DadStopAllWorkerState.Acknowledged &&
                                status.LocalResult.LocalCleanupCompleted;
        var fullyScoped = !status.Partial && localAcknowledged && acknowledged == status.Workers.Count &&
                          status.Workers.Count + 1 >= Math.Max(1, expectedParticipantCount);
        if (!fullyScoped)
            AddSafetyViolation("unscoped-stop", string.Empty);
        else
        {
            configuration.MeasuredPilot.StopAllVerified = true;
            configuration.MeasuredPilot.RecoveryRunRequired = true;
            RecordEvent(DadMeasuredPilotEventKind.StopAll, "dad-pilot-stop-all-verified");
        }
        Persist();
    }

    public void ObserveDiscordHealth(DadAutoPartyDiscordHealth health)
    {
        if (configuration.MeasuredPilot.State != DadMeasuredPilotState.Active || health.State == priorDiscordState)
            return;
        if (priorDiscordState == DadAutoPartyDiscordConnectionState.Ready &&
            health.State is DadAutoPartyDiscordConnectionState.Disconnected or DadAutoPartyDiscordConnectionState.Stale)
            observedDiscordLoss = true;
        if (observedDiscordLoss && health.State == DadAutoPartyDiscordConnectionState.Ready)
            configuration.MeasuredPilot.DiscordReconnectCycleVerified = true;
        priorDiscordState = health.State;
        RecordEvent(DadMeasuredPilotEventKind.DiscordHealth, health.SafeCode);
        Persist();
    }

    public void ObservePairingRevoked(ulong applicationId)
    {
        if (configuration.MeasuredPilot.State != DadMeasuredPilotState.Active || applicationId == 0)
            return;
        configuration.MeasuredPilot.RevokeExclusionVerified = false;
        RecordEvent(DadMeasuredPilotEventKind.PairingRevoked, $"dad-pilot-revoked-{applicationId}");
        Persist();
    }

    public void ObserveRevokedClientExcluded(ulong applicationId)
    {
        if (configuration.MeasuredPilot.State != DadMeasuredPilotState.Active || applicationId == 0)
            return;
        configuration.MeasuredPilot.RevokeExclusionVerified = true;
        Persist();
    }

    public void ObservePairingRestored(ulong applicationId)
    {
        if (configuration.MeasuredPilot.State != DadMeasuredPilotState.Active || applicationId == 0 ||
            !configuration.MeasuredPilot.RevokeExclusionVerified)
            return;
        configuration.MeasuredPilot.RePairVerified = true;
        RecordEvent(DadMeasuredPilotEventKind.PairingRestored, $"dad-pilot-repaired-{applicationId}");
        Persist();
    }

    public void Update()
    {
        if (receiptTask is { IsCompleted: true })
        {
            if (receiptTask.IsCompletedSuccessfully)
            {
                var result = receiptTask.Result;
                if (string.Equals(
                        configuration.MeasuredPilot.CampaignId,
                        result.CampaignId,
                        StringComparison.Ordinal))
                {
                    // Update() is invoked from the Dalamud framework thread. Async receipt IO
                    // returns immutable data only; configuration mutation and save stay here.
                    configuration.MeasuredPilot.ReceiptPath = result.Path;
                    configuration.StateGeneration++;
                    saveConfiguration();
                }
            }
            else if (receiptTask.IsFaulted)
            {
                _ = receiptTask.Exception;
                receiptDirty = true;
                nextReceiptAttemptUtc = DateTime.UtcNow + ReceiptRetryDelay;
                diagnostic("dad-pilot-receipt-write-failed");
            }
            else if (receiptTask.IsCanceled)
            {
                receiptDirty = true;
                nextReceiptAttemptUtc = DateTime.UtcNow + ReceiptRetryDelay;
                diagnostic("dad-pilot-receipt-write-cancelled");
            }
            receiptTask = null;
        }
        if (receiptDirty && receiptTask == null && DateTime.UtcNow >= nextReceiptAttemptUtc && HasSigningIdentity())
        {
            receiptDirty = false;
            var snapshot = configuration.MeasuredPilot.Clone();
            var receiptRoot = configuration.GetPilotReceiptRoot();
            var signingPublicKey = configuration.SigningPublicKey;
            receiptTask = WriteReceiptAsync(snapshot, receiptRoot, signingPublicKey);
        }
    }

    public static DadMeasuredPilotEvaluation Evaluate(DadMeasuredPilotCampaign campaign)
    {
        var qualifying = campaign.Runs.Where(static run => run.Qualifies).ToList();
        var plans = qualifying.Count(static run => run.Origin == DadMeasuredPilotOrigin.Plans);
        var schedules = qualifying.Count(static run => run.Origin == DadMeasuredPilotOrigin.Schedules);
        var jobs = qualifying.Count(static run => run.RequestedJobRun && run.RequestedJobMatched);
        var switches = qualifying.Count(static run => run.RequestedJobSwitched);
        var missing = new List<string>();
        AddMissing(missing, qualifying.Count, 10, "successful multi-client executions");
        AddMissing(missing, plans, 3, "direct Plans executions");
        AddMissing(missing, schedules, 3, "Schedule executions");
        AddMissing(missing, jobs, 2, "requested-job executions");
        AddMissing(missing, switches, 1, "verified requested-job switches");
        if (!campaign.StopAllVerified) missing.Add("Stop-all acknowledgement and drain exercise");
        if (!campaign.RecoveryRunVerified) missing.Add("successful recovery run after Stop-all");
        if (!campaign.DiscordReconnectCycleVerified) missing.Add("Discord disconnect/stale/reconnect cycle");
        if (!campaign.RevokeExclusionVerified) missing.Add("revoked-client exclusion proof");
        if (!campaign.RePairVerified) missing.Add("re-pair proof after revocation");
        var state = campaign.SafetyViolations.Count > 0
            ? DadMeasuredPilotState.HardFailed
            : campaign.StoppedAtUtc.HasValue && missing.Count == 0
                ? DadMeasuredPilotState.Passed
                : campaign.StoppedAtUtc.HasValue
                    ? DadMeasuredPilotState.EvaluationIncomplete
                    : campaign.State;
        return new(state, qualifying.Count, plans, schedules, jobs, switches, missing, campaign.SafetyViolations);
    }

    private async Task<DadPilotReceiptWriteResult> WriteReceiptAsync(
        DadMeasuredPilotCampaign snapshot,
        string receiptRoot,
        string signingPublicKey)
    {
        var evaluation = Evaluate(snapshot);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = ReceiptSchema,
            campaignId = snapshot.CampaignId,
            coordinatorIdentity = snapshot.CoordinatorIdentity,
            dadAssemblySha256 = snapshot.AssemblySha256,
            generatedAtUtc = DateTime.UtcNow,
            finalized = snapshot.StoppedAtUtc.HasValue,
            state = evaluation.State.ToString(),
            counters = new
            {
                qualifyingSuccesses = evaluation.QualifyingSuccesses,
                plans = evaluation.PlanSuccesses,
                schedules = evaluation.ScheduleSuccesses,
                requestedJobs = evaluation.RequestedJobSuccesses,
                requestedJobSwitches = evaluation.RequestedJobSwitches,
            },
            stopAllVerified = snapshot.StopAllVerified,
            recoveryRunVerified = snapshot.RecoveryRunVerified,
            discordReconnectCycleVerified = snapshot.DiscordReconnectCycleVerified,
            revokeExclusionVerified = snapshot.RevokeExclusionVerified,
            rePairVerified = snapshot.RePairVerified,
            missing = evaluation.Missing,
            safetyViolations = evaluation.SafetyViolations,
            runs = snapshot.Runs,
            events = snapshot.Events,
        }, JsonOptions);
        byte[]? signature = null;
        byte[]? envelope = null;
        string? temporary = null;
        try
        {
            signature = await signing.SignAsync(payload).ConfigureAwait(false);
            envelope = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = ReceiptSchema,
                payloadBase64 = Convert.ToBase64String(payload),
                signingPublicKey,
                signature = Convert.ToBase64String(signature),
            }, JsonOptions);
            Directory.CreateDirectory(receiptRoot);
            var path = Path.Combine(receiptRoot, $"dad-pilot-evidence-{snapshot.CampaignId}.json");
            temporary = path + ".tmp";
            await File.WriteAllBytesAsync(temporary, envelope).ConfigureAwait(false);
            File.Move(temporary, path, true);
            temporary = null;
            return new DadPilotReceiptWriteResult(snapshot.CampaignId, path);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            if (signature != null) CryptographicOperations.ZeroMemory(signature);
            if (envelope != null) CryptographicOperations.ZeroMemory(envelope);
            if (!string.IsNullOrWhiteSpace(temporary))
            {
                try
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private sealed record DadPilotReceiptWriteResult(string CampaignId, string Path);

    private bool HasSigningIdentity() =>
        !string.IsNullOrWhiteSpace(configuration.EndpointIdentityReference) &&
        !string.IsNullOrWhiteSpace(configuration.RegisteredIslandId) &&
        !string.IsNullOrWhiteSpace(configuration.SigningPublicKey);

    private void AddSafetyViolation(string violation, string runId)
    {
        if (configuration.MeasuredPilot.SafetyViolations.Contains(violation, StringComparer.Ordinal))
            return;
        configuration.MeasuredPilot.SafetyViolations.Add(violation);
        configuration.MeasuredPilot.State = DadMeasuredPilotState.HardFailed;
        RecordEvent(DadMeasuredPilotEventKind.SafetyViolation, violation, runId);
    }

    private void RecordEvent(DadMeasuredPilotEventKind kind, string safeCode, string runId = "")
        => configuration.MeasuredPilot.Events.Add(new DadMeasuredPilotEvent
        {
            Kind = kind,
            RunId = runId,
            SafeCode = safeCode.Length <= 128 ? safeCode : safeCode[..128],
        });

    private void Persist()
    {
        configuration.MeasuredPilot.Normalize();
        configuration.StateGeneration++;
        receiptDirty = true;
        saveConfiguration();
    }

    private DadAutoPartyPolicyDecision Allowed(string code) => new(true, code, configuration.StateGeneration);
    private DadAutoPartyPolicyDecision Denied(string code) => new(false, code, configuration.StateGeneration);
    private static void AddMissing(List<string> missing, int actual, int required, string label)
    {
        if (actual < required)
            missing.Add($"{label}: {actual}/{required}");
    }
}
