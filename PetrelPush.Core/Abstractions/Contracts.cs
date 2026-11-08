using PetrelPush.Core.Model;

namespace PetrelPush.Core.Abstractions;

/// <summary>单个传输任务的结果：上传时返回“本次上传流算出的哈希”，用于回写 synced_hash。</summary>
public sealed record TransferOutcome(byte[]? Hash, EntryKind Kind);

/// <summary>不可恢复的错误（认证失败、配置错误）：不重试，直接告警。</summary>
public sealed class SyncFatalException : Exception
{
    public SyncFatalException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>远端写入能力（SFTP 实现）。</summary>
public interface ITransferExecutor : IAsyncDisposable
{
    /// <summary>上传字节进度：目标相对路径、已上传字节、文件总字节。</summary>
    event Action<string, long, long>? UploadProgress;

    Task<TransferOutcome> ExecuteAsync(SyncTask task, SyncProfile profile, CancellationToken cancellationToken);

    Task TestConnectionAsync(SyncProfile profile, CancellationToken cancellationToken);

    /// <summary>向上清理空目录（受 sync.deleteEmptyDirs 控制，由周期末尾调用）。</summary>
    Task<int> CleanupEmptyDirectoriesAsync(
        SyncProfile profile,
        IReadOnlySet<string> protectedDirectories,
        CancellationToken cancellationToken);
}

/// <summary>状态库（SQLite 实现）。</summary>
public interface IStateStore : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, FileEntry>> LoadEntriesAsync(CancellationToken cancellationToken);

    /// <summary>是否存在可执行任务（pending，或 failed 且已到重试时间且未超过重试上限）。</summary>
    Task<bool> HasRunnableTasksAsync(DateTimeOffset now, int maxAttempts, CancellationToken cancellationToken);

    Task<IReadOnlyList<SyncTask>> LoadRunnableTasksAsync(DateTimeOffset now, int maxAttempts, CancellationToken cancellationToken);

    /// <summary>快照更新 + 任务入库（同一事务）。</summary>
    Task CommitScanAsync(
        IReadOnlyList<ScannedEntry> scan,
        IReadOnlyList<SyncTask> tasks,
        CancellationToken cancellationToken);

    Task MarkTaskRunningAsync(long id, CancellationToken cancellationToken);

    Task MarkTaskDoneAsync(SyncTask task, byte[]? actualHash, CancellationToken cancellationToken);

    Task MarkTaskFailedAsync(
        long id,
        int attempts,
        DateTimeOffset nextRetryAt,
        string error,
        CancellationToken cancellationToken);

    Task ResetFailedTasksAsync(CancellationToken cancellationToken);

    /// <summary>清空快照与任务队列（本地 / 远端目录变更后重建全量同步用）。</summary>
    Task ResetAsync(CancellationToken cancellationToken);

    Task<int> CountTasksAsync(string state, CancellationToken cancellationToken);

    Task<string?> GetMetaAsync(string key, CancellationToken cancellationToken);

    Task SetMetaAsync(string key, string value, CancellationToken cancellationToken);
}
