using dad.Models;
using Dalamud.Plugin.Services;

namespace dad.Services;

internal static partial class DadCompletionActionRunner
{
    private static Configuration? memberConfiguration;
    private static DadPresenceService? memberPresence;
    private static DadTransportService? memberTransport;
    private static DadWorkerExecutionService? memberWorker;
    private static readonly HashSet<string> memberOperationsInFlight = new(StringComparer.Ordinal);

    private static bool HasPendingMemberWork => memberConfiguration?.RoundCompletions.Any(round =>
        round.Ready && !round.Cancelled && (!round.GlobalActionsQueued || round.Members.Any(member =>
            member.State is DadMemberCompletionState.Pending or DadMemberCompletionState.Submitted))) == true;

    internal static void ConfigureMembers(Configuration configuration, DadPresenceService presence,
        DadTransportService transport, DadWorkerExecutionService worker)
    {
        memberConfiguration = configuration;
        memberPresence = presence;
        memberTransport = transport;
        memberWorker = worker;
        configuration.RoundCompletions ??= [];
        configuration.MemberCompletionPermissions ??= [];
        configuration.MemberCompletionReceipts ??= [];
        foreach (var round in configuration.RoundCompletions)
        {
            round.Members ??= []; round.GlobalActions ??= [];
            round.ShoppingCompletion ??= new(StringComparer.Ordinal);
        }
        memberOperationsInFlight.Clear();
        worker.RecordSuccessfulRoundAssignment = (command, status) => RememberMemberPermissions(configuration, presence, command, status);
        transport.ConfigureMemberCompletionHandler(command => ReceiveMemberCompletion(configuration, presence, worker, command));
    }

    private static bool RememberMemberPermissions(Configuration configuration, DadPresenceService presence,
        DadWorkerExecutionCommand command, DadWorkerExecutionStatus status)
    {
        if (!status.Success || !status.IsTerminal) return true;
        var local = presence.BuildLiveSafetySnapshot();
        var assigned = command.Participants.FirstOrDefault(participant => DadMemberCompletionRules.SameRecipient(participant, local));
        if (assigned == null) return true;
        var assignments = command.Plan.Request.ShoppingAssociations.Where(association =>
            !string.IsNullOrWhiteSpace(association.CustomCommand) && DadShoppingAssociationRules.MatchesLocalShopper(association,
                assigned)).ToList();
        if (assignments.Count == 0) return true;
        if (status.ShoppingResults.Any(result => !result.Succeeded || result.Disposition == "partial" ||
                result.CreditedQuantities != null && !result.NonRepeatableRowsFulfilled))
        {
            foreach (var permission in configuration.MemberCompletionPermissions.Where(value =>
                         value.RoundId == DadMemberCompletionRules.RoundId(command.Plan.Request) &&
                         DadMemberCompletionRules.SameRecipient(value.Recipient, local))) permission.State = DadMemberCompletionState.Cancelled;
            return configuration.TryPersistNow();
        }
        foreach (var assignment in assignments.TakeLast(1))
        {
            if (!DadCompletionCommandRules.TryNormalizeCustomCommand(assignment.CustomCommand, out var normalized, out _)) return false;
            var permission = new DadMemberCompletionCommand
            {
                RoundId = DadMemberCompletionRules.RoundId(command.Plan.Request), RunId = command.RunId,
                Recipient = DadMemberCompletionRecipient.From(assigned), Command = normalized,
            };
            configuration.MemberCompletionPermissions.RemoveAll(value => value.RoundId == permission.RoundId &&
                DadMemberCompletionRules.SameRecipient(value.Recipient, permission.Recipient) && value.State == DadMemberCompletionState.Pending);
            if (!configuration.MemberCompletionPermissions.Any(value => DadMemberCompletionRules.SameIntent(value, permission)))
                configuration.MemberCompletionPermissions.Add(permission);
        }
        TrimMemberHistory(configuration);
        return configuration.TryPersistNow();
    }

