using System.Collections.Concurrent;
using dad.Models;
using Dalamud.Plugin.Services;

namespace dad.Services;

public sealed class DadAlliancePartyFinderService : IDisposable
{
    private readonly DadPresenceService presenceService;
    private readonly DadTransportService transportService;
    private readonly DadAutoPartyDiscordService discordService;
    private readonly DadAlliancePartyFinderNativeGateway nativeGateway;
    private readonly DadAlliancePfAuditLog audit;
    private readonly Func<string> conflictBlocker;
    private readonly Func<string> coordinatorIdentity;
    private readonly IPluginLog log;
    private readonly DadAllianceDeliveryDedupe receiverDedupe = new();
    private readonly ConcurrentQueue<Action> frameworkCompletions = new();
    private readonly ConcurrentQueue<DadAllianceRecruitmentInstructionDto> discordInstructions = new();
    private readonly Dictionary<string, DadAllianceRecruitmentTarget> coordinatorTargets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DadAllianceRecruitmentInstructionDto> coordinatorInstructions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DadAllianceRecruitmentResultDto> coordinatorResults = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task> outboundTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ulong> discordMessageIds = [];
    private readonly DadAlliancePartyFinderCreateCycleCoordinator createCycles =
        new();
    private readonly object statusGate = new();
    private DadAlliancePartyFinderStatus status = new();
    private DadAllianceRecruitmentInstructionDto? receiverInstruction;
    private DadAllianceRecruitmentResultDto receiverResult = new()
    {
        State = DadAllianceRecruitmentState.Idle,
        ResultKind = DadAllianceRecruitmentResultKind.Pending,
        Summary = "No alliance PF instruction is active.",
    };
    private CancellationTokenSource operationCancellation = new();
    private DateTime receiverNextAttemptUtc = DateTime.MinValue;
    private DateTime coordinatorNextResendUtc = DateTime.MinValue;
    private int receiverCompletedAttempts;
    private bool grabRequested;
    private bool cleanupRequested;
    private bool stopApplied;
    private string lastCreateAuditFingerprint = string.Empty;
    private bool disposed;

    private sealed record DadAlliancePfCreatePreflightEvaluation(
        DadAlliancePartyFinderStatus Status,
        DadParticipantSnapshot? Local,
        List<DadAllianceRecruitmentTarget> Targets);

    internal DadAlliancePartyFinderService(
        DadPresenceService presenceService,
        DadTransportService transportService,
        DadAutoPartyDiscordService discordService,
        DadAlliancePartyFinderNativeGateway nativeGateway,
        DadAlliancePfAuditLog audit,
        Func<string> conflictBlocker,
        Func<string> coordinatorIdentity,
        IPluginLog log)
    {
        this.presenceService = presenceService;
        this.transportService = transportService;
        this.discordService = discordService;
        this.nativeGateway = nativeGateway;
        this.audit = audit;
        this.conflictBlocker = conflictBlocker;
        this.coordinatorIdentity = coordinatorIdentity;
        this.log = log;
        discordService.AllianceRecruitmentReceived += QueueDiscordInstruction;
    }

    public DadAlliancePartyFinderStatus GetStatus()
    {
        lock (statusGate)
            return status.Clone();
    }

    public async Task<string> CheckPartyFinderDiagnosticsAsync()
    {
        try
        {
            ThrowIfDisposed();
            var capturedAtUtc = DateTime.UtcNow;
            var content = await nativeGateway
                .CaptureLookingForGroupDiagnosticsAsync(capturedAtUtc)
                .ConfigureAwait(false);
            return audit.TryWriteLookingForGroupDiagnostics(
                content,
                capturedAtUtc,
                out var path,
                out var error)
                ? $"Party Finder diagnostics saved: {path}"
                : $"Party Finder diagnostics failed: {error}";
        }
        catch (Exception exception)
        {
            return $"Party Finder diagnostics failed: {exception.Message}";
        }
    }

    public DadAlliancePartyFinderStatus Preview(
        DadPlannerGroup? group,
        DadActivityPreset? preview)
        => EvaluateCreatePreflight(group, preview).Status.Clone();

    public DadAlliancePartyFinderStatus CreateParty(
        DadPlannerGroup? group,
        DadActivityPreset? preview)
    {
        ThrowIfDisposed();
        var preflight = EvaluateCreatePreflight(group, preview);
        if (!preflight.Status.CreatePreflightReady)
            return RejectCreate(preflight.Status);

        var local = preflight.Local ??
                    throw new InvalidOperationException("Alliance PF Create preflight lost the local participant.");
        var selectedGroup = group ??
                            throw new InvalidOperationException("Alliance PF Create preflight lost the selected preset.");
        var targets = preflight.Targets;
        var validation = preflight.Status.Validation;

        ResetOperation();
        createCycles.Reset();
        nativeGateway.Reset();
        foreach (var target in targets)
            coordinatorTargets[target.CharacterKey.Value] = target;

        var now = DateTime.UtcNow;
        var passcode = DadAlliancePartyFinderRules.GeneratePasscode();
        var next = new DadAlliancePartyFinderStatus
        {
            RecruitmentId = Guid.NewGuid().ToString("N"),
            State = DadAllianceRecruitmentState.CreatingListing,
            PresetGroupId = selectedGroup.GroupId,
            PresetName = selectedGroup.DisplayName,
            LeaderName = local.Character.CharacterName,
            LeaderWorld = local.Character.WorldName,
            Passcode = passcode,
            CreateStage = DadAlliancePfCreateStage.CloseStaleWindows.ToString(),
            CreatePreflightReady = false,
            CreatePreflightBlocker = DadAlliancePartyFinderCreatePreflight.ActiveRecruitmentBlocker,
            StopGeneration = status.StopGeneration,
            StartedAtUtc = now,
            UpdatedAtUtc = now,
            Summary = $"Creating a private {DadAlliancePartyFinderNativeGateway.FormationDutyName} alliance recruitment.",
            Validation = validation,
        };
        SetStatus(next);
        Audit("create-requested", null, 0, string.Empty, next.Summary);
        return next.Clone();
    }

