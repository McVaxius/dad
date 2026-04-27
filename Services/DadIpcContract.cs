namespace dad.Services;

internal static class DadIpcContract
{
    public const string Prefix = "dad";
    public const string IsReady = $"{Prefix}.IsReady";
    public const string GetStatus = $"{Prefix}.GetStatus";
    public const string GetLeaderStatus = $"{Prefix}.GetLeaderStatus";
    public const string GetParticipantStatusSnapshot = $"{Prefix}.GetParticipantStatusSnapshot";
    public const string GetLanPartyPresets = $"{Prefix}.GetLanPartyPresets";
    public const string GetRosterPreview = $"{Prefix}.GetRosterPreview";
    public const string GetPlannerGroups = $"{Prefix}.GetPlannerGroups";
    public const string GetPlannerGroupPreview = $"{Prefix}.GetPlannerGroupPreview";
    public const string GetModuleCapabilities = $"{Prefix}.GetModuleCapabilities";
    public const string GetSupportedJobHints = $"{Prefix}.GetSupportedJobHints";
    public const string StartTasks = $"{Prefix}.StartTasks";
    public const string StartRun = $"{Prefix}.StartRun";
    public const string StartPlannerGroup = $"{Prefix}.StartPlannerGroup";
    public const string CancelActiveRun = $"{Prefix}.CancelActiveRun";
    public const string OnRunStatusChanged = $"{Prefix}.OnRunStatusChanged";
}
