using System.Collections.Concurrent;
using System.Diagnostics;
using Serilog;
using PetrelPush.Core.Abstractions;
using PetrelPush.Core.Diff;
using PetrelPush.Core.Model;
using PetrelPush.Core.Scan;

namespace PetrelPush.Core.Pipeline;

public enum CycleStage
{
    Scanning,
    Hashing,
    Diffing,
    Transferring,
    Cleanup,
}

/// <summary>单个同步周期：扫描 → 哈希 → diff → 落库 → 分阶段执行。</summary>
public sealed class CycleRunner
{
    private readonly IStateStore _store;
    private readonly ITransferExecutor _transport;
    private readonly ILogger _logger;
    private readonly Scanner _scanner = new();
    private readonly SnapshotHasher _hasher;
    private readonly DiffEngine _differ = new();
    private readonly ConcurrentDictionary<string, (long Done, long Total)> _activeUploads =
        new(StringComparer.Ordinal);

    private int _totalTasks;
    private int _completedTasks;
    private volatile string? _currentFile;

    // 传输速度：按文件记录上一次上报的字节数，增量累加成单调递增的发送总量，
    // 再按窗口采样做指数平滑，避免速度数字抖动
    private readonly ConcurrentDictionary<string, long> _reportedBytes = new(StringComparer.Ordinal);
    private readonly object _speedGate = new();
    private long _sentBytes;
    private long _speedSampleTimestamp;
    private long _speedSampleBytes;
    private double _bytesPerSecond;

    // 分文件速度采样与开始顺序（并发上传时按开始先后逐行展示）
    private readonly ConcurrentDictionary<string, FileSpeedSample> _fileSpeeds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _fileOrder = new(StringComparer.Ordinal);
    private int _nextFileOrder;

    private readonly record struct FileSpeedSample(long Timestamp, long Bytes, double BytesPerSecond);

    public CycleRunner(IStateStore store, ITransferExecutor transport, ILogger logger)
    {
        _store = store;
        _transport = transport;
        _logger = logger;
        _hasher = new SnapshotHasher(logger);
        _transport.UploadProgress += OnUploadProgress;
    }

    /// <summary>阶段变化通知（用于 UI 显示"扫描中/传输中"）。</summary>
    public event Action<CycleStage, string>? Progress;

    /// <summary>本轮传输进度：已完成 / 总数 / 进行中文件的字节进度。</summary>
    public event Action<TransferProgressInfo>? TransferProgress;

