namespace dad.Services;

internal sealed class DadRosterKnowledgeLearningCursor
{
    private long peerCatalogRevision = -1;
    private DateTime characterPoolUpdatedUtc = DateTime.MinValue;

    public bool TryAdvance(long currentPeerCatalogRevision, DateTime currentCharacterPoolUpdatedUtc)
    {
        if (currentPeerCatalogRevision == peerCatalogRevision &&
            currentCharacterPoolUpdatedUtc <= characterPoolUpdatedUtc)
        {
            return false;
        }

        peerCatalogRevision = currentPeerCatalogRevision;
        if (currentCharacterPoolUpdatedUtc > characterPoolUpdatedUtc)
            characterPoolUpdatedUtc = currentCharacterPoolUpdatedUtc;
        return true;
    }
}
