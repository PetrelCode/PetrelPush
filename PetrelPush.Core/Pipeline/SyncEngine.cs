using System.Diagnostics;
using Serilog;
using PetrelPush.Core.Abstractions;
using PetrelPush.Core.Model;
using PetrelPush.Core.Scan;

namespace PetrelPush.Core.Pipeline;

/// <summary>
/// 常驻同步引擎：Watcher + 去抖 + 轮询 + 深度校验汇入同一入口，正确性由每轮 diff 保证。
/// </summary>
public sealed class SyncEngine : IAsyncDisposable
{
    private static readonly TimeSpan LoopTick = TimeSpan.FromMilliseconds(1000);

    private readonly Func<SyncProfile> _profileProvider;
    private readonly IStateStore _store;
    private readonly ILogger _logger;
    private readonly CycleRunner _runner;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _cycleGate = new(1, 1);
    private readonly object _statusGate = new();

    private long _lastProgressTimestamp;
    private int _lastProgressCompleted;

    private Task? _loop;
    private FileSystemWatcher? _watcher;
    private Debouncer? _debouncer;
    private string? _watcherRoot;
    private int _watcherDebounceMs = -1;
    private int _watcherMaxForceMs = -1;
    private IgnoreRules? _lastRules;

    private volatile bool _paused;
    private int _pending;
    private int _deepRequested;
    private bool _started;
    private bool _disposed;
    private DateTimeOffset? _lastCycleAt;
    private DateTimeOffset? _lastDeepVerifyAt;
    private EnginePhase _phase = EnginePhase.Stopped;
    private string? _lastError;
    private string? _message = "未启动";
    private int _pendingTasks;
    private int _failedTasks;

    public SyncEngine(Func<SyncProfile> profileProvider, IStateStore store, ITransferExecutor transport, ILogger logger)
    {
        _profileProvider = profileProvider;
        _store = store;
        _logger = logger;
        _runner = new CycleRunner(store, transport, logger);
        _runner.Progress += OnProgress;
        _runner.TransferProgress += OnTransferProgress;
    }

    public event EventHandler<EngineStatus>? StatusChanged;

    /// <summary>本轮传输进度（已完成 / 总数 / 当前文件字节进度），已按 100ms 节流。</summary>
    public event EventHandler<TransferProgressInfo>? TransferProgress;

    public event Action<string>? LogLine;

    public EngineStatus Status
    {
        get
        {
            lock (_statusGate)
            {
                return new EngineStatus(_phase, _lastCycleAt, _lastError, _pendingTasks, _failedTasks, _message);
            }
        }
    }

