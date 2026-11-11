using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using PetrelPush.Core.Config;
using PetrelPush.Core.Model;
using PetrelPush.Core.Pipeline;
using PetrelPush.State;
using PetrelPush.Transport;

namespace PetrelPush.App;

/// <summary>同步宿主：持有状态库 / 传输层 / 引擎的生命周期，供界面遥控。</summary>
public sealed class SyncHost : IAsyncDisposable
{
    /// <summary>单个远端目录最多返回的条目数（超出需进入子目录查看）。</summary>
    public const int RemoteListLimit = 2000;

    private readonly ProfileStore _store;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    private SyncProfile _profile;
    private SqliteStateStore? _state;
    private SftpTransferExecutor? _transport;
    private SftpTransferExecutor? _browser;
    private SyncEngine? _engine;
    private bool _disposed;

    public SyncHost(ProfileStore store, SyncProfile profile)
    {
        _store = store;
        _profile = profile;
    }

    /// <summary>
    /// 日志统一带 <c>Connection</c> 上下文（连接名），界面上每行都能看出属于哪条连接；
    /// 每次写日志时按当前连接名取上下文，改名后无需重建宿主。
    /// </summary>
    private ILogger _logger => Log.ForContext("Connection", DisplayName);

    public event EventHandler<EngineStatus>? StatusChanged;

    public event EventHandler<string>? Progress;

    /// <summary>本轮传输进度（已完成 / 总数 / 当前文件字节进度）。</summary>
    public event EventHandler<TransferProgressInfo>? TransferProgress;

    public SyncProfile Profile => _profile;

    /// <summary>连接在界面上的展示名：优先配置名称，缺失时退回 id。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(_profile.ProfileName)
        ? _profile.ProfileId
        : _profile.ProfileName;

    /// <summary>连接下拉框里的显示文本：名称 + 启用状态，便于一眼看出哪些连接在运行。</summary>
    public string ListDisplayName => $"{DisplayName}（{(_profile.Enabled ? "已启用" : "已禁用")}）";

    public EngineStatus Status => _engine?.Status ?? EngineStatus.Stopped;

    public bool IsPaused => _engine?.IsPaused ?? false;

