using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AutoParty.Contracts;
using dad.Models;

namespace dad.Services;

public interface IAutoPartyExecutionFacade
{
    ValueTask<DadAutoPartyExecutionResult> PrepareAsync(
        ExecutionOperation operation,
        IntegrationProfile? profile,
        CancellationToken cancellationToken = default);

    ValueTask<DadAutoPartyExecutionResult> ReserveAsync(
        ExecutionOperation operation,
        CancellationToken cancellationToken = default);

    ValueTask<DadAutoPartyExecutionResult> FormAsync(
        ExecutionOperation operation,
        DadAutoPartyObservedPartyReceipt observedParty,
        CancellationToken cancellationToken = default);

    ValueTask<DadAutoPartyExecutionResult> QueueAsync(
        ExecutionOperation operation,
        CancellationToken cancellationToken = default);

    ValueTask<DadAutoPartyExecutionResult> CancelAsync(
        ExecutionOperation operation,
        CancellationToken cancellationToken = default);

    ValueTask<DadAutoPartyExecutionResult> SettleAsync(
        ExecutionOperation operation,
        CancellationToken cancellationToken = default);

    ValueTask<DadAutoPartyExecutionResult> RestoreAsync(
        ExecutionOperation operation,
        CancellationToken cancellationToken = default);

    void StopAll(string safeReason);
}

public sealed class DadAutoPartyRuntimeExecutionFacade : IAutoPartyExecutionFacade
{
    private readonly IAutoPartyPolicyFacade policy;
    private readonly Func<ExecutionOperation, IntegrationProfile?, DadAutoPartyObservedPartyReceipt?, CancellationToken,
        ValueTask<DadAutoPartyExecutionResult>> execute;
    private readonly Action<string> stopAll;

    public DadAutoPartyRuntimeExecutionFacade(
        IAutoPartyPolicyFacade policy,
        Func<ExecutionOperation, IntegrationProfile?, DadAutoPartyObservedPartyReceipt?, CancellationToken,
            ValueTask<DadAutoPartyExecutionResult>> execute,
        Action<string>? stopAll = null)
    {
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
        this.stopAll = stopAll ?? (_ => { });
    }

    public static DadAutoPartyRuntimeExecutionFacade CreateUnavailable(IAutoPartyPolicyFacade policy)
        => new(
            policy,
            static (operation, _, _, _) => ValueTask.FromResult(Denied(
                operation,
                "dad-runtime-execution-not-configured",
                operation.ExpectedStateGeneration)));

