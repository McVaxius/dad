using dad.Models;

namespace dad.Services;

internal static class DadAutoPartyRuntimeRequestRules
{
    public static DadRunRequest CloneForAdmission(DadRunRequest source)
    {
        ArgumentNullException.ThrowIfNull(source);
        source.ApplyOrchestrationDefaults();
        var clone = DadIpcJson.DeserializeRaw<DadRunRequest>(DadIpcJson.Serialize(source))
                    ?? throw new InvalidOperationException("Dad runtime request clone failed.");
        clone.ApplyOrchestrationDefaults();
        CopyRuntimeIdentities(
            source.Orchestration.PreferredRosterCharacters,
            clone.Orchestration.PreferredRosterCharacters);
        CopyRuntimeIdentities(
            source.Orchestration.RequiredRosterCharacters,
            clone.Orchestration.RequiredRosterCharacters);
        clone.Orchestration.AutoPartyProposalId = string.Empty;
        clone.Orchestration.AutoPartyFormationOnly = source.Orchestration.AutoPartyFormationOnly;
        return clone;
    }

    public static bool RequiresRegisteredIslandRoute(DadRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.ApplyOrchestrationDefaults();
        return request.Orchestration.RequiredRosterCharacters.Any(static reference =>
            !string.IsNullOrWhiteSpace(reference.SharedIdentityToken));
    }

    public static Guid BindNewProposal(DadRunRequest runtimeRequest)
    {
        ArgumentNullException.ThrowIfNull(runtimeRequest);
        runtimeRequest.ApplyOrchestrationDefaults();
        if (!RequiresRegisteredIslandRoute(runtimeRequest))
        {
            runtimeRequest.Orchestration.AutoPartyProposalId = string.Empty;
            return Guid.Empty;
        }

        var proposalId = Guid.NewGuid();
        runtimeRequest.Orchestration.AutoPartyProposalId = proposalId.ToString("D");
        return proposalId;
    }

    private static void CopyRuntimeIdentities(
        IReadOnlyList<DadRosterCharacterRef> source,
        IReadOnlyList<DadRosterCharacterRef> target)
    {
        if (source.Count != target.Count)
            throw new InvalidOperationException("Dad runtime roster clone changed its ordered slot count.");
        for (var index = 0; index < source.Count; index++)
            target[index].SharedIdentityToken = source[index].SharedIdentityToken;
    }
}
