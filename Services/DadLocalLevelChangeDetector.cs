namespace dad.Services;

// B5: pure detector that fires once per distinct (active job, level) change of the local character, and
// never on the very first capture (initial login). Kept Dalamud-free so the trigger logic is unit-tested.
public sealed class DadLocalLevelChangeDetector
{
    private bool hasObserved;
    private uint lastJobId;
    private int lastLevel;

    // Returns true when the caller should trigger a roster republish/refresh: i.e. the (job, level) pair
    // changed from the previously observed value AND this is not the first observation.
    public bool Register(uint jobId, int level)
    {
        if (!hasObserved)
        {
            hasObserved = true;
            lastJobId = jobId;
            lastLevel = level;
            return false;
        }

        if (jobId == lastJobId && level == lastLevel)
            return false;

        lastJobId = jobId;
        lastLevel = level;
        return true;
    }

    public void Reset()
    {
        hasObserved = false;
        lastJobId = 0;
        lastLevel = 0;
    }
}
