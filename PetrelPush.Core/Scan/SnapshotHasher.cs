using System.Security.Cryptography;
using Serilog;
using PetrelPush.Core.Model;

namespace PetrelPush.Core.Scan;

public sealed record HashStats(int Reused, int Computed, int Failed);

/// <summary>
/// 哈希计算：mtime + size 捷径（带 racy 安全阀），流式 SHA-256（64KB 缓冲）。
/// </summary>
public sealed class SnapshotHasher
{
    private const int BufferSize = 64 * 1024;

    private readonly ILogger? _logger;

    public SnapshotHasher(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>捷径判定：尺寸与时间戳都相同、且 mtime 早于本轮起点（racy 安全阀）。</summary>
    public static bool CanReuseCache(ScannedEntry scan, FileEntry? previous, long cycleStartTicks, bool deepVerify)
    {
        if (deepVerify || previous?.Hash is null)
        {
            return false;
        }

        if (previous.Size != scan.Size || previous.MtimeTicks != scan.MtimeTicks)
        {
            return false;
        }

        // racy 安全阀：时间戳落在本轮范围内（含未来），不信任缓存
        return scan.MtimeTicks < cycleStartTicks;
    }

    public async Task<HashStats> FillAsync(
        string localRoot,
        IReadOnlyList<ScannedEntry> entries,
        IReadOnlyDictionary<string, FileEntry> previous,
        long cycleStartTicks,
        bool deepVerify,
        int parallelism,
        CancellationToken cancellationToken)
    {
        var files = new List<ScannedEntry>();
        foreach (var entry in entries)
        {
            if (entry.Kind != EntryKind.File)
            {
                continue;
            }

            previous.TryGetValue(entry.PathKey, out var old);
            if (CanReuseCache(entry, old, cycleStartTicks, deepVerify))
            {
                entry.Hash = old!.Hash;
                continue;
            }

            files.Add(entry);
        }

        var reused = entries.Count(e => e.Kind == EntryKind.File) - files.Count;
        var computed = 0;
        var failed = 0;

        if (files.Count > 0)
        {
            await Parallel.ForEachAsync(
                files,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, parallelism),
                    CancellationToken = cancellationToken,
                },
                (entry, ct) =>
                {
                    try
                    {
                        var fullPath = Path.Combine(localRoot, entry.Path.Replace('/', Path.DirectorySeparatorChar));
                        entry.Hash = HashFile(fullPath, ct);
                        Interlocked.Increment(ref computed);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failed);
                        _logger?.Warning(ex, "计算文件哈希失败：{Path}", entry.Path);
                    }

                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);
        }

        return new HashStats(reused, computed, failed);
    }

    /// <summary>流式 SHA-256。允许读写共享，尽量不因 IDE 占用而失败。</summary>
    public static byte[] HashFile(string fullPath, CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            BufferSize,
            FileOptions.SequentialScan);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }

        return hash.GetHashAndReset();
    }
}
