using System.Collections.Concurrent;
using dad.Models;
using Dalamud.Plugin.Services;

namespace dad.Services;

public sealed class DadAlliancePartyFinderService : IDisposable
{
    private readonly DadPresenceService presenceService;
    private readonly DadTransportService transportService;
    private readonly DadAutoPartyEndpointService endpointService;
    private readonly DadAlliancePartyFinderNativeGateway nativeGateway;
    private readonly DadAlliancePfAuditLog audit;
    private readonly Func<DadAlliancePartyFinderActionContext, string> conflictBlocker;
    private readonly Func<string> coordinatorIdentity;
    private readonly Func<IReadOnlyList<DadAutoPartyRemoteBinding>> currentRemoteBindingsProvider;
    private readonly Func<IReadOnlyList<DadAutoPartyCrewCandidate>> currentLocalCrewProvider;
    private readonly IPluginLog log;
    private readonly DadAllianceDeliveryDedupe receiverDedupe = new();
    private readonly ConcurrentQueue<Action> frameworkCompletions = new();
    private readonly ConcurrentQueue<DadAllianceCentralOperationContext> centralOperations = new();
    private readonly ConcurrentQueue<DadAllianceCentralReceiptContext> centralReceipts = new();
    private readonly Dictionary<string, DadAllianceRecruitmentTarget> coordinatorTargets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DadAllianceRecruitmentInstructionDto> coordinatorInstructions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DadAllianceRecruitmentResultDto> coordinatorResults = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task> outboundTasks = new(StringComparer.OrdinalIgnoreCase);
    private DadAllianceRecruitmentTarget? coordinatorHostTarget;
    private DadAllianceRecruitmentInstructionDto? coordinatorHostInstruction;
    private DadAllianceRecruitmentResultDto? coordinatorHostResult;
    private Task? coordinatorHostTask;
    private Task? coordinatorHostCancellationTask;
    private Task? coordinatorHostTerminalAuditTask;
    private bool coordinatorHostDispatched;
    private bool coordinatorHostAccepted;
    private bool coordinatorHostOwnsRecruitment;
    private int coordinatorHostDispatchAttempts;
    private int coordinatorHostCleanupAttempts;
    private readonly List<Guid> centralDeliveryIds = [];
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
    private bool cleanupTerminalPartial;
    private DateTime? cleanupDeadlineUtc;
    private int cleanupTerminalAuditAttempts;
    private DateTime cleanupTerminalNextAuditUtc = DateTime.MinValue;
    private bool stopApplied;
    private bool receiverHostOwnsRecruitment;
    private bool receiverHostCleanupRequested;
    private bool receiverCleanupTerminalPartial;
    private DateTime? receiverCleanupDeadlineUtc;
    private int receiverTerminalAuditAttempts;
    private string lastCreateAuditFingerprint = string.Empty;
    private long coordinatorOperationGeneration;
    private bool disposed;

    private sealed record DadAlliancePfCreatePreflightEvaluation(
        DadAlliancePartyFinderStatus Status,
        DadParticipantSnapshot? Local,
        List<DadAllianceRecruitmentTarget> Targets,
        DadAllianceRecruitmentTarget? Host);

    internal DadAlliancePartyFinderService(
        DadPresenceService presenceService,
        DadTransportService transportService,
        DadAutoPartyEndpointService endpointService,
        DadAlliancePartyFinderNativeGateway nativeGateway,
        DadAlliancePfAuditLog audit,
        Func<DadAlliancePartyFinderActionContext, string> conflictBlocker,
        Func<string> coordinatorIdentity,
        IPluginLog log,
        Func<IReadOnlyList<DadAutoPartyRemoteBinding>>? currentRemoteBindingsProvider = null,
        Func<IReadOnlyList<DadAutoPartyCrewCandidate>>? currentLocalCrewProvider = null)
    {
        this.presenceService = presenceService;
        this.transportService = transportService;
        this.endpointService = endpointService;
        this.nativeGateway = nativeGateway;
        this.audit = audit;
        this.conflictBlocker = conflictBlocker;
        this.coordinatorIdentity = coordinatorIdentity;
        this.currentRemoteBindingsProvider = currentRemoteBindingsProvider ?? (() => []);
        this.currentLocalCrewProvider = currentLocalCrewProvider ?? (() => []);
        this.log = log;
        endpointService.AllianceRecruitmentReceived += QueueCentralInstruction;
        endpointService.AllianceRecruitmentReceiptReceived += QueueCentralReceipt;
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
        => EvaluateCreatePreflight(
            group,
            preview,
            DadAlliancePartyFinderActionContext.Debug).Status.Clone();

    public DadAlliancePartyFinderStatus CreateParty(
        DadPlannerGroup? group,
        DadActivityPreset? preview)
        => CreateParty(
            group,
            preview,
            DadAlliancePartyFinderActionContext.Debug);

    internal DadAlliancePartyFinderStatus CreateCrewFormationParty(
        string crewFormationRunId,
        DadPlannerGroup? group,
        DadActivityPreset? preview)
        => CreateParty(
            group,
            preview,
            DadAlliancePartyFinderActionContext.CrewFormation(crewFormationRunId));

    private DadAlliancePartyFinderStatus CreateParty(
        DadPlannerGroup? group,
        DadActivityPreset? preview,
        DadAlliancePartyFinderActionContext actionContext)
    {
        ThrowIfDisposed();
        var preflight = EvaluateCreatePreflight(group, preview, actionContext);
        if (!preflight.Status.CreatePreflightReady)
            return RejectCreate(preflight.Status);

        var host = preflight.Host ??
                   throw new InvalidOperationException("Alliance PF Create preflight lost exact Slot1.");
        var selectedGroup = group ??
                            throw new InvalidOperationException("Alliance PF Create preflight lost the selected preset.");
        var targets = preflight.Targets;
        var validation = preflight.Status.Validation;

        ResetOperation();
        createCycles.Reset();
        nativeGateway.Reset();
        coordinatorHostTarget = string.Equals(
            host.WorkerSessionId.Value,
            presenceService.WorkerSessionId.Value,
            StringComparison.OrdinalIgnoreCase)
            ? null
            : host;
        foreach (var target in targets.Where(target =>
                     !string.Equals(target.SlotId, host.SlotId, StringComparison.OrdinalIgnoreCase)))
            coordinatorTargets[target.CharacterKey.Value] = target;

        var now = DateTime.UtcNow;
        var passcode = DadAlliancePartyFinderRules.GeneratePasscode();
        var next = new DadAlliancePartyFinderStatus
        {
            RecruitmentId = Guid.NewGuid().ToString("N"),
            State = DadAllianceRecruitmentState.CreatingListing,
            PresetGroupId = selectedGroup.GroupId,
            PresetName = selectedGroup.DisplayName,
            LeaderName = host.CharacterName,
            LeaderWorld = host.WorldName,
            Passcode = passcode,
            CreateStage = coordinatorHostTarget == null
                ? DadAlliancePfCreateStage.CloseStaleWindows.ToString()
                : "RemoteSlot1Host",
            CreatePreflightReady = false,
            CreatePreflightBlocker = DadAlliancePartyFinderCreatePreflight.ActiveRecruitmentBlocker,
            StopGeneration = status.StopGeneration,
            StartedAtUtc = now,
            UpdatedAtUtc = now,
            Summary = coordinatorHostTarget == null
                ? $"Creating a private {DadAlliancePartyFinderNativeGateway.FormationDutyName} alliance recruitment."
                : $"Dispatching private {DadAlliancePartyFinderNativeGateway.FormationDutyName} listing creation to exact remote Slot1.",
            Validation = validation,
        };
        SetStatus(next);
        Audit("create-requested", null, 0, string.Empty, next.Summary);
        return next.Clone();
    }

    private DadAlliancePfCreatePreflightEvaluation EvaluateCreatePreflight(
        DadPlannerGroup? group,
        DadActivityPreset? preview,
        DadAlliancePartyFinderActionContext actionContext)
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
        var slotOne = preview!.SelectedCharacters
            .OrderBy(static slot => DadPlannerSlotRules.GetSlotSortKey(slot.SlotId))
            .FirstOrDefault(static slot => string.Equals(
                slot.SlotId,
                DadPlannerSlotRules.LeaderSlotId,
                StringComparison.OrdinalIgnoreCase));
        var validation = DadAlliancePartyFinderRules.ValidateEffectiveSlots(
            preview.SelectedCharacters,
            slotOne == null ? null : new DadCharacterKey(slotOne.CharacterKey));
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

        var operationalBlocker = conflictBlocker(actionContext);
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
                target.SlotId,
                DadPlannerSlotRules.LeaderSlotId,
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
        return BuildCreatePreflightEvaluation(group, validation, input, local, targets, host);
    }