    internal static bool CaptureRound(Configuration configuration, DadRunRequest request,
        IReadOnlyCollection<DadParticipantSnapshot> participants, IReadOnlyCollection<DadShoppingRunResult> results)
    {
        var roundId = DadMemberCompletionRules.RoundId(request);
        var round = configuration.RoundCompletions.SingleOrDefault(value => value.RoundId == roundId);
        var globalActions = DadCompletionActionSnapshots.Resolve(request.CompletionActions, configuration.CompletionActions);
        var hasGlobals = globalActions.PlaySound || globalActions.RunCommands && globalActions.Commands.Any(value => !string.IsNullOrWhiteSpace(value)) ||
            globalActions.Utilities.OpenGearCoffers || globalActions.Utilities.RegisterTripleTriadCards || globalActions.Utilities.SellTripleTriadCards ||
            globalActions.Utilities.GrandCompanyHandInViaAutoRetainer;
        if (round == null && !hasGlobals && !request.ShoppingAssociations.Any(value => !string.IsNullOrWhiteSpace(value.CustomCommand))) return true;
        if (round == null)
        {
            round = new DadRoundCompletionSnapshot { RoundId = roundId };
            configuration.RoundCompletions.Add(round);
        }
        if (round.Ready || round.Cancelled) return false;
        if (hasGlobals)
        {
            round.GlobalActions.Add(globalActions);
            round.GlobalActions = [MergeRoundActions(round.GlobalActions)];
        }
        foreach (var assignment in request.ShoppingAssociations)
        {
            if (assignment.PresetId.Length > 0)
            {
                var observations = results.Where(result => DadShoppingAssociationRules.SameProvenance(result.Association, assignment)).ToList();
                round.ShoppingCompletion[$"{assignment.OwnerKind}:{assignment.OwnerId}:{assignment.AssociationId}"] =
                    (observations.Count > 0 || assignment.NonRepeatableRowsFulfilled) && !observations.Any(result =>
                        !result.Succeeded || result.Disposition == "partial" ||
                        result.CreditedQuantities != null && !result.NonRepeatableRowsFulfilled);
            }
            if (string.IsNullOrWhiteSpace(assignment.CustomCommand)) continue;
            var recipients = participants.Where(participant => DadShoppingAssociationRules.MatchesLocalShopper(assignment, participant)).ToList();
            if (recipients.Count != 1 || !DadCompletionCommandRules.TryNormalizeCustomCommand(assignment.CustomCommand, out var command, out _)) return false;
            var recipient = recipients[0];
            var existing = round.Members.SingleOrDefault(value => DadMemberCompletionRules.SameRecipient(value.Recipient, recipient));
            if (existing != null && existing.Recipient.WorkerSessionId != recipient.WorkerSessionId) return false;
            // Plan entries are visited in schedule order; the last applicable
            // Plan wins, and the Schedule assignment follows the Plan.
            if (existing != null) round.Members.Remove(existing);
            round.Members.Add(new DadMemberCompletionCommand
            {
                RoundId = roundId, RunId = request.RequestId, Recipient = DadMemberCompletionRecipient.From(recipient), Command = command,
                State = results.Any(result => DadShoppingAssociationRules.MatchesLocalShopper(result.Association, recipient) &&
                    result.Succeeded && result.AcknowledgedPostCommands.Contains(command, StringComparer.Ordinal))
                    ? DadMemberCompletionState.Acknowledged : DadMemberCompletionState.Pending,
            });
        }
        round.ShoppingComplete = round.ShoppingCompletion.Values.All(static value => value);
        TrimMemberHistory(configuration);
        return configuration.TryPersistNow();
    }

    internal static void FinalizeRound(Configuration configuration, string roundId)
    {
        var round = configuration.RoundCompletions.SingleOrDefault(value => value.RoundId == roundId);
        if (round == null || round.Cancelled || round.Ready) return;
        if (!round.ShoppingComplete)
        {
            round.Cancelled = true;
            foreach (var member in round.Members)
            { member.State = DadMemberCompletionState.Rejected; member.Summary = "Final command withheld because round shopping is incomplete."; }
            SetStatus("dad-round-shopping-incomplete", "Final actions withheld because round shopping is incomplete.", true);
        }
        else round.Ready = true;
        if (!configuration.TryPersistNow())
        {
            round.Ready = false;
            round.Cancelled = true;
            SetStatus("dad-round-completion-save-failed", "Round completion could not be persisted; no final command dispatched.", true);
        }
    }

    internal static void CancelRound(Configuration configuration, string? roundId = null)
    {
        foreach (var round in configuration.RoundCompletions.Where(value => roundId == null || value.RoundId == roundId))
        {
            round.Cancelled = true;
            foreach (var member in round.Members.Where(value => value.State == DadMemberCompletionState.Pending))
            { member.State = DadMemberCompletionState.Cancelled; member.Summary = "Cancelled before submission."; }
        }
        foreach (var permission in configuration.MemberCompletionPermissions.Where(value =>
                     (roundId == null || value.RoundId == roundId) && value.State == DadMemberCompletionState.Pending))
            permission.State = DadMemberCompletionState.Cancelled;
        if (roundId == null) PendingSteps.Clear();
        configuration.Save();
    }