    private DadAlliancePfCreatePreflightEvaluation EvaluateCreatePreflight(
        DadPlannerGroup? group,
        DadActivityPreset? preview)
    {
        var hasConcretePreset =
            group != null &&
            preview != null &&
            preview.UsingPlannerGroup &&
            string.Equals(
                group.GroupId,
                preview.SelectedPlannerGroupId,
                StringComparison.OrdinalIgnoreCase);
        if (!hasConcretePreset)
        {
            return BuildCreatePreflightEvaluation(
                group,
                new DadAlliancePresetValidation
                {
                    Blockers = [DadAlliancePartyFinderCreatePreflight.PresetBlocker],
                    Summary = DadAlliancePartyFinderCreatePreflight.PresetBlocker,
                },
                new DadAlliancePfCreatePreflightInput
                {
                    HasConcretePreset = false,
                });
        }

        var local = nativeGateway.BuildLocalSnapshot();
        var validation = DadAlliancePartyFinderRules.ValidateEffectiveSlots(
            preview!.SelectedCharacters,
            local.ActiveCharacterKey);
        if (!validation.IsValid)
        {
            return BuildCreatePreflightEvaluation(
                group,
                validation,
                new DadAlliancePfCreatePreflightInput
                {
                    HasConcretePreset = true,
                    Validation = validation,
                },
                local);
        }

        var recruitmentActive = HasActiveRecruitment();
        if (recruitmentActive)
        {
            return BuildCreatePreflightEvaluation(
                group,
                validation,
                new DadAlliancePfCreatePreflightInput
                {
                    HasConcretePreset = true,
                    Validation = validation,
                    RecruitmentActive = true,
                },
                local);
        }

        var operationalBlocker = conflictBlocker();
        if (!string.IsNullOrWhiteSpace(operationalBlocker))
        {
            return BuildCreatePreflightEvaluation(
                group,
                validation,
                new DadAlliancePfCreatePreflightInput
                {
                    HasConcretePreset = true,
                    Validation = validation,
                    OperationalBlocker = operationalBlocker,
                },
                local);
        }

        var targetsResolved = TryBuildTargets(
            preview.SelectedCharacters,
            local,
            out var targets,
            out var targetBlocker);
        var host = targetsResolved
            ? targets.SingleOrDefault(target => string.Equals(
                target.CharacterKey.Value,
                local.ActiveCharacterKey.Value,
                StringComparison.OrdinalIgnoreCase))
            : null;
        var input = new DadAlliancePfCreatePreflightInput
        {
            HasConcretePreset = true,
            Validation = validation,
            TargetsResolved = targetsResolved,
            TargetBlocker = targetBlocker,
            HostIsAllianceA = host?.Assignment == DadAllianceAssignment.A,
        };
        return BuildCreatePreflightEvaluation(group, validation, input, local, targets);
    }

    private static DadAlliancePfCreatePreflightEvaluation BuildCreatePreflightEvaluation(
        DadPlannerGroup? group,
        DadAlliancePresetValidation validation,
        DadAlliancePfCreatePreflightInput input,
        DadParticipantSnapshot? local = null,
        List<DadAllianceRecruitmentTarget>? targets = null)
    {
        var decision = DadAlliancePartyFinderCreatePreflight.Evaluate(input);
        return new DadAlliancePfCreatePreflightEvaluation(
            new DadAlliancePartyFinderStatus
            {
                State = decision.Ready
                    ? DadAllianceRecruitmentState.Idle
                    : DadAllianceRecruitmentState.Blocked,
                PresetGroupId = group?.GroupId ?? string.Empty,
                PresetName = group?.DisplayName ?? string.Empty,
                Validation = validation,
                CreatePreflightReady = decision.Ready,
                CreatePreflightBlocker = decision.Blocker,
                Summary = decision.Ready ? validation.Summary : decision.Blocker,
            },
            local,
            targets ?? []);
    }

    private bool HasActiveRecruitment()
    {
        lock (statusGate)
        {
            return !string.IsNullOrWhiteSpace(status.RecruitmentId) &&
                   status.State is not DadAllianceRecruitmentState.Complete
                       and not DadAllianceRecruitmentState.Stopped
                       and not DadAllianceRecruitmentState.Blocked;
        }
    }

    private DadAlliancePartyFinderStatus RejectCreate(DadAlliancePartyFinderStatus rejected)
    {
        DadAlliancePartyFinderStatus result;
        lock (statusGate)
        {
            if (!string.IsNullOrWhiteSpace(status.RecruitmentId) &&
                status.State is not DadAllianceRecruitmentState.Complete
                    and not DadAllianceRecruitmentState.Stopped
                    and not DadAllianceRecruitmentState.Blocked)
            {
                status.CreatePreflightReady = false;
                status.CreatePreflightBlocker = rejected.CreatePreflightBlocker;
                status.UpdatedAtUtc = DateTime.UtcNow;
                result = status.Clone();
            }
            else
            {
                rejected.State = DadAllianceRecruitmentState.Blocked;
                rejected.CreateRejected = true;
                rejected.Summary = rejected.CreatePreflightBlocker;
                rejected.StopGeneration = status.StopGeneration;
                rejected.UpdatedAtUtc = DateTime.UtcNow;
                status = rejected.Clone();
                result = status.Clone();
            }
        }

        Audit(
            "create-rejected",
            null,
            0,
            rejected.CreatePreflightBlocker,
            rejected.CreatePreflightBlocker);
        return result;
    }

    public DadAlliancePartyFinderStatus GrabDads()
    {
        ThrowIfDisposed();
        lock (statusGate)
        {
            if (!DadAlliancePartyFinderRules.CanGrabDads(status))
                return SetBlocked("Create and verify active DAD-owned Labyrinth recruitment before grabbing dads.", status.Validation);
            grabRequested = true;
            coordinatorNextResendUtc = DateTime.MinValue;
            status.Summary = "Dispatching unresolved alliance targets concurrently.";
            status.UpdatedAtUtc = DateTime.UtcNow;
            return status.Clone();
        }
    }

    public void Update()
    {
        if (disposed)
            return;

        while (frameworkCompletions.TryDequeue(out var completion))
            completion();
        while (discordInstructions.TryDequeue(out var instruction))
            AcceptDiscordInstructionOnFramework(instruction);

        UpdateReceiver();
        UpdateCoordinator();
    }

