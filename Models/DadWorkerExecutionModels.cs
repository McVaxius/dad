namespace dad.Models;

public enum DadWorkerExecutionRole
{
    QueueLeader,
    Participant,
}

public enum DadWorkerExecutionState
{
    Idle = 0,
    Accepted = 1,
    Starting = 2,
    WaitingForQueue = 3,
    Running = 4,
    Completed = 5,
    Failed = 6,
    Cancelled = 7,
    TimedOut = 8,
    Preparing = 9,
    Repairing = 10,
}

internal static class DadWorkerCommandSchemaRules
{
    public const int LegacySchema = 1;
    public const int RepairPolicySchema = 2;

    public static bool IsSupported(int schemaVersion)
        => schemaVersion is LegacySchema or RepairPolicySchema;

    public static int ResolveEmissionSchema(DadPreDutyRepairPolicy? policy)
        => policy?.Enabled == true ? RepairPolicySchema : LegacySchema;
}

public sealed class DadWorkerExecutionCommand
{
    public int SchemaVersion { get; set; } = 1;
    public string CommandId { get; set; } = Guid.NewGuid().ToString("N");
    public string RunId { get; set; } = string.Empty;
    public int ModuleIndex { get; set; }
    public DadWorkerExecutionRole Role { get; set; }
    public DadRunPlan Plan { get; set; } = new();
    public List<DadParticipantSnapshot> Participants { get; set; } = [];
    public int TimeoutSeconds { get; set; } = 1800;
}

public sealed class DadWorkerExecutionStatus
{
    public int SchemaVersion { get; set; } = 1;
    public string CommandId { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public DadWorkerSessionId WorkerSessionId { get; set; } = new(string.Empty);
    public DadWorkerExecutionRole Role { get; set; }
    public DadWorkerExecutionState State { get; set; } = DadWorkerExecutionState.Idle;
    public DadModuleId ModuleId { get; set; } = DadModuleId.None;
    public bool EnteredDuty { get; set; }
    public bool IsTerminal { get; set; }
    public bool Success { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public string Summary { get; set; } = string.Empty;
    public string FailureReason { get; set; } = string.Empty;
    public DadRunStepResultDto StepResult { get; set; } = new();

    public DadWorkerExecutionStatus Clone()
        => new()
        {
            SchemaVersion = SchemaVersion,
            CommandId = CommandId,
            RunId = RunId,
            WorkerSessionId = WorkerSessionId,
            Role = Role,
            State = State,
            ModuleId = ModuleId,
            EnteredDuty = EnteredDuty,
            IsTerminal = IsTerminal,
            Success = Success,
            UpdatedAtUtc = UpdatedAtUtc,
            Summary = Summary,
            FailureReason = FailureReason,
            StepResult = StepResult.Clone(),
        };
}

public sealed class DadWorkerExecutionCancel
{
    public int SchemaVersion { get; set; } = 1;
    public string RunId { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

public sealed class DadWorkerExecutionAck
{
    public int SchemaVersion { get; set; } = 1;
    public string CommandId { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public DadWorkerSessionId WorkerSessionId { get; set; } = new(string.Empty);
    public bool Accepted { get; set; }
    public string Summary { get; set; } = string.Empty;
    public DadWorkerExecutionStatus Status { get; set; } = new();
}