    public async Task<CycleResult> RunOnceAsync(
        SyncProfile profile,
        IgnoreRules rules,
        bool deepVerify,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var cycleStart = DateTimeOffset.UtcNow;

        Progress?.Invoke(CycleStage.Scanning, "扫描本地目录…");
        var scanOptions = new ScannerOptions
        {
            SyncEmptyDirs = profile.Sync.SyncEmptyDirs,
            FollowSymlinks = profile.Sync.FollowSymlinks,
        };
        var scan = await Task.Run(
            () => _scanner.Scan(profile.LocalRoot, rules, scanOptions, _logger, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        if (scan.Incomplete)
        {
            // 不完整扫描会导致“本地消失”的误判，本轮跳过 diff 与执行，等下一轮补齐
            stopwatch.Stop();
            return new CycleResult
            {
                ScannedFiles = scan.FileCount,
                ScannedDirectories = scan.DirectoryCount,
                SkippedReparsePoints = scan.Skipped.Count,
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            };
        }

        var previous = await _store.LoadEntriesAsync(cancellationToken).ConfigureAwait(false);

        Progress?.Invoke(CycleStage.Hashing, "计算内容哈希…");
        var hashStats = await _hasher.FillAsync(
            profile.LocalRoot,
            scan.Entries,
            previous,
            cycleStart.UtcTicks,
            deepVerify,
            Math.Max(1, profile.Sync.MaxParallelTransfers),
            cancellationToken).ConfigureAwait(false);

        Progress?.Invoke(CycleStage.Diffing, "比对变更集…");
        var diff = _differ.Compute(
            previous,
            scan.Entries,
            scan.NonEmptyDirectories,
            new DiffOptions(profile.Sync.SyncEmptyDirs));
        var planned = TaskOrderer.Normalize(diff.Tasks);

        if (profile.Sync.DryRun)
        {
            _logger.Information(
                "dry-run：扫描 {Files} 个文件 / {Dirs} 个目录，计划 {Tasks} 个动作（未执行）",
                scan.FileCount,
                scan.DirectoryCount,
                planned.Count);
            return new CycleResult
            {
                ScannedFiles = scan.FileCount,
                ScannedDirectories = scan.DirectoryCount,
                ChangedEntries = planned.Count,
                SkippedReparsePoints = scan.Skipped.Count,
                DryRun = true,
                PlannedTasks = planned,
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            };
        }

        if (planned.Count > 0 || diff.SnapshotDirty)
        {
            await _store.CommitScanAsync(scan.Entries, planned, cancellationToken).ConfigureAwait(false);
        }

        var executable = await _store
            .LoadRunnableTasksAsync(DateTimeOffset.UtcNow, profile.Retry.MaxAttempts, cancellationToken)
            .ConfigureAwait(false);

        var executed = 0;
        var failed = 0;
        var protectedDirectories = CollectProtectedDirectories(planned);

        if (executable.Count > 0)
        {
            Progress?.Invoke(CycleStage.Transferring, $"传输 {executable.Count} 个动作…");
            _logger.Information(
                "本轮扫描 {Files} 个文件 / {Dirs} 个目录（哈希复用 {Reused}，重算 {Computed}），待执行 {Tasks} 个动作",
                scan.FileCount,
                scan.DirectoryCount,
                hashStats.Reused,
                hashStats.Computed,
                executable.Count);

            _totalTasks = executable.Count;
            _completedTasks = 0;
            _currentFile = null;
            _activeUploads.Clear();
            _reportedBytes.Clear();
            _fileSpeeds.Clear();
            _fileOrder.Clear();
            _nextFileOrder = 0;
            _sentBytes = 0;
            _bytesPerSecond = 0;
            _speedSampleTimestamp = 0;
            _speedSampleBytes = 0;
            ReportTransferProgress();

            var threshold = Math.Max(0, profile.Sync.LargeFileThresholdMB) * 1024L * 1024L;
            foreach (var batch in TaskOrderer.BuildStages(executable, profile.Sync.MaxParallelTransfers, threshold))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var results = await Task.WhenAll(
                    batch.Select(task => ExecuteOneAsync(task, profile, cancellationToken))).ConfigureAwait(false);
                executed += results.Count(ok => ok);
                failed += results.Count(ok => !ok);

                _activeUploads.Clear();
                ReportTransferProgress();
            }
        }
        else
        {
            _totalTasks = 0;
            _completedTasks = 0;
            _activeUploads.Clear();
            ReportTransferProgress();
        }

        if (profile.Sync.DeleteEmptyDirs && planned.Any(t => t.Action == SyncAction.Delete))
        {
            Progress?.Invoke(CycleStage.Cleanup, "清理远端空目录…");
            try
            {
                var removed = await _transport
                    .CleanupEmptyDirectoriesAsync(profile, protectedDirectories, cancellationToken)
                    .ConfigureAwait(false);
                if (removed > 0)
                {
                    _logger.Debug("清理远端空目录 {Count} 个", removed);
                }
            }
            catch (SyncFatalException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "清理远端空目录失败（不影响本轮同步结果）");
            }
        }

        await _store.SetMetaAsync("last_cycle_at", DateTimeOffset.UtcNow.ToString("O"), cancellationToken)
            .ConfigureAwait(false);
        if (deepVerify)
        {
            await _store.SetMetaAsync("last_deep_verify_at", DateTimeOffset.UtcNow.ToString("O"), cancellationToken)
                .ConfigureAwait(false);
        }

        stopwatch.Stop();
        return new CycleResult
        {
            ScannedFiles = scan.FileCount,
            ScannedDirectories = scan.DirectoryCount,
            ChangedEntries = planned.Count,
            ExecutedTasks = executed,
            FailedTasks = failed,
            SkippedReparsePoints = scan.Skipped.Count,
            DryRun = false,
            PlannedTasks = planned,
            ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
        };
    }

