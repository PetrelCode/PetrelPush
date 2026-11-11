using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PetrelPush.Core.Config;
using PetrelPush.Core.Model;
using PetrelPush.Core.Pipeline;
using Serilog;

namespace PetrelPush.App;

/// <summary>
/// 多连接管理：每条连接（profile）一个 <see cref="SyncHost"/>，彼此独立的本地目录、远端主机与状态库。
/// 负责加载全部连接、新建 / 删除、逐条启用停用，并把各连接状态聚合成托盘展示。
/// </summary>
public sealed class ConnectionManager : IAsyncDisposable
{
    private readonly ProfileStore _store;
    private readonly ILogger _logger;
    private readonly List<SyncHost> _hosts = new();
    private readonly HashSet<string> _persistedLogged = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public ConnectionManager(ProfileStore store, ILogger logger)
    {
        _store = store;
        _logger = logger;
        LoadAll();
    }

    /// <summary>全部连接（顺序与配置目录加载顺序一致，新建的追加在末尾）。</summary>
    public IReadOnlyList<SyncHost> Hosts => _hosts;

    /// <summary>界面当前选中的连接。</summary>
    public SyncHost Current { get; private set; } = null!;

    /// <summary>连接列表、名称或启用状态变化（新增 / 删除 / 重命名 / 启停），用于刷新界面。</summary>
    public event EventHandler? Changed;

    /// <summary>任一连接的引擎状态变化，用于聚合托盘图标。</summary>
    public event EventHandler? StateChanged;

    public int EnabledCount => _hosts.Count(host => host.Profile.Enabled);

    public void Select(SyncHost host)
    {
        if (_hosts.Contains(host))
        {
            Current = host;
        }
    }

    /// <summary>新建一条内存中的连接（首次保存时才落盘），默认禁用，避免未填完就被启动。</summary>
    public SyncHost Create()
    {
        var profile = new SyncProfile
        {
            ProfileId = CreateProfileId(),
            ProfileName = "新建连接",
            Enabled = false,
        };

        var host = AddHost(profile);
        Current = host;
        // 此时只是内存里的占位连接：取消新建会被直接丢弃，所以不写日志；
        // 真正落盘后由 <see cref="NotifyProfilePersisted"/> 补一条「新建连接」。
        RaiseChanged();
        return host;
    }

    /// <summary>
    /// 连接配置首次写入磁盘后记录一条「新建连接」。
    /// 只在配置文件真的存在、且此前没记过时才写，避免「打开弹窗点一下测试连接就多一条日志」。
    /// </summary>
    public void NotifyProfilePersisted(SyncHost host)
    {
        var profileId = host.Profile.ProfileId;
        if (_persistedLogged.Contains(profileId) || !_store.Exists(profileId))
        {
            return;
        }

        _persistedLogged.Add(profileId);
        _logger.Information("新建连接：{Profile}（{Id}）", host.DisplayName, profileId);
    }

    /// <summary>删除连接：停止引擎、释放传输层，并删掉该连接在磁盘上的配置 / 凭据 / 快照 / 日志。</summary>
    public async Task DeleteAsync(SyncHost host)
    {
        if (_hosts.Count <= 1)
        {
            throw new InvalidOperationException("至少需要保留一条连接");
        }

        if (!_hosts.Remove(host))
        {
            return;
        }

        var profileId = host.Profile.ProfileId;
        var displayName = host.DisplayName;

        try
        {
            await host.DisposeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "释放连接 {Profile} 的引擎失败", profileId);
        }

        try
        {
            var directory = _store.GetProfileDirectory(profileId);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warning(ex, "删除连接目录失败：{Profile}", profileId);
        }

        if (ReferenceEquals(Current, host))
        {
            Current = _hosts[0];
        }