    public async Task StartAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(true);
        try
        {
            // 已禁用的连接不启动引擎，配置与快照仍保留在磁盘上
            if (!_profile.Enabled)
            {
                return;
            }

            await StartCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task RestartAsync(SyncProfile profile)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(true);
        try
        {
            await StopCoreAsync().ConfigureAwait(true);
            _profile = profile;

            if (!profile.Enabled)
            {
                StatusChanged?.Invoke(
                    this,
                    new EngineStatus(EnginePhase.Stopped, null, null, 0, 0, "连接已禁用"));
                return;
            }

            await StartCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(true);
        try
        {
            await StopCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public void RequestSync() => _engine?.RequestCycle();

    public void RequestDeepVerify() => _engine?.RequestDeepVerify();

    public void Pause() => _engine?.Pause();

    public void Resume() => _engine?.Resume();

    public async Task ResetFailedTasksAsync()
    {
        if (_state is null)
        {
            return;
        }

        await _state.ResetFailedTasksAsync(CancellationToken.None).ConfigureAwait(true);
        _engine?.RequestCycle();
    }

    /// <summary>保存配置并重建引擎（远端/本地参数变更后立即生效）。</summary>
    public async Task SaveAndRestartAsync(SyncProfile profile, string? password)
    {
        _store.Save(profile, password);
        await RestartAsync(profile).ConfigureAwait(true);
    }
    /// <summary>远端浏览是否可用：连接已建立，且主机指纹已完成确认并与当前远端地址匹配。</summary>
    public bool CanBrowseRemote => _browser is not null && _profile.Remote.HasConfirmedHostKey;

    /// <summary>远端浏览不可用时的原因说明；可用时返回 null。</summary>
    public string? RemoteBrowseHint
    {
        get
        {
            if (!_profile.Enabled)
            {
                return "该连接已禁用：请在「配置 · 连接」卡片勾选「启用此连接」后重试";
            }

            if (_browser is null)
            {
                return "远端连接未就绪：请先在「配置」页填写完整配置并保存";
            }

            if (string.IsNullOrWhiteSpace(_profile.Remote.HostKeyFingerprint))
            {
                return "主机指纹尚未确认：请先在「配置」页点「测试连接」";
            }

            return _profile.Remote.HasConfirmedHostKey
                ? null
                : "远端地址已变更：请先在「配置」页点「测试连接」重新确认主机指纹";
        }
    }

    /// <summary>读取本地快照（只读，用于文件页的“远端已知态”标记，不影响同步）。</summary>
    public async Task<IReadOnlyDictionary<string, FileEntry>> LoadSnapshotAsync(CancellationToken cancellationToken)
    {
        var state = _state;
        if (state is null)
        {
            return new Dictionary<string, FileEntry>(StringComparer.Ordinal);
        }

        // SQLite 的读取是同步实现，放到线程池执行，避免大树快照阻塞界面
        return await Task.Run(() => state.LoadEntriesAsync(cancellationToken), cancellationToken)
            .ConfigureAwait(true);
    }

    /// <summary>列出一层远端目录；使用独立连接，避免与传输任务争用连接池。</summary>
    public async Task<IReadOnlyList<RemoteEntry>> ListRemoteDirectoryAsync(
        string relativeDirectory,
        CancellationToken cancellationToken)
    {
        var browser = RequireBrowser();
        return await browser.ListDirectoryAsync(relativeDirectory, _profile, RemoteListLimit, cancellationToken)
            .ConfigureAwait(true);
    }

    /// <summary>下载远端文件到本地任意位置（只读远端，不改变同步状态）。</summary>
    public async Task<RemoteDownloadResult> DownloadRemoteFileAsync(
        string relativePath,
        string localDestinationPath,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        var browser = RequireBrowser();
        return await browser
            .DownloadFileAsync(relativePath, localDestinationPath, _profile, progress, cancellationToken)
            .ConfigureAwait(true);
    }

    /// <summary>删除远端文件，或递归删除远端目录（文件页右键用，不进入同步队列）。</summary>
    public async Task<RemoteDeleteResult> DeleteRemotePathAsync(
        string relativePath,
        bool deleteDirectory,
        CancellationToken cancellationToken)
    {
        var browser = RequireBrowser();
        return await browser
            .DeletePathAsync(relativePath, deleteDirectory, _profile, cancellationToken)
            .ConfigureAwait(true);
    }

    /// <summary>在远端当前目录下新建文件夹（文件页右键用，不进入同步队列）。</summary>
    public async Task CreateRemoteDirectoryAsync(string relativePath, CancellationToken cancellationToken)
    {
        var browser = RequireBrowser();
        await browser.CreateDirectoryAsync(relativePath, _profile, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>把本地文件直接上传到远端指定相对路径（拖拽上传，不进入同步队列）。</summary>
    public async Task<long> UploadLocalFileAsync(
        string localFilePath,
        string relativePath,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        var browser = RequireBrowser();
        return await browser
            .UploadFileAsync(localFilePath, relativePath, _profile, progress, cancellationToken)
            .ConfigureAwait(true);
    }

    private SftpTransferExecutor RequireBrowser()
    {
        if (!_profile.Enabled)
        {
            throw new InvalidOperationException("该连接已禁用：请在「配置 · 连接」卡片勾选「启用此连接」后重试");
        }

        var browser = _browser
                      ?? throw new InvalidOperationException("远端连接未就绪：请先在「配置」页填写完整配置并保存");
        if (string.IsNullOrWhiteSpace(_profile.Remote.HostKeyFingerprint))
        {
            throw new InvalidOperationException("主机指纹尚未确认：请先在「配置」页点「测试连接」");
        }

        if (!_profile.Remote.HasConfirmedHostKey)
        {
            throw new InvalidOperationException("远端地址已变更，主机指纹需重新确认：请先在「配置」页点「测试连接」");
        }

        return browser;
    }

    /// <summary>
    /// 测试连接：保存配置并用临时连接完成 TOFU 指纹确认，返回（可能新固定的）主机指纹。
    /// 调用方确认成功后再 <see cref="RestartAsync"/>，让引擎使用已确认指纹的新配置。
    /// <paramref name="trustNewHostKey"/> 仅在同一地址指纹变化且用户已显式确认时传 true。
    /// </summary>
    public async Task<string?> TestConnectionAsync(
        SyncProfile profile,
        string? password,
        bool trustNewHostKey = false)
    {
        _store.Save(profile, password);

        await using var temporary = new SftpTransferExecutor(profile, _logger);
        temporary.HostKeyTrusted += trust =>
        {
            profile.Remote.HostKeyFingerprint = trust.Fingerprint;
            profile.Remote.HostKeyTarget = trust.Target;
            _store.Save(profile);
            _logger.Information(
                "已固定远端主机指纹 {Fingerprint}（{Target}）",
                HostKeyValidator.Mask(trust.Fingerprint),
                trust.Target);
        };

        await temporary.TestConnectionAsync(profile, CancellationToken.None, trustNewHostKey).ConfigureAwait(true);
        return profile.Remote.HostKeyFingerprint;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
    }

    private async Task StartCoreAsync()
    {
        if (_engine is not null)
        {
            return;
        }

        _state = new SqliteStateStore(_store.GetStateDatabasePath(_profile.ProfileId), _logger);
        await _state.InitializeAsync(CancellationToken.None).ConfigureAwait(true);

        _transport = new SftpTransferExecutor(_profile, _logger);
        _transport.HostKeyTrusted += OnHostKeyTrusted;

        // 文件页浏览使用独立连接（单连接），避免与引擎传输争用连接槽
        _browser = new SftpTransferExecutor(_profile, _logger, poolSize: 1);
        _browser.HostKeyTrusted += OnHostKeyTrusted;

        var engine = new SyncEngine(() => _profile, _state, _transport, _logger);
        engine.StatusChanged += (_, status) => StatusChanged?.Invoke(this, status);
        engine.LogLine += message => Progress?.Invoke(this, message);
        engine.TransferProgress += (_, info) => TransferProgress?.Invoke(this, info);
        _engine = engine;

        await engine.StartAsync().ConfigureAwait(true);
    }

    private async Task StopCoreAsync()
    {
        if (_engine is not null)
        {
            await _engine.DisposeAsync().ConfigureAwait(true);
            _engine = null;
        }

        if (_browser is not null)
        {
            _browser.HostKeyTrusted -= OnHostKeyTrusted;
            await _browser.DisposeAsync().ConfigureAwait(true);
            _browser = null;
        }

        if (_transport is not null)
        {
            _transport.HostKeyTrusted -= OnHostKeyTrusted;
            await _transport.DisposeAsync().ConfigureAwait(true);
            _transport = null;
        }

        if (_state is not null)
        {
            await _state.DisposeAsync().ConfigureAwait(true);
            _state = null;
        }
    }

    private void OnHostKeyTrusted(HostKeyTrust trust)
    {
        _profile.Remote.HostKeyFingerprint = trust.Fingerprint;
        _profile.Remote.HostKeyTarget = trust.Target;
        try
        {
            _store.Save(_profile);
            _logger.Information(
                "已固定远端主机指纹 {Fingerprint}（{Target}）",
                HostKeyValidator.Mask(trust.Fingerprint),
                trust.Target);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "保存主机指纹失败");
        }
    }
}
