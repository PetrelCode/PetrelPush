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

    public CycleRunner(IStateStore store, ITransferExecutor transport, ILogger logger)
    {
        _store = store;
        _transport = transport;
        _logger = logger;
        _hasher = new SnapshotHasher(logger);
    }

    /// <summary>阶段变化通知（用于 UI 显示"扫描中/传输中"）。</summary>
    public event Action<CycleStage, string>? Progress;

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

            var threshold = Math.Max(0, profile.Sync.LargeFileThresholdMB) * 1024L * 1024L;
            foreach (var batch in TaskOrderer.BuildStages(executable, profile.Sync.MaxParallelTransfers, threshold))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var results = await Task.WhenAll(
                    batch.Select(task => ExecuteOneAsync(task, profile, cancellationToken))).ConfigureAwait(false);
                executed += results.Count(ok => ok);
                failed += results.Count(ok => !ok);
            }
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

            return false;
        }
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
