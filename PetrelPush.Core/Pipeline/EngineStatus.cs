namespace PetrelPush.Core.Pipeline;

public enum EnginePhase
{
    Stopped,
    Idle,
    Scanning,
    Transferring,
    Paused,
    Error,
}

public sealed record EngineStatus(
    EnginePhase Phase,
    DateTimeOffset? LastCycleAt,
    string? LastError,
    int PendingTasks,
    int FailedTasks,
    string? Message)
{
    public static readonly EngineStatus Stopped = new(EnginePhase.Stopped, null, null, 0, 0, "未启动");
}
