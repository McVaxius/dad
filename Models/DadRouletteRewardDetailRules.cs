namespace dad.Models;

// Runtime UI evidence only; not part of the persisted roster or transport protocol.
internal readonly record struct DadRouletteRewardDetailSnapshot(
    ulong CharacterContentId,
    uint RouletteId,
    string ListFingerprint,
    nuint DetailIdentity,
    bool DetailsReady,
    string ExpectedTitle,
    string DisplayedTitle,
    bool MarkerNodesAvailable,
    bool MarkerVisible,
    bool MarkerTextVisible,
    string MarkerText,
    DateTime CapturedAtUtc,
    string ExpectedReceivedText = "",
    string ExpectedUnclaimedText = "");

internal static class DadRouletteRewardDetailRules
{
    // Addon sheet IDs; resolve through the client's own localized game data.
    public const uint ReceivedLabelRowId = 102588;
    public const uint UnclaimedLabelRowId = 16469;
    public static bool TryCreateObservation(
        DadRouletteRewardDetailSnapshot detail,
        ulong expectedCharacterContentId,
        uint expectedRouletteId,
        out DadRouletteRewardObservation observation,
        out string reason)
    {
        observation = default;
        reason = "Waiting for the exact ready Duty Finder reward detail.";
        var expectedTitle = Normalize(detail.ExpectedTitle);
        var displayedTitle = Normalize(detail.DisplayedTitle);
        if (expectedCharacterContentId == 0 || detail.CharacterContentId != expectedCharacterContentId ||
            expectedRouletteId == 0 || detail.RouletteId != expectedRouletteId ||
            string.IsNullOrWhiteSpace(detail.ListFingerprint) || detail.DetailIdentity == 0 ||
            !detail.DetailsReady || !detail.MarkerNodesAvailable || detail.CapturedAtUtc == default ||
            expectedTitle.Length == 0 || !string.Equals(expectedTitle, displayedTitle, StringComparison.Ordinal))
            return false;

        // The SAME visible label displays both "Reward Unclaimed" and "Reward already received".
        // Visibility is a prerequisite, never the reward state. Hidden/unknown text is not proof.
        var markerText = Normalize(detail.MarkerText);
        var receivedText = Normalize(detail.ExpectedReceivedText);
        var unclaimedText = Normalize(detail.ExpectedUnclaimedText);
        if (!detail.MarkerVisible || !detail.MarkerTextVisible || receivedText.Length == 0 || unclaimedText.Length == 0 ||
            string.Equals(receivedText, unclaimedText, StringComparison.Ordinal))
        {
            reason = "Waiting for a visible reward status label and distinct localized status texts.";
            return false;
        }
        var received = string.Equals(markerText, receivedText, StringComparison.Ordinal);
        if (!received && !string.Equals(markerText, unclaimedText, StringComparison.Ordinal))
        {
            reason = "The visible reward status does not match either exact localized game status.";
            return false;
        }

        observation = new DadRouletteRewardObservation(
            detail.CharacterContentId,
            detail.RouletteId,
            $"{detail.ListFingerprint}|JournalDetail:{detail.DetailIdentity:X}|{displayedTitle}|{markerText}",
            ExactRouletteSelected: true,
            ReceivedRewardCount: received ? 1 : 0,
            MaxRewardCount: 1,
            detail.CapturedAtUtc);
        reason = string.Empty;
        return true;
    }

    private static string Normalize(string? text)
        => string.Join(" ", (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
