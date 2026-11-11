using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Serilog.Events;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using VBFileSystem = Microsoft.VisualBasic.FileIO.FileSystem;
using VBRecycleOption = Microsoft.VisualBasic.FileIO.RecycleOption;
using VBUIOption = Microsoft.VisualBasic.FileIO.UIOption;
using PetrelPush.Core.Config;
using PetrelPush.Core.Model;
using PetrelPush.Core.Pipeline;
using PetrelPush.Core.Scan;
using PetrelPush.Transport;

namespace PetrelPush.App;

public partial class MainWindow : Window
{
    private const int MaxLogLines = 4000;
    private const string DefaultIgnoreTemplate = """
        # .syncignore（优先级最高，可覆盖 .gitignore，支持 ! 重新包含）
        /bin/
        /obj/
        /dist/
        **/node_modules/
        *.log
        !keep.log
        Thumbs.db
        desktop.ini
        """;

    /// <summary>当前选中的连接（切换连接时整体替换，事件订阅成对解绑 / 绑定）。</summary>
    private SyncHost _host;

    private readonly ConnectionManager _connections;
    private readonly ProfileStore _store;
    private readonly TrayIcon _tray;
    private readonly ObservableCollection<LogEntry> _visibleLogs = new();
    private readonly List<LogEntry> _allLogs = new();

    /// <summary>配置页当前展示的连接行，用于状态变化时就地刷新行内标签（不重建列表）。</summary>
    private List<ConnectionRow> _connectionRows = new();

    private static readonly IReadOnlyDictionary<string, FileEntry> EmptySnapshot =
        new Dictionary<string, FileEntry>(StringComparer.Ordinal);

    private LogEventLevel _minLevel = LogEventLevel.Verbose;

    /// <summary>日志页的连接筛选：null 表示全部，「应用」表示应用自身日志，其余为连接名。</summary>
    private string? _connectionFilter;

    /// <summary>重建连接下拉框时屏蔽变更回调。</summary>
    private bool _suppressConnectionFilter;

    private bool _allowClose;

    // 文件页状态：两侧各自维护相对同步根的路径
    private string _localBrowsePath = string.Empty;
    private string _remoteBrowsePath = string.Empty;
    private IReadOnlyDictionary<string, FileEntry> _snapshot = EmptySnapshot;
    private IgnoreRules? _ignoreRules;
    private bool _remoteLoaded;
    private bool _remoteLoading;

    /// <summary>手动下载占用右下角进度面板，期间不被引擎传输进度覆盖。</summary>
    private bool _downloadInProgress;

    /// <summary>进度面板令牌：让旧的淡出动画失效，避免刚显示又被折叠。</summary>
    private int _transferPanelToken;

    // 进度面板里的逐文件行（按路径增量维护，避免整表重建）
    private readonly ObservableCollection<TransferRow> _transferRows = new();
    private readonly Dictionary<string, TransferRow> _transferRowIndex = new(StringComparer.Ordinal);

    // 手动下载的速度采样状态
    private long _manualSpeedTimestamp;
    private long _manualSpeedBytes;
    private double _manualBytesPerSecond;

    /// <summary>消息条令牌：连续消息时让旧的隐藏定时器失效。</summary>
    private int _messageToken;

    /// <summary>设置页回填控件时屏蔽变更回调，避免把读回的值再写一遍注册表。</summary>
    private bool _suppressAutoStartChange;

    /// <summary>设置页回填「本地终端」下拉框时屏蔽变更回调。</summary>
    private bool _suppressLocalTerminalChange;

    /// <summary>回填连接下拉框 / 启用勾选时屏蔽变更回调，避免程序化赋值触发切换或启停。</summary>
    private bool _suppressConnectionChange;

    private static readonly Geometry CheckIcon = Freeze(Geometry.Parse("M2,6.5 L5,9.5 L10,3.5"));

    private static readonly Geometry AlertIcon = Freeze(Geometry.Parse("M6,2 V7.2 M6,10.2 V10.3"));

    private static readonly Geometry SyncIcon = Freeze(
        Geometry.Parse("M4.2,4.4 V9.6 M2.8,8.2 L4.2,9.6 L5.6,8.2 M7.8,7.6 V2.4 M6.4,3.8 L7.8,2.4 L9.2,3.8"));

    private static readonly Geometry DownloadIcon = Freeze(
        Geometry.Parse("M6,2 V9.2 M3.2,6.4 L6,9.2 L8.8,6.4 M2.6,10.8 H9.4"));

    private static Geometry Freeze(Geometry geometry)
    {
        geometry.Freeze();
        return geometry;
    }

    public MainWindow(
        ConnectionManager connections,
        ProfileStore store,
        TrayIcon tray,
        UiLogSink sink,
        string initialPage = "status")
    {
        InitializeComponent();

        // 顶栏是深色渐变，标题栏也跟着切成深色
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);

        _connections = connections;
        _host = connections.Current;
        _store = store;
        _tray = tray;

        StatusLogList.ItemsSource = _visibleLogs;
        LogList.ItemsSource = _visibleLogs;
        TransferList.ItemsSource = _transferRows;

        AttachHost(_host);
        _connections.Changed += OnConnectionsChanged;
        _connections.StateChanged += OnConnectionsStateChanged;
        sink.LogEmitted += OnLogEmitted;

        Closing += OnClosing;

        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        HeaderVersionText.Text = $"v{version}";
        AboutVersionText.Text = $"v{version}";
        Title = $"PetrelPush {version}";
        AboutOsText.Text = $"Windows · {(Environment.Is64BitOperatingSystem ? "x64" : "x86")}";
        AboutRuntimeText.Text = $".NET {Environment.Version}（自包含）";