    private static DadMemberCompletionCommand ReceiveMemberCompletion(Configuration configuration, DadPresenceService presence,
        DadWorkerExecutionService worker, DadMemberCompletionCommand command)
    {
        DadMemberCompletionCommand Reject(string summary)
        { var result = command.Clone(); result.State = DadMemberCompletionState.Rejected; result.Summary = summary; return result; }
        if (command.Recipient == null) return new DadMemberCompletionCommand
        { OperationId = command.OperationId, State = DadMemberCompletionState.Rejected, Summary = "Final-command recipient is missing." };
        if (!Guid.TryParseExact(command.OperationId, "N", out var operationId) || operationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(command.RoundId) || command.RoundId.Length > 128 ||
            string.IsNullOrWhiteSpace(command.RunId) || command.RunId.Length > 128 ||
            !DadCompletionCommandRules.TryNormalizeCustomCommand(command.Command, out var normalized, out _) || normalized != command.Command)
            return Reject("Malformed final command.");
        var receipt = configuration.MemberCompletionReceipts.SingleOrDefault(value => value.OperationId == command.OperationId);
        if (receipt != null) return DadMemberCompletionRules.SameIntent(receipt, command)
            ? receipt.Clone() : Reject("Final-command operation identity changed.");
        var local = presence.BuildLiveSafetySnapshot();
        if (configuration.RoundCompletions.Any(round => round.RoundId == command.RoundId && round.Cancelled))
            return Reject("The owning round was cancelled or incomplete.");
        if (!DadMemberCompletionRules.SameRecipient(command.Recipient, local) ||
            command.Recipient.WorkerSessionId != presence.WorkerSessionId || !local.WorldReadyStable || !local.PostArReady ||
            worker.GetStatus() is { IsTerminal: false, State: not DadWorkerExecutionState.Idle })
            return Reject("The exact frozen recipient is unavailable, changed, or busy.");
        var permissions = configuration.MemberCompletionPermissions.Where(value =>
            DadMemberCompletionRules.SameIntent(value, command) && value.State == DadMemberCompletionState.Pending).ToList();
        if (permissions.Count != 1 || configuration.MemberCompletionReceipts.Any(value => value.RoundId == command.RoundId &&
            DadMemberCompletionRules.SameRecipient(value.Recipient, command.Recipient)))
            return Reject("No unused successful frozen member assignment authorizes this round command.");
        receipt = command.Clone(); receipt.State = DadMemberCompletionState.Submitted; receipt.SubmittedAtUtc = DadClock.UtcNow;
        receipt.Summary = "Submission intent saved; registered command outcome is uncertain.";
        configuration.MemberCompletionReceipts.Add(receipt);
        foreach (var permission in configuration.MemberCompletionPermissions.Where(value => value.RoundId == command.RoundId &&
                     DadMemberCompletionRules.SameRecipient(value.Recipient, command.Recipient))) permission.State = DadMemberCompletionState.Cancelled;
        if (!configuration.TryPersistNow()) return Reject("Could not persist the final-command intent; no dispatch requested.");
        try
        {
            var accepted = Plugin.CommandManager.ProcessCommand(command.Command);
            receipt.State = accepted ? DadMemberCompletionState.Acknowledged : DadMemberCompletionState.Rejected;
            receipt.AcknowledgedAtUtc = accepted ? DadClock.UtcNow : null;
            receipt.Summary = accepted ? "Registered command manager acknowledged submission; command effect is not independently verified."
                : "The registered command manager rejected the command.";
        }
        catch { receipt.State = DadMemberCompletionState.Uncertain; receipt.Summary = "Command submission threw; no replay is permitted."; }
        if (!configuration.TryPersistNow())
        { receipt.State = DadMemberCompletionState.Uncertain; receipt.Summary = "Submission receipt could not be persisted; no replay is permitted."; }
        return receipt.Clone();
    }