        _persistedLogged.Remove(profileId);
        _logger.Information("已删除连接：{Profile}（{Id}）", displayName, profileId);
        RaiseChanged();
    }

    /// <summary>丢弃一条尚未落盘的空白连接（新建后取消）：只释放内存中的宿主，不删磁盘目录。</summary>
    public async Task DiscardAsync(SyncHost host)
    {
        if (_hosts.Count <= 1 || !_hosts.Remove(host))
        {
            return;
        }

        try
        {
            await host.DisposeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "释放空白连接失败：{Profile}", host.DisplayName);
        }

        if (ReferenceEquals(Current, host))
        {
            Current = _hosts[0];
        }

        RaiseChanged();
    }

    /// <summary>配置保存后通知界面与托盘刷新（名称、启用状态可能已变化）。</summary>
    public void NotifyProfilesChanged()
    {
        RaiseChanged();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>启用 / 禁用单条连接。启用前先校验配置，配置不完整时直接拒绝。</summary>
    public async Task SetEnabledAsync(SyncHost host, bool enabled)
    {
        var profile = host.Profile;
        if (profile.Enabled == enabled)
        {
            return;
        }

        if (enabled)
        {
            var errors = profile.Validate().ToList();
            if (errors.Count > 0)
            {
                throw new InvalidOperationException($"配置不完整，无法启用：{errors[0]}");
            }
        }

        profile.Enabled = enabled;
        _store.Save(profile);
        _logger.Information("{Action}连接：{Profile}（{Id}）", enabled ? "启用" : "禁用", host.DisplayName, profile.ProfileId);

        if (enabled)
        {
            await host.StartAsync().ConfigureAwait(true);
        }
        else
        {
            await host.StopAsync().ConfigureAwait(true);
        }

        RaiseChanged();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>应用启动后按启用状态逐个拉起引擎；配置不完整的连接跳过，不阻塞其它连接。</summary>
    public async Task StartEnabledAsync(Action<string>? report)
    {
        foreach (var host in _hosts.ToArray())
        {
            if (!host.Profile.Enabled)
            {
                continue;
            }

            var errors = host.Profile.Validate().ToList();
            if (errors.Count > 0)
            {
                _logger.Warning("连接 {Profile} 配置不完整，未启动：{Error}", host.DisplayName, errors[0]);
                report?.Invoke($"「{host.DisplayName}」未启动：{errors[0]}（请在「配置」页补全后保存）");
                continue;
            }

            try
            {
                await host.StartAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "连接 {Profile} 启动失败", host.DisplayName);
                report?.Invoke($"「{host.DisplayName}」启动失败：{ex.Message}");
            }
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RequestSyncAll()
    {
        foreach (var host in _hosts)
        {
            if (host.Profile.Enabled && host.Status.Phase != EnginePhase.Stopped)
            {
                host.RequestSync();
            }
        }
    }

    public void RequestDeepVerifyAll()
    {
        foreach (var host in _hosts)
        {
            if (host.Profile.Enabled && host.Status.Phase != EnginePhase.Stopped)
            {
                host.RequestDeepVerify();
            }
        }
    }

    /// <summary>全部启用连接都已暂停时为 true（部分暂停时点托盘会全部暂停）。</summary>
    public bool IsAllPaused
    {
        get
        {
            var active = _hosts.Where(host => host.Profile.Enabled).ToList();
            return active.Count > 0 && active.All(host => host.IsPaused);
        }
    }

    public void TogglePauseAll()
    {
        var pause = !IsAllPaused;
        foreach (var host in _hosts)
        {
            if (!host.Profile.Enabled)
            {
                continue;
            }

            if (pause)
            {
                host.Pause();
            }
            else
            {
                host.Resume();
            }
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>托盘图标状态与提示：全部禁用 → 灰，任一异常 → 红，任一传输 → 黄，否则绿。</summary>
    public (TrayState State, string Tooltip) DescribeTray()
    {
        var active = _hosts.Where(host => host.Profile.Enabled).ToList();
        if (active.Count == 0)
        {
            return (TrayState.Paused, $"PetrelPush — 共 {_hosts.Count} 条连接，全部已禁用");
        }

        var failed = active.Count(host => host.Status.Phase == EnginePhase.Error);
        if (failed > 0)
        {
            return (TrayState.Error,
                $"PetrelPush — {failed} 条连接异常（共 {active.Count} 条启用）");
        }

        if (active.Any(host => host.Status.Phase is EnginePhase.Scanning or EnginePhase.Transferring))
        {
            return (TrayState.Busy, $"PetrelPush — 同步中（{active.Count} 条启用连接）");
        }

        if (active.All(host => host.IsPaused || host.Status.Phase == EnginePhase.Stopped))
        {
            return (TrayState.Paused, $"PetrelPush — 已暂停或未运行（{active.Count} 条启用连接）");
        }

        return (TrayState.Idle, $"PetrelPush — 就绪（{active.Count} 条启用连接）");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var host in _hosts.ToArray())
        {
            try
            {
                await host.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "退出时释放连接 {Profile} 失败", host.DisplayName);
            }
        }

        _hosts.Clear();
    }

    private void LoadAll()
    {
        foreach (var id in _store.ListProfileIds())
        {
            try
            {
                AddHost(_store.Load(id));
                // 启动时已存在的连接不必再补「新建连接」日志
                _persistedLogged.Add(id);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "读取连接 {Profile} 失败，已跳过", id);
            }
        }

        if (_hosts.Count == 0)
        {
            // 首次使用：内存里放一条空白连接，用户填写并保存后才落盘
            AddHost(new SyncProfile
            {
                ProfileId = CreateProfileId(),
                ProfileName = "默认连接",
            });
        }

        Current = _hosts[0];
        _logger.Information("已加载 {Count} 条连接，其中 {Enabled} 条启用", _hosts.Count, EnabledCount);
    }

    private SyncHost AddHost(SyncProfile profile)
    {
        var host = new SyncHost(_store, profile);
        host.StatusChanged += (_, _) => StateChanged?.Invoke(this, EventArgs.Empty);
        _hosts.Add(host);
        return host;
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>生成不与现有连接（内存或磁盘）冲突的 profile id。</summary>
    private string CreateProfileId()
    {
        var baseId = $"conn-{DateTime.Now:yyyyMMdd-HHmmss}";
        var id = baseId;
        var suffix = 1;
        while (_hosts.Any(host => string.Equals(host.Profile.ProfileId, id, StringComparison.OrdinalIgnoreCase))
               || _store.Exists(id))
        {
            id = $"{baseId}-{suffix++}";
        }

        return id;
    }
}