    private void OnUploadProgress(string path, long done, long total)
    {
        _activeUploads[path] = (done, total);

        // 并发上传时多个文件交替上报，若每个回调都覆盖文件名，UI 上就会来回跳；
        // 这里只在当前显示的文件已结束（不在活动集合中）或还没有时切换，保持显示稳定
        var current = _currentFile;
        if (current is null || !_activeUploads.ContainsKey(current))
        {
            _currentFile = path;
        }

        if (!_fileOrder.ContainsKey(path))
        {
            _fileOrder.TryAdd(path, Interlocked.Increment(ref _nextFileOrder));
        }

        SampleFileSpeed(path, done);

        // 同一个文件按增量累加，重试时 done 会回退，只累加正增量
        var previous = _reportedBytes.TryGetValue(path, out var reported) ? reported : 0;
        if (done > previous)
        {
            Interlocked.Add(ref _sentBytes, done - previous);
            _reportedBytes[path] = done;
        }

        ReportTransferProgress();
    }

    /// <summary>单文件速度采样：窗口内字节增量除以耗时，再与上一值做指数平滑。</summary>
    private void SampleFileSpeed(string path, long done)
    {
        var now = Stopwatch.GetTimestamp();
        if (!_fileSpeeds.TryGetValue(path, out var sample))
        {
            _fileSpeeds[path] = new FileSpeedSample(now, done, 0);
            return;
        }

        var elapsed = (now - sample.Timestamp) / (double)Stopwatch.Frequency;
        if (elapsed < 0.3)
        {
            return;
        }

        var instant = Math.Max(0, done - sample.Bytes) / elapsed;
        var smoothed = sample.BytesPerSecond <= 0
            ? instant
            : (sample.BytesPerSecond * 0.65) + (instant * 0.35);
        _fileSpeeds[path] = new FileSpeedSample(now, done, smoothed);
    }

    /// <summary>
    /// 有进行中的上传时，进度取这些文件的合计字节进度；否则退化为已完成任务占比。
    /// 速度按最近一个采样窗口内的发送字节数计算，无进行中上传时归零。
    /// </summary>
    private void ReportTransferProgress()
    {
        var completed = Volatile.Read(ref _completedTasks);
        var total = Volatile.Read(ref _totalTasks);

        long doneBytes = 0;
        long totalBytes = 0;
        foreach (var entry in _activeUploads.Values)
        {
            doneBytes += entry.Done;
            totalBytes += entry.Total;
        }

        double percent;
        if (totalBytes > 0)
        {
            percent = Math.Clamp((double)doneBytes / totalBytes, 0, 1);
        }
        else
        {
            percent = total > 0 ? Math.Clamp((double)completed / total, 0, 1) : 0;
        }

        var speed = SampleBytesPerSecond(_activeUploads.Count > 0);
        var files = BuildActiveFiles();
        TransferProgress?.Invoke(
            new TransferProgressInfo(completed, total, percent, _currentFile, speed) { Files = files });
    }

    /// <summary>进行中文件的分项进度，按开始顺序排列，供 UI 逐文件展示。</summary>
    private IReadOnlyList<FileTransferProgress> BuildActiveFiles()
    {
        if (_activeUploads.IsEmpty)
        {
            return Array.Empty<FileTransferProgress>();
        }

        var rows = new List<(int Order, FileTransferProgress File)>(_activeUploads.Count);
        foreach (var entry in _activeUploads)
        {
            var (done, total) = entry.Value;
            var percent = total > 0 ? Math.Clamp((double)done / total, 0, 1) : 0;
            var speed = _fileSpeeds.TryGetValue(entry.Key, out var sample) ? sample.BytesPerSecond : 0;
            var order = _fileOrder.TryGetValue(entry.Key, out var seq) ? seq : int.MaxValue;
            rows.Add((order, new FileTransferProgress(entry.Key, done, total, percent, speed)));
        }

        rows.Sort(static (left, right) => left.Order.CompareTo(right.Order));

        var files = new FileTransferProgress[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            files[i] = rows[i].File;
        }

        return files;
    }