    private static void UpdateMembers(Configuration configuration, IPluginLog log)
    {
        if (memberPresence == null || memberTransport == null || memberWorker == null) return;
        foreach (var round in configuration.RoundCompletions.Where(value => value.Ready && !value.Cancelled))
        {
            if (!round.GlobalActionsQueued)
            {
                if (PendingSteps.Count > 0) return;
                var actions = MergeRoundActions(round.GlobalActions);
                actions.Commands.RemoveAll(command => round.Members.Any(member =>
                    member.Recipient.WorkerSessionId == memberPresence.WorkerSessionId && member.Command == command));
                round.GlobalActionsQueued = true;
                if (!configuration.TryPersistNow()) { round.GlobalActionsQueued = false; return; }
                Enqueue(configuration, log, new DadRunRequest { CompletionActions = actions });
            }
            foreach (var member in round.Members.Where(value => value.State is DadMemberCompletionState.Pending or DadMemberCompletionState.Submitted))
            {
                var start = member.State == DadMemberCompletionState.Pending;
                if (!start && !memberOperationsInFlight.Contains(member.OperationId))
                { member.State = DadMemberCompletionState.Uncertain; member.Summary = "Reload interrupted submission; no replay is permitted."; configuration.Save(); continue; }
                if (start)
                {
                    member.State = DadMemberCompletionState.Submitted; member.SubmittedAtUtc = DadClock.UtcNow;
                    member.Summary = "Submitted; awaiting the exact recipient acknowledgement.";
                    if (!configuration.TryPersistNow()) { member.State = DadMemberCompletionState.Rejected; member.Summary = "Submission intent could not be saved."; continue; }
                    memberOperationsInFlight.Add(member.OperationId);
                }
                var local = member.Recipient.WorkerSessionId == memberPresence.WorkerSessionId;
                var reply = local ? ReceiveMemberCompletion(configuration, memberPresence, memberWorker, member)
                    : memberTransport.SendMemberCompletion(member, start, out _);
                if (reply == null) continue;
                memberOperationsInFlight.Remove(member.OperationId);
                if (!DadMemberCompletionRules.SameIntent(member, reply) || reply.OperationId != member.OperationId)
                { member.State = DadMemberCompletionState.Uncertain; member.Summary = "Recipient acknowledgement did not match the frozen command."; }
                else { member.State = reply.State; member.Summary = reply.Summary; member.AcknowledgedAtUtc = reply.AcknowledgedAtUtc; }
                configuration.TryPersistNow();
                SetStatus("dad-member-final-command", member.Summary, member.State != DadMemberCompletionState.Acknowledged);
            }
        }
    }

    private static DadCompletionActions MergeRoundActions(IEnumerable<DadCompletionActions> snapshots)
    {
        var result = new DadCompletionActions();
        foreach (var actions in snapshots)
        {
            if (actions.PlaySound) { result.PlaySound = true; result.SoundEffectId = actions.SoundEffectId; }
            if (actions.RunCommands) result.Commands.AddRange(actions.Commands);
            result.Utilities.OpenGearCoffers |= actions.Utilities.OpenGearCoffers;
            result.Utilities.RegisterTripleTriadCards |= actions.Utilities.RegisterTripleTriadCards;
            result.Utilities.SellTripleTriadCards |= actions.Utilities.SellTripleTriadCards;
            if (actions.Utilities.GrandCompanyHandInViaAutoRetainer)
            { result.Utilities.GrandCompanyHandInViaAutoRetainer = true; result.Utilities.GrandCompanyHandInCommand = actions.Utilities.GrandCompanyHandInCommand; }
        }
        result.Commands = result.Commands.Select(command => DadCompletionCommandRules.TryNormalizeCustomCommand(command, out var normalized, out _)
            ? normalized : command).Distinct(StringComparer.Ordinal).ToList();
        if (result.Utilities.GrandCompanyHandInViaAutoRetainer)
            result.Commands.RemoveAll(command => command == result.Utilities.GrandCompanyHandInCommand);
        result.RunCommands = result.Commands.Count > 0;
        return result;
    }

    private static void TrimMemberHistory(Configuration configuration)
    {
        // Match the existing compact run-history retention. Removing both an
        // old permission and receipt makes late old requests unauthorized.
        var active = configuration.RoundCompletions.Where(round => !round.Cancelled &&
            (!round.Ready || !round.GlobalActionsQueued || round.Members.Any(member =>
                member.State is DadMemberCompletionState.Pending or DadMemberCompletionState.Submitted)))
            .Select(round => round.RoundId).ToHashSet(StringComparer.Ordinal);
        var retained = configuration.RoundCompletions.TakeLast(DadRunHistoryPersistenceRules.MaximumEntries).Select(round => round.RoundId)
            .Concat(active).ToHashSet(StringComparer.Ordinal);
        configuration.RoundCompletions.RemoveAll(round => !retained.Contains(round.RoundId));
        var permissionRounds = configuration.MemberCompletionPermissions.Select(value => value.RoundId)
            .Distinct(StringComparer.Ordinal).TakeLast(DadRunHistoryPersistenceRules.MaximumEntries).Concat(retained).ToHashSet(StringComparer.Ordinal);
        configuration.MemberCompletionPermissions.RemoveAll(value => !permissionRounds.Contains(value.RoundId));
        configuration.MemberCompletionReceipts.RemoveAll(value => !permissionRounds.Contains(value.RoundId));
    }
}