    public bool IsPaused => _paused;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started)
        {
            return;
        }

        _started = true;

        try
        {
            var meta = await _store.GetMetaAsync("last_deep_verify_at", cancellationToken).ConfigureAwait(false);
            if (DateTimeOffset.TryParse(meta, out var value))
            {
                _lastDeepVerifyAt = value;
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "读取深度校验时间失败");
        }

        UpdateStatus(EnginePhase.Idle, "启动中…");
        _loop = Task.Run(() => LoopAsync(_cts.Token), CancellationToken.None);
        RequestCycle();
    }

    public async Task StopAsync()
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        await _cts.CancelAsync().ConfigureAwait(false);
        TryRelease();

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _loop = null;
        DisposeWatcher();
        UpdateStatus(EnginePhase.Stopped, "已停止");
    }

    /// <summary>请求一次周期（去抖后的 Watcher 信号 / 轮询 / 手动）。信号不会被丢弃。</summary>
    public void RequestCycle()
    {
        if (_paused)
        {
            return;
        }

        Interlocked.Exchange(ref _pending, 1);
        TryRelease();
    }

    public void RequestDeepVerify()
    {
        Interlocked.Exchange(ref _deepRequested, 1);
        RequestCycle();
    }

    public void Pause()
    {
        _paused = true;
        UpdateStatus(EnginePhase.Paused, "已暂停");
    }

    public void Resume()
    {
        if (!_paused)
        {
            return;
        }

        _paused = false;
        _lastError = null;
        UpdateStatus(EnginePhase.Idle, "已恢复");
        RequestCycle();
    }

    /// <summary>异步手动跑一轮（CLI / UI 手动同步用，与后台周期串行）。</summary>
    public async Task<CycleResult> RunOnceAsync(bool deepVerify, CancellationToken cancellationToken = default)
    {
        var profile = _profileProvider();
        var rules = IgnoreRules.Load(profile.LocalRoot, profile.Sync, _logger);
        _lastRules = rules;
        return await RunCycleGuardedAsync(profile, rules, deepVerify, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _runner.Progress -= OnProgress;
        _runner.TransferProgress -= OnTransferProgress;
        _wake.Dispose();
        _cycleGate.Dispose();
        _cts.Dispose();
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            bool signaled;
            try
            {
                signaled = await _wake.WaitAsync(LoopTick, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (_paused)
            {
                continue;
            }

            SyncProfile profile;
            try
            {
                profile = _profileProvider();
            }
            catch (Exception ex)
            {
                UpdateStatus(EnginePhase.Error, "配置不可用", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                continue;
            }

            var now = DateTimeOffset.UtcNow;
            var deepDue = IsDeepVerifyDue(profile, now);
            var pollDue = _lastCycleAt is null
                          || now - _lastCycleAt.Value >= TimeSpan.FromSeconds(Math.Max(1, profile.Sync.PollIntervalSec));

            var retryDue = false;
            if (!signaled && !pollDue)
            {
                try
                {
                    retryDue = await _store
                        .HasRunnableTasksAsync(now, profile.Retry.MaxAttempts, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "检查重试队列失败");
                }
            }

            if (!signaled && !pollDue && !retryDue && !deepDue)
            {
                continue;
            }

            try
            {
                await DrainCyclesAsync(profile, deepDue, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SyncFatalException ex)
            {
                _logger.Error(ex, "同步中止：{Message}", ex.Message);
                _paused = true;
                UpdateStatus(EnginePhase.Error, "需要修正配置后恢复", ex.Message);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "同步周期异常");
                UpdateStatus(EnginePhase.Error, null, ex.Message);
            }
        }
    }

    private async Task DrainCyclesAsync(SyncProfile profile, bool deepDue, CancellationToken cancellationToken)
    {
        var guard = 0;
        while (guard++ < 64)
        {
            Interlocked.Exchange(ref _pending, 0);
            var deep = Interlocked.Exchange(ref _deepRequested, 0) == 1 || deepDue;
            deepDue = false;

            if (!Directory.Exists(profile.LocalRoot))
            {
                UpdateStatus(EnginePhase.Error, null, $"本地目录不存在：{profile.LocalRoot}");
                return;
            }

            EnsureWatcher(profile);

            var rules = IgnoreRules.Load(profile.LocalRoot, profile.Sync, _logger);
            _lastRules = rules;

            UpdateStatus(EnginePhase.Scanning, "扫描本地目录…");
            var result = await RunCycleGuardedAsync(profile, rules, deep, cancellationToken).ConfigureAwait(false);
            _lastCycleAt = DateTimeOffset.UtcNow;
            if (deep)
            {
                _lastDeepVerifyAt = _lastCycleAt;
            }

            await RefreshCountsAsync(cancellationToken).ConfigureAwait(false);
            UpdateStatus(
                EnginePhase.Idle,
                $"上次同步 {_lastCycleAt.Value.ToLocalTime():HH:mm:ss}：扫描 {result.ScannedFiles} 文件 / {result.ScannedDirectories} 目录，完成 {result.ExecutedTasks}，失败 {result.FailedTasks}");

            if (Volatile.Read(ref _pending) == 0)
            {
                return;
            }
        }
    }

    private async Task<CycleResult> RunCycleGuardedAsync(
        SyncProfile profile,
        IgnoreRules rules,
        bool deepVerify,
        CancellationToken cancellationToken)
    {
        await _cycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _runner.RunOnceAsync(profile, rules, deepVerify, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _cycleGate.Release();
        }
    }

    private async Task RefreshCountsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var pending = await _store.CountTasksAsync("pending", cancellationToken).ConfigureAwait(false);
            var failed = await _store.CountTasksAsync("failed", cancellationToken).ConfigureAwait(false);
            lock (_statusGate)
            {
                _pendingTasks = pending;
                _failedTasks = failed;
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "刷新任务计数失败");
        }
    }

    private bool IsDeepVerifyDue(SyncProfile profile, DateTimeOffset now)
    {
        var days = Math.Max(1, profile.Sync.DeepVerifyIntervalDays);
        return _lastDeepVerifyAt is null || now - _lastDeepVerifyAt.Value >= TimeSpan.FromDays(days);
    }

    private void EnsureWatcher(SyncProfile profile)
    {
        if (!Directory.Exists(profile.LocalRoot))
        {
            DisposeWatcher();
            return;
        }

        var root = Path.GetFullPath(profile.LocalRoot);
        var debounceMs = Math.Max(50, profile.Sync.DebounceMs);
        var maxForceMs = Math.Max(debounceMs, profile.Sync.MaxForceTriggerMs);

        if (_watcher is not null
            && string.Equals(_watcherRoot, root, StringComparison.OrdinalIgnoreCase)
            && _watcherDebounceMs == debounceMs
            && _watcherMaxForceMs == maxForceMs)
        {
            return;
        }

        DisposeWatcher();

        try
        {
            _watcherRoot = root;
            _watcherDebounceMs = debounceMs;
            _watcherMaxForceMs = maxForceMs;
            _debouncer = new Debouncer(
                TimeSpan.FromMilliseconds(debounceMs),
                TimeSpan.FromMilliseconds(maxForceMs),
                RequestCycle);

            _watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                               | NotifyFilters.DirectoryName
                               | NotifyFilters.LastWrite
                               | NotifyFilters.Size
                               | NotifyFilters.CreationTime,
                InternalBufferSize = 64 * 1024,
                EnableRaisingEvents = true,
            };

            _watcher.Changed += OnFileSystemEvent;
            _watcher.Created += OnFileSystemEvent;
            _watcher.Deleted += OnFileSystemEvent;
            _watcher.Renamed += OnFileSystemRenamed;
            _watcher.Error += OnWatcherError;
            _logger.Debug("已监听本地目录：{Root}", root);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "创建目录监听失败，将依赖轮询兜底：{Root}", root);
            DisposeWatcher();
        }
    }

    private void DisposeWatcher()
    {
        if (_watcher is not null)
        {
            try
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Changed -= OnFileSystemEvent;
                _watcher.Created -= OnFileSystemEvent;
                _watcher.Deleted -= OnFileSystemEvent;
                _watcher.Renamed -= OnFileSystemRenamed;
                _watcher.Error -= OnWatcherError;
                _watcher.Dispose();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "释放目录监听失败");
            }

            _watcher = null;
        }

        _debouncer?.Dispose();
        _debouncer = null;
        _watcherRoot = null;
        _watcherDebounceMs = -1;
        _watcherMaxForceMs = -1;
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        if (_paused || _watcherRoot is null)
        {
            return;
        }

        if (ShouldIgnoreWatcherPath(e.FullPath))
        {
            return;
        }

        _debouncer?.Push();
    }

    private void OnFileSystemRenamed(object sender, RenamedEventArgs e)
    {
        if (_paused || _watcherRoot is null)
        {
            return;
        }

        if (ShouldIgnoreWatcherPath(e.OldFullPath) && ShouldIgnoreWatcherPath(e.FullPath))
        {
            return;
        }

        _debouncer?.Push();
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _logger.Warning(e.GetException(), "目录监听缓冲区溢出/异常，立即触发全量扫描");
        RequestCycle();
    }

    private bool ShouldIgnoreWatcherPath(string fullPath)
    {
        var rules = _lastRules;
        if (rules is null || _watcherRoot is null)
        {
            return false;
        }

        var relative = Path.GetRelativePath(_watcherRoot, fullPath);
        if (relative == ".")
        {
            return true;
        }

        relative = PathKeys.Normalize(relative);
        var isDirectory = Directory.Exists(fullPath);

        // Watcher 冗余过滤：命中内置规则的路径直接丢弃，不触发周期
        return rules.IsBuiltinIgnored(relative, isDirectory);
    }

    private void OnProgress(CycleStage stage, string message)
    {
        var phase = stage switch
        {
            CycleStage.Scanning or CycleStage.Hashing or CycleStage.Diffing => EnginePhase.Scanning,
            _ => EnginePhase.Transferring,
        };

        UpdateStatus(phase, message);
        LogLine?.Invoke(message);
    }

    private void OnTransferProgress(TransferProgressInfo info)
    {
        // 关键节点（单个任务完成 / 全部完成 / 无任务）立即上报，中间进度按 100ms 节流，避免刷爆 UI
        var now = Stopwatch.GetTimestamp();
        var elapsedMs = (now - _lastProgressTimestamp) * 1000.0 / Stopwatch.Frequency;
        var isKeyFrame = info.Total == 0
                         || info.Completed >= info.Total
                         || info.Completed != Volatile.Read(ref _lastProgressCompleted);
        if (!isKeyFrame && elapsedMs < 100)
        {
            return;
        }

        Interlocked.Exchange(ref _lastProgressTimestamp, now);
        Interlocked.Exchange(ref _lastProgressCompleted, info.Completed);
        TransferProgress?.Invoke(this, info);
    }

    private void UpdateStatus(EnginePhase phase, string? message = null, string? error = null)
    {
        EngineStatus status;
        lock (_statusGate)
        {
            _phase = phase;
            if (message is not null)
            {
                _message = message;
            }

            if (error is not null)
            {
                _lastError = error;
            }

            status = new EngineStatus(_phase, _lastCycleAt, _lastError, _pendingTasks, _failedTasks, _message);
        }

        StatusChanged?.Invoke(this, status);
    }

    private void TryRelease()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