    /// <summary>采样传输速度：窗口内字节增量除以耗时，再与上一值做指数平滑。</summary>
    private double SampleBytesPerSecond(bool hasActiveUploads)
    {
        var now = Stopwatch.GetTimestamp();
        var sent = Interlocked.Read(ref _sentBytes);

        lock (_speedGate)
        {
            if (!hasActiveUploads)
            {
                _bytesPerSecond = 0;
                _speedSampleTimestamp = now;
                _speedSampleBytes = sent;
                return 0;
            }

            if (_speedSampleTimestamp == 0)
            {
                _speedSampleTimestamp = now;
                _speedSampleBytes = sent;
                return 0;
            }

            var elapsed = (now - _speedSampleTimestamp) / (double)Stopwatch.Frequency;
            if (elapsed < 0.4)
            {
                return _bytesPerSecond;
            }

            var instant = Math.Max(0, sent - _speedSampleBytes) / elapsed;
            _bytesPerSecond = _bytesPerSecond <= 0 ? instant : (_bytesPerSecond * 0.65) + (instant * 0.35);
            _speedSampleTimestamp = now;
            _speedSampleBytes = sent;
            return _bytesPerSecond;
        }
    }

    private async Task<bool> ExecuteOneAsync(SyncTask task, SyncProfile profile, CancellationToken cancellationToken)
    {
        if (task.Id > 0)
        {
            await _store.MarkTaskRunningAsync(task.Id, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var outcome = await _transport.ExecuteAsync(task, profile, cancellationToken).ConfigureAwait(false);
            if (task.Id > 0)
            {
                await _store.MarkTaskDoneAsync(task, outcome.Hash, cancellationToken).ConfigureAwait(false);
            }

            _logger.Debug("完成：{Task}", task);
            CompleteTask(task);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SyncFatalException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var attempts = task.Attempts + 1;
            var delay = RetryBackoff.Compute(attempts, profile.Retry);
            _logger.Warning(
                "任务失败（第 {Attempts} 次，{Delay} 后重试）：{Task}：{Error}",
                attempts,
                delay,
                task,
                ex.Message);
            _logger.Debug(ex, "任务失败详情：{Task}", task);

            if (task.Id > 0)
            {
                try
                {
                    await _store
                        .MarkTaskFailedAsync(task.Id, attempts, DateTimeOffset.UtcNow + delay, ex.Message, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception storeError)
                {
                    _logger.Error(storeError, "记录任务失败状态时出错：{Task}", task);
                }
            }

            CompleteTask(task);
            return false;
        }
    }

    /// <summary>
    /// 单个任务结束（无论成功失败）即计入已完成并立即上报，
    /// 避免并发批次里要等同批全部传完才跳数。
    /// </summary>
    private void CompleteTask(SyncTask task)
    {
        // 该文件已结束，先移除活动字节，进度不再按它计算
        _activeUploads.TryRemove(task.DstPath, out _);
        _fileSpeeds.TryRemove(task.DstPath, out _);
        _fileOrder.TryRemove(task.DstPath, out _);
        Interlocked.Increment(ref _completedTasks);
        ReportTransferProgress();
    }

    private static HashSet<string> CollectProtectedDirectories(IReadOnlyList<SyncTask> tasks)
    {
        var protectedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            protectedDirectories.Add(task.DstPath);
            var parent = PathKeys.Parent(task.DstPath);
            while (parent is not null && protectedDirectories.Add(parent))
            {
                parent = PathKeys.Parent(parent);
            }
        }

        return protectedDirectories;
    }
}