        RefreshConnectionList();
        LoadProfileToUi();
        ApplyStatus(_host.Status);
        UpdateEmptyLogHint();
        ApplyInitialPage(initialPage);
    }

    /// <summary>绑定当前连接的事件（切换连接时与 <see cref="DetachHost"/> 成对调用）。</summary>
    private void AttachHost(SyncHost host)
    {
        host.StatusChanged += OnStatusChanged;
        host.Progress += OnProgress;
        host.TransferProgress += OnTransferProgress;
    }

    private void DetachHost(SyncHost host)
    {
        host.StatusChanged -= OnStatusChanged;
        host.Progress -= OnProgress;
        host.TransferProgress -= OnTransferProgress;
    }

    /// <summary>连接列表 / 启用状态变化后刷新下拉框与勾选（可能来自后台线程）。</summary>
    private void OnConnectionsChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnConnectionsChanged(sender, e)));
            return;
        }

        RefreshConnectionList();
    }

    /// <summary>
    /// 任一连接的状态变化：只让行内状态标签重新取值，不重建列表——
    /// 重建会丢掉当前选中项并让滚动位置跳动。
    /// </summary>
    private void OnConnectionsStateChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnConnectionsStateChanged(sender, e)));
            return;
        }

        foreach (var row in _connectionRows)
        {
            row.Refresh();
        }
    }

    /// <summary>连接列表：每行一条连接，当前连接与启用状态一目了然。</summary>
    private void RefreshConnectionList()
    {
        var currentId = _host.Profile.ProfileId;

        _suppressConnectionChange = true;
        try
        {
            var rows = _connections.Hosts.Select(host => new ConnectionRow(host)).ToList();
            _connectionRows = rows;
            ConnectionListBox.ItemsSource = rows;
            ConnectionListBox.SelectedItem = rows.FirstOrDefault(
                                                   row => string.Equals(row.Host.Profile.ProfileId, currentId, StringComparison.Ordinal))
                                               ?? rows.FirstOrDefault();
        }
        finally
        {
            _suppressConnectionChange = false;
        }

        ConnectionEmptyHint.Visibility = _connections.Hosts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateConnectionUi();
        RefreshLogConnectionOptions();
    }

    /// <summary>状态页与配置页头部的连接计数（只更新文本，不碰勾选状态）。</summary>
    private void UpdateConnectionUi()
    {
        ConnectionsSummaryText.Text = $"共 {_connections.Hosts.Count} 条连接，{_connections.EnabledCount} 条启用";
        ConnectionCountText.Text = $"共 {_connections.Hosts.Count} 条 · 启用 {_connections.EnabledCount} 条";
    }

    private void OnConnectionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressConnectionChange || ConnectionListBox.SelectedItem is not ConnectionRow row)
        {
            return;
        }

        SwitchConnection(row.Host);
    }

    /// <summary>切换当前连接：解绑旧宿主事件、换上新宿主并重载各页面状态。</summary>
    private void SwitchConnection(SyncHost host)
    {
        if (!ReferenceEquals(host, _host))
        {
            DetachHost(_host);
            _host = host;
            _connections.Select(host);
            AttachHost(host);
        }

        ReloadCurrentConnection();

        // 若正停留在文件页 / 忽略页，立即按新连接重新加载
        if (PageFiles.Visibility == Visibility.Visible)
        {
            OnFilesPageShown();
        }
        else if (PageIgnore.Visibility == Visibility.Visible)
        {
            LoadIgnoreEditor(host.Profile);
        }
    }

    /// <summary>按当前宿主重新加载界面（切换连接，或在弹窗里保存 / 测试连接后调用）。</summary>
    private void ReloadCurrentConnection()
    {
        LoadProfileToUi();
        ApplyStatus(_host.Status);
        SetStatusHint(_host.Status.LastError);
        ApplyPauseButton();

        // 文件页属于单连接状态：路径、快照与远端缓存全部从头来
        _localBrowsePath = string.Empty;
        InvalidateFilePage();
        UpdateConnectionUi();
    }

    /// <summary>列表行上的「启用」勾选：逐条启停，不需要先切换当前连接。</summary>
    private async void OnConnectionRowEnabledClick(object sender, RoutedEventArgs e)
    {
        if (_suppressConnectionChange || sender is not CheckBox { Tag: ConnectionRow row } checkBox)
        {
            return;
        }

        var enabled = checkBox.IsChecked == true;
        if (enabled == row.Host.Profile.Enabled)
        {
            return;
        }

        try
        {
            await _connections.SetEnabledAsync(row.Host, enabled);
            ShowResult(
                ConnectionResultText,
                enabled
                    ? $"已启用「{row.Host.DisplayName}」，同步引擎已启动"
                    : $"已禁用「{row.Host.DisplayName}」，同步引擎已停止",
                enabled ? ResultSuccess : ResultIdle);
        }
        catch (Exception ex)
        {
            ShowResult(ConnectionResultText, $"「{row.Host.DisplayName}」{ex.Message}", ResultError);
        }

        RefreshConnectionList();
        ApplyStatus(_host.Status);
    }

    private async void OnNewConnection(object sender, RoutedEventArgs e)
    {
        var host = _connections.Create();
        SwitchConnection(host);
        RefreshConnectionList();
        await OpenConnectionEditorAsync(host);
    }

    private async void OnEditConnection(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ConnectionRow row })
        {
            return;
        }

        SwitchConnection(row.Host);
        RefreshConnectionList();
        await OpenConnectionEditorAsync(row.Host);
    }

    /// <summary>列表行的「同步参数」：弹出独立的同步参数窗口，只改该连接的 sync 段。</summary>
    private void OnEditSyncOptions(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ConnectionRow row })
        {
            return;
        }

        var editor = new SyncOptionsWindow(row.Host) { Owner = this };
        if (editor.ShowDialog() == true && editor.Saved)
        {
            ShowResult(
                ConnectionResultText,
                $"「{row.Host.DisplayName}」同步参数已保存并应用",
                ResultSuccess);

            if (ReferenceEquals(row.Host, _host))
            {
                ApplyStatus(_host.Status);
            }
        }

        RefreshConnectionList();
    }

    /// <summary>打开连接配置弹窗：保存后刷新列表；取消则丢弃尚未落盘的空白连接。</summary>
    private async Task OpenConnectionEditorAsync(SyncHost host)
    {
        var editor = new ConnectionEditorWindow(host, _store, Serilog.Log.Logger) { Owner = this };
        editor.ShowDialog();

        if (editor.Saved)
        {
            ShowResult(ConnectionResultText, $"「{host.DisplayName}」配置已保存", ResultSuccess);
        }
        else if (!editor.Persisted)
        {
            // 新建后直接取消：这条连接从未落盘，直接丢掉，避免留下空白项
            await _connections.DiscardAsync(host);
            ShowResult(ConnectionResultText, "已取消：未保存的新连接已丢弃", ResultIdle);
        }

        // 配置确实写盘了才记一条「新建连接」：仅打开弹窗、点测试连接而没保存的不算
        _connections.NotifyProfilePersisted(host);

        _connections.NotifyProfilesChanged();
        SwitchConnection(_connections.Current);
        RefreshConnectionList();
    }

    private async void OnDeleteConnection(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ConnectionRow row })
        {
            return;
        }

        if (_connections.Hosts.Count <= 1)
        {
            ShowResult(ConnectionResultText, "至少需要保留一条连接", ResultError);
            return;
        }

        var host = row.Host;
        var answer = MessageBox.Show(
            this,
            $"删除连接「{host.DisplayName}」？{Environment.NewLine}{Environment.NewLine}"
            + "该连接的配置、凭据、同步快照与日志目录都会被删除，操作不可恢复。",
            "删除连接",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _connections.DeleteAsync(host);
            SwitchConnection(_connections.Current);
            RefreshConnectionList();
            ShowResult(ConnectionResultText, "连接已删除", ResultIdle);
        }
        catch (Exception ex)
        {
            ShowResult(ConnectionResultText, $"删除失败：{ex.Message}", ResultError);
        }
    }

    /// <summary>连接列表行：只做展示，配置本身始终以 <see cref="SyncHost.Profile"/> 为准。</summary>
    private sealed class ConnectionRow : INotifyPropertyChanged
    {
        public ConnectionRow(SyncHost host)
        {
            Host = host;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public SyncHost Host { get; }

        public string Name => Host.DisplayName;

        public bool Enabled => Host.Profile.Enabled;

        /// <summary>启用状态标签：勾选框之外再给一句可读文字，禁用时一眼能看出这条连接没在跑。</summary>
        public string EnabledText => Enabled ? "已启用" : "已禁用";

        public Brush EnabledForeground => StateBrush(Enabled ? "StateSuccess" : "StateIdle", soft: false);

        public Brush EnabledBackground => StateBrush(Enabled ? "StateSuccess" : "StateIdle", soft: true);

        /// <summary>
        /// 传输 / 运行状态标签：禁用的连接给「未运行」，已启用但引擎还没起来的给「待启动」，
        /// 其余跟随引擎阶段（已同步 / 扫描中 / 传输中 / 已暂停 / 异常）。
        /// </summary>
        public string PhaseText => !Enabled
            ? "未运行"
            : Host.Status.Phase == EnginePhase.Stopped
                ? "待启动"
                : PhaseBadgeText(Host.Status.Phase);

        public Brush PhaseForeground =>
            StateBrush(Enabled ? PhaseStateKey(Host.Status.Phase) : "StateIdle", soft: false);

        public Brush PhaseBackground =>
            StateBrush(Enabled ? PhaseStateKey(Host.Status.Phase) : "StateIdle", soft: true);

        /// <summary>引擎状态变化后刷新行内标签；空属性名表示所有绑定属性都要重新取值。</summary>
        public void Refresh() =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

        private static Brush StateBrush(string key, bool soft) =>
            (Brush)Application.Current.FindResource(soft ? $"{key}SoftBrush" : $"{key}Brush");

        public string Detail
        {
            get
            {
                var profile = Host.Profile;
                var local = string.IsNullOrWhiteSpace(profile.LocalRoot)
                    ? "（未设置本地目录）"
                    : profile.LocalRoot;
                var remoteRoot = string.IsNullOrWhiteSpace(profile.RemoteRoot)
                    ? "（未设置远端目录）"
                    : profile.RemoteRoot;
                var remote = string.IsNullOrWhiteSpace(profile.Remote.Host)
                    ? "（未设置主机）"
                    : $"{profile.Remote.User}@{profile.Remote.Host}:{profile.Remote.Port}";
                return $"{local}  →  {remote}  {remoteRoot}";
            }
        }
    }

    private void ApplyInitialPage(string page)
    {
        switch (page)
        {
            case "config":
                NavConfig.IsChecked = true;
                ShowPage("config");
                break;
            case "files":
                NavFiles.IsChecked = true;
                ShowPage("files");
                break;
            case "ignore":
                NavIgnore.IsChecked = true;
                ShowPage("ignore");
                break;
            case "log":
                NavLog.IsChecked = true;
                ShowPage("log");
                break;
            case "settings":
                NavSettings.IsChecked = true;
                ShowPage("settings");
                break;
            case "help":
                NavHelp.IsChecked = true;
                ShowPage("help");
                break;
            case "about":
                NavHelp.IsChecked = true;
                ShowPage("help");
                HelpNavAbout.IsChecked = true;
                ShowHelpPage("about");
                break;
            default:
                NavStatus.IsChecked = true;
                ShowPage("status");
                break;
        }
    }

    private void OnNavigate(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag })
        {
            ShowPage(tag);
        }
    }

    private void ShowPage(string tag)
    {
        PageStatus.Visibility = tag == "status" ? Visibility.Visible : Visibility.Collapsed;
        PageConfig.Visibility = tag == "config" ? Visibility.Visible : Visibility.Collapsed;
        PageFiles.Visibility = tag == "files" ? Visibility.Visible : Visibility.Collapsed;
        PageIgnore.Visibility = tag == "ignore" ? Visibility.Visible : Visibility.Collapsed;
        PageLog.Visibility = tag == "log" ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
        PageHelp.Visibility = tag == "help" ? Visibility.Visible : Visibility.Collapsed;

        if (tag == "files")
        {
            OnFilesPageShown();
        }
    }

    private void OnHelpNavigate(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag })
        {
            ShowHelpPage(tag);
        }
    }

    private void ShowHelpPage(string tag)
    {
        HelpDocPanel.Visibility = tag == "doc" ? Visibility.Visible : Visibility.Collapsed;
        HelpAboutPanel.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
    }

    public void TogglePause()
    {
        if (_host.IsPaused)
        {
            _host.Resume();
        }
        else
        {
            _host.Pause();
        }

        ApplyPauseButton();
    }

    /// <summary>运行中显示红色「暂停」，暂停中显示绿色「恢复」，与右上角徽章配色一致。</summary>
    private void ApplyPauseButton()
    {
        var paused = _host.IsPaused;
        PauseButton.Content = paused ? "恢复" : "暂停";
        PauseButton.Style = (Style)FindResource(paused ? "SuccessButton" : "DangerButton");
    }

    /// <summary>
    /// 状态页的错误行：有内容才占位，空文本时整行折叠——
    /// 否则空 TextBlock 仍占一行行高，会把动作区与日志卡之间撑出多余的空白。
    /// </summary>
    private void SetStatusHint(string? message)
    {
        var text = message?.Trim() ?? string.Empty;
        StatusHintText.Text = text;
        StatusHintText.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    public void ShowHint(string message)
    {
        SetStatusHint(message);
        StatusBarText.Text = message;
    }

    public void AllowClose()
    {
        _allowClose = true;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
        _tray.ShowBalloon("PetrelPush", "窗口已隐藏到托盘，同步继续在后台运行");
    }

    private void OnStatusChanged(object? sender, EngineStatus status)
    {
        // 切换连接后可能还有旧宿主排队的回调，只认当前连接
        if (!ReferenceEquals(sender, _host))
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (ReferenceEquals(sender, _host))
            {
                ApplyStatus(status);
            }
        }));
    }

    private void OnProgress(object? sender, string message)
    {
        if (!ReferenceEquals(sender, _host))
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (ReferenceEquals(sender, _host))
            {
                StatusBarText.Text = message;
            }
        }));
    }

    private void OnTransferProgress(object? sender, TransferProgressInfo info)
    {
        if (!ReferenceEquals(sender, _host))
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ReferenceEquals(sender, _host))
                {
                    ApplyTransferProgress(info);
                }
            }));
            return;
        }

        ApplyTransferProgress(info);
    }

    /// <summary>右下角进度面板：本轮汇总 + 每个进行中文件一行的进度与速度。</summary>
    private void ApplyTransferProgress(TransferProgressInfo info)
    {
        // 手动下载期间面板由下载进度占用，引擎进度只更新、不抢显示
        if (_downloadInProgress)
        {
            return;
        }

        var files = info.Files ?? Array.Empty<FileTransferProgress>();
        if (info.Total <= 0 && files.Count == 0)
        {
            HideTransferPanel();
            return;
        }

        ShowTransferPanel("传输中", SyncIcon);
        UpdateTransferSummary(info.Completed, info.Total);
        SyncTransferRows(files);
    }

    private void UpdateTransferSummary(int completed, int total)
    {
        TransferCountText.Text = total > 0 ? $"{completed}/{total}" : "—";
    }

    /// <summary>按文件路径增量同步行：不重建集合，避免卡片闪烁；顺序沿用引擎上报的开始顺序。</summary>
    private void SyncTransferRows(IReadOnlyList<FileTransferProgress> files)
    {
        for (var i = _transferRows.Count - 1; i >= 0; i--)
        {
            var row = _transferRows[i];
            var stillActive = false;
            foreach (var file in files)
            {
                if (string.Equals(file.Path, row.Path, StringComparison.Ordinal))
                {
                    stillActive = true;
                    break;
                }
            }

            if (stillActive)
            {
                continue;
            }

            _transferRowIndex.Remove(row.Path);
            _transferRows.RemoveAt(i);
        }

        foreach (var file in files)
        {
            if (!_transferRowIndex.TryGetValue(file.Path, out var row))
            {
                row = new TransferRow(file.Path);
                _transferRowIndex[file.Path] = row;
                _transferRows.Add(row);
            }

            row.Update(file.Percent * 100, file.BytesPerSecond);
        }
    }

    /// <summary>手动传输（下载）的速度采样：字节增量除以耗时，再做指数平滑。</summary>
    private double SampleManualSpeed(long bytes, bool reset = false)
    {
        var now = Stopwatch.GetTimestamp();
        if (reset || _manualSpeedTimestamp == 0)
        {
            _manualSpeedTimestamp = now;
            _manualSpeedBytes = bytes;
            _manualBytesPerSecond = 0;
            return 0;
        }

        var elapsed = (now - _manualSpeedTimestamp) / (double)Stopwatch.Frequency;
        if (elapsed < 0.3)
        {
            return _manualBytesPerSecond;
        }

        var instant = Math.Max(0, bytes - _manualSpeedBytes) / elapsed;
        _manualBytesPerSecond = _manualBytesPerSecond <= 0
            ? instant
            : (_manualBytesPerSecond * 0.65) + (instant * 0.35);
        _manualSpeedTimestamp = now;
        _manualSpeedBytes = bytes;
        return _manualBytesPerSecond;
    }

    /// <summary>展开右下角进度面板；从隐藏态出现时淡入。</summary>
    private void ShowTransferPanel(string title, Geometry icon)
    {
        TransferTitleText.Text = title;
        TransferIconPath.Data = icon;
        if (TransferPanel.Visibility == Visibility.Visible)
        {
            return;
        }

        _transferPanelToken++;
        TransferPanel.Visibility = Visibility.Visible;
        TransferPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));
    }

    /// <summary>收起右下角进度面板：淡出结束后再折叠，避免闪断。</summary>
    private void HideTransferPanel()
    {
        if (TransferPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        var token = ++_transferPanelToken;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
        fade.Completed += (_, _) =>
        {
            if (token == _transferPanelToken)
            {
                TransferPanel.Visibility = Visibility.Collapsed;
            }
        };
        TransferPanel.BeginAnimation(OpacityProperty, fade);
    }

    private void ApplyStatus(EngineStatus status)
    {
        ApplyPhaseBadge(status.Phase);

        // 离开传输阶段后收起进度面板（手动下载期间由下载进度占用）
        if (!_downloadInProgress && status.Phase != EnginePhase.Transferring)
        {
            HideTransferPanel();
        }

        if (status.LastCycleAt is null)
        {
            LastCycleText.Text = "—";
            LastCycleText.ToolTip = null;
        }
        else
        {
            LastCycleText.Text = status.LastCycleAt.Value.LocalDateTime.ToString("MM-dd HH:mm:ss");
            LastCycleText.ToolTip = status.LastCycleAt.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
        }

        PendingText.Text = status.PendingTasks.ToString();
        PendingText.Foreground = status.PendingTasks > 0
            ? (Brush)FindResource("BrandBrush")
            : (Brush)FindResource("TextPrimaryBrush");
        FailedText.Text = status.FailedTasks.ToString();
        FailedText.Foreground = status.FailedTasks > 0
            ? (Brush)FindResource("StateErrorBrush")
            : (Brush)FindResource("TextPrimaryBrush");

        if (!string.IsNullOrWhiteSpace(status.Message))
        {
            StatusBarText.Text = status.Message;
        }

        if (!string.IsNullOrWhiteSpace(status.LastError))
        {
            SetStatusHint(status.LastError);
        }
        else if (status.Phase == EnginePhase.Idle)
        {
            SetStatusHint(null);
        }

        ApplyPauseButton();
        UpdateConnectionUi();
    }

    private void ApplyPhaseBadge(EnginePhase phase)
    {
        // 顶栏是深色底，状态胶囊用半透明白底 + 对应状态的亮色文字
        var (text, foregroundKey) = phase switch
        {
            EnginePhase.Idle => ("已同步", "BadgeSuccessBrush"),
            EnginePhase.Scanning => ("扫描中", "BadgeWarningBrush"),
            EnginePhase.Transferring => ("传输中", "BadgeWarningBrush"),
            EnginePhase.Paused => ("已暂停", "BadgeErrorBrush"),
            EnginePhase.Error => ("异常", "BadgeErrorBrush"),
            _ => ("已停止", "BadgeIdleBrush"),
        };

        // 右上角只显示状态文字，传输进度统一在右下角面板展示
        PhaseText.Text = text;
        PhaseText.Foreground = (Brush)FindResource(foregroundKey);
        PhaseDot.Fill = (Brush)FindResource(foregroundKey);
    }

    /// <summary>手动下载占用右下角面板：标题为下载中，行内是文件名与进度。</summary>
    private void EnterDownloadPanel(string fileName)
    {
        _downloadInProgress = true;
        SampleManualSpeed(0, reset: true);
        ShowTransferPanel("下载中", DownloadIcon);
        UpdateTransferSummary(0, 0);
        SyncTransferRows(new[] { new FileTransferProgress(fileName, 0, 0, 0, 0) });
    }

    private void UpdateDownloadPanel(string fileName, double percent, long bytes)
    {
        var speed = SampleManualSpeed(bytes);
        UpdateTransferSummary(0, 0);
        SyncTransferRows(new[] { new FileTransferProgress(fileName, bytes, 0, percent, speed) });
    }

    private void ExitDownloadPanel()
    {
        _downloadInProgress = false;
        _transferRows.Clear();
        _transferRowIndex.Clear();
        HideTransferPanel();
    }

    private void LoadProfileToUi()
    {
        var profile = _host.Profile;

        ProfileHeader.Text = string.IsNullOrWhiteSpace(profile.ProfileName)
            ? profile.ProfileId
            : $"{profile.ProfileName}（{profile.ProfileId}）";

        // 摘要卡的第二行：一眼看清「本地目录 → 远端目标与远端目录」
        var remote = profile.Remote;
        ProfileDetailText.Text = string.IsNullOrWhiteSpace(profile.LocalRoot)
            ? "尚未配置本地目录与远端目标"
            : $"{profile.LocalRoot}  →  {remote.User}@{remote.Host}:{remote.Port}  {profile.RemoteRoot}";

        LoadSettingsUi(profile);
        LoadIgnoreEditor(profile);
    }

    /// <summary>设置页只承载应用级偏好：从 profile 读回控件，并刷新存储路径展示。</summary>
    private void LoadSettingsUi(SyncProfile profile)
    {
        _suppressAutoStartChange = true;
        try
        {
            AutoStartSettingCheck.IsChecked = profile.Sync.AutoStart;
        }
        finally
        {
            _suppressAutoStartChange = false;
        }

        SettingsResultText.Text = string.Empty;
        SettingsProfilePathText.Text = _store.GetProfileDirectory(profile.ProfileId);
        // 多连接共用一份日志：显示实际写入目录，而不是当前连接的（可能为空）目录
        SettingsLogPathText.Text = LoggingSetup.LogDirectory;
        LoadLocalTerminalOptions(profile);
    }

    /// <summary>设置页「本地终端」：枚举本机可用的 shell，并回填已保存的偏好。</summary>
    private void LoadLocalTerminalOptions(SyncProfile profile)
    {
        var saved = LocalShells.NormalizeKey(profile.Sync.LocalTerminal);
        var missing = false;

        _suppressLocalTerminalChange = true;
        try
        {
            LocalTerminalCombo.Items.Clear();
            LocalTerminalCombo.Items.Add(new ComboBoxItem
            {
                Content = LocalShells.AutoDisplayName,
                Tag = LocalShells.AutoKey,
            });

            foreach (var shell in LocalShells.Detect())
            {
                LocalTerminalCombo.Items.Add(new ComboBoxItem { Content = shell.Name, Tag = shell.Key });
            }

            // 本机没有的项不列出来（不可选），直接回落到「自动」并由提示说明
            var selected = FindLocalTerminalItem(saved);
            missing = selected is null;
            if (missing)
            {
                selected = FindLocalTerminalItem(LocalShells.AutoKey);
            }

            LocalTerminalCombo.SelectedItem = selected;
        }
        finally
        {
            _suppressLocalTerminalChange = false;
        }

        UpdateLocalTerminalHint(missing ? LocalShells.AutoKey : saved, missing ? saved : null);
    }

    private ComboBoxItem? FindLocalTerminalItem(string key)
    {
        foreach (var item in LocalTerminalCombo.Items)
        {
            if (item is ComboBoxItem { Tag: string tag } candidate
                && string.Equals(tag, key, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// 设置页不再常驻显示终端说明，只在需要用户注意时（所选终端本机缺失、发生回退、
    /// 外部终端缺失等）给一行提示；正常情况保持空白。
    /// </summary>
    /// <param name="key">当前生效的偏好键。</param>
    /// <param name="missingKey">已配置但本机检测不到的键，非空时说明已回退。</param>
    private void UpdateLocalTerminalHint(string key, string? missingKey = null)
    {
        var launch = LocalShells.DescribeLaunch(key);
        var message = missingKey is null
            ? launch
            : $"本机未检测到「{LocalShells.Describe(missingKey)}」，已回退。{launch}";

        // DescribeLaunch 正常返回「将启动：…」，其余文案都属于需要告知的异常/回退
        var isNotice = missingKey is not null
                       || !launch.StartsWith("将启动：", StringComparison.Ordinal)
                       || launch.Contains("回退", StringComparison.Ordinal);

        LocalTerminalHintText.Text = isNotice ? message : string.Empty;
        LocalTerminalHintText.Visibility = isNotice ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LoadIgnoreEditor(SyncProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.LocalRoot))
        {
            IgnorePathText.Text = "（请先在「配置」页点该连接的「编辑」设置本地目录）";
            SyncIgnoreEditor.Text = DefaultIgnoreTemplate;
            return;
        }

        var path = Path.Combine(profile.LocalRoot, profile.Sync.ExtraIgnoreFile);
        IgnorePathText.Text = path;
        try
        {
            SyncIgnoreEditor.Text = File.Exists(path) ? File.ReadAllText(path) : DefaultIgnoreTemplate;
        }
        catch (Exception ex)
        {
            SyncIgnoreEditor.Text = DefaultIgnoreTemplate;
            IgnorePathText.Text = $"{path}（读取失败：{ex.Message}）";
        }
    }

    /// <summary>连接行上的紧凑阶段文字（配置页一行放得下，与顶栏胶囊同一口径）。</summary>
    private static string PhaseBadgeText(EnginePhase phase) => phase switch
    {
        EnginePhase.Idle => "已同步",
        EnginePhase.Scanning => "扫描中",
        EnginePhase.Transferring => "传输中",
        EnginePhase.Paused => "已暂停",
        EnginePhase.Error => "异常",
        _ => "已停止",
    };

    /// <summary>阶段对应的主题状态色键：成功 / 处理中 / 异常 / 空闲四档。</summary>
    private static string PhaseStateKey(EnginePhase phase) => phase switch
    {
        EnginePhase.Idle => "StateSuccess",
        EnginePhase.Scanning or EnginePhase.Transferring => "StateWarning",
        EnginePhase.Paused or EnginePhase.Error => "StateError",
        _ => "StateIdle",
    };

    // ── 文件页：本地 / 远端浏览、拖拽与下载 ───────────────────────

    /// <summary>轻量消息条：显示在内容区底部，自动淡出，不占用页面布局。</summary>
    private async void ShowMessage(string? message, bool isError = false)
    {
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        var accent = (Brush)FindResource(isError ? "StateErrorBrush" : "BrandBrush");
        MessageIconPath.Stroke = accent;
        MessageIconPath.Data = isError ? AlertIcon : CheckIcon;
        MessageText.Text = message;

        // 每条消息都把透明度拉回 1：若上一条正在淡出，也能被新消息接住
        MessageBar.Visibility = Visibility.Visible;
        MessageBar.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));

        var token = ++_messageToken;
        await Task.Delay(isError ? 6000 : 3000);
        if (token != _messageToken)
        {
            return;
        }

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
        fade.Completed += (_, _) =>
        {
            if (token == _messageToken)
            {
                MessageBar.Visibility = Visibility.Collapsed;
            }
        };
        MessageBar.BeginAnimation(OpacityProperty, fade);
    }

    private async void OnFilesPageShown()
    {
        try
        {
            await ReloadSnapshotAsync();
            LoadLocalEntries();
            if (!_remoteLoaded)
            {
                await LoadRemoteEntriesAsync();
            }
        }
        catch (Exception ex)
        {
            ShowMessage($"加载文件列表失败：{ex.Message}", isError: true);
        }
    }

    private async Task ReloadSnapshotAsync()
    {
        try
        {
            _snapshot = await _host.LoadSnapshotAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _snapshot = EmptySnapshot;
            ShowMessage($"读取同步快照失败：{ex.Message}", isError: true);
        }
    }

    /// <summary>列出本地当前目录的一层条目，并按忽略规则与远端已知态打标。</summary>
    private void LoadLocalEntries()
    {
        LocalFileList.ItemsSource = null;
        LocalCountText.Text = string.Empty;

        var profile = _host.Profile;
        var root = profile.LocalRoot;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            LocalPathText.Text = string.IsNullOrWhiteSpace(root)
                ? "（请先在「配置」页设置本地目录）"
                : $"（本地目录不存在：{root}）";
            return;
        }

        var current = _localBrowsePath.Length == 0
            ? root
            : Path.Combine(root, _localBrowsePath.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(current))
        {
            _localBrowsePath = string.Empty;
            current = root;
        }

        LocalPathText.Text = current;
        var rules = BuildIgnoreRules(profile, root);

        var items = new List<FileBrowserEntry>();
        try
        {
            foreach (var info in new DirectoryInfo(current).EnumerateFileSystemInfos())
            {
                var relative = _localBrowsePath.Length == 0 ? info.Name : _localBrowsePath + "/" + info.Name;
                var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
                var isReparsePoint = (info.Attributes & FileAttributes.ReparsePoint) != 0;
                var ignored = rules.IsIgnored(relative, isDirectory);
                var size = info is FileInfo file ? file.Length : 0;

                _snapshot.TryGetValue(PathKeys.Key(relative), out var snapshotEntry);
                var state = FileEntryStates.ForLocal(
                    snapshotEntry,
                    isDirectory ? EntryKind.Dir : EntryKind.File,
                    ignored);

                items.Add(FileBrowserEntry.FromLocal(
                    info.Name,
                    relative,
                    isDirectory,
                    isReparsePoint,
                    false,
                    size,
                    info.LastWriteTime,
                    state));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            ShowMessage($"读取本地目录失败：{ex.Message}", isError: true);
            return;
        }

        items.Sort(CompareBrowserEntries);
        LocalFileList.ItemsSource = items;
        LocalCountText.Text = $"{items.Count} 项";
    }

    private IgnoreRules BuildIgnoreRules(SyncProfile profile, string root)
    {
        _ignoreRules ??= IgnoreRules.Load(root, profile.Sync);

        var current = string.Empty;
        _ignoreRules.EnsureDirectoryRules(current);
        foreach (var segment in _localBrowsePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current.Length == 0 ? segment : current + "/" + segment;
            _ignoreRules.EnsureDirectoryRules(current);
        }

        return _ignoreRules;
    }

    private static int CompareBrowserEntries(FileBrowserEntry left, FileBrowserEntry right)
    {
        if (left.IsDirectory != right.IsDirectory)
        {
            return left.IsDirectory ? -1 : 1;
        }

        return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>列出一层远端目录，并按本地是否存在打标。</summary>
    private async Task LoadRemoteEntriesAsync()
    {
        if (_remoteLoading)
        {
            return;
        }

        _remoteLoading = true;
        RemoteFileList.ItemsSource = null;
        RemoteCountText.Text = string.Empty;

        try
        {
            var profile = _host.Profile;
            RemotePathText.Text = BuildRemoteDisplayPath(profile.RemoteRoot, _remoteBrowsePath);

            if (!_host.CanBrowseRemote)
            {
                ShowMessage(_host.RemoteBrowseHint ?? "远端连接未就绪", isError: true);
                return;
            }

            ShowMessage("正在读取远端目录…");
            var entries = await _host.ListRemoteDirectoryAsync(_remoteBrowsePath, CancellationToken.None);

            var items = new List<FileBrowserEntry>(entries.Count);
            foreach (var entry in entries)
            {
                items.Add(FileBrowserEntry.FromRemote(entry, LocalPathExists(profile, entry.RelativePath)));
            }

            RemoteFileList.ItemsSource = items;
            RemoteCountText.Text = $"{items.Count} 项";
            _remoteLoaded = true;

            if (items.Count == 0)
            {
                ShowMessage(_remoteBrowsePath.Length == 0
                    ? "远端根目录为空（可能还未同步过）"
                    : "远端目录为空");
            }
            else if (items.Count >= SyncHost.RemoteListLimit)
            {
                ShowMessage($"条目过多，仅显示前 {SyncHost.RemoteListLimit} 项，请进入子目录查看");
            }
        }
        catch (Exception ex)
        {
            ShowMessage($"读取远端目录失败：{ex.Message}", isError: true);
            _remoteLoaded = false;
        }
        finally
        {
            _remoteLoading = false;
        }
    }

    private static string BuildRemoteDisplayPath(string remoteRoot, string relativePath)
    {
        var root = string.IsNullOrWhiteSpace(remoteRoot) ? "/" : remoteRoot;
        return relativePath.Length == 0 ? root : root.TrimEnd('/') + "/" + relativePath;
    }

    private static bool LocalPathExists(SyncProfile profile, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(profile.LocalRoot))
        {
            return false;
        }

        var full = Path.Combine(profile.LocalRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(full) || Directory.Exists(full);
    }

    private void OnLocalUp(object sender, RoutedEventArgs e)
    {
        if (_localBrowsePath.Length == 0)
        {
            return;
        }

        _localBrowsePath = PathKeys.Parent(_localBrowsePath) ?? string.Empty;
        LoadLocalEntries();
    }

    private async void OnLocalRefresh(object sender, RoutedEventArgs e)
    {
        _ignoreRules = null;
        await ReloadSnapshotAsync();
        LoadLocalEntries();
    }

    private void OnLocalDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (ItemsControl.ContainerFromElement(LocalFileList, source) is not ListBoxItem { DataContext: FileBrowserEntry entry })
        {
            return;
        }

        if (!entry.CanEnter)
        {
            return;
        }

        _localBrowsePath = entry.RelativePath;
        LoadLocalEntries();
    }

    /// <summary>复制路径条上的完整路径，按钮短暂切换为「已复制」。</summary>
    private async void OnCopyPath(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        var text = button == RemoteCopyButton ? RemotePathText.Text : LocalPathText.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (!await TryCopyPathToClipboardAsync(text))
        {
            ShowMessage("复制路径失败：剪贴板正被其他程序占用，请稍后重试", isError: true);
            MessageBox.Show(
                this,
                $"剪贴板被其他程序占用，未能复制。此对话框内可按 Ctrl+C 复制路径：\n\n{text}",
                "PetrelPush — 复制路径",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        button.Content = "已复制";
        await Task.Delay(1200);
        button.Content = "复制";
    }

    /// <summary>
    /// 剪贴板被占用（CLIPBRD_E_CANT_OPEN）通常只持续几十到几百毫秒：
    /// 先做一小段同步重试，再退避异步重试，既不长时间卡界面，也能扛住较久的占用。
    /// </summary>
    private static async Task<bool> TryCopyPathToClipboardAsync(string text)
    {
        if (TrySetClipboardText(text))
        {
            return true;
        }

        for (var i = 0; i < 5; i++)
        {
            await Task.Delay(200);
            if (TrySetClipboardText(text))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TrySetClipboardText(string text)
    {
        try
        {
            // WinForms 的 SetDataObject 带 retryTimes / retryDelay，专门处理 CLIPBRD_E_CANT_OPEN
            System.Windows.Forms.Clipboard.SetDataObject(text, true, 4, 40);
            return true;
        }
        catch (Exception ex) when (ex is ExternalException or COMException)
        {
            // 落到下面的兜底通道
        }

        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (Exception ex) when (ex is ExternalException or COMException or ArgumentException or ThreadStateException)
        {
            return false;
        }
    }

    private void OnLocalListRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(LocalFileList, source) is ListBoxItem item)
        {
            item.IsSelected = true;
            return;
        }

        LocalFileList.SelectedItem = null;
    }

    private void OnLocalContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var ready = TryResolveLocalDirectory(out _);
        LocalOpenExplorerMenuItem.IsEnabled = ready;
        LocalRefreshMenuItem.IsEnabled = ready;
        LocalTerminalMenuItem.IsEnabled = ready;
        LocalNewFolderMenuItem.IsEnabled = ready;
        // 上传依赖远端连接：保持可点，点击时由提示说明原因，比灰掉更清楚
        LocalUploadMenuItem.IsEnabled = ready;
        LocalDeleteMenuItem.IsEnabled = ready && LocalFileList.SelectedItem is FileBrowserEntry;
    }

    /// <summary>
    /// 在本地终端中打开当前目录（选中目录时进入该目录，选中文件时进入其所在目录）。
    /// 设置页选了 Windows Terminal 这类外部终端时改为独立窗口启动，不走内置终端。
    /// </summary>
    private void OnOpenLocalTerminal(object sender, RoutedEventArgs e)
    {
        if (!TryResolveLocalDirectory(out var currentDirectory))
        {
            ShowMessage("请先在「配置」页设置有效的本地目录", isError: true);
            return;
        }

        var directory = currentDirectory;
        if (LocalFileList.SelectedItem is FileBrowserEntry entry)
        {
            var full = Path.Combine(
                _host.Profile.LocalRoot,
                entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));

            if (Directory.Exists(full))
            {
                directory = full;
            }
            else if (File.Exists(full))
            {
                directory = Path.GetDirectoryName(full) ?? directory;
            }
        }

        var preference = _host.Profile.Sync.LocalTerminal;
        if (LocalShells.IsExternalKey(preference))
        {
            OpenExternalTerminal(preference, directory);
            return;
        }

        if (!ConPtySession.IsSupported)
        {
            ShowMessage("本地终端需要 Windows 10 1809 或更高版本", isError: true);
            return;
        }

        var profileId = _host.Profile.ProfileId;
        try
        {
            var window = TerminalWindow.ForLocal(
                directory,
                preference,
                _host.Profile.QuickAccess,
                quickAccess => PersistQuickAccess(profileId, quickAccess));
            window.Owner = this;
            window.Show();
        }
        catch (Exception ex)
        {
            ShowMessage($"打开本地终端失败：{ex.Message}", isError: true);
        }
    }

    /// <summary>
    /// 终端窗口增删快速命令 / 快速路径后回写 config.json。
    /// 只改这一块配置，不重建引擎（快速入口与同步无关）。
    /// 按 profileId 找回所属连接，避免窗口打开后切换连接写错目标。
    /// </summary>
    private void PersistQuickAccess(string profileId, QuickAccessOptions quickAccess)
    {
        var host = _connections.Hosts.FirstOrDefault(
            candidate => string.Equals(candidate.Profile.ProfileId, profileId, StringComparison.Ordinal));
        if (host is null)
        {
            return;
        }

        host.Profile.QuickAccess = quickAccess;
        _store.Save(host.Profile);
    }

    /// <summary>外部终端（Windows Terminal）：新开独立窗口并落在指定目录。</summary>
    private void OpenExternalTerminal(string preference, string directory)
    {
        var external = LocalShells.ResolveExternal(preference);
        if (external is null)
        {
            ShowMessage("未检测到 Windows Terminal（wt.exe）：请在「设置」页改用内置终端，或安装后重试", isError: true);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(external.Executable, $"-d \"{directory}\"") { UseShellExecute = true });
            ShowMessage($"已在 {external.Name} 中打开 {directory}");
        }
        catch (Exception ex)
        {
            ShowMessage($"打开 {external.Name} 失败：{ex.Message}", isError: true);
        }
    }

    /// <summary>删除本地条目（移到回收站）；远端对应内容由下一轮同步的 diff 自动删除。</summary>
    private void OnLocalDelete(object sender, RoutedEventArgs e)
    {
        if (LocalFileList.SelectedItem is not FileBrowserEntry entry)
        {
            return;
        }

        if (!TryResolveLocalDirectory(out _))
        {
            ShowMessage("请先在「配置」页设置有效的本地目录", isError: true);
            return;
        }

        var full = Path.Combine(
            _host.Profile.LocalRoot,
            entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));

        var exists = entry.IsDirectory ? Directory.Exists(full) : File.Exists(full);
        if (!exists)
        {
            ShowMessage($"条目已不存在：{entry.Name}");
            LoadLocalEntries();
            return;
        }

        var kind = entry.IsDirectory ? "文件夹" : "文件";
        var answer = MessageBox.Show(
            this,
            $"确定删除{kind}「{entry.Name}」吗？\n\n它会移到回收站，对应远端内容也会在下一轮同步中被删除。",
            "PetrelPush — 删除确认",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            DeleteToRecycleBin(full, entry.IsDirectory);
        }
        catch (Exception ex)
        {
            ShowMessage($"删除失败：{ex.Message}", isError: true);
            MessageBox.Show(this, ex.Message, "删除失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ShowMessage($"已删除 {entry.Name}，远端对应内容会在下一轮同步中删除");
        StatusBarText.Text = $"已删除 {entry.Name}";
        LoadLocalEntries();
        _host.RequestSync();
    }

    private static void DeleteToRecycleBin(string fullPath, bool isDirectory)
    {
        if (isDirectory)
        {
            VBFileSystem.DeleteDirectory(fullPath, VBUIOption.OnlyErrorDialogs, VBRecycleOption.SendToRecycleBin);
            return;
        }

        VBFileSystem.DeleteFile(fullPath, VBUIOption.OnlyErrorDialogs, VBRecycleOption.SendToRecycleBin);
    }

    /// <summary>在资源管理器打开：选中目录则打开该目录，选中文件则定位文件，未选中则打开当前目录。</summary>
    private void OnOpenInExplorer(object sender, RoutedEventArgs e)
    {
        if (!TryResolveLocalDirectory(out var currentDirectory))
        {
            ShowMessage("请先在「配置」页设置有效的本地目录", isError: true);
            return;
        }

        var arguments = $"\"{currentDirectory}\"";
        if (LocalFileList.SelectedItem is FileBrowserEntry entry)
        {
            var full = Path.Combine(
                _host.Profile.LocalRoot,
                entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));

            if (Directory.Exists(full))
            {
                arguments = $"\"{full}\"";
            }
            else if (File.Exists(full))
            {
                arguments = $"/select,\"{full}\"";
            }
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowMessage($"打开资源管理器失败：{ex.Message}", isError: true);
        }
    }

    /// <summary>本地侧「上传文件」：把本机文件复制到本地当前目录，随后由同步引擎上传到远端。</summary>
    private async void OnLocalUploadFiles(object sender, RoutedEventArgs e)
    {
        if (!TryGetLocalDirectory(out var targetDirectory))
        {
            return;
        }

        var files = PickFilesToUpload(_localBrowsePath, "选择要上传到本地目录的文件");
        if (files.Length == 0)
        {
            return;
        }

        var conflicts = CountLocalConflicts(files, targetDirectory);
        if (conflicts > 0 && !ConfirmOverwrite($"本地目录已存在 {conflicts} 个同名项，继续将覆盖。是否继续？"))
        {
            return;
        }

        ShowMessage("正在复制到本地目录…");
        try
        {
            var copied = await Task.Run(() => CopyIntoLocal(files, targetDirectory));
            var done = $"已复制 {copied} 个文件到 {targetDirectory}，同步引擎会自动上传";
            ShowMessage(done);
            StatusBarText.Text = done;
            LoadLocalEntries();
            _host.RequestSync();
        }
        catch (Exception ex)
        {
            ShowMessage($"复制失败：{ex.Message}", isError: true);
            MessageBox.Show(this, ex.Message, "复制失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>本地侧「新建文件夹」：在本地当前目录新建，随后由同步引擎上传到远端。</summary>
    private void OnLocalNewFolder(object sender, RoutedEventArgs e)
    {
        if (!TryResolveLocalDirectory(out var currentDirectory))
        {
            ShowMessage("请先在「配置」页设置有效的本地目录", isError: true);
            return;
        }

        var relative = ResolveLocalCreateParent();
        var parentDirectory = relative.Length == 0
            ? currentDirectory
            : Path.Combine(_host.Profile.LocalRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(parentDirectory))
        {
            ShowMessage($"目标目录不存在：{parentDirectory}", isError: true);
            return;
        }

        var dialog = new TextInputWindow("新建文件夹", $"在 {parentDirectory} 下新建文件夹", "新建文件夹")
        {
            Owner = this,
            Validate = name => ValidateNewFolderName(name, parentDirectory, local: true),
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.Combine(parentDirectory, dialog.Value));
        }
        catch (Exception ex)
        {
            ShowMessage($"新建文件夹失败：{ex.Message}", isError: true);
            MessageBox.Show(this, ex.Message, "新建文件夹失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var message = $"已创建文件夹 {dialog.Value}，同步引擎会上传到远端";
        ShowMessage(message);
        StatusBarText.Text = message;
        LoadLocalEntries();
        _host.RequestSync();
    }

    private async void OnRemoteUp(object sender, RoutedEventArgs e)
    {
        if (_remoteBrowsePath.Length == 0)
        {
            return;
        }

        _remoteBrowsePath = PathKeys.Parent(_remoteBrowsePath) ?? string.Empty;
        await LoadRemoteEntriesAsync();
    }

    private async void OnRemoteRefresh(object sender, RoutedEventArgs e)
    {
        _remoteLoaded = false;
        await LoadRemoteEntriesAsync();
    }

    private async void OnRemoteDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (ItemsControl.ContainerFromElement(RemoteFileList, source) is not ListBoxItem { DataContext: FileBrowserEntry entry })
        {
            return;
        }

        if (!entry.CanEnter)
        {
            return;
        }

        _remoteBrowsePath = entry.RelativePath;
        await LoadRemoteEntriesAsync();
    }

    /// <summary>右键落在某一行时先选中该行，落在空白处则清空选择。</summary>
    private void OnRemoteListRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(RemoteFileList, source) is ListBoxItem item)
        {
            item.IsSelected = true;
            return;
        }

        RemoteFileList.SelectedItem = null;
    }

    private void OnRemoteContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        RemoteDownloadMenuItem.IsEnabled =
            _host.CanBrowseRemote && RemoteFileList.SelectedItem is FileBrowserEntry { CanDownload: true };
        RemoteTerminalMenuItem.IsEnabled = _host.CanBrowseRemote;
        RemoteUploadMenuItem.IsEnabled = _host.CanBrowseRemote;
        RemoteNewFolderMenuItem.IsEnabled = _host.CanBrowseRemote;
        RemoteDeleteMenuItem.IsEnabled =
            _host.CanBrowseRemote && RemoteFileList.SelectedItem is FileBrowserEntry;
    }

    /// <summary>在远端终端中打开当前目录（选中目录时进入该目录），并自动 cd 过去。</summary>
    private void OnOpenRemoteTerminal(object sender, RoutedEventArgs e)
    {
        if (!_host.CanBrowseRemote)
        {
            ShowMessage(_host.RemoteBrowseHint ?? "远端连接未就绪", isError: true);
            return;
        }

        var relative = _remoteBrowsePath;
        if (RemoteFileList.SelectedItem is FileBrowserEntry { IsDirectory: true } directory)
        {
            relative = directory.RelativePath;
        }

        var absolute = BuildRemoteDisplayPath(_host.Profile.RemoteRoot, relative);
        var profile = _host.Profile;
        var profileId = profile.ProfileId;

        try
        {
            var window = TerminalWindow.ForRemote(
                profile,
                absolute,
                quickAccess => PersistQuickAccess(profileId, quickAccess));
            window.Owner = this;
            window.Show();
        }
        catch (Exception ex)
        {
            ShowMessage($"打开终端失败：{ex.Message}", isError: true);
        }
    }

    /// <summary>远端侧「上传文件」：选择本机文件，直接上传到远端当前目录。</summary>
    private async void OnRemoteUploadFiles(object sender, RoutedEventArgs e)
    {
        if (!_host.CanBrowseRemote)
        {
            ShowMessage(_host.RemoteBrowseHint ?? "远端连接未就绪", isError: true);
            return;
        }

        var files = PickFilesToUpload(_remoteBrowsePath, "选择要上传的文件（上传到远端当前目录）");
        if (files.Length == 0)
        {
            return;
        }

        await UploadFilesToRemoteAsync(files, _remoteBrowsePath);
    }

    /// <summary>远端侧「新建文件夹」：直接在远端当前目录创建，不进入同步队列。</summary>
    private async void OnRemoteNewFolder(object sender, RoutedEventArgs e)
    {
        if (!_host.CanBrowseRemote)
        {
            ShowMessage(_host.RemoteBrowseHint ?? "远端连接未就绪", isError: true);
            return;
        }

        var parent = ResolveRemoteCreateParent();
        var parentDisplay = BuildRemoteDisplayPath(_host.Profile.RemoteRoot, parent);
        var dialog = new TextInputWindow("新建文件夹", $"在 {parentDisplay} 下新建文件夹", "新建文件夹")
        {
            Owner = this,
            Validate = name => ValidateNewFolderName(name, parentDisplay, local: false),
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var relative = parent.Length == 0 ? dialog.Value : parent + "/" + dialog.Value;
        try
        {
            ShowMessage($"正在创建远端文件夹 {dialog.Value}…");
            await _host.CreateRemoteDirectoryAsync(relative, CancellationToken.None);
            var done = $"已创建远端文件夹 {BuildRemoteDisplayPath(_host.Profile.RemoteRoot, relative)}";
            ShowMessage(done);
            StatusBarText.Text = done;
            await LoadRemoteEntriesAsync();
        }
        catch (Exception ex)
        {
            ShowMessage($"新建文件夹失败：{ex.Message}", isError: true);
            MessageBox.Show(this, ex.Message, "新建文件夹失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnDownloadSelected(object sender, RoutedEventArgs e)
    {
        if (RemoteFileList.SelectedItem is not FileBrowserEntry entry)
        {
            ShowMessage("请先在右侧列表选中一个远端文件");
            return;
        }

        if (!entry.CanDownload)
        {
            ShowMessage(entry.IsDirectory
                ? "目录不支持直接下载，请进入后选择具体文件"
                : "该条目不是普通文件，不支持下载");
            return;
        }

        if (!_host.CanBrowseRemote)
        {
            ShowMessage(_host.RemoteBrowseHint ?? "远端连接未就绪", isError: true);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "保存远端文件",
            FileName = LocalFileName.Sanitize(entry.Name),
            Filter = "所有文件|*.*",
            InitialDirectory = DefaultDownloadDirectory(),
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var destination = dialog.FileName;
        if (IsUnderLocalRoot(_host.Profile.LocalRoot, destination))
        {
            var answer = MessageBox.Show(
                this,
                "保存位置在同步目录之内，该文件会被当作本地变更再次上传到远端。仍然继续？",
                "PetrelPush — 保存位置提醒",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        var progress = new Progress<long>(bytes =>
        {
            var percent = entry.SizeBytes > 0
                ? Math.Clamp((double)bytes / entry.SizeBytes, 0, 1)
                : 0;
            UpdateDownloadPanel(entry.Name, percent, bytes);
        });

        EnterDownloadPanel(entry.Name);
        try
        {
            ShowMessage($"正在下载 {entry.Name}…");
            var result = await _host.DownloadRemoteFileAsync(
                entry.RelativePath,
                destination,
                progress,
                CancellationToken.None);

            var done = $"已下载 {entry.Name}（{FileBrowserEntry.FormatSize(result.Size)}）→ {destination}";
            ShowMessage(done);
            StatusBarText.Text = done;
        }
        catch (Exception ex)
        {
            ShowMessage($"下载失败：{ex.Message}", isError: true);
            MessageBox.Show(this, ex.Message, "下载失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            ExitDownloadPanel();
        }
    }

    /// <summary>
    /// 删除远端条目：直接作用在服务器上，不进回收站，目录会连同内容递归删除。
    /// 同步方向是「本地 → 远端」，删掉远端不会让引擎自动补传，本地文件再次修改后才会重新上传。
    /// </summary>
    private async void OnRemoteDelete(object sender, RoutedEventArgs e)
    {
        if (RemoteFileList.SelectedItem is not FileBrowserEntry entry)
        {
            ShowMessage("请先在右侧列表选中要删除的远端条目");
            return;
        }

        if (!_host.CanBrowseRemote)
        {
            ShowMessage(_host.RemoteBrowseHint ?? "远端连接未就绪", isError: true);
            return;
        }

        // 符号链接只删链接本身，不能按目录递归
        var recursive = entry.IsDirectory && !entry.IsSymbolicLink;
        var kind = recursive ? "文件夹及其全部内容" : "文件";
        var localHint = LocalPathExists(_host.Profile, entry.RelativePath)
            ? "\n\n本地仍存在同名项：同步只按本地变更推进，删除远端后引擎不会自动补传，本地文件再次修改后才会重新上传。"
            : string.Empty;

        var answer = MessageBox.Show(
            this,
            $"确定删除远端{kind}「{entry.Name}」吗？\n\n删除直接作用在服务器上，不进回收站，也无法撤销。{localHint}",
            "PetrelPush — 删除远端",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            ShowMessage($"正在删除远端 {entry.Name}…");
            var result = await _host.DeleteRemotePathAsync(entry.RelativePath, recursive, CancellationToken.None);
            var done = recursive
                ? $"已删除远端文件夹 {entry.Name}（{result.Files} 个文件、{result.Directories} 个目录）"
                : $"已删除远端文件 {entry.Name}";
            ShowMessage(done);
            StatusBarText.Text = done;
            await LoadRemoteEntriesAsync();
        }
        catch (Exception ex)
        {
            ShowMessage($"删除失败：{ex.Message}", isError: true);
            MessageBox.Show(this, ex.Message, "删除失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ── 拖拽：左框复制到本地目录，右框直接上传到远端目录 ─────────

    private void OnLocalDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetDropPaths(e, out _) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnLocalDrop(object sender, DragEventArgs e)
    {
        if (!TryGetDropPaths(e, out var sources))
        {
            return;
        }

        e.Handled = true;
        if (!TryGetLocalDirectory(out var targetDirectory))
        {
            return;
        }

        var conflicts = CountLocalConflicts(sources, targetDirectory);
        if (conflicts > 0 && !ConfirmOverwrite($"本地目录已存在 {conflicts} 个同名项，继续将覆盖。是否继续？"))
        {
            return;
        }

        ShowMessage("正在复制到本地目录…");
        try
        {
            var copied = await Task.Run(() => CopyIntoLocal(sources, targetDirectory));
            var done = $"已复制 {copied} 个文件到 {targetDirectory}，同步引擎会自动上传";
            ShowMessage(done);
            StatusBarText.Text = done;
            LoadLocalEntries();
        }
        catch (Exception ex)
        {
            ShowMessage($"复制失败：{ex.Message}", isError: true);
        }
    }

    private void OnRemoteDragOver(object sender, DragEventArgs e)
    {
        // 拖拽过程中不逐帧检查文件存在性，放到 Drop 里再判定，避免大文件集卡顿
        var acceptable = TryGetDropPaths(e, out _) && _host.CanBrowseRemote;
        e.Effects = acceptable ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnRemoteDrop(object sender, DragEventArgs e)
    {
        if (!TryGetDropPaths(e, out var sources))
        {
            return;
        }

        e.Handled = true;
        if (!_host.CanBrowseRemote)
        {
            ShowMessage(_host.RemoteBrowseHint ?? "远端连接未就绪", isError: true);
            return;
        }

        var files = sources.Where(File.Exists).ToArray();
        if (files.Length == 0)
        {
            ShowMessage("只支持把文件拖到远端列表；文件夹请先进入目标目录，再拖入其中的文件");
            return;
        }

        await UploadFilesToRemoteAsync(files, _remoteBrowsePath);
    }

    /// <summary>上传用的文件选择框：初始目录尽量落在本地同步目录的对应路径上。</summary>
    private string[] PickFilesToUpload(string relativePath, string title)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = "所有文件|*.*",
            Multiselect = true,
            CheckFileExists = true,
            InitialDirectory = ResolveUploadInitialDirectory(relativePath),
        };

        return dialog.ShowDialog(this) == true ? dialog.FileNames : Array.Empty<string>();
    }

    /// <summary>上传选择框的初始目录：本地同步目录中存在对应路径时用它，否则退回系统「下载」目录。</summary>
    private string ResolveUploadInitialDirectory(string relativePath)
    {
        var root = _host.Profile.LocalRoot;
        if (!string.IsNullOrWhiteSpace(root))
        {
            var candidate = relativePath.Length == 0
                ? root
                : Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return DefaultDownloadDirectory();
    }

    /// <summary>逐个上传本机文件到远端目录：同名确认 → 逐个上传 → 刷新右侧列表。</summary>
    private async Task UploadFilesToRemoteAsync(string[] files, string targetRelativePath)
    {
        if (!_host.CanBrowseRemote)
        {
            ShowMessage(_host.RemoteBrowseHint ?? "远端连接未就绪", isError: true);
            return;
        }

        var conflicts = await CountRemoteConflictsAsync(files, targetRelativePath);
        if (conflicts > 0 && !ConfirmOverwrite($"远端目录已存在 {conflicts} 个同名文件，继续将覆盖。是否继续？"))
        {
            return;
        }

        var total = 0L;
        try
        {
            for (var i = 0; i < files.Length; i++)
            {
                var file = files[i];
                var name = Path.GetFileName(file);
                var relative = targetRelativePath.Length == 0 ? name : targetRelativePath + "/" + name;
                var index = i + 1;

                var progress = new Progress<long>(bytes => StatusBarText.Text =
                    $"上传中（{index}/{files.Length}）{name}：{FileBrowserEntry.FormatSize(bytes)}");

                ShowMessage($"正在上传（{index}/{files.Length}）{name}…");
                total += await _host.UploadLocalFileAsync(file, relative, progress, CancellationToken.None);
            }

            var target = BuildRemoteDisplayPath(_host.Profile.RemoteRoot, targetRelativePath);
            var done = $"已上传 {files.Length} 个文件（{FileBrowserEntry.FormatSize(total)}）到 {target}";
            ShowMessage(done);
            StatusBarText.Text = done;
            _remoteLoaded = false;
            await LoadRemoteEntriesAsync();
        }
        catch (Exception ex)
        {
            ShowMessage($"上传失败：{ex.Message}", isError: true);
            MessageBox.Show(this, ex.Message, "上传失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>统计远端目标目录里与本机待传文件同名的条目；列目录失败时按无冲突处理，交给上传阶段报错。</summary>
    private async Task<int> CountRemoteConflictsAsync(string[] files, string targetRelativePath)
    {
        try
        {
            var entries = await _host.ListRemoteDirectoryAsync(targetRelativePath, CancellationToken.None);
            var existing = entries
                .Where(entry => !entry.IsDirectory)
                .Select(entry => entry.Name)
                .ToHashSet(StringComparer.Ordinal);
            return files.Count(file => existing.Contains(Path.GetFileName(file)));
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>新建文件夹的落点：选中文件夹时建在该文件夹内，否则建在当前目录。</summary>
    private string ResolveLocalCreateParent()
    {
        return LocalFileList.SelectedItem is FileBrowserEntry { IsDirectory: true } directory
            ? directory.RelativePath
            : _localBrowsePath;
    }

    /// <summary>远端新建文件夹的落点：选中文件夹时建在该文件夹内，否则建在当前远端目录。</summary>
    private string ResolveRemoteCreateParent()
    {
        return RemoteFileList.SelectedItem is FileBrowserEntry { IsDirectory: true } directory
            ? directory.RelativePath
            : _remoteBrowsePath;
    }

    /// <summary>新建文件夹的名称校验：本地按 Windows 规则，远端按 Linux 规则。</summary>
    private static string? ValidateNewFolderName(string name, string parent, bool local)
    {
        if (name.Length == 0)
        {
            return "请输入文件夹名称";
        }

        if (name is "." or "..")
        {
            return "名称不能是 . 或 ..";
        }

        if (name.IndexOfAny(['/', '\\']) >= 0)
        {
            return "名称不能包含 / 或 \\";
        }

        if (local)
        {
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') || name.EndsWith(' '))
            {
                return "名称包含 Windows 不允许的字符，或以 . / 空格结尾";
            }

            var target = Path.Combine(parent, name);
            if (Directory.Exists(target) || File.Exists(target))
            {
                return "当前目录已存在同名文件或文件夹";
            }

            return null;
        }

        if (name.Any(char.IsControl))
        {
            return "名称不能包含控制字符";
        }

        return null;
    }

    private static bool TryGetDropPaths(DragEventArgs e, out string[] paths)
    {
        paths = Array.Empty<string>();
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return false;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] dropped || dropped.Length == 0)
        {
            return false;
        }

        paths = dropped;
        return true;
    }

    private bool TryGetLocalDirectory(out string directory)
    {
        if (TryResolveLocalDirectory(out directory))
        {
            return true;
        }

        ShowMessage("请先在「配置」页设置有效的本地目录", isError: true);
        return false;
    }

    /// <summary>静默解析本地当前目录（不弹提示），供右键菜单等场景使用。</summary>
    private bool TryResolveLocalDirectory(out string directory)
    {
        directory = string.Empty;
        var root = _host.Profile.LocalRoot;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return false;
        }

        var current = _localBrowsePath.Length == 0
            ? root
            : Path.Combine(root, _localBrowsePath.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(current))
        {
            _localBrowsePath = string.Empty;
            current = root;
        }

        directory = current;
        return true;
    }

    private static int CountLocalConflicts(string[] sources, string targetDirectory)
    {
        var conflicts = 0;
        foreach (var source in sources)
        {
            var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (name.Length == 0)
            {
                continue;
            }

            var destination = Path.Combine(targetDirectory, name);
            if (File.Exists(destination) || Directory.Exists(destination))
            {
                conflicts++;
            }
        }

        return conflicts;
    }

    private static int CopyIntoLocal(string[] sources, string targetDirectory)
    {
        var copied = 0;
        foreach (var source in sources)
        {
            var trimmed = source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(trimmed);
            if (name.Length == 0)
            {
                continue;
            }

            var destination = Path.Combine(targetDirectory, name);
            if (string.Equals(Path.GetFullPath(trimmed), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (File.Exists(trimmed))
            {
                File.Copy(trimmed, destination, true);
                copied++;
                continue;
            }

            if (!Directory.Exists(trimmed))
            {
                continue;
            }

            Directory.CreateDirectory(destination);
            foreach (var file in Directory.EnumerateFiles(trimmed, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(trimmed, file);
                var target = Path.Combine(destination, relative);
                var parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                File.Copy(file, target, true);
                copied++;
            }
        }

        return copied;
    }

    private bool ConfirmOverwrite(string message)
    {
        return MessageBox.Show(
            this,
            message,
            "PetrelPush — 覆盖确认",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    /// <summary>默认保存到系统「下载」目录，避免落入同步目录触发回传。</summary>
    private static string DefaultDownloadDirectory()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(userProfile, "Downloads");
        if (Directory.Exists(downloads))
        {
            return downloads;
        }

        return Directory.Exists(userProfile) ? userProfile : Environment.CurrentDirectory;
    }

    private static bool IsUnderLocalRoot(string localRoot, string destination)
    {
        if (string.IsNullOrWhiteSpace(localRoot))
        {
            return false;
        }

        try
        {
            var root = Path.GetFullPath(localRoot).TrimEnd(Path.DirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
            return Path.GetFullPath(destination).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>配置或忽略规则变化后，让文件页下次进入时重新加载。</summary>
    private void InvalidateFilePage()
    {
        _remoteBrowsePath = string.Empty;
        _remoteLoaded = false;
        _ignoreRules = null;
        _snapshot = EmptySnapshot;
    }

    /// <summary>配置页结果文本的颜色：取主题状态色，避免硬编码 Brushes 与整体配色脱节。</summary>
    private Brush ResultSuccess => (Brush)FindResource("StateSuccessBrush");

    private Brush ResultIdle => (Brush)FindResource("StateIdleBrush");

    private Brush ResultError => (Brush)FindResource("StateErrorBrush");

    /// <summary>在卡片右上角的结果文本里显示提示。</summary>
    private static void ShowResult(System.Windows.Controls.TextBlock target, string message, Brush brush)
    {
        target.Foreground = brush;
        target.Text = message;
    }

    private void OnSyncNow(object sender, RoutedEventArgs e)
    {
        _host.RequestSync();
        StatusBarText.Text = "已请求立即同步…";
    }

    private void OnPauseResume(object sender, RoutedEventArgs e)
    {
        TogglePause();
    }

    private void OnDeepVerify(object sender, RoutedEventArgs e)
    {
        _host.RequestDeepVerify();
        StatusBarText.Text = "已请求深度校验（忽略 mtime 捷径，全部重算哈希）";
    }

    private async void OnResetFailed(object sender, RoutedEventArgs e)
    {
        try
        {
            await _host.ResetFailedTasksAsync();
            StatusBarText.Text = "已重置失败任务，将在下一轮重试";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "重置失败任务", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnSaveIgnore(object sender, RoutedEventArgs e)
    {
        var profile = _host.Profile;
        if (string.IsNullOrWhiteSpace(profile.LocalRoot) || !Directory.Exists(profile.LocalRoot))
        {
            MessageBox.Show(this, "请先在「配置」页设置有效的本地目录。", "PetrelPush", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var path = Path.Combine(profile.LocalRoot, profile.Sync.ExtraIgnoreFile);
        try
        {
            File.WriteAllText(path, SyncIgnoreEditor.Text);
            _host.RequestSync();
            InvalidateFilePage();
            if (PageFiles.Visibility == Visibility.Visible)
            {
                OnFilesPageShown();
            }

            StatusBarText.Text = $"已保存忽略规则并触发同步：{path}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "保存忽略规则失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnReloadIgnore(object sender, RoutedEventArgs e)
    {
        LoadIgnoreEditor(_host.Profile);
    }

    private void OnQueryIgnore(object sender, RoutedEventArgs e)
    {
        var profile = _host.Profile;
        if (string.IsNullOrWhiteSpace(profile.LocalRoot) || !Directory.Exists(profile.LocalRoot))
        {
            IgnoreQueryResult.Text = "本地目录不存在，无法判定。";
            return;
        }

        var path = IgnoreQueryBox.Text.Trim().Replace('\\', '/').Trim('/');
        if (path.Length == 0)
        {
            IgnoreQueryResult.Text = "请输入相对路径，例如 dist/runtime.json";
            return;
        }

        var rules = IgnoreRules.Load(profile.LocalRoot, profile.Sync);
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = string.Empty;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            rules.EnsureDirectoryRules(current);
            current = current.Length == 0 ? segments[i] : current + "/" + segments[i];
        }

        var isDirectory = Directory.Exists(
            Path.Combine(profile.LocalRoot, path.Replace('/', Path.DirectorySeparatorChar)));
        var explanation = rules.Explain(path, isDirectory);
        IgnoreQueryResult.Text = $"{explanation.Summary}（{(isDirectory ? "目录" : "文件")}）";
    }

    private void OnLogLevelChanged(object sender, SelectionChangedEventArgs e)
    {
        _minLevel = LogLevelCombo.SelectedIndex switch
        {
            1 => LogEventLevel.Debug,
            2 => LogEventLevel.Information,
            3 => LogEventLevel.Warning,
            4 => LogEventLevel.Error,
            _ => LogEventLevel.Verbose,
        };

        RefreshLogView();
    }

    private void OnLogConnectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressConnectionFilter)
        {
            return;
        }

        _connectionFilter = (LogConnectionCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        RefreshLogView();
    }

    /// <summary>日志页「连接」下拉：全部 / 应用 / 各条连接，连接增删改名后重建并保留当前选择。</summary>
    private void RefreshLogConnectionOptions()
    {
        var previous = _connectionFilter;

        _suppressConnectionFilter = true;
        try
        {
            LogConnectionCombo.Items.Clear();
            LogConnectionCombo.Items.Add(new ComboBoxItem { Content = "全部" });
            LogConnectionCombo.Items.Add(new ComboBoxItem { Content = "应用", Tag = "应用" });

            foreach (var host in _connections.Hosts)
            {
                LogConnectionCombo.Items.Add(new ComboBoxItem
                {
                    Content = host.DisplayName,
                    Tag = host.DisplayName,
                });
            }

            var match = LogConnectionCombo.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag is string tag && string.Equals(tag, previous, StringComparison.Ordinal));

            LogConnectionCombo.SelectedItem = match ?? LogConnectionCombo.Items[0];
            _connectionFilter = (LogConnectionCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        }
        finally
        {
            _suppressConnectionFilter = false;
        }
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        var directory = LoggingSetup.LogDirectory;
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
    }

    private void OnClearLogs(object sender, RoutedEventArgs e)
    {
        _allLogs.Clear();
        _visibleLogs.Clear();
        UpdateEmptyLogHint();
    }

    /// <summary>设置页：开机自启勾选后立即写注册表并保存到 profile，不依赖「应用」按钮。</summary>
    private void OnAutoStartChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressAutoStartChange)
        {
            return;
        }

        var enabled = AutoStartSettingCheck.IsChecked == true;
        var profile = _host.Profile;
        profile.Sync.AutoStart = enabled;

        try
        {
            _store.Save(profile);

            // 开机自启是应用级开关：任一条连接开启即注册；全部关闭才移除
            var applied = StartupRegistration.Apply(_connections.Hosts.Any(host => host.Profile.Sync.AutoStart));

            if (!applied)
            {
                SettingsResultText.Foreground = (Brush)FindResource("StateWarningBrush");
                SettingsResultText.Text = "已保存到配置，但写入注册表被拒绝（可能受组策略限制），登录后可能不会自动启动";
                return;
            }

            SettingsResultText.Foreground = (Brush)FindResource("StateSuccessBrush");
            SettingsResultText.Text = enabled
                ? $"已开启本连接的开机自启（{DateTime.Now:HH:mm:ss} 生效；任一条连接开启即随系统启动）"
                : "已关闭本连接的开机自启（仍有其它连接开启时保持自启）";
        }
        catch (Exception ex)
        {
            SettingsResultText.Foreground = (Brush)FindResource("StateErrorBrush");
            SettingsResultText.Text = $"保存开机自启设置失败：{ex.Message}";
        }
    }

    /// <summary>设置页：本地终端 shell 选择后立即保存，下次打开终端生效（不影响已打开的窗口）。</summary>
    private void OnLocalTerminalChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLocalTerminalChange)
        {
            return;
        }

        if (LocalTerminalCombo.SelectedItem is not ComboBoxItem { Tag: string key })
        {
            return;
        }

        var profile = _host.Profile;
        profile.Sync.LocalTerminal = key;

        try
        {
            _store.Save(profile);
            SettingsResultText.Foreground = (Brush)FindResource("StateSuccessBrush");
            SettingsResultText.Text = $"已保存本地终端：{LocalShells.Describe(key)}（下次打开终端生效）";
        }
        catch (Exception ex)
        {
            SettingsResultText.Foreground = (Brush)FindResource("StateErrorBrush");
            SettingsResultText.Text = $"保存本地终端设置失败：{ex.Message}";
        }

        UpdateLocalTerminalHint(key);
    }

    /// <summary>设置页「打开」按钮：按 Tag 打开 profile 目录或日志目录。</summary>
    private void OnOpenSettingsDirectory(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string kind })
        {
            return;
        }

        var profileId = _host.Profile.ProfileId;
        var directory = kind == "logs"
            ? LoggingSetup.LogDirectory
            : _store.GetProfileDirectory(profileId);

        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SettingsResultText.Foreground = (Brush)FindResource("StateErrorBrush");
            SettingsResultText.Text = $"打开目录失败：{ex.Message}";
        }
    }

    private void UpdateEmptyLogHint()
    {
        EmptyLogHint.Visibility = _visibleLogs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnLogEmitted(LogEvent logEvent)
    {
        // 多连接共用一条日志流：每行带上 Connection 上下文（连接名），否则分不清来自哪条连接
        var connection = logEvent.Properties.TryGetValue("Connection", out var property)
                         && property is ScalarValue { Value: string name }
                         && !string.IsNullOrWhiteSpace(name)
            ? name
            : null;

        var line = $"[{logEvent.Timestamp:HH:mm:ss} {LevelTag(logEvent.Level)}] ";
        if (connection is not null)
        {
            line += $"[{connection}] ";
        }

        line += logEvent.RenderMessage();
        if (logEvent.Exception is not null)
        {
            line += $"  ({logEvent.Exception.Message})";
        }

        Dispatcher.BeginInvoke(new Action(() => AppendLog(LogEntry.Create(logEvent.Level, line, connection))));
    }

    private void AppendLog(LogEntry entry)
    {
        _allLogs.Add(entry);
        if (_allLogs.Count > MaxLogLines)
        {
            _allLogs.RemoveRange(0, MaxLogLines / 4);
        }

        if (!MatchesLogFilter(entry))
        {
            return;
        }

        _visibleLogs.Add(entry);
        while (_visibleLogs.Count > MaxLogLines / 2)
        {
            _visibleLogs.RemoveAt(0);
        }

        UpdateEmptyLogHint();

        if (_visibleLogs.Count > 0)
        {
            if (LogList.IsVisible)
            {
                LogList.ScrollIntoView(_visibleLogs[^1]);
            }

            if (StatusLogList.IsVisible)
            {
                StatusLogList.ScrollIntoView(_visibleLogs[^1]);
            }
        }
    }

    private void RefreshLogView()
    {
        _visibleLogs.Clear();
        foreach (var entry in _allLogs)
        {
            if (MatchesLogFilter(entry))
            {
                _visibleLogs.Add(entry);
            }
        }

        UpdateEmptyLogHint();
    }

    /// <summary>日志页筛选：级别与连接两个维度同时生效。</summary>
    private bool MatchesLogFilter(LogEntry entry) =>
        entry.Level >= _minLevel
        && (_connectionFilter is null
            || string.Equals(entry.Connection, _connectionFilter, StringComparison.Ordinal));

    private static string LevelTag(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => "VRB",
        LogEventLevel.Debug => "DBG",
        LogEventLevel.Information => "INF",
        LogEventLevel.Warning => "WRN",
        LogEventLevel.Error => "ERR",
        _ => "FTL",
    };
}

/// <summary>传输面板里的单文件进度行：文件名、百分比与速度。</summary>
public sealed class TransferRow : INotifyPropertyChanged
{
    public TransferRow(string path) => Path = path;

    /// <summary>文件相对路径，也作为行的稳定标识。</summary>
    public string Path { get; }

    public string Name => Path;

    public double Percent { get; private set; }

    public string PercentText { get; private set; } = "0%";

    public string SpeedText { get; private set; } = "—";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(double percent, double bytesPerSecond)
    {
        Percent = Math.Clamp(percent, 0, 100);
        PercentText = $"{Percent:0}%";
        SpeedText = FormatSpeed(bytesPerSecond);

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Percent)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PercentText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SpeedText)));
    }

    public static string FormatSpeed(double bytesPerSecond) =>
        $"{FileBrowserEntry.FormatSize((long)Math.Max(0, bytesPerSecond))}/s";
}

/// <summary>日志行：按级别提供颜色与字重，供日志列表着色显示。</summary>
public sealed class LogEntry
{
    private LogEntry(
        LogEventLevel level,
        string text,
        string? connection,
        Brush foreground,
        FontWeight fontWeight)
    {
        Level = level;
        Text = text;
        Connection = connection;
        Foreground = foreground;
        FontWeight = fontWeight;
    }

    public LogEventLevel Level { get; }

    public string Text { get; }

    /// <summary>日志所属连接（Serilog 的 Connection 上下文）；应用自身日志为「应用」。</summary>
    public string? Connection { get; }

    public Brush Foreground { get; }

    public FontWeight FontWeight { get; }

    public static LogEntry Create(LogEventLevel level, string text, string? connection) =>
        new(level, text, connection, BrushFor(level), WeightFor(level));

    private static Brush BrushFor(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose or LogEventLevel.Debug => Frozen(0x9C, 0x95, 0xAB),
        LogEventLevel.Warning => Frozen(0xB7, 0x79, 0x1F),
        LogEventLevel.Error => Frozen(0xD0, 0x34, 0x2C),
        LogEventLevel.Fatal => Frozen(0xA6, 0x1B, 0x14),
        _ => Frozen(0x4A, 0x44, 0x58),
    };

    private static FontWeight WeightFor(LogEventLevel level) => level switch
    {
        LogEventLevel.Fatal => FontWeights.Bold,
        LogEventLevel.Warning or LogEventLevel.Error => FontWeights.SemiBold,
        _ => FontWeights.Normal,
    };

    private static SolidColorBrush Frozen(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