    public ValueTask<DadAutoPartyExecutionResult> PrepareAsync(
        ExecutionOperation operation,
        IntegrationProfile? profile,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(operation, ExecutionOperationKind.Prepare, profile, null, cancellationToken);

    public ValueTask<DadAutoPartyExecutionResult> ReserveAsync(
        ExecutionOperation operation,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(operation, ExecutionOperationKind.Reserve, null, null, cancellationToken);

    public ValueTask<DadAutoPartyExecutionResult> FormAsync(
        ExecutionOperation operation,
        DadAutoPartyObservedPartyReceipt observedParty,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(operation, ExecutionOperationKind.Form, null, observedParty, cancellationToken);

    public ValueTask<DadAutoPartyExecutionResult> QueueAsync(
        ExecutionOperation operation,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(operation, ExecutionOperationKind.Queue, null, null, cancellationToken);

    public ValueTask<DadAutoPartyExecutionResult> CancelAsync(
        ExecutionOperation operation,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(operation, ExecutionOperationKind.Cancel, null, null, cancellationToken);

    public ValueTask<DadAutoPartyExecutionResult> SettleAsync(
        ExecutionOperation operation,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(operation, ExecutionOperationKind.Settle, null, null, cancellationToken);

    public ValueTask<DadAutoPartyExecutionResult> RestoreAsync(
        ExecutionOperation operation,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(operation, ExecutionOperationKind.Restore, null, null, cancellationToken);

    public void StopAll(string safeReason)
        => stopAll(safeReason);

    private async ValueTask<DadAutoPartyExecutionResult> ExecuteAsync(
        ExecutionOperation operation,
        ExecutionOperationKind expectedKind,
        IntegrationProfile? profile,
        DadAutoPartyObservedPartyReceipt? observedParty,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (operation.Kind != expectedKind || operation.OperationId == Guid.Empty)
            return Denied(operation, "dad-typed-operation-kind-mismatch", operation.ExpectedStateGeneration);
        var authorization = policy.AuthorizeExecution(operation);
        if (!authorization.Allowed)
            return Denied(operation, authorization.SafeCode, authorization.StateGeneration);
        return await execute(operation, profile, observedParty, cancellationToken).ConfigureAwait(false);
    }

    private static DadAutoPartyExecutionResult Denied(
        ExecutionOperation operation,
        string safeCode,
        long generation)
        => new(
            operation.OperationId,
            operation.ProposalId,
            operation.Kind,
            ExecutionOutcome.Denied,
            DadRunPhase.Idle,
            safeCode,
            generation);
}

public sealed class DadAutoPartyFakeExecutionFacade : IAutoPartyExecutionFacade
{
    private readonly object gate = new();
    private readonly IAutoPartyPolicyFacade policy;
    private readonly Dictionary<Guid, FakeSessionState> sessions = [];

    public DadAutoPartyFakeExecutionFacade(IAutoPartyPolicyFacade policy)
        => this.policy = policy ?? throw new ArgumentNullException(nameof(policy));

    public ValueTask<DadAutoPartyExecutionResult> PrepareAsync(
        ExecutionOperation operation,
        IntegrationProfile? profile,
        CancellationToken cancellationToken = default)
        => Execute(
            operation,
            ExecutionOperationKind.Prepare,
            cancellationToken,
            state =>
            {
                if (profile != null &&
                    (profile.ProposalId != operation.ProposalId ||
                     profile.OwnerId != operation.OwnerId ||
                     profile.ExpectedStateGeneration != operation.ExpectedStateGeneration))
                    return Denied(operation, "dad-profile-contract-mismatch", state.Generation);

                state = state with
                {
                    Prepared = true,
                    FormationOnly = operation.FormationOnly,
                    ProfileCaptured = profile != null,
                    ProfileApplied = profile != null,
                    ProfileVerified = profile != null,
                    ProfileRestored = false,
                    Generation = operation.ExpectedStateGeneration,
                };
                sessions[operation.ProposalId] = state;
                return Completed(operation, DadRunPhase.Planning, "dad-prepare-complete", state);
            });

    public ValueTask<DadAutoPartyExecutionResult> ReserveAsync(
        ExecutionOperation operation,
        CancellationToken cancellationToken = default)
        => Execute(
            operation,
            ExecutionOperationKind.Reserve,
            cancellationToken,
            state =>
            {
                if (!state.Prepared)
                    return Denied(operation, "dad-reserve-before-prepare", state.Generation);
                state = state with { Reserved = true };
                sessions[operation.ProposalId] = state;
                return Completed(operation, DadRunPhase.ClaimingSlots, "dad-reserve-complete", state);
            });

    public ValueTask<DadAutoPartyExecutionResult> FormAsync(
        ExecutionOperation operation,
        DadAutoPartyObservedPartyReceipt observedParty,
        CancellationToken cancellationToken = default)
        => Execute(
            operation,
            ExecutionOperationKind.Form,
            cancellationToken,
            state =>
            {
                if (!state.Prepared || !state.Reserved)
                    return Denied(operation, "dad-form-prerequisites-missing", state.Generation);
                if (!IsValidInviteLocator(operation.InviteLocator))
                    return Denied(operation, "dad-invite-locator-invalid", state.Generation);
                if (!IsValidObservedPartyReceipt(operation, observedParty))
                    return Denied(operation, "dad-observed-party-receipt-invalid", state.Generation);

                state = state with
                {
                    Formed = true,
                    FormationOnly = state.FormationOnly || operation.FormationOnly,
                    ObservedParty = observedParty,
                };
                sessions[operation.ProposalId] = state;
                var phase = state.FormationOnly ? DadRunPhase.GroupReady : DadRunPhase.AssemblingParty;
                var code = state.FormationOnly ? "dad-group-ready" : "dad-form-complete";
                return Completed(operation, phase, code, state, observedParty);
            });

    public ValueTask<DadAutoPartyExecutionResult> QueueAsync(
        ExecutionOperation operation,
        CancellationToken cancellationToken = default)
        => Execute(
            operation,
            ExecutionOperationKind.Queue,
            cancellationToken,
            state =>
            {
                if (!state.Formed)
                    return Denied(operation, "dad-queue-before-form", state.Generation);
                if (state.FormationOnly || operation.FormationOnly)
                    return Denied(operation, "dad-formation-only-queue-denied", state.Generation);
                state = state with { Queued = true };
                sessions[operation.ProposalId] = state;
                return Completed(operation, DadRunPhase.QueueStarting, "dad-queue-complete", state, state.ObservedParty);
            });

    public ValueTask<DadAutoPartyExecutionResult> CancelAsync(
        ExecutionOperation operation,
        CancellationToken cancellationToken = default)
        => Execute(
            operation,
            ExecutionOperationKind.Cancel,
            cancellationToken,
            state =>
            {
                state = RestoreState(state) with { Cancelled = true };
                sessions[operation.ProposalId] = state;
                return Completed(operation, DadRunPhase.Finalizing, "dad-cancel-complete", state, state.ObservedParty);
            });

    public ValueTask<DadAutoPartyExecutionResult> SettleAsync(
        ExecutionOperation operation,
        CancellationToken cancellationToken = default)
        => Execute(
            operation,
            ExecutionOperationKind.Settle,
            cancellationToken,
            state =>
            {
                if (state.FormationOnly)
                    return Denied(operation, "dad-formation-only-settle-denied", state.Generation);
                if (!state.Queued)
                    return Denied(operation, "dad-settle-before-queue", state.Generation);
                state = state with { Settled = true };
                sessions[operation.ProposalId] = state;
                return Completed(operation, DadRunPhase.Finalizing, "dad-settle-complete", state, state.ObservedParty);
            });

    public ValueTask<DadAutoPartyExecutionResult> RestoreAsync(
        ExecutionOperation operation,
        CancellationToken cancellationToken = default)
        => Execute(
            operation,
            ExecutionOperationKind.Restore,
            cancellationToken,
            state =>
            {
                state = RestoreState(state);
                sessions[operation.ProposalId] = state;
                return Completed(
                    operation,
                    DadRunPhase.Finalizing,
                    state.ProfileRestored ? "dad-profile-restored" : "dad-profile-restoration-not-applicable",
                    state,
                    state.ObservedParty);
            });

    public void StopAll(string safeReason)
    {
        lock (gate)
        {
            foreach (var pair in sessions.ToList())
                sessions[pair.Key] = RestoreState(pair.Value) with { Cancelled = true };
        }
    }

    public static DadAutoPartyObservedPartyReceipt CreateObservedPartyReceipt(
        Guid proposalId,
        ImmutableArray<ulong> contentIds,
        long stateGeneration)
    {
        if (!HasValidContentIds(contentIds))
            throw new ArgumentException("Party Content IDs must contain 1-8 unique nonzero values.", nameof(contentIds));
        return new(
            contentIds.Length,
            contentIds,
            ComputeObservedStateHash(proposalId, stateGeneration, contentIds),
            DateTime.UtcNow);
    }

    private ValueTask<DadAutoPartyExecutionResult> Execute(
        ExecutionOperation operation,
        ExecutionOperationKind expectedKind,
        CancellationToken cancellationToken,
        Func<FakeSessionState, DadAutoPartyExecutionResult> mutation)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (operation.Kind != expectedKind || operation.OperationId == Guid.Empty)
                return ValueTask.FromResult(Denied(operation, "dad-typed-operation-kind-mismatch", operation.ExpectedStateGeneration));
            var authorization = policy.AuthorizeExecution(operation);
            if (!authorization.Allowed)
                return ValueTask.FromResult(Denied(operation, authorization.SafeCode, authorization.StateGeneration));

            var state = sessions.TryGetValue(operation.ProposalId, out var existing)
                ? existing
                : new FakeSessionState(operation.ProposalId, operation.ExpectedStateGeneration);
            if (state.Cancelled && expectedKind is not ExecutionOperationKind.Cancel and not ExecutionOperationKind.Restore)
                return ValueTask.FromResult(Denied(operation, "dad-session-cancelled", state.Generation));
            return ValueTask.FromResult(mutation(state));
        }
    }

    private static bool IsValidInviteLocator(InviteLocator? locator)
    {
        if (locator == null)
            return false;
        var now = DateTimeOffset.UtcNow;
        return !string.IsNullOrWhiteSpace(locator.LocatorId) &&
               locator.LocatorId.Length <= AutoPartyProtocol.MaximumIdentifierLength &&
               locator.ValidUntil > now &&
               locator.ValidUntil <= now + TimeSpan.FromMinutes(5) &&
               !locator.OpaqueLocator.IsDefaultOrEmpty &&
               locator.OpaqueLocator.Length <= 256;
    }

    private static bool IsValidObservedPartyReceipt(
        ExecutionOperation operation,
        DadAutoPartyObservedPartyReceipt receipt)
        => receipt.MemberCount == receipt.ContentIds.Length &&
           HasValidContentIds(receipt.ContentIds) &&
           string.Equals(
               receipt.ObservedStateHash,
               ComputeObservedStateHash(
                   operation.ProposalId,
                   operation.ExpectedStateGeneration,
                   receipt.ContentIds),
               StringComparison.Ordinal);

    private static bool HasValidContentIds(ImmutableArray<ulong> contentIds)
        => !contentIds.IsDefault &&
           contentIds.Length is >= 1 and <= 8 &&
           contentIds.All(static contentId => contentId != 0) &&
           contentIds.Distinct().Count() == contentIds.Length;

    private static string ComputeObservedStateHash(
        Guid proposalId,
        long stateGeneration,
        ImmutableArray<ulong> contentIds)
    {
        var orderedIds = string.Join(",", contentIds.Select(static contentId =>
            contentId.ToString(CultureInfo.InvariantCulture)));
        var material = Encoding.UTF8.GetBytes(FormattableString.Invariant(
            $"{proposalId:D}:{stateGeneration}:{orderedIds}"));
        return Convert.ToHexString(SHA256.HashData(material));
    }

    private static FakeSessionState RestoreState(FakeSessionState state)
        => state with
        {
            ProfileRestored = state.ProfileCaptured,
            ProfileApplied = false,
            ProfileVerified = false,
        };

    private static DadAutoPartyExecutionResult Completed(
        ExecutionOperation operation,
        DadRunPhase phase,
        string safeCode,
        FakeSessionState state,
        DadAutoPartyObservedPartyReceipt? partyReceipt = null)
        => new(
            operation.OperationId,
            operation.ProposalId,
            operation.Kind,
            ExecutionOutcome.Completed,
            phase,
            safeCode,
            state.Generation,
            partyReceipt,
            state.ProfileRestored);

    private static DadAutoPartyExecutionResult Denied(
        ExecutionOperation operation,
        string safeCode,
        long generation)
        => new(
            operation.OperationId,
            operation.ProposalId,
            operation.Kind,
            ExecutionOutcome.Denied,
            DadRunPhase.Idle,
            safeCode,
            generation);

    private sealed record FakeSessionState(
        Guid ProposalId,
        long Generation,
        bool Prepared = false,
        bool Reserved = false,
        bool Formed = false,
        bool Queued = false,
        bool Settled = false,
        bool Cancelled = false,
        bool FormationOnly = false,
        bool ProfileCaptured = false,
        bool ProfileApplied = false,
        bool ProfileVerified = false,
        bool ProfileRestored = false,
        DadAutoPartyObservedPartyReceipt? ObservedParty = null);
}
