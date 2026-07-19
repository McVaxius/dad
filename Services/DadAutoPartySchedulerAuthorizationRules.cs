using dad.Models;

namespace dad.Services;

public static class DadAutoPartySchedulerAuthorizationRules
{
    public static DadAutoPartyAuthorizationDecision Evaluate(
        DadRunRequest request,
        Func<Guid, DadAutoPartyAuthorizationDecision> authorizationResolver)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(authorizationResolver);

        var proposalText = request.Orchestration?.AutoPartyProposalId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(proposalText))
            return new(DadAutoPartyAuthorizationState.NotRequired, "dad-autoparty-not-requested", Guid.Empty);
        if (!Guid.TryParse(proposalText, out var proposalId) || proposalId == Guid.Empty)
            return new(DadAutoPartyAuthorizationState.Denied, "dad-autoparty-proposal-id-invalid", Guid.Empty);
        return authorizationResolver(proposalId);
    }
}