    private static DadAlliancePfCreatePreflightEvaluation BuildCreatePreflightEvaluation(
        DadPlannerGroup? group,
        DadAlliancePresetValidation validation,
        DadAlliancePfCreatePreflightInput input,
        DadParticipantSnapshot? local = null,
        List<DadAllianceRecruitmentTarget>? targets = null,
        DadAllianceRecruitmentTarget? host = null)
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
            targets ?? [],
            host);
    }

    private bool HasActiveRecruitment()
    {
        lock (statusGate)
        {
            var remoteHostUnresolved = coordinatorHostTarget != null &&
                                       (coordinatorHostDispatched || coordinatorHostAccepted) &&
                                       !DadAllianceRemoteHostRules.IsStoppedProof(coordinatorHostResult);
            return DadAllianceRemoteHostRules.HasActiveOperation(
                status,
                cleanupRequested,
                cleanupTerminalPartial,
                remoteHostUnresolved);
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
        while (centralOperations.TryDequeue(out var operation))
            AcceptCentralOperationOnFramework(operation);
        while (centralReceipts.TryDequeue(out var receipt))
            AcceptCentralReceiptOnFramework(receipt);

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

        if (receiverInstruction.CreateListingAsHost)
        {
            receiverInstruction.StopGeneration = cancellation.StopGeneration;
            if (!receiverHostOwnsRecruitment)
            {
                nativeGateway.StopCreate();
                receiverCleanupDeadlineUtc = null;
                receiverCleanupTerminalPartial = false;
                receiverResult = new DadAllianceRecruitmentResultDto
                {
                    RecruitmentId = cancellation.RecruitmentId,
                    WorkerSessionId = presenceService.WorkerSessionId,
                    ParticipantOwnerId = cancellation.TargetOwnerId,
                    TargetOpaqueCharacterId = cancellation.TargetOpaqueCharacterId,
                    TargetCharacterKey = cancellation.TargetCharacterKey,
                    ExpectedAlliance = DadAllianceAssignment.A,
                    ObservedAlliance = DadAllianceAssignment.A,
                    Attempt = receiverInstruction.Attempt,
                    State = DadAllianceRecruitmentState.Stopped,
                    ResultKind = DadAllianceRecruitmentResultKind.Stopped,
                    Retryable = false,
                    StopGeneration = cancellation.StopGeneration,
                    Summary = "Remote Slot1 PF create stopped before listing ownership was established.",
                };
                receiverInstruction = null;
                return receiverResult.Clone();
            }

            if (receiverCleanupTerminalPartial)
                return receiverResult.Clone();
            receiverHostCleanupRequested = true;
            receiverCleanupDeadlineUtc = DadAllianceRemoteHostRules.GetFixedCleanupDeadline(
                receiverCleanupDeadlineUtc,
                DateTime.UtcNow);
            receiverResult.ResultKind = DadAllianceRecruitmentResultKind.Waiting;
            receiverResult.State = DadAllianceRecruitmentState.Verifying;
            receiverResult.Retryable = true;
            receiverResult.StopGeneration = cancellation.StopGeneration;
            receiverResult.Summary = "Remote Slot1 accepted PF cleanup; ownership remains active until native listing clearance is proven.";
            return receiverResult.Clone();
        }

        receiverResult = new DadAllianceRecruitmentResultDto
        {
            RecruitmentId = cancellation.RecruitmentId,
            WorkerSessionId = presenceService.WorkerSessionId,
            ParticipantOwnerId = cancellation.TargetOwnerId,
            TargetOpaqueCharacterId = cancellation.TargetOpaqueCharacterId,
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
                DadAllianceRecruitmentResultKind.Stopped => DadAllianceRemoteHostRules.StoppedSafeStatusCode,
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
        coordinatorOperationGeneration++;
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
            if (status.OwnsRecruitment || coordinatorHostTarget != null)
                BeginCoordinatorCleanup(DateTime.UtcNow);
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
                TargetIslandId = receiverInstruction.TargetIslandId,
                TargetOwnerId = receiverInstruction.TargetOwnerId,
                TargetOpaqueCharacterId = receiverInstruction.TargetOpaqueCharacterId,
                TargetCharacterKey = receiverInstruction.TargetCharacterKey,
                StopGeneration = nextGeneration,
                Reason = reason,
            });
        }

        foreach (var target in coordinatorTargets.Values)
            QueueCancellation(target, nextGeneration, reason);
        QueueCentralCleanup();
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
        endpointService.AllianceRecruitmentReceived -= QueueCentralInstruction;
        endpointService.AllianceRecruitmentReceiptReceived -= QueueCentralReceipt;
        operationCancellation.Cancel();
        operationCancellation.Dispose();
        nativeGateway.Dispose();
    }

    private void UpdateCoordinator()
    {
        DadAlliancePartyFinderStatus current;
        lock (statusGate)
            current = status.Clone();

        var now = DateTime.UtcNow;
        if (cleanupRequested &&
            DadAllianceRemoteHostRules.CleanupExpired(cleanupDeadlineUtc, now))
        {
            FinishCoordinatorCleanupPartial(current);
            return;
        }

        if (cleanupTerminalPartial)
        {
            UpdateCoordinatorTerminalPartial(now);
            return;
        }

        if (!cleanupRequested &&
            current.State == DadAllianceRecruitmentState.Complete &&
            current.OwnsRecruitment)
        {
            return;
        }

        if (coordinatorHostTarget != null &&
            (cleanupRequested || current.State != DadAllianceRecruitmentState.ListingOpen))
        {
            UpdateRemoteHostCoordinator(current);
            return;
        }

        if (coordinatorHostTarget == null &&
            !current.OwnsRecruitment &&
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
                cleanupDeadlineUtc = null;
                cleanupTerminalPartial = false;
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
                QueueCentralCleanup();
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

        now = DateTime.UtcNow;
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
            lock (statusGate)
            {
                status.State = DadAllianceRecruitmentState.Complete;
                status.OwnsRecruitment = true;
                status.Summary = $"Verified all {successful} effective characters in their exact A-G subgroups; retaining the owned recruitment for operator Stop.";
                status.Results = BuildCoordinatorResultList();
                status.UpdatedAtUtc = now;
            }
            Audit("all-verified", null, 0, string.Empty, status.Summary);
            return;
        }

        coordinatorNextResendUtc = now + DadAlliancePartyFinderRules.GetRetryDelay(
            coordinatorResults.Values.DefaultIfEmpty().Max(static result => result?.Attempt ?? 0));
        lock (statusGate)
        {
            status.Results = BuildCoordinatorResultList();
            status.Summary = $"Alliance verification: {successful}/{coordinatorTargets.Count} exact subgroup assignments complete; unresolved targets will retry.";
            status.UpdatedAtUtc = now;
        }
    }

    private void UpdateRemoteHostCoordinator(DadAlliancePartyFinderStatus current)
    {
        var host = coordinatorHostTarget;
        if (host == null)
            return;

        var now = DateTime.UtcNow;
        var lifecycle = DadAllianceRemoteHostRules.Evaluate(
            true,
            coordinatorHostDispatched || coordinatorHostAccepted,
            cleanupRequested,
            coordinatorHostResult);
        if (cleanupRequested)
        {
            if (lifecycle == DadAllianceRemoteHostLifecycleState.CleanupComplete &&
                !coordinatorHostDispatched &&
                !coordinatorHostAccepted)
            {
                cleanupRequested = false;
                cleanupDeadlineUtc = null;
                cleanupTerminalPartial = false;
                lock (statusGate)
                {
                    status.OwnsRecruitment = false;
                    status.ListingId = 0;
                    status.State = current.State == DadAllianceRecruitmentState.Stopped
                        ? DadAllianceRecruitmentState.Stopped
                        : DadAllianceRecruitmentState.Complete;
                    status.Summary = "Remote Slot1 never accepted PF host ownership; no listing cleanup is pending.";
                    status.UpdatedAtUtc = now;
                }
                return;
            }

            if (lifecycle == DadAllianceRemoteHostLifecycleState.CleanupComplete)
            {
                cleanupRequested = false;
                cleanupDeadlineUtc = null;
                cleanupTerminalPartial = false;
                coordinatorHostOwnsRecruitment = false;
                lock (statusGate)
                {
                    status.OwnsRecruitment = false;
                    status.ListingId = 0;
                    status.State = current.State == DadAllianceRecruitmentState.Stopped
                        ? DadAllianceRecruitmentState.Stopped
                        : DadAllianceRecruitmentState.Complete;
                    status.Summary = coordinatorHostResult.Summary;
                    status.Results = BuildCoordinatorResultList();
                    status.UpdatedAtUtc = now;
                }
                QueueCentralCleanup();
                Audit("remote-host-recruitment-ended", coordinatorHostResult, 0, string.Empty, coordinatorHostResult.Summary);
                return;
            }

            if ((coordinatorHostCancellationTask == null || coordinatorHostCancellationTask.IsCompleted) &&
                now >= coordinatorNextResendUtc)
            {
                QueueHostCancellation(host, current.StopGeneration, current.Summary);
                coordinatorNextResendUtc = now +
                    DadAllianceRemoteHostRules.GetAuditBackoff(coordinatorHostCleanupAttempts);
            }

            lock (statusGate)
            {
                status.OwnsRecruitment = coordinatorHostOwnsRecruitment;
                status.State = current.State == DadAllianceRecruitmentState.Stopped
                    ? DadAllianceRecruitmentState.Stopped
                    : coordinatorHostResult?.ResultKind == DadAllianceRecruitmentResultKind.Blocked
                        ? DadAllianceRecruitmentState.Blocked
                        : DadAllianceRecruitmentState.Verifying;
                status.Summary = coordinatorHostResult?.ResultKind == DadAllianceRecruitmentResultKind.Blocked
                    ? $"Remote Slot1 cleanup is blocked but retained ownership cleanup will retry until {cleanupDeadlineUtc:O}: {coordinatorHostResult.Summary}"
                    : "Waiting for remote Slot1 to prove Party Finder listing ownership is cleared.";
                status.Results = BuildCoordinatorResultList();
                status.UpdatedAtUtc = now;
            }
            return;
        }

        if (lifecycle == DadAllianceRemoteHostLifecycleState.ListingOpen)
        {
            coordinatorHostOwnsRecruitment = true;
            coordinatorHostDispatchAttempts = 0;
            lock (statusGate)
            {
                status.OwnsRecruitment = coordinatorHostOwnsRecruitment;
                status.State = DadAllianceRecruitmentState.ListingOpen;
                status.Summary = "Remote Slot1 proved its owned Alliance-A Party Finder listing is open.";
                status.Results = BuildCoordinatorResultList();
                status.UpdatedAtUtc = now;
            }
            Audit("remote-host-listing-open", coordinatorHostResult, 0, string.Empty, status.Summary);
            return;
        }

        if (lifecycle == DadAllianceRemoteHostLifecycleState.Blocked)
        {
            lock (statusGate)
            {
                status.OwnsRecruitment = coordinatorHostOwnsRecruitment;
                status.State = DadAllianceRecruitmentState.Blocked;
                status.Summary = coordinatorHostResult.Summary;
                status.Results = BuildCoordinatorResultList();
                status.UpdatedAtUtc = now;
            }
            return;
        }

        if ((coordinatorHostTask == null || coordinatorHostTask.IsCompleted) &&
            now >= coordinatorNextResendUtc)
        {
            QueueHostInstruction(host);
            coordinatorNextResendUtc = now +
                DadAllianceRemoteHostRules.GetAuditBackoff(coordinatorHostDispatchAttempts);
        }

        lock (statusGate)
        {
            status.OwnsRecruitment = coordinatorHostOwnsRecruitment;
            status.State = DadAllianceRecruitmentState.CreatingListing;
            status.Summary = coordinatorHostResult?.Summary ??
                             "Dispatching authenticated Party Finder create instruction to exact remote Slot1.";
            status.Results = BuildCoordinatorResultList();
            status.UpdatedAtUtc = now;
        }
    }

    private void QueueHostInstruction(DadAllianceRecruitmentTarget host)
    {
        var participant = ResolveParticipant(host.WorkerSessionId);
        if (participant == null)
        {
            coordinatorHostResult = BuildCoordinatorRetry(host, "Remote Slot1 worker is temporarily disconnected.");
            return;
        }

        var current = GetStatus();
        var instruction = new DadAllianceRecruitmentInstructionDto
        {
            RecruitmentId = current.RecruitmentId,
            CoordinatorWorkerSessionId = presenceService.WorkerSessionId,
            CoordinatorIdentity = coordinatorIdentity().Trim(),
            LeaderName = host.CharacterName,
            LeaderWorld = host.WorldName,
            TargetWorkerSessionId = host.WorkerSessionId,
            TargetIslandId = host.RegisteredIslandId,
            TargetOwnerId = host.OwnerId,
            TargetOpaqueCharacterId = host.OpaqueCharacterId,
            TargetCharacterKey = host.CharacterKey,
            TargetCharacterName = host.CharacterName,
            TargetCharacterWorld = host.WorldName,
            TargetContentId = host.ContentId,
            AssignedAlliance = DadAllianceAssignment.A,
            CreateListingAsHost = true,
            Passcode = current.Passcode,
            Attempt = (coordinatorHostResult?.Attempt ?? 0) + 1,
            State = DadAllianceRecruitmentState.CreatingListing,
            StopGeneration = current.StopGeneration,
            IssuedAtUtc = DateTime.UtcNow,
        };
        coordinatorHostInstruction = instruction.Clone();
        coordinatorHostDispatched = true;
        coordinatorHostDispatchAttempts++;
        coordinatorHostTask = DispatchHostInstructionAsync(
            participant,
            instruction,
            coordinatorOperationGeneration);
    }

    private async Task DispatchHostInstructionAsync(
        DadParticipantSnapshot participant,
        DadAllianceRecruitmentInstructionDto instruction,
        long dispatchedGeneration)
    {
        DadAllianceCentralSendResult relay;
        try
        {
            relay = await endpointService.SendAllianceInstructionAsync(
                    instruction,
                    operationCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
        {
            relay = new(false, Guid.Empty, "dad-alliance-central-cancelled");
        }
        catch
        {
            relay = new(false, Guid.Empty, "dad-alliance-central-send-failed");
        }
        try
        {
            var result = await transportService.SendAllianceRecruitmentInstructionAsync(
                    participant,
                    instruction,
                    operationCancellation.Token)
                .ConfigureAwait(false);
            frameworkCompletions.Enqueue(() =>
            {
                if (!IsCurrentHostInstruction(dispatchedGeneration, instruction))
                {
                    AuditLateCompletion(
                        dispatchedGeneration,
                        instruction,
                        "Remote host completion arrived after its instruction changed.");
                    return;
                }
                if (!DadAlliancePartyFinderRules.TryValidateAsyncResult(
                        dispatchedGeneration,
                        coordinatorOperationGeneration,
                        instruction,
                        result,
                        presenceService.WorkerSessionId,
                        out var lateBlocker))
                {
                    AuditLateCompletion(dispatchedGeneration, instruction, lateBlocker);
                    return;
                }
                coordinatorHostAccepted = true;
                coordinatorHostResult = result.Clone();
                if (relay.Sent)
                    centralDeliveryIds.Add(relay.MessageId);
                Audit(
                    "remote-host-transport-delivery",
                    result,
                    0,
                    string.Empty,
                    $"hub=delivered; central={relay.SafeCode}");
            });
        }
        catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            frameworkCompletions.Enqueue(() =>
            {
                if (!IsCurrentHostInstruction(dispatchedGeneration, instruction))
                {
                    AuditLateCompletion(dispatchedGeneration, instruction, "Remote host failure arrived after its operation changed.");
                    return;
                }
                coordinatorHostResult = BuildCoordinatorRetry(
                    coordinatorHostTarget!,
                    exception.Message,
                    instruction.Attempt);
                if (relay.Sent)
                    centralDeliveryIds.Add(relay.MessageId);
                Audit(
                    "remote-host-transport-failure",
                    coordinatorHostResult,
                    0,
                    exception.Message,
                    $"Hub delivery will retry; central={relay.SafeCode}");
            });
        }
    }

    private void QueueHostCancellation(
        DadAllianceRecruitmentTarget host,
        long stopGeneration,
        string reason)
    {
        var participant = ResolveParticipant(host.WorkerSessionId);
        var current = GetStatus();
        if (participant == null || string.IsNullOrWhiteSpace(current.RecruitmentId))
            return;

        var cancellation = new DadAllianceRecruitmentCancellationDto
            {
                RecruitmentId = current.RecruitmentId,
                CoordinatorWorkerSessionId = presenceService.WorkerSessionId,
                TargetWorkerSessionId = host.WorkerSessionId,
                TargetIslandId = host.RegisteredIslandId,
                TargetOwnerId = host.OwnerId,
                TargetOpaqueCharacterId = host.OpaqueCharacterId,
                TargetCharacterKey = host.CharacterKey,
                StopGeneration = stopGeneration,
                Reason = reason,
            };
        var instruction = coordinatorHostInstruction?.Clone();
        if (instruction == null)
            return;
        coordinatorHostCleanupAttempts++;
        coordinatorHostCancellationTask = DispatchHostCancellationAsync(
            participant,
            cancellation,
            instruction,
            coordinatorOperationGeneration);
    }

    private async Task DispatchHostCancellationAsync(
        DadParticipantSnapshot participant,
        DadAllianceRecruitmentCancellationDto cancellation,
        DadAllianceRecruitmentInstructionDto instruction,
        long dispatchedGeneration)
    {
        DadAllianceCentralSendResult relay;
        try
        {
            relay = await endpointService.SendAllianceCancellationAsync(
                    cancellation,
                    instruction,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            relay = new(false, Guid.Empty, "dad-alliance-central-cancellation-failed");
        }
        try
        {
            var result = await transportService.SendAllianceRecruitmentCancellationAsync(
                    participant,
                    cancellation,
                    CancellationToken.None)
                .ConfigureAwait(false);
            frameworkCompletions.Enqueue(() =>
            {
                if (!IsCurrentHostInstruction(dispatchedGeneration, instruction))
                {
                    AuditLateCompletion(
                        dispatchedGeneration,
                        instruction,
                        "Remote host cleanup completion arrived after its instruction changed.");
                    return;
                }
                if (!DadAlliancePartyFinderRules.TryValidateAsyncCancellationResult(
                        dispatchedGeneration,
                        coordinatorOperationGeneration,
                        cancellation,
                        instruction,
                        result,
                        out var lateBlocker))
                {
                    AuditLateCompletion(dispatchedGeneration, instruction, lateBlocker);
                    return;
                }
                coordinatorHostResult = result.Clone();
                if (relay.Sent)
                    centralDeliveryIds.Add(relay.MessageId);
            });
        }
        catch (Exception exception)
        {
            frameworkCompletions.Enqueue(() =>
            {
                if (!IsCurrentHostInstruction(dispatchedGeneration, instruction))
                {
                    AuditLateCompletion(dispatchedGeneration, instruction, "Remote host cleanup failure arrived after its operation changed.");
                    return;
                }
                coordinatorHostResult = BuildCoordinatorRetry(
                    coordinatorHostTarget!,
                    $"Remote Slot1 cleanup response failed: {exception.Message}",
                    instruction.Attempt);
                if (relay.Sent)
                    centralDeliveryIds.Add(relay.MessageId);
            });
        }
    }

    private List<DadAllianceRecruitmentResultDto> BuildCoordinatorResultList()
    {
        var results = coordinatorResults.Values.Select(static result => result.Clone()).ToList();
        if (coordinatorHostResult != null)
            results.Insert(0, coordinatorHostResult.Clone());
        return results;
    }

    private void UpdateReceiver()
    {
        var instruction = receiverInstruction;
        if (instruction == null || DateTime.UtcNow < receiverNextAttemptUtc)
            return;
        if (instruction.CreateListingAsHost)
        {
            UpdateHostReceiver(instruction);
            return;
        }
        if (receiverResult.IsTerminal)
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

    private void UpdateHostReceiver(DadAllianceRecruitmentInstructionDto instruction)
    {
        var started = DateTime.UtcNow;
        if (receiverCleanupTerminalPartial)
        {
            var activeRecruitment = nativeGateway.ObserveActiveRecruitment();
            receiverTerminalAuditAttempts++;
            receiverNextAttemptUtc = started +
                                     DadAllianceRemoteHostRules.GetAuditBackoff(
                                         receiverTerminalAuditAttempts);
            if (!DadAllianceRemoteHostRules.CanClearTerminalPartial(
                    receiverCleanupTerminalPartial,
                    activeRecruitment))
            {
                return;
            }

            receiverHostOwnsRecruitment = false;
            receiverHostCleanupRequested = false;
            receiverCleanupTerminalPartial = false;
            receiverCleanupDeadlineUtc = null;
            receiverTerminalAuditAttempts = 0;
            receiverResult.ObservedAtUtc = started;
            receiverResult.State = DadAllianceRecruitmentState.Stopped;
            receiverResult.ResultKind = DadAllianceRecruitmentResultKind.Stopped;
            receiverResult.Retryable = false;
            receiverResult.StopGeneration = instruction.StopGeneration;
            receiverResult.Summary =
                "Observed that operator cleanup removed the remote Slot1 Party Finder listing after the automatic cleanup deadline.";
            receiverInstruction = null;
            Audit(
                "remote-host-operator-cleanup-observed",
                receiverResult,
                0,
                string.Empty,
                receiverResult.Summary);
            return;
        }

        if (receiverHostCleanupRequested)
        {
            if (DadAllianceRemoteHostRules.CleanupExpired(
                    receiverCleanupDeadlineUtc,
                    DateTime.UtcNow))
            {
                receiverHostCleanupRequested = false;
                receiverCleanupTerminalPartial = true;
                receiverTerminalAuditAttempts = 0;
                receiverResult.ObservedAtUtc = DateTime.UtcNow;
                receiverResult.State = DadAllianceRecruitmentState.Blocked;
                receiverResult.ResultKind = DadAllianceRecruitmentResultKind.Blocked;
                receiverResult.Retryable = false;
                receiverResult.Summary =
                    "PARTIAL: remote Slot1 cleanup deadline elapsed while DAD still owns or may own the Party Finder listing; operator cleanup is required.";
                Audit(
                    "remote-host-cleanup-deadline",
                    receiverResult,
                    (int)DadAllianceRemoteHostRules.CleanupDeadline.TotalMilliseconds,
                    receiverResult.Summary,
                    receiverResult.Summary);
                return;
            }

            var cleanup = nativeGateway.AdvanceEndRecruitment(receiverHostOwnsRecruitment);
            receiverResult.ObservedAtUtc = DateTime.UtcNow;
            receiverResult.StopGeneration = instruction.StopGeneration;
            receiverResult.Summary = cleanup.Summary;
            receiverResult.State = cleanup.State;
            if (cleanup.Kind == DadAllianceNativeStepKind.Succeeded)
            {
                receiverHostOwnsRecruitment = false;
                receiverHostCleanupRequested = false;
                receiverCleanupDeadlineUtc = null;
                receiverCleanupTerminalPartial = false;
                receiverTerminalAuditAttempts = 0;
                receiverResult.State = DadAllianceRecruitmentState.Stopped;
                receiverResult.ResultKind = DadAllianceRecruitmentResultKind.Stopped;
                receiverResult.Retryable = false;
                receiverInstruction = null;
            }
            else if (cleanup.Kind == DadAllianceNativeStepKind.Blocked)
            {
                receiverResult.State = DadAllianceRecruitmentState.Blocked;
                receiverResult.ResultKind = DadAllianceRecruitmentResultKind.Waiting;
                receiverResult.Retryable = true;
                receiverNextAttemptUtc = DateTime.UtcNow + TimeSpan.FromMilliseconds(250);
            }
            else
            {
                receiverResult.State = DadAllianceRecruitmentState.Verifying;
                receiverResult.ResultKind = DadAllianceRecruitmentResultKind.Waiting;
                receiverResult.Retryable = true;
                receiverNextAttemptUtc = DateTime.UtcNow + TimeSpan.FromMilliseconds(250);
            }

            Audit(
                "remote-host-cleanup",
                receiverResult,
                (int)(DateTime.UtcNow - started).TotalMilliseconds,
                cleanup.Kind == DadAllianceNativeStepKind.Blocked ? cleanup.Summary : string.Empty,
                cleanup.Summary);
            return;
        }

        if (receiverResult is
            {
                ResultKind: DadAllianceRecruitmentResultKind.Succeeded,
                State: DadAllianceRecruitmentState.ListingOpen,
            })
        {
            return;
        }

        var step = nativeGateway.AdvanceCreate(instruction.Passcode);
        receiverResult.RecruitmentId = instruction.RecruitmentId;
        receiverResult.WorkerSessionId = presenceService.WorkerSessionId;
        receiverResult.TargetCharacterKey = instruction.TargetCharacterKey;
        receiverResult.TargetCharacterName = instruction.TargetCharacterName;
        receiverResult.TargetCharacterWorld = instruction.TargetCharacterWorld;
        receiverResult.TargetContentId = instruction.TargetContentId;
        receiverResult.ExpectedAlliance = DadAllianceAssignment.A;
        receiverResult.ObservedAlliance = step.Kind == DadAllianceNativeStepKind.Succeeded
            ? DadAllianceAssignment.A
            : DadAllianceAssignment.None;
        receiverResult.Attempt = instruction.Attempt;
        receiverResult.State = step.State;
        receiverResult.StopGeneration = instruction.StopGeneration;
        receiverResult.ObservedAtUtc = DateTime.UtcNow;
        receiverResult.Summary = step.Summary;

        if (step.Kind == DadAllianceNativeStepKind.Succeeded)
        {
            receiverHostOwnsRecruitment = true;
            receiverResult.State = DadAllianceRecruitmentState.ListingOpen;
            receiverResult.ResultKind = DadAllianceRecruitmentResultKind.Succeeded;
            receiverResult.Retryable = false;
        }
        else if (step.Kind == DadAllianceNativeStepKind.Blocked)
        {
            receiverHostOwnsRecruitment |= step.ActiveRecruitment;
            receiverResult.State = DadAllianceRecruitmentState.Blocked;
            receiverResult.ResultKind = DadAllianceRecruitmentResultKind.Blocked;
            receiverResult.Retryable = false;
        }
        else
        {
            receiverHostOwnsRecruitment |= step.ActiveRecruitment;
            receiverResult.ResultKind = step.Kind == DadAllianceNativeStepKind.Retry
                ? DadAllianceRecruitmentResultKind.Retry
                : DadAllianceRecruitmentResultKind.Waiting;
            receiverResult.Retryable = true;
            receiverNextAttemptUtc = DateTime.UtcNow + TimeSpan.FromMilliseconds(250);
        }

        Audit(
            "remote-host-create",
            receiverResult,
            (int)(DateTime.UtcNow - started).TotalMilliseconds,
            step.Kind == DadAllianceNativeStepKind.Blocked ? step.Summary : string.Empty,
            step.Summary);
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
            TargetIslandId = target.RegisteredIslandId,
            TargetOwnerId = target.OwnerId,
            TargetOpaqueCharacterId = target.OpaqueCharacterId,
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
        var task = DispatchInstructionAsync(
            participant,
            instruction,
            operationCancellation.Token,
            coordinatorOperationGeneration);
        outboundTasks[target.CharacterKey.Value] = task;
    }

    private async Task DispatchInstructionAsync(
        DadParticipantSnapshot participant,
        DadAllianceRecruitmentInstructionDto instruction,
        CancellationToken cancellationToken,
        long dispatchedGeneration)
    {
        var hubTask = transportService.SendAllianceRecruitmentInstructionAsync(
            participant,
            instruction,
            cancellationToken);
        var relayTask = endpointService.SendAllianceInstructionAsync(
            instruction,
            cancellationToken).AsTask();
        DadAllianceRecruitmentResultDto? hubResult = null;
        Exception? hubFailure = null;
        var relay = new DadAllianceCentralSendResult(
            false,
            Guid.Empty,
            "dad-alliance-central-not-attempted");
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
            relay = await relayTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            relay = new(false, Guid.Empty, "dad-alliance-central-cancelled");
        }
        catch (Exception)
        {
            relay = new(false, Guid.Empty, "dad-alliance-central-send-failed");
        }

        if (hubFailure is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            frameworkCompletions.Enqueue(() =>
            {
                if (!IsCurrentCoordinatorInstruction(dispatchedGeneration, instruction))
                {
                    AuditLateCompletion(dispatchedGeneration, instruction, "Cancelled transport completion arrived after its operation changed.");
                    return;
                }
                Audit(
                    "transport-cancelled",
                    null,
                    0,
                    string.Empty,
                    "Pending hub/central delivery was cancelled by Stop.");
            });
            return;
        }

        if (hubFailure != null || hubResult == null)
        {
            var failureSummary = hubFailure?.Message ?? "The authenticated hub returned no result.";
            frameworkCompletions.Enqueue(() =>
            {
                if (!IsCurrentCoordinatorInstruction(dispatchedGeneration, instruction))
                {
                    AuditLateCompletion(dispatchedGeneration, instruction, "Failed transport completion arrived after its operation changed.");
                    return;
                }
                if (!coordinatorTargets.TryGetValue(instruction.TargetCharacterKey.Value, out var target))
                {
                    AuditLateCompletion(
                        dispatchedGeneration,
                        instruction,
                        "Failed transport completion no longer has an active coordinator target.");
                    return;
                }
                coordinatorResults[instruction.TargetCharacterKey.Value] = BuildCoordinatorRetry(
                    target,
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
            if (!IsCurrentCoordinatorInstruction(dispatchedGeneration, instruction))
            {
                AuditLateCompletion(
                    dispatchedGeneration,
                    instruction,
                    "Transport completion arrived after its coordinator instruction changed.");
                return;
            }
            if (!DadAlliancePartyFinderRules.TryValidateAsyncResult(
                    dispatchedGeneration,
                    coordinatorOperationGeneration,
                    instruction,
                    hubResult,
                    presenceService.WorkerSessionId,
                    out var lateBlocker))
            {
                AuditLateCompletion(dispatchedGeneration, instruction, lateBlocker);
                return;
            }
            coordinatorResults[instruction.TargetCharacterKey.Value] = hubResult!;
            if (relay.Sent)
                centralDeliveryIds.Add(relay.MessageId);
            Audit(
                "transport-delivery",
                hubResult!,
                0,
                string.Empty,
                $"hub=delivered; central={relay.SafeCode}");
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
            blocker = "The authenticated DAD hub cannot currently prove the central-route coordinator is connected.";
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
                ParticipantOwnerId = instruction.TargetOwnerId,
                TargetOpaqueCharacterId = instruction.TargetOpaqueCharacterId,
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
        if (instruction.CreateListingAsHost)
        {
            createCycles.Reset();
            nativeGateway.Reset();
            receiverHostOwnsRecruitment = false;
            receiverHostCleanupRequested = false;
            receiverCleanupTerminalPartial = false;
            receiverCleanupDeadlineUtc = null;
            receiverTerminalAuditAttempts = 0;
        }
        stopApplied = false;
        receiverCompletedAttempts = 0;
        receiverNextAttemptUtc = DateTime.MinValue;
        receiverResult = new DadAllianceRecruitmentResultDto
        {
            RecruitmentId = instruction.RecruitmentId,
            WorkerSessionId = presenceService.WorkerSessionId,
            ParticipantOwnerId = instruction.TargetOwnerId,
            TargetOpaqueCharacterId = instruction.TargetOpaqueCharacterId,
            TargetCharacterKey = instruction.TargetCharacterKey,
            TargetCharacterName = instruction.TargetCharacterName,
            TargetCharacterWorld = instruction.TargetCharacterWorld,
            TargetContentId = instruction.TargetContentId,
            ExpectedAlliance = instruction.AssignedAlliance,
            Attempt = instruction.Attempt,
            State = instruction.CreateListingAsHost
                ? DadAllianceRecruitmentState.CreatingListing
                : DadAllianceRecruitmentState.Searching,
            ResultKind = DadAllianceRecruitmentResultKind.Pending,
            Retryable = true,
            StopGeneration = instruction.StopGeneration,
            Summary = instruction.CreateListingAsHost
                ? $"Accepted exact remote Slot1 Alliance-A PF host instruction over {transport}."
                : $"Accepted exact Alliance {instruction.AssignedAlliance} recruitment over {transport}.",
        };
        Audit("instruction-accepted", receiverResult, 0, string.Empty, receiverResult.Summary);
        return receiverResult.Clone();
    }

    private void QueueCentralInstruction(DadAllianceCentralOperationContext operation)
        => centralOperations.Enqueue(operation);

    private void QueueCentralReceipt(DadAllianceCentralReceiptContext receipt)
        => centralReceipts.Enqueue(receipt);

    private void AcceptCentralOperationOnFramework(DadAllianceCentralOperationContext context)
    {
        DadAllianceRecruitmentResultDto result;
        if (context.Instruction != null)
        {
            if (TryHydrateCentralInstruction(context, context.Instruction, out var instruction, out var blocker))
            {
                result = AcceptInstruction(instruction, "central", requireConnectedCoordinator: false);
            }
            else
            {
                result = BuildCentralRejectedResult(context.Instruction, blocker);
            }
        }
        else if (context.Cancellation != null)
        {
            var cancellation = context.Cancellation;
            var current = receiverInstruction;
            if (current != null &&
                string.Equals(current.RecruitmentId, cancellation.RecruitmentId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    current.TargetOpaqueCharacterId,
                    cancellation.TargetOpaqueCharacterId,
                    StringComparison.Ordinal))
            {
                cancellation.CoordinatorWorkerSessionId = current.CoordinatorWorkerSessionId;
                cancellation.TargetWorkerSessionId = current.TargetWorkerSessionId;
                cancellation.TargetCharacterKey = current.TargetCharacterKey;
                result = AcceptCancellation(cancellation);
            }
            else
            {
                result = BuildCentralRejectedCancellation(cancellation);
            }
        }
        else
        {
            return;
        }

        var queued = endpointService.QueueAllianceReceipt(context.OperationId, result);
        Audit(
            queued.Allowed ? "central-operation" : "central-receipt-rejected",
            result,
            0,
            queued.Allowed ? string.Empty : queued.SafeCode,
            queued.SafeCode);
    }

    private bool TryHydrateCentralInstruction(
        DadAllianceCentralOperationContext context,
        DadAllianceRecruitmentInstructionDto source,
        out DadAllianceRecruitmentInstructionDto instruction,
        out string blocker)
    {
        instruction = source.Clone();
        blocker = string.Empty;
        var local = presenceService.BuildLiveSafetySnapshot();
        var rows = currentLocalCrewProvider()
            .Where(candidate => candidate != null &&
                          string.Equals(candidate.Identity.OpaqueCharacterId, source.TargetOpaqueCharacterId, StringComparison.Ordinal) &&
                          string.Equals(candidate.Character.CharacterKey, local.ActiveCharacterKey.Value, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        if (rows.Count != 1 || local.WorkerSessionId.IsEmpty || local.ActiveCharacterKey.IsEmpty ||
            local.Character.ContentId == 0 || string.IsNullOrWhiteSpace(local.Character.CharacterName) ||
            string.IsNullOrWhiteSpace(local.Character.WorldName))
        {
            blocker = "dad-alliance-central-target-not-local";
            return false;
        }

        instruction.CoordinatorWorkerSessionId = new DadWorkerSessionId(context.SenderIslandId);
        instruction.CoordinatorIdentity = context.SenderIslandId;
        instruction.TargetWorkerSessionId = local.WorkerSessionId;
        instruction.TargetCharacterKey = local.ActiveCharacterKey;
        instruction.TargetCharacterName = local.Character.CharacterName;
        instruction.TargetCharacterWorld = local.Character.WorldName;
        instruction.TargetContentId = local.Character.ContentId;
        blocker = DadAlliancePartyFinderRules.ValidateInstruction(instruction);
        return blocker.Length == 0;
    }

    private static DadAllianceRecruitmentResultDto BuildCentralRejectedResult(
        DadAllianceRecruitmentInstructionDto instruction,
        string blocker)
        => new()
        {
            RecruitmentId = instruction.RecruitmentId,
            ParticipantOwnerId = instruction.TargetOwnerId,
            TargetOpaqueCharacterId = instruction.TargetOpaqueCharacterId,
            ExpectedAlliance = instruction.AssignedAlliance,
            Attempt = instruction.Attempt,
            State = DadAllianceRecruitmentState.Blocked,
            ResultKind = DadAllianceRecruitmentResultKind.Blocked,
            Retryable = false,
            StopGeneration = instruction.StopGeneration,
            Summary = blocker,
        };

    private DadAllianceRecruitmentResultDto BuildCentralRejectedCancellation(
        DadAllianceRecruitmentCancellationDto cancellation)
        => new()
        {
            RecruitmentId = cancellation.RecruitmentId,
            ParticipantOwnerId = cancellation.TargetOwnerId,
            TargetOpaqueCharacterId = cancellation.TargetOpaqueCharacterId,
            ExpectedAlliance = DadAlliancePartyFinderRules.IsConcreteAssignment(receiverResult.ExpectedAlliance)
                ? receiverResult.ExpectedAlliance
                : DadAllianceAssignment.A,
            Attempt = Math.Max(0, receiverResult.Attempt),
            State = DadAllianceRecruitmentState.Blocked,
            ResultKind = DadAllianceRecruitmentResultKind.Blocked,
            Retryable = false,
            StopGeneration = cancellation.StopGeneration,
            Summary = "dad-alliance-central-cancellation-unmatched",
        };

    private void AcceptCentralReceiptOnFramework(DadAllianceCentralReceiptContext context)
    {
        var instruction = context.Instruction;
        var result = context.Result.Clone();
        result.WorkerSessionId = instruction.TargetWorkerSessionId;
        result.TargetCharacterKey = instruction.TargetCharacterKey;
        result.TargetCharacterName = instruction.TargetCharacterName;
        result.TargetCharacterWorld = instruction.TargetCharacterWorld;
        result.TargetContentId = instruction.TargetContentId;

        if (coordinatorHostInstruction != null &&
            IsSameInstruction(coordinatorHostInstruction, instruction))
        {
            var valid = context.Cancellation == null
                ? DadAlliancePartyFinderRules.TryValidateAsyncResult(
                    coordinatorOperationGeneration,
                    coordinatorOperationGeneration,
                    instruction,
                    result,
                    presenceService.WorkerSessionId,
                    out var blocker)
                : DadAlliancePartyFinderRules.TryValidateAsyncCancellationResult(
                    coordinatorOperationGeneration,
                    coordinatorOperationGeneration,
                    context.Cancellation,
                    instruction,
                    result,
                    out blocker);
            if (!valid)
            {
                AuditLateCompletion(coordinatorOperationGeneration, instruction, blocker);
                return;
            }
            coordinatorHostAccepted = true;
            coordinatorHostResult = result;
            Audit("central-host-receipt", result, 0, string.Empty, result.Summary);
            return;
        }

        var target = coordinatorInstructions
            .SingleOrDefault(pair => IsSameInstruction(pair.Value, instruction));
        if (string.IsNullOrWhiteSpace(target.Key))
        {
            AuditLateCompletion(
                coordinatorOperationGeneration,
                instruction,
                "Central Alliance receipt no longer has an active target.");
            return;
        }
        var targetValid = context.Cancellation == null
            ? DadAlliancePartyFinderRules.TryValidateAsyncResult(
                coordinatorOperationGeneration,
                coordinatorOperationGeneration,
                instruction,
                result,
                presenceService.WorkerSessionId,
                out var resultBlocker)
            : DadAlliancePartyFinderRules.TryValidateAsyncCancellationResult(
                coordinatorOperationGeneration,
                coordinatorOperationGeneration,
                context.Cancellation,
                instruction,
                result,
                out resultBlocker);
        if (!targetValid)
        {
            AuditLateCompletion(coordinatorOperationGeneration, instruction, resultBlocker);
            return;
        }
        if (coordinatorResults.TryGetValue(target.Key, out var current) &&
            current.ResultKind == DadAllianceRecruitmentResultKind.Succeeded &&
            result.ResultKind != DadAllianceRecruitmentResultKind.Succeeded)
            return;
        coordinatorResults[target.Key] = result;
        Audit("central-receipt", result, 0, string.Empty, result.Summary);
    }

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
            var opaqueCharacterId = (slot.SharedIdentityToken ?? string.Empty).Trim();
            DadAutoPartyRemoteBinding? registeredBinding = null;
            if (opaqueCharacterId.Length > 0)
            {
                var bindings = currentRemoteBindingsProvider()
                    .Where(binding => binding.IsValid &&
                        string.Equals(binding.OpaqueCharacterId, opaqueCharacterId, StringComparison.Ordinal))
                    .ToList();
                if (bindings.Count != 1)
                {
                    blocker = $"{slot.SlotId} registered-island identity does not resolve to one current runtime binding.";
                    return false;
                }
                registeredBinding = bindings[0];
            }

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
                RegisteredIslandId = registeredBinding?.IslandId ?? participant.RegisteredIslandId,
                OwnerId = registeredBinding?.OwnerId ?? string.Empty,
                OpaqueCharacterId = opaqueCharacterId,
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
        if (participant == null || string.IsNullOrWhiteSpace(current.RecruitmentId) ||
            !coordinatorInstructions.TryGetValue(target.CharacterKey.Value, out var instruction))
            return;
        var cancellation = new DadAllianceRecruitmentCancellationDto
        {
            RecruitmentId = current.RecruitmentId,
            CoordinatorWorkerSessionId = presenceService.WorkerSessionId,
            TargetWorkerSessionId = target.WorkerSessionId,
            TargetIslandId = target.RegisteredIslandId,
            TargetOwnerId = target.OwnerId,
            TargetOpaqueCharacterId = target.OpaqueCharacterId,
            TargetCharacterKey = target.CharacterKey,
            StopGeneration = stopGeneration,
            Reason = reason,
        };
        ObserveBackground(
            DispatchCancellationBestEffortAsync(
            participant,
            cancellation,
            instruction),
            "hub/central cancellation");
    }

    private async Task DispatchCancellationBestEffortAsync(
        DadParticipantSnapshot participant,
        DadAllianceRecruitmentCancellationDto cancellation,
        DadAllianceRecruitmentInstructionDto instruction)
    {
        var hubTask = transportService.SendAllianceRecruitmentCancellationAsync(
            participant,
            cancellation,
            CancellationToken.None);
        DadAllianceCentralSendResult relay;
        try
        {
            relay = await endpointService.SendAllianceCancellationAsync(
                    cancellation,
                    instruction,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            relay = new(false, Guid.Empty, "dad-alliance-central-cancellation-failed");
        }
        _ = await hubTask.ConfigureAwait(false);
        if (relay.Sent)
            frameworkCompletions.Enqueue(() => centralDeliveryIds.Add(relay.MessageId));
    }

    private void QueueCentralCleanup()
    {
        var messageIds = centralDeliveryIds.ToList();
        centralDeliveryIds.Clear();
        if (messageIds.Count == 0)
            return;
        ObserveBackground(
            endpointService.ForgetAllianceDeliveriesBestEffortAsync(messageIds),
            "central instruction cleanup");
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
        coordinatorOperationGeneration++;
        operationCancellation.Cancel();
        operationCancellation.Dispose();
        operationCancellation = new CancellationTokenSource();
        coordinatorTargets.Clear();
        coordinatorInstructions.Clear();
        coordinatorResults.Clear();
        outboundTasks.Clear();
        coordinatorHostTarget = null;
        coordinatorHostInstruction = null;
        coordinatorHostResult = null;
        coordinatorHostTask = null;
        coordinatorHostCancellationTask = null;
        coordinatorHostTerminalAuditTask = null;
        coordinatorHostDispatched = false;
        coordinatorHostAccepted = false;
        coordinatorHostOwnsRecruitment = false;
        coordinatorHostDispatchAttempts = 0;
        coordinatorHostCleanupAttempts = 0;
        centralDeliveryIds.Clear();
        grabRequested = false;
        cleanupRequested = false;
        cleanupTerminalPartial = false;
        cleanupDeadlineUtc = null;
        cleanupTerminalAuditAttempts = 0;
        cleanupTerminalNextAuditUtc = DateTime.MinValue;
        stopApplied = false;
        coordinatorNextResendUtc = DateTime.MinValue;
        lastCreateAuditFingerprint = string.Empty;
    }

    private void BeginCoordinatorCleanup(DateTime nowUtc)
    {
        cleanupRequested = true;
        cleanupTerminalPartial = false;
        cleanupTerminalAuditAttempts = 0;
        cleanupTerminalNextAuditUtc = DateTime.MinValue;
        coordinatorHostTerminalAuditTask = null;
        cleanupDeadlineUtc = DadAllianceRemoteHostRules.GetFixedCleanupDeadline(
            cleanupDeadlineUtc,
            nowUtc);
    }

    private void FinishCoordinatorCleanupPartial(DadAlliancePartyFinderStatus current)
    {
        cleanupRequested = false;
        cleanupTerminalPartial = true;
        cleanupTerminalAuditAttempts = 0;
        cleanupTerminalNextAuditUtc = DateTime.MinValue;
        coordinatorHostTerminalAuditTask = null;
        var ownershipSummary = coordinatorHostTarget == null
            ? "DAD still owns the local Party Finder listing"
            : coordinatorHostOwnsRecruitment
                ? "DAD still owns the remote Slot1 Party Finder listing"
                : "remote Slot1 Party Finder ownership clearance remains unproven";
        var summary =
            $"PARTIAL: the fixed Alliance PF cleanup deadline elapsed and {ownershipSummary}; operator cleanup is required.";
        lock (statusGate)
        {
            status.OwnsRecruitment = coordinatorHostTarget == null
                ? status.OwnsRecruitment
                : coordinatorHostOwnsRecruitment;
            status.State = current.State == DadAllianceRecruitmentState.Stopped
                ? DadAllianceRecruitmentState.Stopped
                : DadAllianceRecruitmentState.Blocked;
            status.CreateLastError = summary;
            status.Summary = summary;
            status.Results = BuildCoordinatorResultList();
            status.UpdatedAtUtc = DateTime.UtcNow;
        }
        Audit(
            "cleanup-deadline-partial",
            coordinatorHostResult,
            (int)DadAllianceRemoteHostRules.CleanupDeadline.TotalMilliseconds,
            summary,
            summary);
    }

    private void UpdateCoordinatorTerminalPartial(DateTime nowUtc)
    {
        if (nowUtc < cleanupTerminalNextAuditUtc)
            return;

        if (coordinatorHostTarget == null)
        {
            var activeRecruitment = nativeGateway.ObserveActiveRecruitment();
            cleanupTerminalAuditAttempts++;
            cleanupTerminalNextAuditUtc = nowUtc +
                                          DadAllianceRemoteHostRules.GetAuditBackoff(
                                              cleanupTerminalAuditAttempts);
            if (DadAllianceRemoteHostRules.CanClearTerminalPartial(
                    cleanupTerminalPartial,
                    activeRecruitment))
            {
                CompleteCoordinatorTerminalPartial(
                    "Observed that operator cleanup removed the local Party Finder listing after the automatic cleanup deadline.");
            }
            return;
        }

        if (coordinatorHostTerminalAuditTask is { IsCompleted: false })
            return;

        var participant = ResolveParticipant(coordinatorHostTarget.WorkerSessionId);
        var instruction = coordinatorHostInstruction?.Clone();
        var current = GetStatus();
        cleanupTerminalAuditAttempts++;
        cleanupTerminalNextAuditUtc = nowUtc +
                                      DadAllianceRemoteHostRules.GetAuditBackoff(
                                          cleanupTerminalAuditAttempts);
        if (participant == null || instruction == null)
            return;

        coordinatorHostTerminalAuditTask = DispatchHostTerminalAuditAsync(
            participant,
            instruction,
            current.StopGeneration,
            coordinatorOperationGeneration,
            operationCancellation.Token);
    }

    private async Task DispatchHostTerminalAuditAsync(
        DadParticipantSnapshot participant,
        DadAllianceRecruitmentInstructionDto instruction,
        long expectedStopGeneration,
        long dispatchedGeneration,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await transportService.RequestAllianceUiSnapshotAsync(
                    participant,
                    cancellationToken)
                .ConfigureAwait(false);
            frameworkCompletions.Enqueue(() =>
            {
                var current = GetStatus();
                if (!cleanupTerminalPartial ||
                    current.StopGeneration != expectedStopGeneration ||
                    !IsCurrentHostInstruction(dispatchedGeneration, instruction))
                {
                    AuditLateCompletion(
                        dispatchedGeneration,
                        instruction,
                        "Remote host terminal cleanup audit arrived after its operation changed.");
                    return;
                }
                if (!DadAllianceRemoteHostRules.TryValidateTerminalCleanupSnapshot(
                        instruction,
                        expectedStopGeneration,
                        snapshot,
                        out var blocker))
                {
                    Audit(
                        "remote-host-terminal-cleanup-audit-rejected",
                        coordinatorHostResult,
                        0,
                        blocker,
                        blocker);
                    return;
                }

                coordinatorHostOwnsRecruitment = false;
                coordinatorHostResult = new DadAllianceRecruitmentResultDto
                {
                    RecruitmentId = snapshot!.RecruitmentId,
                    WorkerSessionId = snapshot.WorkerSessionId,
                    TargetCharacterKey = snapshot.TargetCharacterKey,
                    TargetCharacterName = instruction.TargetCharacterName,
                    TargetCharacterWorld = instruction.TargetCharacterWorld,
                    TargetContentId = instruction.TargetContentId,
                    ExpectedAlliance = snapshot.AssignedAlliance,
                    ObservedAlliance = snapshot.ObservedAlliance,
                    Attempt = snapshot.Attempt,
                    State = DadAllianceRecruitmentState.Stopped,
                    ResultKind = DadAllianceRecruitmentResultKind.Stopped,
                    Retryable = false,
                    StopGeneration = snapshot.StopGeneration,
                    ObservedAtUtc = snapshot.UpdatedAtUtc,
                    Summary =
                        "Observed exact remote Slot1 operator cleanup after the automatic Party Finder cleanup deadline.",
                };
                CompleteCoordinatorTerminalPartial(
                    "Observed exact remote Slot1 operator cleanup after the automatic Party Finder cleanup deadline.");
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            frameworkCompletions.Enqueue(() =>
            {
                if (!cleanupTerminalPartial ||
                    !IsCurrentHostInstruction(dispatchedGeneration, instruction))
                {
                    return;
                }
                Audit(
                    "remote-host-terminal-cleanup-audit-failed",
                    coordinatorHostResult,
                    0,
                    exception.Message,
                    "The read-only remote Slot1 terminal cleanup audit failed and will retry with bounded backoff.");
            });
        }
    }

    private void CompleteCoordinatorTerminalPartial(string summary)
    {
        cleanupTerminalPartial = false;
        cleanupDeadlineUtc = null;
        cleanupTerminalAuditAttempts = 0;
        cleanupTerminalNextAuditUtc = DateTime.MinValue;
        coordinatorHostTerminalAuditTask = null;
        coordinatorHostOwnsRecruitment = false;
        lock (statusGate)
        {
            var finalState = status.State == DadAllianceRecruitmentState.Stopped
                ? DadAllianceRecruitmentState.Stopped
                : DadAllianceRecruitmentState.Complete;
            status.OwnsRecruitment = false;
            status.ListingId = 0;
            status.CreateActiveRecruitment = false;
            status.CreateLastError = string.Empty;
            status.State = finalState;
            status.Summary = summary;
            status.Results = BuildCoordinatorResultList();
            status.UpdatedAtUtc = DateTime.UtcNow;
        }
        QueueCentralCleanup();
        Audit(
            "operator-cleanup-observed",
            coordinatorHostResult,
            0,
            string.Empty,
            summary);
    }

    private bool IsCurrentCoordinatorInstruction(
        long dispatchedGeneration,
        DadAllianceRecruitmentInstructionDto instruction)
        => dispatchedGeneration > 0 &&
           dispatchedGeneration == coordinatorOperationGeneration &&
           !stopApplied &&
           coordinatorTargets.ContainsKey(instruction.TargetCharacterKey.Value) &&
           coordinatorInstructions.TryGetValue(instruction.TargetCharacterKey.Value, out var current) &&
           IsSameInstruction(current, instruction);

    private bool IsCurrentHostInstruction(
        long dispatchedGeneration,
        DadAllianceRecruitmentInstructionDto instruction)
        => dispatchedGeneration > 0 &&
           dispatchedGeneration == coordinatorOperationGeneration &&
           coordinatorHostTarget != null &&
           coordinatorHostInstruction != null &&
           IsSameInstruction(coordinatorHostInstruction, instruction);

    private static bool IsSameInstruction(
        DadAllianceRecruitmentInstructionDto current,
        DadAllianceRecruitmentInstructionDto dispatched)
        => current.SchemaVersion == dispatched.SchemaVersion &&
           Same(current.RecruitmentId, dispatched.RecruitmentId) &&
           Same(current.CoordinatorWorkerSessionId.Value, dispatched.CoordinatorWorkerSessionId.Value) &&
           string.Equals(current.CoordinatorIdentity, dispatched.CoordinatorIdentity, StringComparison.Ordinal) &&
           string.Equals(current.LeaderName, dispatched.LeaderName, StringComparison.Ordinal) &&
           string.Equals(current.LeaderWorld, dispatched.LeaderWorld, StringComparison.Ordinal) &&
           Same(current.TargetWorkerSessionId.Value, dispatched.TargetWorkerSessionId.Value) &&
           string.Equals(current.TargetIslandId, dispatched.TargetIslandId, StringComparison.Ordinal) &&
           string.Equals(current.TargetOwnerId, dispatched.TargetOwnerId, StringComparison.Ordinal) &&
           string.Equals(current.TargetOpaqueCharacterId, dispatched.TargetOpaqueCharacterId, StringComparison.Ordinal) &&
           Same(current.TargetCharacterKey.Value, dispatched.TargetCharacterKey.Value) &&
           string.Equals(current.TargetCharacterName, dispatched.TargetCharacterName, StringComparison.Ordinal) &&
           string.Equals(current.TargetCharacterWorld, dispatched.TargetCharacterWorld, StringComparison.Ordinal) &&
           current.TargetContentId == dispatched.TargetContentId &&
           current.AssignedAlliance == dispatched.AssignedAlliance &&
           current.CreateListingAsHost == dispatched.CreateListingAsHost &&
           current.Passcode == dispatched.Passcode &&
           current.Attempt == dispatched.Attempt &&
           current.StopGeneration == dispatched.StopGeneration &&
           current.IssuedAtUtc == dispatched.IssuedAtUtc;

    private void AuditLateCompletion(
        long dispatchedGeneration,
        DadAllianceRecruitmentInstructionDto instruction,
        string blocker)
    {
        var current = GetStatus();
        audit.TryWrite(new DadAlliancePfAuditRecord
        {
            TimestampUtc = DateTime.UtcNow,
            Event = "late-completion-dropped",
            RecruitmentId = instruction.RecruitmentId,
            PfOwnerHandle = current.ListingId,
            SessionId = presenceService.WorkerSessionId.Value,
            HostName = instruction.LeaderName,
            HostWorld = instruction.LeaderWorld,
            TargetName = instruction.TargetCharacterName,
            TargetWorld = instruction.TargetCharacterWorld,
            TargetCharacterKey = instruction.TargetCharacterKey.Value,
            TargetContentId = instruction.TargetContentId,
            ExpectedAlliance = instruction.AssignedAlliance,
            Passcode = instruction.Passcode,
            Attempt = instruction.Attempt,
            StopGeneration = instruction.StopGeneration,
            State = current.State.ToString(),
            Error = blocker,
            Summary =
                $"Dropped stale Alliance PF completion generation {dispatchedGeneration}; current generation is {coordinatorOperationGeneration}.",
        });
    }

    private static bool Same(string? left, string? right)
        => string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

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
            Transport = eventName.Contains("central", StringComparison.OrdinalIgnoreCase)
                ? "central"
                : eventName.Contains("transport", StringComparison.OrdinalIgnoreCase)
                    ? "hub+central"
                    : string.Empty,
            State = (result?.State ?? current.State).ToString(),
            Error = error,
            Summary = summary,
        });
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(disposed, this);
}
