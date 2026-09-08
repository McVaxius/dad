using System.Text.Json;
using dad.Services;

namespace dad.Headless;

// External FrenRider responses; production owns framing, routing and temporary ownership.
internal sealed class VirtualProfileIpc(Action<string> record)
{
    internal const string ProfileJson = "{\"label\":\"Synthetic shared profile\",\"enabled\":true}";
    private string? activeOwnership;
    public object Invoke(string endpoint, object?[] values)
    {
        if (values is not [string json]) throw new InvalidOperationException("Invalid synthetic FrenRider payload.");
        record($"ipc:{endpoint}:{json}");
        if (endpoint == "FrenRider.Dad.ConfigureAndEnable")
            return !string.IsNullOrWhiteSpace(json);
        using var document = JsonDocument.Parse(json);
        var request = document.RootElement;
        if (request.GetProperty("version").GetInt32() != 1 ||
            !Guid.TryParse(request.GetProperty("proposalId").GetString(), out var proposalId) || proposalId == Guid.Empty)
            throw new InvalidOperationException("Invalid synthetic profile contract identity.");
        var owner = string.Join(":", new[] { "ownerId", "islandId", "characterId", "proposalId" }
            .Select(key => request.GetProperty(key).GetString()));
        string outcome;
        switch (endpoint)
        {
            case "FrenRider.Dad.ResolveOrCreateProfile": outcome = "exported"; break;
            case "FrenRider.Dad.ApplyProfile":
                if (request.GetProperty("profileJson").GetString() != ProfileJson || activeOwnership != null)
                    throw new InvalidOperationException("Unexpected profile content or duplicate application.");
                activeOwnership = owner; outcome = "temporary-applied"; break;
            case "FrenRider.Dad.ReleaseTemporaryProfile":
                if (activeOwnership != owner) throw new InvalidOperationException("Profile release ownership mismatch.");
                activeOwnership = null; outcome = "released"; break;
            default: throw new InvalidOperationException($"Unknown FrenRider IPC: {endpoint}");
        }
        return DadIpcJson.Serialize(new { version = 1, code = "ok", outcome, profileJson = ProfileJson });
    }
}
