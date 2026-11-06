namespace PetrelPush.Core.Model;

/// <summary>同步动作。数值即执行阶段顺序：Mkdir → Rename → Upload → Delete。</summary>
public enum SyncAction
{
    Mkdir = 0,
    Rename = 1,
    Upload = 2,
    Delete = 3,
}

public static class SyncActionExtensions
{
    public static string ToDbString(this SyncAction action) => action switch
    {
        SyncAction.Mkdir => "mkdir",
        SyncAction.Rename => "rename",
        SyncAction.Upload => "upload",
        SyncAction.Delete => "delete",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    public static SyncAction Parse(string value) => value switch
    {
        "mkdir" => SyncAction.Mkdir,
        "rename" => SyncAction.Rename,
        "upload" => SyncAction.Upload,
        "delete" => SyncAction.Delete,
        _ => throw new InvalidOperationException($"未知的同步动作：{value}"),
    };

    /// <summary>执行阶段序号（直接复用枚举值）。</summary>
    public static int Stage(this SyncAction action) => (int)action;
}

/// <summary>sync_task 行（意图）。</summary>
public sealed record SyncTask
{
    public long Id { get; init; }

    public required SyncAction Action { get; init; }

    /// <summary>rename 的源远端相对路径。</summary>
    public string? SrcPath { get; init; }

    /// <summary>目标远端相对路径（对 delete 来说即待删除路径）。</summary>
    public required string DstPath { get; init; }

    /// <summary>期望内容哈希：upload 为本地内容哈希，delete 为远端已存在哈希（用于冲突消解），rename 为改名后内容哈希。</summary>
    public byte[]? WantHash { get; init; }

    public required EntryKind WantKind { get; init; }

    /// <summary>内容大小（仅内存用途，不落库）。</summary>
    public long Size { get; init; }

    public string State { get; init; } = "pending";

    public int Attempts { get; init; }

    public DateTimeOffset? NextRetryAt { get; init; }

    public string? LastError { get; init; }

    public string DstKey => PathKeys.Key(DstPath);

    public string? SrcKey => SrcPath is null ? null : PathKeys.Key(SrcPath);

    public int Depth => PathKeys.Depth(DstPath);

    public override string ToString()
    {
        return Action switch
        {
            SyncAction.Rename => $"rename {SrcPath} -> {DstPath}",
            SyncAction.Delete => $"delete {DstPath} ({WantKind})",
            SyncAction.Mkdir => $"mkdir {DstPath}",
            _ => $"upload {DstPath}",
        };
    }
}

/// <summary>一次性周期的结果摘要。</summary>
public sealed record CycleResult
{
    public static readonly CycleResult Empty = new();

    public int ScannedFiles { get; init; }

    public int ScannedDirectories { get; init; }

    public int ChangedEntries { get; init; }

    public int ExecutedTasks { get; init; }

    public int FailedTasks { get; init; }

    public int SkippedReparsePoints { get; init; }

    public bool DryRun { get; init; }

    public IReadOnlyList<SyncTask> PlannedTasks { get; init; } = Array.Empty<SyncTask>();

    public long ElapsedMilliseconds { get; init; }
}
