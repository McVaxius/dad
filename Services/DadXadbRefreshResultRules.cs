using dad.Models;

namespace dad.Services;

internal static class DadXadbRefreshResultRules
{
    internal static bool MutationSucceeded(DadXadbStatus status, bool saveAfterRefresh)
        => status.IsReady &&
           status.LastRefreshUtc.HasValue &&
           (!saveAfterRefresh || status.LastSaveUtc.HasValue);
}
