namespace dad.Services;

internal static class DadIpcContract
{
    public const string Prefix = "dad";

    // Review M17: bump this on any breaking change to the dad.* gate payload shapes so external
    // consumers / remote peers can detect schema drift via the dad.ApiVersion gate.
    public const int ApiVersion = 1;
    public const string Version = $"{Prefix}.ApiVersion";

    public const string IsReady = $"{Prefix}.IsReady";
    public const string GetStatus = $"{Prefix}.GetStatus";
    public const string GetLeaderStatus = $"{Prefix}.GetLeaderStatus";
    public const string GetParticipantStatusSnapshot = $"{Prefix}.GetParticipantStatusSnapshot";
    public const string GetLanPartyPresets = $"{Prefix}.GetLanPartyPresets";
    public const string GetRosterPreview = $"{Prefix}.GetRosterPreview";
    public const string GetPlannerGroups = $"{Prefix}.GetPlannerGroups";
    public const string GetPlannerGroupPreview = $"{Prefix}.GetPlannerGroupPreview";
    public const string GetSchedulerPreview = $"{Prefix}.GetSchedulerPreview";
    public const string StartSchedulerPreset = $"{Prefix}.StartSchedulerPreset";
    public const string GetLaunchProfiles = $"{Prefix}.GetLaunchProfiles";
    public const string GetProfileCatalog = $"{Prefix}.GetProfileCatalog";
    public const string UpdateProfile = $"{Prefix}.UpdateProfile";
    public const string GetAccountDirectory = $"{Prefix}.GetAccountDirectory";
    public const string UpdateLaunchProfile = $"{Prefix}.UpdateLaunchProfile";
    public const string GetWorkerExecutionStatus = $"{Prefix}.GetWorkerExecutionStatus";
    public const string GetRosterCatalog = $"{Prefix}.GetRosterCatalog";
    public const string RefreshPeerRosterCatalog = $"{Prefix}.RefreshPeerRosterCatalog";
    public const string SetRosterVisibility = $"{Prefix}.SetRosterVisibility";
    public const string ChangeRosterAssignment = $"{Prefix}.ChangeRosterAssignment";
    public const string EnqueueRosterUpdate = $"{Prefix}.EnqueueRosterUpdate";
    public const string GetCrewStatus = $"{Prefix}.GetCrewStatus";
    public const string GetSchedulerQueue = $"{Prefix}.GetSchedulerQueue";
    public const string EnqueueScheduledPreset = $"{Prefix}.EnqueueScheduledPreset";
    public const string CancelScheduledJob = $"{Prefix}.CancelScheduledJob";
    public const string GetSchedules = $"{Prefix}.GetSchedules";
    public const string StartSchedule = $"{Prefix}.StartSchedule";
    public const string CancelSchedule = $"{Prefix}.CancelSchedule";
    public const string GetModuleCapabilities = $"{Prefix}.GetModuleCapabilities";
    public const string GetSupportedJobHints = $"{Prefix}.GetSupportedJobHints";
    // Review M17: StartRun is an alias of StartTasks (same handler/payload) kept for back-compat.
    public const string StartTasks = $"{Prefix}.StartTasks";
    public const string StartRun = $"{Prefix}.StartRun";
    public const string StartPlannerGroup = $"{Prefix}.StartPlannerGroup";
    public const string CancelActiveRun = $"{Prefix}.CancelActiveRun";
    public const string OnRunStatusChanged = $"{Prefix}.OnRunStatusChanged";
}