    public DadAllianceRecruitmentResultDto AcceptHubInstruction(
        DadAllianceRecruitmentInstructionDto instruction)
        => AcceptInstruction(instruction, "hub", requireConnectedCoordinator: false);

    public DadAllianceRecruitmentResultDto AcceptCancellation(
        DadAllianceRecruitmentCancellationDto cancellation)
    {
        if (receiverInstruction == null ||
            !string.Equals(receiverInstruction.RecruitmentId, cancellation.RecruitmentId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                receiverInstruction.CoordinatorWorkerSessionId.Value,
                cancellation.CoordinatorWorkerSessionId.Value,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                presenceService.WorkerSessionId.Value,
                cancellation.TargetWorkerSessionId.Value,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                receiverInstruction.TargetCharacterKey.Value,
                cancellation.TargetCharacterKey.Value,
                StringComparison.OrdinalIgnoreCase) ||
            cancellation.StopGeneration < receiverInstruction.StopGeneration)
        {
            return receiverResult.Clone();
        }

        receiverResult = new DadAllianceRecruitmentResultDto
        {
            RecruitmentId = cancellation.RecruitmentId,
            WorkerSessionId = presenceService.WorkerSessionId,
            TargetCharacterKey = cancellation.TargetCharacterKey,
            ExpectedAlliance = receiverInstruction.AssignedAlliance,
            ObservedAlliance = nativeGateway.ObserveAlliance(receiverInstruction.TargetContentId),
            Attempt = receiverInstruction.Attempt,
            State = DadAllianceRecruitmentState.Stopped,
            ResultKind = DadAllianceRecruitmentResultKind.Stopped,
            Retryable = false,
            StopGeneration = cancellation.StopGeneration,
            Summary = string.IsNullOrWhiteSpace(cancellation.Reason)
                ? "Alliance recruitment stopped."
                : cancellation.Reason,
        };
        nativeGateway.StopJoin();
        receiverInstruction = null;
        Audit("receiver-stopped", receiverResult, 0, string.Empty, receiverResult.Summary);
        return receiverResult.Clone();
    }

    public DadAlliancePfUiSnapshotDto BuildUiSnapshot()
        => new()
        {
            RecruitmentId = receiverResult.RecruitmentId,
            WorkerSessionId = presenceService.WorkerSessionId,
            TargetCharacterKey = receiverResult.TargetCharacterKey,
            AssignedAlliance = receiverResult.ExpectedAlliance,
            ObservedAlliance = receiverResult.ObservedAlliance,
            Attempt = receiverResult.Attempt,
            State = receiverResult.State,
            StopGeneration = receiverResult.StopGeneration,
            UpdatedAtUtc = receiverResult.ObservedAtUtc,
            SafeStatusCode = receiverResult.ResultKind switch
            {
                DadAllianceRecruitmentResultKind.Succeeded => "dad-alliance-verified",
                DadAllianceRecruitmentResultKind.Stopped => "dad-alliance-stopped",
                DadAllianceRecruitmentResultKind.Blocked => "dad-alliance-blocked",
                DadAllianceRecruitmentResultKind.Retry => "dad-alliance-retrying",
                DadAllianceRecruitmentResultKind.Waiting => "dad-alliance-waiting",
                _ => "dad-alliance-pending",
            },
        };

    public void Stop(string reason)
    {
        if (disposed || stopApplied)
            return;

        stopApplied = true;
        createCycles.Stop();
        operationCancellation.Cancel();
        var stopCreate = false;
        var nextGeneration = Math.Max(status.StopGeneration, receiverInstruction?.StopGeneration ?? 0) + 1;
        lock (statusGate)
        {
            stopCreate = !status.OwnsRecruitment &&
                         !string.IsNullOrWhiteSpace(status.CreateStage) &&
                         status.State is not DadAllianceRecruitmentState.Complete
                             and not DadAllianceRecruitmentState.Stopped;
            status.StopGeneration = nextGeneration;
            status.State = DadAllianceRecruitmentState.Stopped;
            status.UpdatedAtUtc = DateTime.UtcNow;
            status.Summary = string.IsNullOrWhiteSpace(reason) ? "Alliance recruitment stopped." : reason.Trim();
            cleanupRequested |= status.OwnsRecruitment;
        }
        if (stopCreate)
            nativeGateway.StopCreate();

        if (receiverInstruction != null)
        {
            AcceptCancellation(new DadAllianceRecruitmentCancellationDto
            {
                RecruitmentId = receiverInstruction.RecruitmentId,
                CoordinatorWorkerSessionId = receiverInstruction.CoordinatorWorkerSessionId,
                TargetWorkerSessionId = presenceService.WorkerSessionId,
                TargetCharacterKey = receiverInstruction.TargetCharacterKey,
                StopGeneration = nextGeneration,
                Reason = reason,
            });
        }

        foreach (var target in coordinatorTargets.Values)
            QueueCancellation(target, nextGeneration, reason);
        QueueDiscordCleanup();
        Audit("stop", null, 0, string.Empty, reason);
        operationCancellation.Dispose();
        operationCancellation = new CancellationTokenSource();
    }

    public void Dispose()
    {
        if (disposed)
            return;
        Stop("Alliance PF service disposed.");
        disposed = true;
        discordService.AllianceRecruitmentReceived -= QueueDiscordInstruction;
        operationCancellation.Cancel();
        operationCancellation.Dispose();
        nativeGateway.Dispose();
    }

