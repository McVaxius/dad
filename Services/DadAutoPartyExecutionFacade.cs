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
                if (observedParty.MemberCount is < 1 or > 8 ||
                    string.IsNullOrWhiteSpace(observedParty.ObservedStateHash) ||
                    observedParty.ObservedStateHash.Length > 128)
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
        int memberCount,
        long stateGeneration)
    {
        var material = Encoding.UTF8.GetBytes($"{proposalId:D}:{memberCount}:{stateGeneration}");
        var hash = Convert.ToHexString(SHA256.HashData(material));
        return new(memberCount, hash, DateTime.UtcNow);
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

public sealed class DadAutoPartyCoordinatorExecutionFacade : IAutoPartyExecutionFacade
{
    private readonly IAutoPartyExecutionFacade inner;
    private readonly Func<Guid, bool> isActiveProposal;
    private readonly Func<DadRunResult> getCoordinatorResult;
    private readonly Func<DadRunResult> cancelCoordinator;

    public DadAutoPartyCoordinatorExecutionFacade(
        IAutoPartyExecutionFacade inner,
        Func<Guid, bool> isActiveProposal,
        Func<DadRunResult> getCoordinatorResult,
        Func<DadRunResult> cancelCoordinator)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.isActiveProposal = isActiveProposal ?? throw new ArgumentNullException(nameof(isActiveProposal));
        this.getCoordinatorResult = getCoordinatorResult ?? throw new ArgumentNullException(nameof(getCoordinatorResult));
        this.cancelCoordinator = cancelCoordinator ?? throw new ArgumentNullException(nameof(cancelCoordinator));
    }

    public ValueTask<DadAutoPartyExecutionResult> PrepareAsync(
        ExecutionOperation operation,
        IntegrationProfile? profile,
        CancellationToken cancellationToken = default)
        => ExecuteAgainstCoordinatorAsync(
            operation,
            () => inner.PrepareAsync(operation, profile, cancellationToken),
            cancellationToken);

    public ValueTask<DadAutoPartyExecutionResult> ReserveAsync(
        ExecutionOperation operation,
        CancellationToken cancellationToken = default)
        => ExecuteAgainstCoordinatorAsync(
            operation,
            () => inner.ReserveAsync(operation, cancellationToken),
            cancellationToken);

    public ValueTask<DadAutoPartyExecutionResult> FormAsync(
        ExecutionOperation operation,
        DadAutoPartyObservedPartyReceipt observedParty,
        CancellationToken cancellationToken = default)
        => ExecuteAgainstCoordinatorAsync(
            operation,
            () => inner.FormAsync(operation, observedParty, cancellationToken),
            cancellationToken);

    public ValueTask<DadAutoPartyExecutionResult> QueueAsync(
        ExecutionOperation operation,
        CancellationToken cancellationToken = default)
        => ExecuteAgainstCoordinatorAsync(
            operation,
            () => inner.QueueAsync(operation, cancellationToken),
            cancellationToken);

    public async ValueTask<DadAutoPartyExecutionResult> CancelAsync(
        ExecutionOperation operation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await inner.CancelAsync(operation, cancellationToken).ConfigureAwait(false);
        if (result.Outcome != ExecutionOutcome.Denied && isActiveProposal(operation.ProposalId))
            _ = cancelCoordinator();
        return ProjectCoordinatorPhase(result);
    }

    public ValueTask<DadAutoPartyExecutionResult> SettleAsync(
        ExecutionOperation operation,
        CancellationToken cancellationToken = default)
        => ExecuteAgainstCoordinatorAsync(
            operation,
            () => inner.SettleAsync(operation, cancellationToken),
            cancellationToken);

    public ValueTask<DadAutoPartyExecutionResult> RestoreAsync(
        ExecutionOperation operation,
        CancellationToken cancellationToken = default)
        => ExecuteAgainstCoordinatorAsync(
            operation,
            () => inner.RestoreAsync(operation, cancellationToken),
            cancellationToken);

    public void StopAll(string safeReason)
    {
        inner.StopAll(safeReason);
        var current = getCoordinatorResult();
        if (!current.IsTerminal && current.Status != DadRunStatus.Idle)
            _ = cancelCoordinator();
    }

    private async ValueTask<DadAutoPartyExecutionResult> ExecuteAgainstCoordinatorAsync(
        ExecutionOperation operation,
        Func<ValueTask<DadAutoPartyExecutionResult>> execute,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!isActiveProposal(operation.ProposalId))
            return Denied(operation, "dad-coordinator-proposal-not-active");
        var result = await execute().ConfigureAwait(false);
        return ProjectCoordinatorPhase(result);
    }

    private DadAutoPartyExecutionResult ProjectCoordinatorPhase(DadAutoPartyExecutionResult result)
    {
        if (result.Outcome == ExecutionOutcome.Denied)
            return result;
        var coordinator = getCoordinatorResult();
        return result with
        {
            Phase = coordinator.Phase,
            SafeCode = coordinator.Phase == DadRunPhase.GroupReady
                ? "dad-coordinator-group-ready"
                : result.SafeCode,
        };
    }

    private static DadAutoPartyExecutionResult Denied(ExecutionOperation operation, string safeCode) => new(
        operation.OperationId,
        operation.ProposalId,
        operation.Kind,
        ExecutionOutcome.Denied,
        DadRunPhase.Idle,
        safeCode,
        operation.ExpectedStateGeneration);
}
