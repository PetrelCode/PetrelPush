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

/// <summary>单个进行中文件的传输进度：相对路径、已传字节、总字节、进度（0~1）与该文件速度（字节/秒）。</summary>
public sealed record FileTransferProgress(
    string Path,
    long Done,
    long Total,
    double Percent,
    double BytesPerSecond);

/// <summary>
/// 本轮传输进度：已完成任务数 / 本轮任务总数、聚合字节进度与聚合速度（字节/秒），
/// 以及进行中文件各自的分项进度（按开始顺序排列），供 UI 逐文件展示。
/// Total 为 0 表示本轮没有需要传输的任务。
/// </summary>
public sealed record TransferProgressInfo(
    int Completed,
    int Total,
    double Percent,
    string? CurrentFile = null,
    double BytesPerSecond = 0)
{
    /// <summary>进行中文件的分项进度，空表示当前没有活动上传。</summary>
    public IReadOnlyList<FileTransferProgress> Files { get; init; } = Array.Empty<FileTransferProgress>();

    public static readonly TransferProgressInfo Empty = new(0, 0, 0);
}