    private void UpdateCoordinator()
    {
        DadAlliancePartyFinderStatus current;
        lock (statusGate)
            current = status.Clone();

        if (!current.OwnsRecruitment &&
            !string.IsNullOrWhiteSpace(current.CreateStage) &&
            current.State is DadAllianceRecruitmentState.CreatingListing
                or DadAllianceRecruitmentState.RetryWaiting
                or DadAllianceRecruitmentState.WaitingUnsafe)
        {
            var step = nativeGateway.AdvanceCreate(current.Passcode);
            var decision = createCycles.Observe(
                step.Kind switch
                {
                    DadAllianceNativeStepKind.Succeeded =>
                        DadAlliancePfCreateCycleOutcome.Succeeded,
                    DadAllianceNativeStepKind.Blocked =>
                        DadAlliancePfCreateCycleOutcome.Blocked,
                    _ => DadAlliancePfCreateCycleOutcome.InProgress,
                },
                step.ActiveRecruitment);
            ApplyCreateStep(step);
            if (decision == DadAlliancePfCreateCycleDecision.RestartOnce)
                RestartCreateCycle(step);
            return;
        }

        if (cleanupRequested && current.OwnsRecruitment)
        {
            var cleanup = nativeGateway.AdvanceEndRecruitment(current.OwnsRecruitment);
            lock (statusGate)
            {
                status.CreateStage = cleanup.CreateStage;
                status.CreateAttempt = cleanup.Attempt;
                status.CreateNextRetryUtc = cleanup.NextRetryUtc;
                status.CreateLastError = cleanup.LastError;
                status.CreateActiveRecruitment = cleanup.ActiveRecruitment;
                status.CreateEditorVisible = cleanup.EditorVisible;
                status.CreateSubmitDispatched = cleanup.SubmitDispatched;
                status.UpdatedAtUtc = DateTime.UtcNow;
            }
            if (cleanup.ShouldAudit)
            {
                var eventName = string.IsNullOrWhiteSpace(cleanup.CreateEvent)
                    ? "cleanup-readiness"
                    : $"cleanup-{cleanup.CreateEvent}";
                var fingerprint =
                    $"{eventName}|{cleanup.CreateStage}|{cleanup.Attempt}|{cleanup.NextRetryUtc:O}|" +
                    $"{cleanup.LastError}|{cleanup.Readiness}|{cleanup.ListingId}|" +
                    $"{cleanup.ActiveRecruitment}|{cleanup.Summary}";
                if (!string.Equals(fingerprint, lastCreateAuditFingerprint, StringComparison.Ordinal))
                {
                    lastCreateAuditFingerprint = fingerprint;
                    AuditCreate(eventName, cleanup);
                }
            }
            if (cleanup.Kind == DadAllianceNativeStepKind.Succeeded)
            {
                cleanupRequested = false;
                lock (statusGate)
                {
                    status.OwnsRecruitment = false;
                    status.ListingId = 0;
                    status.State = current.State == DadAllianceRecruitmentState.Stopped
                        ? DadAllianceRecruitmentState.Stopped
                        : DadAllianceRecruitmentState.Complete;
                    status.Summary = cleanup.Summary;
                    status.UpdatedAtUtc = DateTime.UtcNow;
                }
                QueueDiscordCleanup();
                Audit("recruitment-ended", null, 0, string.Empty, cleanup.Summary);
            }
            else if (cleanup.Kind == DadAllianceNativeStepKind.Blocked)
            {
                lock (statusGate)
                {
                    status.State = DadAllianceRecruitmentState.Blocked;
                    status.Summary = cleanup.Summary;
                    status.UpdatedAtUtc = DateTime.UtcNow;
                }
                Audit("cleanup-blocked", null, 0, cleanup.Summary, cleanup.Summary);
            }
            return;
        }

        if (!grabRequested || current.State != DadAllianceRecruitmentState.ListingOpen)
            return;

        var now = DateTime.UtcNow;
        if (now < coordinatorNextResendUtc)
            return;

        foreach (var target in coordinatorTargets.Values)
        {
            if (coordinatorResults.TryGetValue(target.CharacterKey.Value, out var result) &&
                result.ResultKind == DadAllianceRecruitmentResultKind.Succeeded &&
                result.ObservedAlliance == target.Assignment)
            {
                continue;
            }
            if (outboundTasks.TryGetValue(target.CharacterKey.Value, out var active) && !active.IsCompleted)
                continue;
            QueueInstruction(target);
        }

        var successful = coordinatorTargets.Values.Count(target =>
            coordinatorResults.TryGetValue(target.CharacterKey.Value, out var result) &&
            result.ResultKind == DadAllianceRecruitmentResultKind.Succeeded &&
            result.ObservedAlliance == target.Assignment);
        if (successful == coordinatorTargets.Count && successful > 0)
        {
            cleanupRequested = true;
            lock (statusGate)
            {
                status.State = DadAllianceRecruitmentState.Verifying;
                status.Summary = $"Verified all {successful} effective characters in their exact A-G subgroups; ending recruitment only.";
                status.Results = coordinatorResults.Values.Select(static result => result.Clone()).ToList();
                status.UpdatedAtUtc = now;
            }
            Audit("all-verified", null, 0, string.Empty, status.Summary);
            return;
        }

        coordinatorNextResendUtc = now + DadAlliancePartyFinderRules.GetRetryDelay(
            coordinatorResults.Values.DefaultIfEmpty().Max(static result => result?.Attempt ?? 0));
        lock (statusGate)
        {
            status.Results = coordinatorResults.Values.Select(static result => result.Clone()).ToList();
            status.Summary = $"Alliance verification: {successful}/{coordinatorTargets.Count} exact subgroup assignments complete; unresolved targets will retry.";
            status.UpdatedAtUtc = now;
        }
    }

