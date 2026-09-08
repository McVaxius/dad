using System.Collections.Immutable;
using AutoParty.Contracts;
using Dalamud.Plugin.Services;
using dad.Models;

namespace dad.Services;

// Inbound lifecycle decisions shared by the plugin and headless runtime.
internal sealed class DadAutoPartyInboundRuntime(
    Configuration Configuration,
    DadAutoPartyRelayPump autoPartyRelayPump,
    DadAutoPartyInboundAdmissionService autoPartyInboundAdmissionService,
    DadPresenceService PresenceService,
    DadTransportService TransportService,
    DadWorkerExecutionService WorkerExecutionService,
    InfoProxyPartyInviteGateway PartyInviteGateway,
    DadFrenRiderProfileTransferService FrenRiderProfileTransferService,
    DadCombatRotationService CombatRotationService,
    IPluginLog Log)
{
    private readonly HashSet<(Guid ProposalId, string CharacterId)> completedInboundAutoPartyForms = [];
    private readonly Dictionary<DadFrenRiderProfileOwnership, DadFrenRiderProfileApplicationResult>
        inboundFrenRiderProfileOutcomes = [];
    private readonly object inboundFrenRiderProfileGate = new();
    private readonly Dictionary<(Guid ProposalId, string CharacterId, int ModuleIndex),
        InboundWorkerRun> inboundWorkerCommands = [];
    private readonly Dictionary<(Guid ProposalId, string CharacterId), Guid> inboundFormOperations = [];
    private sealed record InboundWorkerRun(Guid QueueOperationId, DadWorkerExecutionCommand Command,
        DadParticipantSnapshot Participant, bool Settled = false);

    private void ReleaseInboundWorkerCommands(Guid proposalId, string characterId)
    {
        inboundFormOperations.Remove((proposalId, characterId));
        foreach (var key in inboundWorkerCommands.Keys.Where(key => key.ProposalId == proposalId &&
                     string.Equals(key.CharacterId, characterId, StringComparison.Ordinal)).ToArray())
            inboundWorkerCommands.Remove(key);
    }


    public ValueTask<DadAutoPartyExecutionResult> ExecuteInboundAutoPartyOperation(
        ExecutionOperation operation,
        IntegrationProfile? profile,
        DadAutoPartyObservedPartyReceipt? observedParty,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DadAutoPartyExecutionResult Result(
            ExecutionOutcome outcome,
            DadRunPhase phase,
            string safeCode,
            bool profileRestored = false)
            => new(
                operation.OperationId,
                operation.ProposalId,
                operation.Kind,
                outcome,
                phase,
                safeCode,
                operation.ExpectedStateGeneration,
                ProfileRestored: profileRestored);
        DadAutoPartyExecutionResult Denied(string safeCode)
            => Result(ExecutionOutcome.Denied, DadRunPhase.Idle, safeCode);
        DadAutoPartyExecutionResult Accepted(DadRunPhase phase, string safeCode)
            => Result(ExecutionOutcome.Accepted, phase, safeCode);
        DadAutoPartyExecutionResult Completed(DadRunPhase phase, string safeCode, bool profileRestored = false)
            => Result(ExecutionOutcome.Completed, phase, safeCode, profileRestored);

        if (!autoPartyRelayPump.TryGetInboundExecutionContext(
                operation.ProposalId,
                operation.CharacterId,
                out var context,
                out var routeSafeCode) ||
            !string.Equals(context.SenderIslandId, operation.Header.SenderIslandId.Value, StringComparison.Ordinal) ||
            !string.Equals(context.OwnerId, operation.OwnerId.Value, StringComparison.Ordinal))
            return ValueTask.FromResult(Denied(routeSafeCode));

        if (operation.Kind == ExecutionOperationKind.Restore)
        {
            completedInboundAutoPartyForms.Remove((operation.ProposalId, operation.CharacterId.Value));
            WorkerExecutionService.Cancel(new DadWorkerExecutionCancel
            {
                RunId = context.ExecutionPlan.RunId,
                Reason = "Authenticated AutoParty restoration.",
            });
            if (context.FrozenInviter != null || context.PartyInviteTargets is { Count: > 0 })
            {
                if (!TryBuildInboundAutoPartyTeardownInstruction(context, out var instruction, out var blocker))
                    return ValueTask.FromResult(Denied(blocker));
                var teardown = PresenceService.HandleAssemblyInstruction(instruction);
                if (!teardown.Success && !teardown.Deferred)
                    return ValueTask.FromResult(Denied("dad-inbound-restore-teardown-failed"));
                if (teardown.Deferred)
                    return ValueTask.FromResult(Accepted(DadRunPhase.TearingDownParty, "dad-inbound-restore-pending"));
            }

            if (!autoPartyInboundAdmissionService.RestoreProposal(
                    operation.ProposalId,
                    "Authenticated AutoParty restoration."))
            {
                return ValueTask.FromResult(Accepted(
                    DadRunPhase.Finalizing,
                    "dad-inbound-takeover-restoration-pending"));
            }

            _ = PresenceService.HandleCancelRun(new DadCancelCommandDto
            {
                RunId = context.ExecutionPlan.RunId,
                AuthorityWorkerSessionId = PresenceService.WorkerSessionId,
                CancellationState = DadRunCancellationState.Finalized,
                Reason = "Authenticated AutoParty restoration complete.",
            });
            if (!ReleaseInboundFrenRiderProfile(operation, context, out var releaseSafeCode))
                return ValueTask.FromResult(Denied(releaseSafeCode));
            ReleaseInboundWorkerCommands(operation.ProposalId, operation.CharacterId.Value);
            autoPartyRelayPump.RemoveInboundExecutionContext(operation.ProposalId, operation.CharacterId);
            return ValueTask.FromResult(Completed(
                DadRunPhase.Finalizing,
                "dad-inbound-restore-complete",
                profileRestored: false));
        }

        if (operation.Kind == ExecutionOperationKind.Cancel)
        {
            completedInboundAutoPartyForms.Remove((operation.ProposalId, operation.CharacterId.Value));
            var ack = WorkerExecutionService.Cancel(new DadWorkerExecutionCancel
            {
                RunId = context.ExecutionPlan.RunId,
                Reason = "Authenticated AutoParty cancellation.",
            });
            return ValueTask.FromResult(ack.Accepted
                ? Completed(DadRunPhase.Finalizing, "dad-inbound-cancel-complete")
                : Denied("dad-inbound-cancel-rejected"));
        }

        var local = PresenceService.BuildLiveSafetySnapshot();
        if (!IsInboundRuntimeTargetReady(local, context))
            return ValueTask.FromResult(Accepted(
                DadRunPhase.WaitingForReadiness,
                "dad-inbound-worker-readiness-pending"));

        if (operation.Kind == ExecutionOperationKind.Prepare)
        {
            if (!TryValidateInboundFrenRiderProfile(operation, context, profile, out var profileSafeCode))
                return ValueTask.FromResult(Denied(profileSafeCode));
            return ValueTask.FromResult(Completed(DadRunPhase.Planning, "dad-inbound-prepare-authorized"));
        }
        if (operation.Kind == ExecutionOperationKind.Reserve)
            return ValueTask.FromResult(Completed(DadRunPhase.ClaimingSlots, "dad-inbound-reserve-authorized"));

        if (operation.Kind is ExecutionOperationKind.Queue or ExecutionOperationKind.Settle)
        {
            if (operation.Kind == ExecutionOperationKind.Queue &&
                !completedInboundAutoPartyForms.Contains((operation.ProposalId, operation.CharacterId.Value)))
            {
                return ValueTask.FromResult(Accepted(
                    DadRunPhase.AssemblingParty,
                    "dad-inbound-form-execution-pending"));
            }
            if (operation.Kind == ExecutionOperationKind.Queue &&
                !IsInboundFrenRiderQueueAllowed(operation, context, out var profileBlocker))
                return ValueTask.FromResult(Denied(profileBlocker));
            var reference = operation.ModuleReference;
            if (reference == null || reference.ModuleIndex < 0 || reference.ModuleIndex >= context.ExecutionPlan.Modules.Length ||
                !string.Equals(context.ExecutionPlan.Modules[reference.ModuleIndex].ModuleId, reference.ModuleId, StringComparison.Ordinal))
                return ValueTask.FromResult(Denied("dad-inbound-queue-module-reference-invalid"));
            var key = (operation.ProposalId, operation.CharacterId.Value, reference.ModuleIndex);
            if (!inboundWorkerCommands.TryGetValue(key, out var execution))
            {
                if (operation.Kind != ExecutionOperationKind.Queue)
                    return ValueTask.FromResult(Denied("dad-inbound-worker-command-missing"));
                if (!DadAutoPartyInboundExecutionRules.TryBuildWorkerCommand(
                        operation, context, local, out var newCommand, out var newParticipant, out var blocker))
                    return ValueTask.FromResult(Denied(blocker));
                var ack = WorkerExecutionService.Accept(newCommand);
                if (!ack.Accepted || !DadWorkerStatusPollingRules.MatchesExactAcknowledgement(newParticipant, newCommand, ack))
                    return ValueTask.FromResult(Denied("dad-inbound-queue-acknowledgement-invalid"));
                execution = new(operation.OperationId, newCommand, newParticipant);
                inboundWorkerCommands.Add(key, execution);
            }
            if (operation.Kind == ExecutionOperationKind.Queue && execution.QueueOperationId != operation.OperationId)
                return ValueTask.FromResult(Denied("dad-inbound-queue-operation-mismatch"));
            var command = execution.Command;
            var commandParticipant = execution.Participant;

            var workerStatus = WorkerExecutionService.GetStatus();
            if (!DadDroppedPeerContinuationRules.MatchesExactCommand(commandParticipant, command, workerStatus))
                return ValueTask.FromResult(Denied("dad-inbound-worker-status-mismatch"));
            if (workerStatus.IsTerminal && !workerStatus.Success)
                return ValueTask.FromResult(Denied("dad-inbound-worker-terminal-failure"));
            if (operation.Kind == ExecutionOperationKind.Queue)
            {
                return ValueTask.FromResult(workerStatus.State is
                        DadWorkerExecutionState.WaitingForQueue or DadWorkerExecutionState.Running ||
                    workerStatus is { IsTerminal: true, Success: true }
                        ? Completed(DadRunPhase.QueueStarting, "dad-inbound-queue-ready")
                        : Accepted(DadRunPhase.QueuePreparing, "dad-inbound-queue-preparing"));
            }
            if (workerStatus is { IsTerminal: true, Success: true })
                inboundWorkerCommands[key] = execution with { Settled = true };
            return ValueTask.FromResult(workerStatus is { IsTerminal: true, Success: true }
                ? Completed(DadRunPhase.Finalizing, "dad-inbound-worker-settled")
                : Accepted(workerStatus.State == DadWorkerExecutionState.Running
                    ? DadRunPhase.InDutyOrTask
                    : DadRunPhase.WaitingForQueuePop, "dad-inbound-worker-settlement-pending"));
        }

        return ValueTask.FromResult(Denied("dad-inbound-operation-unsupported"));
    }

    private static bool TryBuildInboundAutoPartyTeardownInstruction(
        DadAutoPartyInboundExecutionContext context,
        out DadAssemblyInstructionDto instruction,
        out string blocker)
    {
        instruction = new DadAssemblyInstructionDto();
        blocker = "dad-inbound-restore-locator-invalid";
        var inviter = context.FrozenInviter;
        var targets = context.PartyInviteTargets?.Select(static target => target.Clone()).ToList() ?? [];
        var inviterRows = context.ExecutionPlan.Participants.Where(static participant => participant.IsInviter).ToList();
        var localRows = context.ExecutionPlan.Participants.Where(participant =>
            string.Equals(participant.SlotId, context.Target.SlotId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (inviter == null || inviterRows.Count != 1 || localRows.Count != 1 ||
            targets.Count != context.ExecutionPlan.Participants.Length - 1 ||
            targets.Count is < 1 or > 7 ||
            !string.Equals(inviter.RunId, context.ExecutionPlan.RunId, StringComparison.Ordinal) ||
            targets.Any(target => !string.Equals(target.RunId, context.ExecutionPlan.RunId, StringComparison.Ordinal)) ||
            targets.Select(static target => target.SlotId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Count)
            return false;

        var localIsInviter = localRows[0].IsInviter;
        if (localIsInviter
                ? !MatchesInboundRuntimeTarget(inviter, context.Target)
                : targets.Count(target => MatchesInboundRuntimeTarget(target, context.Target)) != 1)
        {
            blocker = "dad-inbound-restore-worker-route-mismatch";
            return false;
        }
        var expectedFollowerSlots = context.ExecutionPlan.Participants
            .Where(static participant => !participant.IsInviter)
            .Select(static participant => participant.SlotId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!expectedFollowerSlots.SetEquals(targets.Select(static target => target.SlotId)))
            return false;

        instruction = new DadAssemblyInstructionDto
        {
            RunId = context.ExecutionPlan.RunId,
            AuthorityWorkerSessionId = context.Target.WorkerSessionId,
            ModuleId = context.Target.ModuleId,
            SlotId = context.Target.SlotId,
            RequiredCharacterKey = context.Target.CharacterKey,
            InstructionKind = localIsInviter
                ? DadAssemblyInstructionKind.DisbandParty
                : DadAssemblyInstructionKind.LeaveParty,
            FrozenInviter = inviter.Clone(),
            InviteTargets = targets,
            Summary = localIsInviter
                ? "Authenticated AutoParty Slot1 is performing guarded teardown."
                : "Authenticated AutoParty follower is performing guarded teardown.",
        };
        blocker = string.Empty;
        return true;
    }

    public ValueTask<DadAutoPartyExecutionResult> ExecuteInboundAutoPartyForm(
        DadAutoPartyFormExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operation = context.Operation;
        DadAutoPartyExecutionResult Denied(string safeCode) => new(
            operation.OperationId,
            operation.ProposalId,
            operation.Kind,
            ExecutionOutcome.Denied,
            DadRunPhase.Idle,
            safeCode,
            operation.ExpectedStateGeneration);
        DadAutoPartyExecutionResult Accepted(string safeCode) => new(
            operation.OperationId,
            operation.ProposalId,
            operation.Kind,
            ExecutionOutcome.Accepted,
            DadRunPhase.WaitingForReadiness,
            safeCode,
            operation.ExpectedStateGeneration);
        if (!autoPartyRelayPump.TryGetInboundExecutionContext(
                operation.ProposalId,
                operation.CharacterId,
                out var runtimeContext,
                out var routeSafeCode) ||
            !string.Equals(runtimeContext.SenderIslandId, operation.Header.SenderIslandId.Value, StringComparison.Ordinal) ||
            !string.Equals(runtimeContext.OwnerId, operation.OwnerId.Value, StringComparison.Ordinal))
            return ValueTask.FromResult(Denied(routeSafeCode));
        var localTarget = runtimeContext.Target;
        if (
            !string.Equals(localTarget.RunId, context.ExpectedInviter?.RunId ??
                context.PartyInviteTargets.FirstOrDefault()?.RunId ?? localTarget.RunId, StringComparison.Ordinal))
            return ValueTask.FromResult(Denied(routeSafeCode));

        var formKey = (operation.ProposalId, operation.CharacterId.Value);
        if (!inboundFormOperations.TryGetValue(formKey, out var currentForm) || currentForm != operation.OperationId)
        {
            if (inboundWorkerCommands.Any(entry => entry.Key.ProposalId == operation.ProposalId &&
                    entry.Key.CharacterId == operation.CharacterId.Value && !entry.Value.Settled))
                return ValueTask.FromResult(Denied("dad-inbound-reform-before-settlement"));
            // Only a new authenticated Form after settlement opens another attempt.
            // Polls of that Form and Queue retain the exact admitted command.
            ReleaseInboundWorkerCommands(operation.ProposalId, operation.CharacterId.Value);
            completedInboundAutoPartyForms.Remove(formKey);
            inboundFormOperations[formKey] = operation.OperationId;
        }

        var candidates = new List<DadParticipantSnapshot>();
        var local = PresenceService.BuildLiveSafetySnapshot();
        if (IsInboundRuntimeTargetReady(local, runtimeContext))
            candidates.Add(local);
        candidates.AddRange(TransportService.CurrentTransport.KnownParticipants.Where(participant =>
            IsInboundRuntimeTargetReady(participant, runtimeContext)));
        var participant = candidates
            .DistinctBy(static candidate => candidate.WorkerSessionId.Value, StringComparer.OrdinalIgnoreCase)
            .SingleOrDefault();
        if (participant == null)
            return ValueTask.FromResult(Accepted("dad-inbound-form-worker-readiness-pending"));

        var slotOne = context.ExpectedInviter == null;
        if (!slotOne && context.PartyInviteTargets.Count != 0)
            return ValueTask.FromResult(Denied("dad-inbound-form-locator-mode-invalid"));
        var inviter = context.ExpectedInviter?.Clone() ?? new DadExpectedPartyInviter
        {
            RunId = localTarget.RunId,
            WorkerSessionId = localTarget.WorkerSessionId,
            AccountKey = localTarget.AccountKey,
            CharacterKey = localTarget.CharacterKey,
            ContentId = localTarget.ContentId,
            CharacterName = localTarget.CharacterName,
            WorldId = localTarget.WorldId,
        };
        var inviteTargets = slotOne
            ? context.PartyInviteTargets.Select(static target => target.Clone()).ToList()
            : new List<DadNativePartyInviteTarget> { localTarget.Clone() };
        var instruction = new DadAssemblyInstructionDto
        {
            RunId = localTarget.RunId,
            AuthorityWorkerSessionId = PresenceService.WorkerSessionId,
            ModuleId = localTarget.ModuleId,
            SlotId = localTarget.SlotId,
            RequiredCharacterKey = localTarget.CharacterKey,
            InstructionKind = slotOne
                ? DadAssemblyInstructionKind.FormParty
                : DadAssemblyInstructionKind.JoinParty,
            FrozenInviter = inviter,
            InviteTargets = inviteTargets,
            Summary = slotOne
                ? "Authenticated AutoParty Slot1 is forming the frozen party."
                : "Authenticated AutoParty follower is joining frozen Slot1.",
        };
        var result = string.Equals(
                participant.WorkerSessionId.Value,
                PresenceService.WorkerSessionId.Value,
                StringComparison.OrdinalIgnoreCase)
            ? PresenceService.HandleAssemblyInstruction(instruction)
            : TransportService.SendAssemblyInstruction(participant, instruction);
        if (result == null || !result.Success && !result.Deferred)
            return ValueTask.FromResult(Denied(result?.FailureReason ?? "dad-inbound-form-acknowledgement-missing"));
        if (!slotOne)
        {
            var followerObservedContentIds = PartyInviteGateway.ReadAuthoritativePartyMembers()
                .Select(static member => member.ContentId)
                .Where(static contentId => contentId != 0)
                .Distinct()
                .ToImmutableArray();
            if (followerObservedContentIds.Length == runtimeContext.ExecutionPlan.Participants.Length &&
                followerObservedContentIds.Contains(inviter.ContentId) &&
                followerObservedContentIds.Contains(localTarget.ContentId))
            {
                if (!TryApplyInboundFrenRiderProfile(operation, runtimeContext, out var followerProfileSafeCode))
                    return ValueTask.FromResult(Denied(followerProfileSafeCode));
                if (!TryActivateInboundFrenRiderAfterGroupReady(
                        runtimeContext,
                        participant,
                        instruction,
                        out var followerActivationPending,
                        out var followerActivationSafeCode))
                {
                    return ValueTask.FromResult(Denied(followerActivationSafeCode));
                }
                if (followerActivationPending)
                {
                    return ValueTask.FromResult(new DadAutoPartyExecutionResult(
                        operation.OperationId,
                        operation.ProposalId,
                        operation.Kind,
                        ExecutionOutcome.Accepted,
                        DadRunPhase.AssemblingParty,
                        followerActivationSafeCode,
                        operation.ExpectedStateGeneration));
                }
                completedInboundAutoPartyForms.Add((operation.ProposalId, operation.CharacterId.Value));
                return ValueTask.FromResult(new DadAutoPartyExecutionResult(
                    operation.OperationId,
                    operation.ProposalId,
                    operation.Kind,
                    ExecutionOutcome.Completed,
                    operation.FormationOnly ? DadRunPhase.GroupReady : DadRunPhase.AssemblingParty,
                    runtimeContext.ExecutionPlan.UseFrenRider
                        ? followerProfileSafeCode
                        : "dad-inbound-follower-party-proof-complete",
                    operation.ExpectedStateGeneration,
                    new DadAutoPartyObservedPartyReceipt(
                        followerObservedContentIds.Length,
                        followerObservedContentIds,
                        "partylist-authoritative",
                        DadClock.UtcNow)));
            }
            return ValueTask.FromResult(new DadAutoPartyExecutionResult(
                operation.OperationId,
                operation.ProposalId,
                operation.Kind,
                ExecutionOutcome.Accepted,
                DadRunPhase.AssemblingParty,
                "dad-inbound-follower-form-accepted",
                operation.ExpectedStateGeneration));
        }

        var expectedContentIds = inviteTargets.Select(static target => target.ContentId)
            .Append(localTarget.ContentId)
            .ToHashSet();
        var observedContentIds = result.AuthoritativePartyMembers
            .Select(static member => member.ContentId)
            .Where(static contentId => contentId != 0)
            .ToImmutableArray();
        if (observedContentIds.Length != expectedContentIds.Count ||
            observedContentIds.Distinct().Count() != observedContentIds.Length ||
            !expectedContentIds.SetEquals(observedContentIds))
        {
            return ValueTask.FromResult(new DadAutoPartyExecutionResult(
                operation.OperationId,
                operation.ProposalId,
                operation.Kind,
                ExecutionOutcome.Accepted,
                DadRunPhase.AssemblingParty,
                "dad-inbound-slot1-party-proof-pending",
                operation.ExpectedStateGeneration));
        }

        if (!TryApplyInboundFrenRiderProfile(operation, runtimeContext, out var profileSafeCode))
            return ValueTask.FromResult(Denied(profileSafeCode));
        if (!TryActivateInboundFrenRiderAfterGroupReady(
                runtimeContext,
                participant,
                instruction,
                out var activationPending,
                out var activationSafeCode))
        {
            return ValueTask.FromResult(Denied(activationSafeCode));
        }
        if (activationPending)
        {
            return ValueTask.FromResult(new DadAutoPartyExecutionResult(
                operation.OperationId,
                operation.ProposalId,
                operation.Kind,
                ExecutionOutcome.Accepted,
                DadRunPhase.AssemblingParty,
                activationSafeCode,
                operation.ExpectedStateGeneration));
        }
        completedInboundAutoPartyForms.Add((operation.ProposalId, operation.CharacterId.Value));
        return ValueTask.FromResult(new DadAutoPartyExecutionResult(
            operation.OperationId,
            operation.ProposalId,
            operation.Kind,
            ExecutionOutcome.Completed,
            operation.FormationOnly ? DadRunPhase.GroupReady : DadRunPhase.AssemblingParty,
            runtimeContext.ExecutionPlan.UseFrenRider
                ? profileSafeCode
                : operation.FormationOnly ? "dad-inbound-group-ready" : "dad-inbound-form-complete",
            operation.ExpectedStateGeneration,
            new DadAutoPartyObservedPartyReceipt(
                observedContentIds.Length,
                observedContentIds,
                "partylist-authoritative",
                DadClock.UtcNow)));
    }

    private bool TryActivateInboundFrenRiderAfterGroupReady(
        DadAutoPartyInboundExecutionContext context,
        DadParticipantSnapshot participant,
        DadAssemblyInstructionDto assemblyInstruction,
        out bool pending,
        out string safeCode)
    {
        pending = false;
        safeCode = "dad-inbound-frenrider-group-ready-not-required";
        if (!context.ExecutionPlan.UseFrenRider)
            return true;

        var activationInstruction = assemblyInstruction.Clone();
        activationInstruction.InstructionKind = DadAssemblyInstructionKind.ActivateFrenRider;
        activationInstruction.ExpectedPartySize = context.ExecutionPlan.Participants.Length;
        activationInstruction.Summary =
            "Exact DAD group formation is complete; apply the selected FrenRider group-ready mode.";
        var result = string.Equals(
                participant.WorkerSessionId.Value,
                PresenceService.WorkerSessionId.Value,
                StringComparison.OrdinalIgnoreCase)
            ? PresenceService.HandleAssemblyInstruction(activationInstruction)
            : TransportService.SendAssemblyInstruction(participant, activationInstruction);
        if (result == null || result.Deferred)
        {
            pending = true;
            safeCode = "dad-inbound-frenrider-group-ready-pending";
            return true;
        }

        if (result.StepName is not "GroupReadyFrenRider" and not "GroupReadyFrenRiderNotRequired")
        {
            safeCode = "dad-inbound-frenrider-group-ready-unsupported";
            return false;
        }

        safeCode = !result.Success
            ? "dad-inbound-frenrider-group-ready-failed"
            : result.StepName == "GroupReadyFrenRiderNotRequired"
                ? "dad-inbound-frenrider-group-ready-not-required"
                : "dad-inbound-frenrider-group-ready-complete";
        return result.Success;
    }

    private bool TryValidateInboundFrenRiderProfile(
        ExecutionOperation operation,
        DadAutoPartyInboundExecutionContext context,
        IntegrationProfile? profile,
        out string safeCode)
    {
        safeCode = "dad-inbound-frenrider-profile-not-required";
        if (!context.ExecutionPlan.UseFrenRider)
            return true;
        if (profile == null ||
            profile.ProposalId != operation.ProposalId ||
            profile.OwnerId != operation.OwnerId ||
            profile.CharacterId != operation.CharacterId ||
            !string.Equals(profile.Header.SenderIslandId.Value, context.SenderIslandId, StringComparison.Ordinal) ||
            !string.Equals(profile.Header.RecipientIslandId.Value, Configuration.AutoParty.RegisteredIslandId,
                StringComparison.Ordinal) ||
            !string.Equals(context.OwnerId, operation.OwnerId.Value, StringComparison.Ordinal) ||
            profile.EnabledIntegrationIds.Length != 1 ||
            !string.Equals(profile.EnabledIntegrationIds[0], "FrenRider", StringComparison.Ordinal) ||
            profile.EnableLevelSync || profile.EnableUnrestrictedParty || profile.EnableMinimumItemLevel ||
            profile.EnableSilenceEcho ||
            context.ExecutionPlan.Participants.Count(participant =>
                string.Equals(participant.OwnerId.Value, operation.OwnerId.Value, StringComparison.Ordinal) &&
                string.Equals(participant.OwnerIslandId.Value, Configuration.AutoParty.RegisteredIslandId,
                    StringComparison.Ordinal) &&
                string.Equals(participant.CharacterId.Value, operation.CharacterId.Value, StringComparison.Ordinal) &&
                string.Equals(participant.SlotId, context.Target.SlotId, StringComparison.OrdinalIgnoreCase)) != 1)
        {
            safeCode = "dad-inbound-frenrider-profile-route-mismatch";
            return false;
        }

        try
        {
            _ = FrenRiderProfileCodec.Decode(profile.FrenRiderProfile);
            safeCode = "dad-inbound-frenrider-profile-ready";
            return true;
        }
        catch (ProtocolException)
        {
            safeCode = "dad-inbound-frenrider-profile-invalid";
            return false;
        }
    }

    private bool TryApplyInboundFrenRiderProfile(
        ExecutionOperation operation,
        DadAutoPartyInboundExecutionContext context,
        out string safeCode)
    {
        safeCode = "dad-inbound-frenrider-profile-not-required";
        if (!context.ExecutionPlan.UseFrenRider)
            return true;
        var ownership = BuildFrenRiderProfileOwnership(operation, context);
        lock (inboundFrenRiderProfileGate)
        {
            if (inboundFrenRiderProfileOutcomes.TryGetValue(ownership, out var existing))
            {
                safeCode = existing.SafeCode;
                return existing.Success;
            }
        }
        if (!autoPartyRelayPump.TryGetInboundIntegrationProfile(
                ownership.ProposalId,
                ownership.SenderIslandId,
                ownership.OwnerId,
                operation.CharacterId,
                out var profile) ||
            !TryValidateInboundFrenRiderProfile(operation, context, profile, out safeCode))
        {
            lock (inboundFrenRiderProfileGate)
                inboundFrenRiderProfileOutcomes[ownership] =
                    DadFrenRiderProfileApplicationResult.Failed(safeCode);
            return false;
        }

        string profileJson;
        try
        {
            profileJson = FrenRiderProfileCodec.Decode(profile.FrenRiderProfile);
        }
        catch (ProtocolException)
        {
            safeCode = "dad-inbound-frenrider-profile-invalid";
            lock (inboundFrenRiderProfileGate)
                inboundFrenRiderProfileOutcomes[ownership] =
                    DadFrenRiderProfileApplicationResult.Failed(safeCode);
            return false;
        }
        var applied = FrenRiderProfileTransferService.Apply(ownership, profileJson);
        lock (inboundFrenRiderProfileGate)
            inboundFrenRiderProfileOutcomes[ownership] = applied;
        safeCode = applied.SafeCode;
        return applied.Success;
    }

    private bool IsInboundFrenRiderQueueAllowed(
        ExecutionOperation operation,
        DadAutoPartyInboundExecutionContext context,
        out string blocker)
    {
        blocker = string.Empty;
        if (!context.ExecutionPlan.UseFrenRider)
            return true;
        var ownership = BuildFrenRiderProfileOwnership(operation, context);
        DadFrenRiderProfileApplicationResult? outcome;
        lock (inboundFrenRiderProfileGate)
            inboundFrenRiderProfileOutcomes.TryGetValue(ownership, out outcome);
        return DadFrenRiderInboundQueueRules.IsAllowed(
            useFrenRider: true,
            outcome: outcome,
            frenRiderLoaded: CombatRotationService.IsFrenRiderLoaded(),
            out blocker);
    }

    private bool ReleaseInboundFrenRiderProfile(
        ExecutionOperation operation,
        DadAutoPartyInboundExecutionContext context,
        out string safeCode)
    {
        var ownership = BuildFrenRiderProfileOwnership(operation, context);
        DadFrenRiderProfileApplicationResult? outcome;
        lock (inboundFrenRiderProfileGate)
            inboundFrenRiderProfileOutcomes.TryGetValue(ownership, out outcome);
        if (outcome?.Outcome != DadFrenRiderProfileApplicationOutcome.TemporaryApplied)
        {
            lock (inboundFrenRiderProfileGate)
                inboundFrenRiderProfileOutcomes.Remove(ownership);
            safeCode = "dad-frenrider-profile-release-not-required";
            return true;
        }
        if (!FrenRiderProfileTransferService.ReleaseTemporary(ownership, out safeCode))
            return false;
        lock (inboundFrenRiderProfileGate)
            inboundFrenRiderProfileOutcomes.Remove(ownership);
        return true;
    }

    public void ReleaseAllInboundFrenRiderProfiles()
    {
        inboundWorkerCommands.Clear();
        DadFrenRiderProfileOwnership[] temporary;
        lock (inboundFrenRiderProfileGate)
            temporary = inboundFrenRiderProfileOutcomes
                .Where(static pair => pair.Value.Outcome ==
                                      DadFrenRiderProfileApplicationOutcome.TemporaryApplied)
                .Select(static pair => pair.Key)
                .ToArray();
        foreach (var ownership in temporary)
            _ = FrenRiderProfileTransferService.ReleaseTemporary(ownership, out _);
        lock (inboundFrenRiderProfileGate)
            inboundFrenRiderProfileOutcomes.Clear();
    }

    public void ReleaseExpiredInboundFrenRiderProfile(DadAutoPartyExpiredRuntimeTarget target)
    {
        var local = PresenceService.BuildSnapshotCopy();
        if (target.Target is { } exact && MatchesInboundRuntimeTarget(local, exact) &&
            string.Equals(local.RunId, exact.RunId, StringComparison.Ordinal))
        {
            WorkerExecutionService.Cancel(new DadWorkerExecutionCancel
            {
                RunId = exact.RunId, Reason = "AutoParty authority expired or was revoked.",
            });
            _ = PresenceService.HandleCancelRun(new DadCancelCommandDto
            {
                RunId = exact.RunId, AuthorityWorkerSessionId = PresenceService.WorkerSessionId,
                CancellationState = DadRunCancellationState.Finalized,
                Reason = "AutoParty authority expired or was revoked.",
            });
        }
        ReleaseInboundWorkerCommands(target.ProposalId, target.OpaqueCharacterId);
        var ownership = new DadFrenRiderProfileOwnership(
            target.ProposalId,
            target.SenderIslandId,
            target.OwnerId,
            target.OpaqueCharacterId);
        completedInboundAutoPartyForms.Remove((target.ProposalId, target.OpaqueCharacterId));
        DadFrenRiderProfileApplicationResult? outcome;
        lock (inboundFrenRiderProfileGate)
            inboundFrenRiderProfileOutcomes.TryGetValue(ownership, out outcome);
        if (outcome?.Outcome == DadFrenRiderProfileApplicationOutcome.TemporaryApplied &&
            !FrenRiderProfileTransferService.ReleaseTemporary(ownership, out var safeCode))
        {
            Log.Warning(
                "[dad][FrenRiderProfile] Exact temporary profile cleanup failed after proposal expiry ({SafeCode}).",
                safeCode);
            return;
        }
        lock (inboundFrenRiderProfileGate)
            inboundFrenRiderProfileOutcomes.Remove(ownership);
    }

    private static DadFrenRiderProfileOwnership BuildFrenRiderProfileOwnership(
        ExecutionOperation operation,
        DadAutoPartyInboundExecutionContext context)
        => new(
            operation.ProposalId,
            context.SenderIslandId,
            operation.OwnerId.Value,
            operation.CharacterId.Value);

    private static bool MatchesInboundRuntimeTarget(
        DadParticipantSnapshot participant,
        DadNativePartyInviteTarget target)
        => string.Equals(
               participant.WorkerSessionId.Value,
               target.WorkerSessionId.Value,
               StringComparison.OrdinalIgnoreCase) &&
           DadRosterIdentity.SameAccount(participant.ManagedAccountKey, target.AccountKey) &&
           DadRosterIdentity.SameCharacter(
               participant.ActiveCharacterKey,
               participant.Character.ContentId,
               target.CharacterKey,
               target.ContentId) &&
           string.Equals(participant.AssignedSlotId, target.SlotId, StringComparison.OrdinalIgnoreCase);

    private static bool IsInboundRuntimeTargetReady(
        DadParticipantSnapshot participant,
        DadAutoPartyInboundExecutionContext context)
    {
        if (!MatchesInboundRuntimeTarget(participant, context.Target) || !participant.WorldReadyStable)
            return false;
        var matches = context.ExecutionPlan.Participants.Where(candidate =>
                string.Equals(candidate.SlotId, context.Target.SlotId, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        return matches.Length == 1 &&
               uint.TryParse(
                   matches[0].RequestedJob.Value,
                   System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out var requestedJobId) &&
               requestedJobId != 0 && participant.Character.CurrentJobId == requestedJobId;
    }

    private static bool MatchesInboundRuntimeTarget(
        DadExpectedPartyInviter inviter,
        DadNativePartyInviteTarget target)
        => string.Equals(inviter.RunId, target.RunId, StringComparison.Ordinal) &&
           string.Equals(inviter.WorkerSessionId.Value, target.WorkerSessionId.Value, StringComparison.OrdinalIgnoreCase) &&
           DadRosterIdentity.SameAccount(inviter.AccountKey, target.AccountKey) &&
           DadRosterIdentity.SameCharacter(inviter.CharacterKey, inviter.ContentId, target.CharacterKey, target.ContentId);

    private static bool MatchesInboundRuntimeTarget(
        DadNativePartyInviteTarget candidate,
        DadNativePartyInviteTarget target)
        => string.Equals(candidate.RunId, target.RunId, StringComparison.Ordinal) &&
           string.Equals(candidate.SlotId, target.SlotId, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(candidate.WorkerSessionId.Value, target.WorkerSessionId.Value, StringComparison.OrdinalIgnoreCase) &&
           DadRosterIdentity.SameAccount(candidate.AccountKey, target.AccountKey) &&
           DadRosterIdentity.SameCharacter(candidate.CharacterKey, candidate.ContentId, target.CharacterKey, target.ContentId);

}
