using System;
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
    private readonly ProfileStore _store;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    private SyncProfile _profile;
    private SqliteStateStore? _state;
    private SftpTransferExecutor? _transport;
    private SyncEngine? _engine;
    private bool _disposed;

    public SyncHost(ProfileStore store, SyncProfile profile, ILogger logger)
    {
        _store = store;
        _profile = profile;
        _logger = logger;
    }

    public event EventHandler<EngineStatus>? StatusChanged;

    public event EventHandler<string>? Progress;

    public SyncProfile Profile => _profile;

    public EngineStatus Status => _engine?.Status ?? EngineStatus.Stopped;

    public bool IsPaused => _engine?.IsPaused ?? false;

    public async Task StartAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(true);
        try
        {
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

    /// <summary>
    /// 测试连接：保存配置并用临时连接完成 TOFU 指纹确认，返回（可能新固定的）主机指纹。
    /// 调用方确认成功后再 <see cref="RestartAsync"/>，让引擎使用已确认指纹的新配置。
    /// </summary>
    public async Task<string?> TestConnectionAsync(SyncProfile profile, string? password)
    {
        _store.Save(profile, password);

        await using var temporary = new SftpTransferExecutor(profile, _logger);
        temporary.HostKeyTrusted += fingerprint =>
        {
            profile.Remote.HostKeyFingerprint = fingerprint;
            _store.Save(profile);
            _logger.Information("已固定远端主机指纹 {Fingerprint}", HostKeyValidator.Mask(fingerprint));
        };

        await temporary.TestConnectionAsync(profile, CancellationToken.None).ConfigureAwait(true);
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

        var engine = new SyncEngine(() => _profile, _state, _transport, _logger);
        engine.StatusChanged += (_, status) => StatusChanged?.Invoke(this, status);
        engine.LogLine += message => Progress?.Invoke(this, message);
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

    private void OnHostKeyTrusted(string fingerprint)
    {
        _profile.Remote.HostKeyFingerprint = fingerprint;
        try
        {
            _store.Save(_profile);
            _logger.Information("已固定远端主机指纹 {Fingerprint}", HostKeyValidator.Mask(fingerprint));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "保存主机指纹失败");
        }
    }
}