    private void UpdateReceiver()
    {
        var instruction = receiverInstruction;
        if (instruction == null || receiverResult.IsTerminal || DateTime.UtcNow < receiverNextAttemptUtc)
            return;

        var started = DateTime.UtcNow;
        var step = nativeGateway.AdvanceJoin(instruction);
        receiverResult.RecruitmentId = instruction.RecruitmentId;
        receiverResult.WorkerSessionId = presenceService.WorkerSessionId;
        receiverResult.TargetCharacterKey = instruction.TargetCharacterKey;
        receiverResult.TargetCharacterName = instruction.TargetCharacterName;
        receiverResult.TargetCharacterWorld = instruction.TargetCharacterWorld;
        receiverResult.TargetContentId = instruction.TargetContentId;
        receiverResult.ExpectedAlliance = instruction.AssignedAlliance;
        receiverResult.ObservedAlliance = step.ObservedAlliance;
        receiverResult.Attempt = Math.Max(instruction.Attempt, receiverCompletedAttempts);
        receiverResult.State = step.State;
        receiverResult.StopGeneration = instruction.StopGeneration;
        receiverResult.ObservedAtUtc = DateTime.UtcNow;
        receiverResult.Summary = step.Summary;

        switch (step.Kind)
        {
            case DadAllianceNativeStepKind.Succeeded:
                receiverResult.ResultKind = DadAllianceRecruitmentResultKind.Succeeded;
                receiverResult.Retryable = false;
                break;
            case DadAllianceNativeStepKind.Blocked:
                receiverResult.ResultKind = DadAllianceRecruitmentResultKind.Blocked;
                receiverResult.Retryable = false;
                break;
            case DadAllianceNativeStepKind.Retry:
                receiverCompletedAttempts++;
                receiverResult.Attempt = receiverCompletedAttempts;
                receiverResult.ResultKind = DadAllianceRecruitmentResultKind.Retry;
                receiverResult.Retryable = true;
                receiverResult.State = DadAllianceRecruitmentState.RetryWaiting;
                receiverNextAttemptUtc = DateTime.UtcNow +
                                         DadAlliancePartyFinderRules.GetRetryDelay(receiverCompletedAttempts - 1);
                break;
            case DadAllianceNativeStepKind.Waiting:
                receiverResult.ResultKind = DadAllianceRecruitmentResultKind.Waiting;
                receiverResult.Retryable = true;
                receiverNextAttemptUtc = DateTime.UtcNow + TimeSpan.FromMilliseconds(250);
                break;
            default:
                receiverResult.ResultKind = DadAllianceRecruitmentResultKind.Pending;
                receiverResult.Retryable = true;
                receiverNextAttemptUtc = DateTime.UtcNow + TimeSpan.FromMilliseconds(250);
                break;
        }

        Audit(
            "receiver-attempt",
            receiverResult,
            (int)(DateTime.UtcNow - started).TotalMilliseconds,
            step.Kind == DadAllianceNativeStepKind.Blocked ? step.Summary : string.Empty,
            step.Summary);
        if (step.ShouldAudit)
        {
            var eventName = string.IsNullOrWhiteSpace(step.CreateEvent)
                ? "join-readiness"
                : $"join-{step.CreateEvent}";
            Audit(
                eventName,
                receiverResult,
                (int)(DateTime.UtcNow - started).TotalMilliseconds,
                step.Kind == DadAllianceNativeStepKind.Retry
                    ? step.LastError
                    : string.Empty,
                $"{step.CreateStage}: {step.Summary}");
        }
    }

    private void RestartCreateCycle(DadAllianceNativeStep blockedStep)
    {
        nativeGateway.RestartCreateCycle();
        var previousPasscode = GetStatus().Passcode;
        var freshPasscode =
            createCycles.GenerateFreshPasscode(previousPasscode);
        const string summary =
            "Create cycle 1 blocked before publication; DAD ran the existing Stop/reset path and automatically started final cycle 2 with a fresh passcode.";
        lock (statusGate)
        {
            status.Passcode = freshPasscode;
            status.State = DadAllianceRecruitmentState.CreatingListing;
            status.Summary = summary;
            status.ListingId = 0;
            status.OwnsRecruitment = false;
            status.CreateStage =
                DadAlliancePfCreateStage.CloseStaleWindows.ToString();
            status.CreateAttempt = 0;
            status.CreateNextRetryUtc = null;
            status.CreateLastError = string.Empty;
            status.CreateElapsedMilliseconds = 0;
            status.CreateActiveRecruitment = false;
            status.CreateEditorVisible = false;
            status.CreateSubmitDispatched = false;
            status.CreateConfigurationTarget = string.Empty;
            status.CreateObservedSettings = string.Empty;
            status.UpdatedAtUtc = DateTime.UtcNow;
        }
        lastCreateAuditFingerprint = string.Empty;
        Audit(
            "create-cycle-restart",
            null,
            blockedStep.ElapsedMilliseconds,
            blockedStep.Summary,
            summary);
    }

    private void ApplyCreateStep(DadAllianceNativeStep step)
    {
        lock (statusGate)
        {
            status.State = step.State;
            status.Summary = step.Summary;
            status.CreateStage = step.CreateStage;
            status.CreateAttempt = step.Attempt;
            status.CreateNextRetryUtc = step.NextRetryUtc;
            status.CreateLastError = step.LastError;
            status.CreateElapsedMilliseconds = step.ElapsedMilliseconds;
            status.CreateActiveRecruitment = step.ActiveRecruitment;
            status.CreateEditorVisible = step.EditorVisible;
            status.CreateSubmitDispatched = step.SubmitDispatched;
            status.CreateConfigurationTarget = step.ConfigurationTarget;
            status.CreateObservedSettings = step.ObservedSettings;
            status.UpdatedAtUtc = DateTime.UtcNow;
            if (step.Kind == DadAllianceNativeStepKind.Succeeded)
            {
                status.ListingId = step.ListingId;
                status.OwnsRecruitment = true;
                status.State = DadAllianceRecruitmentState.ListingOpen;
            }
            else if (step.Kind == DadAllianceNativeStepKind.Blocked)
            {
                status.State = DadAllianceRecruitmentState.Blocked;
            }
        }

        if (step.ShouldAudit)
        {
            var eventName = step.Kind switch
            {
                DadAllianceNativeStepKind.Succeeded => "listing-open",
                DadAllianceNativeStepKind.Blocked => "listing-blocked",
                _ when string.IsNullOrWhiteSpace(step.CreateEvent) => "create-readiness",
                _ => $"create-{step.CreateEvent}",
            };
            var fingerprint =
                $"{eventName}|{step.CreateStage}|{step.Attempt}|{step.NextRetryUtc:O}|" +
                $"{step.LastError}|{step.Readiness}|{step.Category}|{step.DutyId}|" +
                $"{step.ListingId}|{step.ActiveRecruitment}|{step.EditorVisible}|" +
                $"{step.SubmitDispatched}|{step.ConfigurationTarget}|" +
                $"{step.ObservedSettings}|{step.Summary}";
            if (!string.Equals(fingerprint, lastCreateAuditFingerprint, StringComparison.Ordinal))
            {
                lastCreateAuditFingerprint = fingerprint;
                AuditCreate(eventName, step);
            }
        }
    }

