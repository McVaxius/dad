namespace dad.Models;

public static class DadStatusText
{
    public static string FormatWorkerRole(DadWorkerRole role)
        => role switch
        {
            DadWorkerRole.ServerDad => "Dad Coordinator",
            DadWorkerRole.ClientDad => "Client Dad",
            _ => "(none)",
        };

    public static string FormatAuthorityMode(DadAuthorityMode authorityMode)
        => authorityMode switch
        {
            DadAuthorityMode.LocalOnly => "Local-only",
            _ => "Server-coordinated",
        };

    public static string FormatParticipantOwner(DadParticipantSnapshot participant)
    {
        if (participant.WorkerRole == DadWorkerRole.None && !participant.IsAuthority)
            return "-";

        var roleText = participant.WorkerRole == DadWorkerRole.None
            ? "Worker"
            : FormatWorkerRole(participant.WorkerRole);

        return participant.IsAuthority
            ? $"Authority ({roleText})"
            : roleText;
    }

    public static string FormatAuthorityStatus(
        DadWorkerRole authorityRole,
        DadWorkerSessionId authorityWorkerSessionId,
        string authorityEndpoint,
        DadAuthorityMode authorityMode)
    {
        if (authorityWorkerSessionId.IsEmpty && string.IsNullOrWhiteSpace(authorityEndpoint))
        {
            return authorityMode == DadAuthorityMode.LocalOnly
                ? "Local-only authority stays on this worker."
                : "Authority not discovered.";
        }

        var roleText = authorityRole == DadWorkerRole.None
            ? "authority"
            : FormatWorkerRole(authorityRole);
        var endpointText = string.IsNullOrWhiteSpace(authorityEndpoint)
            ? "(no endpoint)"
            : authorityEndpoint;

        return authorityMode == DadAuthorityMode.LocalOnly
            ? $"Local-only via {roleText} {authorityWorkerSessionId} at {endpointText}"
            : $"Server-coordinated via {roleText} {authorityWorkerSessionId} at {endpointText}";
    }
}