    private void QueueInstruction(DadAllianceRecruitmentTarget target)
    {
        var participant = ResolveParticipant(target.WorkerSessionId);
        if (participant == null)
        {
            coordinatorResults[target.CharacterKey.Value] = BuildCoordinatorRetry(
                target,
                "Target worker is temporarily disconnected.");
            return;
        }

        var priorAttempt = coordinatorResults.TryGetValue(target.CharacterKey.Value, out var prior)
            ? prior.Attempt
            : 0;
        var current = GetStatus();
        var instruction = new DadAllianceRecruitmentInstructionDto
        {
            RecruitmentId = current.RecruitmentId,
            CoordinatorWorkerSessionId = presenceService.WorkerSessionId,
            CoordinatorIdentity = coordinatorIdentity().Trim(),
            LeaderName = current.LeaderName,
            LeaderWorld = current.LeaderWorld,
            TargetWorkerSessionId = target.WorkerSessionId,
            TargetApplicationId = target.DiscordApplicationId,
            TargetCharacterKey = target.CharacterKey,
            TargetCharacterName = target.CharacterName,
            TargetCharacterWorld = target.WorldName,
            TargetContentId = target.ContentId,
            AssignedAlliance = target.Assignment,
            Passcode = current.Passcode,
            Attempt = priorAttempt + 1,
            State = DadAllianceRecruitmentState.Searching,
            StopGeneration = current.StopGeneration,
            IssuedAtUtc = DateTime.UtcNow,
        };
        coordinatorInstructions[target.CharacterKey.Value] = instruction;
        var task = DispatchInstructionAsync(participant, instruction, operationCancellation.Token);
        outboundTasks[target.CharacterKey.Value] = task;
    }

    private async Task DispatchInstructionAsync(
        DadParticipantSnapshot participant,
        DadAllianceRecruitmentInstructionDto instruction,
        CancellationToken cancellationToken)
    {
        var hubTask = transportService.SendAllianceRecruitmentInstructionAsync(
            participant,
            instruction,
            cancellationToken);
        var discordTask = discordService.SendAllianceInstructionAsync(
            instruction,
            cancellationToken).AsTask();
        DadAllianceRecruitmentResultDto? hubResult = null;
        Exception? hubFailure = null;
        (bool Sent, ulong MessageId, string SafeCode) discord =
            (false, 0, "dad-alliance-discord-not-attempted");
        try
        {
            hubResult = await hubTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            hubFailure = new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception)
        {
            hubFailure = exception;
        }

        try
        {
            discord = await discordTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            discord = (false, 0, "dad-alliance-discord-cancelled");
        }
        catch (Exception)
        {
            discord = (false, 0, "dad-alliance-discord-send-failed");
        }

        if (hubFailure is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            frameworkCompletions.Enqueue(() => Audit(
                "transport-cancelled",
                null,
                0,
                string.Empty,
                "Pending hub/Discord delivery was cancelled by Stop."));
            return;
        }

        if (hubFailure != null || hubResult == null)
        {
            var failureSummary = hubFailure?.Message ?? "The authenticated hub returned no result.";
            frameworkCompletions.Enqueue(() =>
            {
                coordinatorResults[instruction.TargetCharacterKey.Value] = BuildCoordinatorRetry(
                    coordinatorTargets[instruction.TargetCharacterKey.Value],
                    failureSummary,
                    instruction.Attempt);
                Audit(
                    "transport-failure",
                    coordinatorResults[instruction.TargetCharacterKey.Value],
                    0,
                    failureSummary,
                    "Hub delivery will retry.");
            });
            return;
        }

        frameworkCompletions.Enqueue(() =>
        {
            coordinatorResults[instruction.TargetCharacterKey.Value] = hubResult;
            if (discord.Sent)
                discordMessageIds.Add(discord.MessageId);
            Audit(
                "transport-delivery",
                hubResult,
                0,
                string.Empty,
                $"hub=delivered; discord={discord.SafeCode}");
        });
    }

    private DadAllianceRecruitmentResultDto AcceptInstruction(
        DadAllianceRecruitmentInstructionDto instruction,
        string transport,
        bool requireConnectedCoordinator)
    {
        var blocker = DadAlliancePartyFinderRules.ValidateInstruction(instruction);
        var local = presenceService.BuildLiveSafetySnapshot();
        if (string.IsNullOrWhiteSpace(blocker) &&
            requireConnectedCoordinator &&
            !transportService.IsWorkerOnline(instruction.CoordinatorWorkerSessionId))
        {
            blocker = "The authenticated DAD hub cannot currently prove the Discord coordinator is connected.";
        }
        if (string.IsNullOrWhiteSpace(blocker) &&
            (!string.Equals(
                 instruction.TargetWorkerSessionId.Value,
                 presenceService.WorkerSessionId.Value,
                 StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(
                 instruction.TargetCharacterKey.Value,
                 local.ActiveCharacterKey.Value,
                 StringComparison.OrdinalIgnoreCase) ||
             instruction.TargetContentId == 0 ||
             instruction.TargetContentId != local.Character.ContentId))
        {
            blocker = "Alliance instruction target contradicts this exact app/character.";
        }
        if (!string.IsNullOrWhiteSpace(blocker))
        {
            return new DadAllianceRecruitmentResultDto
            {
                RecruitmentId = instruction.RecruitmentId,
                WorkerSessionId = presenceService.WorkerSessionId,
                TargetCharacterKey = instruction.TargetCharacterKey,
                ExpectedAlliance = instruction.AssignedAlliance,
                Attempt = instruction.Attempt,
                State = DadAllianceRecruitmentState.Blocked,
                ResultKind = DadAllianceRecruitmentResultKind.Blocked,
                StopGeneration = instruction.StopGeneration,
                Summary = blocker,
            };
        }

        var sameActive = receiverInstruction != null &&
                         string.Equals(receiverInstruction.DedupeKey, instruction.DedupeKey, StringComparison.OrdinalIgnoreCase);
        if (sameActive && instruction.StopGeneration < receiverInstruction!.StopGeneration)
            return receiverResult.Clone();
        if (sameActive)
        {
            receiverInstruction!.Attempt = Math.Max(receiverInstruction.Attempt, instruction.Attempt);
            Audit("duplicate-delivery", receiverResult, 0, string.Empty, $"{transport} duplicate deduplicated.");
            return receiverResult.Clone();
        }

        if (!receiverDedupe.TryAccept(
                instruction.RecruitmentId,
                instruction.TargetCharacterKey,
                instruction.StopGeneration))
        {
            return receiverResult.Clone();
        }

        receiverInstruction = instruction.Clone();
        stopApplied = false;
        receiverCompletedAttempts = 0;
        receiverNextAttemptUtc = DateTime.MinValue;
        receiverResult = new DadAllianceRecruitmentResultDto
        {
            RecruitmentId = instruction.RecruitmentId,
            WorkerSessionId = presenceService.WorkerSessionId,
            TargetCharacterKey = instruction.TargetCharacterKey,
            TargetCharacterName = instruction.TargetCharacterName,
            TargetCharacterWorld = instruction.TargetCharacterWorld,
            TargetContentId = instruction.TargetContentId,
            ExpectedAlliance = instruction.AssignedAlliance,
            Attempt = instruction.Attempt,
            State = DadAllianceRecruitmentState.Searching,
            ResultKind = DadAllianceRecruitmentResultKind.Pending,
            Retryable = true,
            StopGeneration = instruction.StopGeneration,
            Summary = $"Accepted exact Alliance {instruction.AssignedAlliance} recruitment over {transport}.",
        };
        Audit("instruction-accepted", receiverResult, 0, string.Empty, receiverResult.Summary);
        return receiverResult.Clone();
    }

    private void QueueDiscordInstruction(DadAllianceRecruitmentInstructionDto instruction)
        => discordInstructions.Enqueue(instruction);

    private void AcceptDiscordInstructionOnFramework(DadAllianceRecruitmentInstructionDto instruction)
        => AcceptInstruction(instruction, "discord", requireConnectedCoordinator: true);

    private bool TryBuildTargets(
        IReadOnlyList<DadPresetCharacterSlot> slots,
        DadParticipantSnapshot local,
        out List<DadAllianceRecruitmentTarget> targets,
        out string blocker)
    {
        targets = [];
        blocker = string.Empty;
        var participants = transportService.CurrentTransport.KnownParticipants
            .Select(static participant => participant.Clone())
            .Append(local)
            .GroupBy(static participant => participant.WorkerSessionId.Value, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderByDescending(participant => participant.IsLocalClient).First())
            .ToList();

        foreach (var slot in slots)
        {
            var matches = participants.Where(participant =>
                    participant.IsAvailable &&
                    participant.WorldReadyStable &&
                    transportService.IsWorkerOnline(participant.WorkerSessionId) &&
                    string.Equals(
                        participant.ActiveCharacterKey.Value,
                        slot.CharacterKey,
                        StringComparison.OrdinalIgnoreCase) &&
                    participant.Character.ContentId == slot.ContentId.GetValueOrDefault())
                .ToList();
            if (matches.Count != 1)
            {
                blocker = matches.Count == 0
                    ? $"{slot.SlotId} exact character is not online, world-ready, and visible through the authenticated DAD hub."
                    : $"{slot.SlotId} exact character resolves to multiple authenticated DAD workers.";
                return false;
            }

            var participant = matches[0];
            targets.Add(new DadAllianceRecruitmentTarget
            {
                SlotId = slot.SlotId,
                Assignment = slot.AllianceAssignment,
                WorkerSessionId = participant.WorkerSessionId,
                DiscordApplicationId = participant.DiscordApplicationId,
                CharacterKey = new DadCharacterKey(slot.CharacterKey),
                ContentId = slot.ContentId.GetValueOrDefault(),
                CharacterName = participant.Character.CharacterName,
                WorldName = participant.Character.WorldName,
            });
        }

        return true;
    }

    private DadParticipantSnapshot? ResolveParticipant(DadWorkerSessionId workerSessionId)
    {
        if (string.Equals(
                workerSessionId.Value,
                presenceService.WorkerSessionId.Value,
                StringComparison.OrdinalIgnoreCase))
        {
            return presenceService.BuildLiveSafetySnapshot();
        }

        return transportService.CurrentTransport.KnownParticipants
            .SingleOrDefault(participant => string.Equals(
                participant.WorkerSessionId.Value,
                workerSessionId.Value,
                StringComparison.OrdinalIgnoreCase))
            ?.Clone();
    }

    private void QueueCancellation(
        DadAllianceRecruitmentTarget target,
        long stopGeneration,
        string reason)
    {
        var participant = ResolveParticipant(target.WorkerSessionId);
        var current = GetStatus();
        if (participant == null || string.IsNullOrWhiteSpace(current.RecruitmentId))
            return;
        ObserveBackground(
            transportService.SendAllianceRecruitmentCancellationAsync(
            participant,
            new DadAllianceRecruitmentCancellationDto
            {
                RecruitmentId = current.RecruitmentId,
                CoordinatorWorkerSessionId = presenceService.WorkerSessionId,
                TargetWorkerSessionId = target.WorkerSessionId,
                TargetCharacterKey = target.CharacterKey,
                StopGeneration = stopGeneration,
                Reason = reason,
            },
            CancellationToken.None),
            "hub cancellation");
    }

    private void QueueDiscordCleanup()
    {
        var messageIds = discordMessageIds.ToList();
        discordMessageIds.Clear();
        if (messageIds.Count == 0)
            return;
        ObserveBackground(
            discordService.DeleteAllianceMessagesBestEffortAsync(messageIds),
            "Discord instruction cleanup");
    }

    private void ObserveBackground(Task task, string operation)
    {
        _ = task.ContinueWith(
            completed =>
            {
                if (!completed.IsFaulted)
                    return;
                var exception = completed.Exception?.GetBaseException();
                _ = completed.Exception;
                frameworkCompletions.Enqueue(() =>
                {
                    log.Warning(exception, "DAD alliance PF {Operation} failed.", operation);
                    Audit(
                        "background-failure",
                        null,
                        0,
                        exception?.Message ?? $"{operation} failed.",
                        $"{operation} failed best-effort.");
                });
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private DadAllianceRecruitmentResultDto BuildCoordinatorRetry(
        DadAllianceRecruitmentTarget target,
        string summary,
        int attempt = 0)
        => new()
        {
            RecruitmentId = status.RecruitmentId,
            WorkerSessionId = target.WorkerSessionId,
            TargetCharacterKey = target.CharacterKey,
            TargetCharacterName = target.CharacterName,
            TargetCharacterWorld = target.WorldName,
            TargetContentId = target.ContentId,
            ExpectedAlliance = target.Assignment,
            Attempt = attempt,
            State = DadAllianceRecruitmentState.RetryWaiting,
            ResultKind = DadAllianceRecruitmentResultKind.Retry,
            Retryable = true,
            StopGeneration = status.StopGeneration,
            Summary = summary,
        };

    private DadAlliancePartyFinderStatus SetBlocked(
        string summary,
        DadAlliancePresetValidation? validation = null)
    {
        lock (statusGate)
        {
            status.State = DadAllianceRecruitmentState.Blocked;
            status.Summary = summary;
            status.Validation = validation ?? new DadAlliancePresetValidation
            {
                Blockers = [summary],
                Summary = summary,
            };
            status.UpdatedAtUtc = DateTime.UtcNow;
            return status.Clone();
        }
    }

    private void SetStatus(DadAlliancePartyFinderStatus next)
    {
        lock (statusGate)
            status = next;
    }

    private void ResetOperation()
    {
        operationCancellation.Cancel();
        operationCancellation.Dispose();
        operationCancellation = new CancellationTokenSource();
        coordinatorTargets.Clear();
        coordinatorInstructions.Clear();
        coordinatorResults.Clear();
        outboundTasks.Clear();
        discordMessageIds.Clear();
        grabRequested = false;
        cleanupRequested = false;
        stopApplied = false;
        coordinatorNextResendUtc = DateTime.MinValue;
        lastCreateAuditFingerprint = string.Empty;
    }

    private void AuditCreate(string eventName, DadAllianceNativeStep step)
    {
        var current = GetStatus();
        audit.TryWrite(new DadAlliancePfAuditRecord
        {
            TimestampUtc = DateTime.UtcNow,
            Event = eventName,
            RecruitmentId = current.RecruitmentId,
            PfOwnerHandle = step.ListingId,
            SessionId = presenceService.WorkerSessionId.Value,
            HostName = current.LeaderName,
            HostWorld = current.LeaderWorld,
            Passcode = current.Passcode,
            Attempt = step.Attempt,
            CreateStage = step.CreateStage,
            NextRetryUtc = step.NextRetryUtc,
            LastError = step.LastError,
            Readiness = step.Readiness,
            Category = step.Category,
            DutyId = step.DutyId,
            ActiveRecruitment = step.ActiveRecruitment,
            EditorVisible = step.EditorVisible,
            SubmitDispatched = step.SubmitDispatched,
            ConfigurationTarget = step.ConfigurationTarget,
            ObservedSettings = step.ObservedSettings,
            ElapsedMilliseconds = step.ElapsedMilliseconds,
            StopGeneration = current.StopGeneration,
            State = current.State.ToString(),
            Error = step.Kind is DadAllianceNativeStepKind.Blocked or DadAllianceNativeStepKind.Retry
                ? step.LastError
                : string.Empty,
            Summary = step.Summary,
            Evidence = step.CreateStage.StartsWith(
                "Cleanup:",
                StringComparison.Ordinal)
                ? []
                : new Dictionary<string, string>
                {
                    ["condition-66-using-party-finder"] =
                        step.ActiveRecruitment.ToString().ToLowerInvariant(),
                    ["condition-84-participating-cross-world-party-or-alliance"] =
                        step.ParticipatingInCrossWorldPartyOrAlliance
                            .ToString()
                            .ToLowerInvariant(),
                },
        });
    }

    private void Audit(
        string eventName,
        DadAllianceRecruitmentResultDto? result,
        int elapsedMilliseconds,
        string error,
        string summary)
    {
        var current = GetStatus();
        var instruction = result == null
            ? null
            : coordinatorInstructions.GetValueOrDefault(result.TargetCharacterKey.Value) ??
              receiverInstruction;
        audit.TryWrite(new DadAlliancePfAuditRecord
        {
            TimestampUtc = DateTime.UtcNow,
            Event = eventName,
            RecruitmentId = result?.RecruitmentId ?? current.RecruitmentId,
            PfOwnerHandle = current.ListingId,
            SessionId = presenceService.WorkerSessionId.Value,
            HostName = current.LeaderName,
            HostWorld = current.LeaderWorld,
            TargetName = result?.TargetCharacterName ?? instruction?.TargetCharacterName ?? string.Empty,
            TargetWorld = result?.TargetCharacterWorld ?? instruction?.TargetCharacterWorld ?? string.Empty,
            TargetCharacterKey = result?.TargetCharacterKey.Value ?? instruction?.TargetCharacterKey.Value ?? string.Empty,
            TargetContentId = result?.TargetContentId ?? instruction?.TargetContentId ?? 0,
            ExpectedAlliance = result?.ExpectedAlliance ?? instruction?.AssignedAlliance ?? DadAllianceAssignment.None,
            ObservedAlliance = result?.ObservedAlliance ?? DadAllianceAssignment.None,
            Passcode = current.Passcode != 0 ? current.Passcode : instruction?.Passcode ?? 0,
            Attempt = result?.Attempt ?? instruction?.Attempt ?? 0,
            CreateStage = current.CreateStage,
            NextRetryUtc = current.CreateNextRetryUtc,
            LastError = current.CreateLastError,
            ActiveRecruitment = current.CreateActiveRecruitment,
            EditorVisible = current.CreateEditorVisible,
            SubmitDispatched = current.CreateSubmitDispatched,
            ElapsedMilliseconds = elapsedMilliseconds,
            StopGeneration = result?.StopGeneration ?? current.StopGeneration,
            Transport = eventName.Contains("discord", StringComparison.OrdinalIgnoreCase)
                ? "discord"
                : eventName.Contains("transport", StringComparison.OrdinalIgnoreCase)
                    ? "hub+discord"
                    : string.Empty,
            State = (result?.State ?? current.State).ToString(),
            Error = error,
            Summary = summary,
        });
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(disposed, this);
}
