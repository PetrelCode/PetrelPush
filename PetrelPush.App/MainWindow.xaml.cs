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

    private readonly SyncHost _host;
    private readonly ProfileStore _store;
    private readonly TrayIcon _tray;
    private readonly ObservableCollection<LogEntry> _visibleLogs = new();
    private readonly List<LogEntry> _allLogs = new();

    private static readonly IReadOnlyDictionary<string, FileEntry> EmptySnapshot =
        new Dictionary<string, FileEntry>(StringComparer.Ordinal);

    private LogEventLevel _minLevel = LogEventLevel.Verbose;
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

    public MainWindow(SyncHost host, ProfileStore store, TrayIcon tray, UiLogSink sink, string initialPage = "status")
    {
        InitializeComponent();

        _host = host;
        _store = store;
        _tray = tray;

        StatusLogList.ItemsSource = _visibleLogs;
        LogList.ItemsSource = _visibleLogs;
        TransferList.ItemsSource = _transferRows;

        _host.StatusChanged += OnStatusChanged;
        _host.Progress += OnProgress;
        _host.TransferProgress += OnTransferProgress;
        sink.LogEmitted += OnLogEmitted;

        Closing += OnClosing;

        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        HeaderVersionText.Text = $"v{version}";
        AboutVersionText.Text = $"v{version}";
        Title = $"PetrelPush {version}";
        AboutOsText.Text = $"Windows · {(Environment.Is64BitOperatingSystem ? "x64" : "x86")}";
        AboutRuntimeText.Text = $".NET {Environment.Version}（自包含）";

        LoadProfileToUi();
        ApplyStatus(_host.Status);
        UpdateEmptyLogHint();
        ApplyInitialPage(initialPage);
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
        _tray.SetPaused(_host.IsPaused);
    }

    /// <summary>运行中显示红色「暂停」，暂停中显示绿色「恢复」，与右上角徽章配色一致。</summary>
    private void ApplyPauseButton()
    {
        var paused = _host.IsPaused;
        PauseButton.Content = paused ? "恢复" : "暂停";
        PauseButton.Style = (Style)FindResource(paused ? "SuccessButton" : "DangerButton");
    }

    public void ShowHint(string message)
    {
        StatusHintText.Text = message;
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
        Dispatcher.BeginInvoke(new Action(() => ApplyStatus(status)));
    }

    private void OnProgress(object? sender, string message)
    {
        Dispatcher.BeginInvoke(new Action(() => StatusBarText.Text = message));
    }

    private void OnTransferProgress(object? sender, TransferProgressInfo info)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => ApplyTransferProgress(info)));
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
            StatusHintText.Text = status.LastError;
        }
        else if (status.Phase == EnginePhase.Idle)
        {
            StatusHintText.Text = string.Empty;
        }

        ApplyPauseButton();
        _tray.SetPaused(_host.IsPaused);
        _tray.SetState(MapTrayState(status.Phase), $"PetrelPush — {PhaseDisplay(status.Phase)}");
    }

    private void ApplyPhaseBadge(EnginePhase phase)
    {
        var (text, foregroundKey, backgroundKey) = phase switch
        {
            EnginePhase.Idle => ("已同步", "StateSuccessBrush", "StateSuccessSoftBrush"),
            EnginePhase.Scanning => ("扫描中", "StateWarningBrush", "StateWarningSoftBrush"),
            EnginePhase.Transferring => ("传输中", "StateWarningBrush", "StateWarningSoftBrush"),
            EnginePhase.Paused => ("已暂停", "StateErrorBrush", "StateErrorSoftBrush"),
            EnginePhase.Error => ("异常", "StateErrorBrush", "StateErrorSoftBrush"),
            _ => ("已停止", "StateIdleBrush", "StateIdleSoftBrush"),
        };

        // 右上角只显示状态文字，传输进度统一在右下角面板展示
        PhaseText.Text = text;
        PhaseText.Foreground = (Brush)FindResource(foregroundKey);
        PhaseBadge.Background = (Brush)FindResource(backgroundKey);
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
        ProfileNameBox.Text = profile.ProfileName;
        LocalRootBox.Text = profile.LocalRoot;
        RemoteRootBox.Text = profile.RemoteRoot;

        HostBox.Text = profile.Remote.Host;
        PortBox.Text = profile.Remote.Port.ToString();
        UserBox.Text = profile.Remote.User;
        AuthTypeCombo.SelectedIndex = string.Equals(profile.Remote.Auth.Type, "key", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        PrivateKeyBox.Text = profile.Remote.Auth.PrivateKeyPath ?? string.Empty;
        PasswordInput.Password = profile.Remote.Password ?? string.Empty;
        FingerprintText.Text = string.IsNullOrWhiteSpace(profile.Remote.HostKeyFingerprint)
            ? "主机指纹：未确认（首次同步前请点“测试连接”完成确认）"
            : $"主机指纹：{profile.Remote.HostKeyFingerprint}";

        DebounceBox.Text = profile.Sync.DebounceMs.ToString();
        MaxForceBox.Text = profile.Sync.MaxForceTriggerMs.ToString();
        PollBox.Text = profile.Sync.PollIntervalSec.ToString();
        DeepVerifyDaysBox.Text = profile.Sync.DeepVerifyIntervalDays.ToString();
        ParallelBox.Text = profile.Sync.MaxParallelTransfers.ToString();
        RateLimitBox.Text = profile.Sync.RateLimitKbps.ToString();
        LargeFileBox.Text = profile.Sync.LargeFileThresholdMB.ToString();
        ExecutableSuffixesBox.Text = string.Join(", ", profile.Sync.ExecutableSuffixes ?? Array.Empty<string>());

        SyncEmptyDirsCheck.IsChecked = profile.Sync.SyncEmptyDirs;
        DeleteEmptyDirsCheck.IsChecked = profile.Sync.DeleteEmptyDirs;
        FollowSymlinksCheck.IsChecked = profile.Sync.FollowSymlinks;
        DryRunCheck.IsChecked = profile.Sync.DryRun;
        AutoStartCheck.IsChecked = profile.Sync.AutoStart;

        LoadIgnoreEditor(profile);
    }

    private SyncProfile BuildProfileFromUi()
    {
        var source = _host.Profile;
        var profile = SyncProfileJson.Parse(SyncProfileJson.Serialize(source));

        profile.ProfileName = ProfileNameBox.Text.Trim();
        profile.LocalRoot = LocalRootBox.Text.Trim();
        profile.RemoteRoot = RemoteRootBox.Text.Trim();

        profile.Remote.Host = HostBox.Text.Trim();
        profile.Remote.Port = ReadInt(PortBox, 22);
        profile.Remote.User = UserBox.Text.Trim();
        profile.Remote.Auth.Type = AuthTypeCombo.SelectedIndex == 1 ? "key" : "password";
        profile.Remote.Auth.PrivateKeyPath = string.IsNullOrWhiteSpace(PrivateKeyBox.Text)
            ? null
            : PrivateKeyBox.Text.Trim();
        profile.Remote.Password = PasswordInput.Password;
        profile.Remote.Auth.PrivateKeyPassphrase = source.Remote.Auth.PrivateKeyPassphrase;

        profile.Sync.DebounceMs = Math.Max(0, ReadInt(DebounceBox, 500));
        profile.Sync.MaxForceTriggerMs = Math.Max(profile.Sync.DebounceMs, ReadInt(MaxForceBox, 5000));
        profile.Sync.PollIntervalSec = Math.Max(1, ReadInt(PollBox, 30));
        profile.Sync.DeepVerifyIntervalDays = Math.Max(1, ReadInt(DeepVerifyDaysBox, 7));
        profile.Sync.MaxParallelTransfers = Math.Max(1, ReadInt(ParallelBox, 4));
        profile.Sync.RateLimitKbps = Math.Max(0, ReadInt(RateLimitBox, 0));
        profile.Sync.LargeFileThresholdMB = Math.Max(0, ReadInt(LargeFileBox, 100));

        var suffixes = (ExecutableSuffixesBox.Text ?? string.Empty)
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        profile.Sync.ExecutableSuffixes = suffixes.Length > 0 ? suffixes : source.Sync.ExecutableSuffixes;

        profile.Sync.SyncEmptyDirs = SyncEmptyDirsCheck.IsChecked == true;
        profile.Sync.DeleteEmptyDirs = DeleteEmptyDirsCheck.IsChecked == true;
        profile.Sync.FollowSymlinks = FollowSymlinksCheck.IsChecked == true;
        profile.Sync.DryRun = DryRunCheck.IsChecked == true;
        profile.Sync.AutoStart = AutoStartCheck.IsChecked == true;

        return profile;
    }

    private void LoadIgnoreEditor(SyncProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.LocalRoot))
        {
            IgnorePathText.Text = "（请先在「配置」页设置本地目录）";
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

    private static int ReadInt(TextBox box, int fallback)
    {
        return int.TryParse((box.Text ?? string.Empty).Trim(), out var value) ? value : fallback;
    }

    /// <summary>远端根目录命中系统目录（或其上级）时，只做一次二次确认，不构成对用法的限制。</summary>
    private bool ConfirmProtectedRemoteRoot(SyncProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.RemoteRoot) || profile.ProtectedRootAcknowledged)
        {
            return true;
        }

        bool isProtected;
        try
        {
            isProtected = PathGuard.IsProtectedRoot(profile.RemoteRoot);
        }
        catch (ArgumentException)
        {
            return true;
        }

        if (!isProtected)
        {
            return true;
        }

        var answer = MessageBox.Show(
            this,
            $"远端根目录 \"{profile.RemoteRoot}\" 位于系统目录或其上级。\n继续同步可能覆盖系统文件，确认使用这个目录？",
            "PetrelPush — 远端根目录确认",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            return false;
        }

        profile.ProtectedRootAcknowledged = true;
        return true;
    }

    private static string PhaseDisplay(EnginePhase phase) => phase switch
    {
        EnginePhase.Idle => "空闲（已同步）",
        EnginePhase.Scanning => "扫描中",
        EnginePhase.Transferring => "传输中",
        EnginePhase.Paused => "已暂停",
        EnginePhase.Error => "异常",
        _ => "已停止",
    };

    private static TrayState MapTrayState(EnginePhase phase) => phase switch
    {
        EnginePhase.Scanning or EnginePhase.Transferring => TrayState.Busy,
        EnginePhase.Error => TrayState.Error,
        EnginePhase.Paused => TrayState.Paused,
        _ => TrayState.Idle,
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
        LocalDeleteMenuItem.IsEnabled = ready && LocalFileList.SelectedItem is FileBrowserEntry;
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

        var existing = RemoteFileList.ItemsSource is IEnumerable<FileBrowserEntry> items
            ? items.Where(x => !x.IsDirectory).Select(x => x.Name).ToHashSet(StringComparer.Ordinal)
            : null;
        var conflicts = existing is null ? 0 : files.Count(file => existing.Contains(Path.GetFileName(file)));
        if (conflicts > 0 && !ConfirmOverwrite($"远端当前目录已存在 {conflicts} 个同名文件，继续将覆盖。是否继续？"))
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
                var relative = _remoteBrowsePath.Length == 0 ? name : _remoteBrowsePath + "/" + name;
                var index = i + 1;

                var progress = new Progress<long>(bytes => StatusBarText.Text =
                    $"上传中（{index}/{files.Length}）{name}：{FileBrowserEntry.FormatSize(bytes)}");

                ShowMessage($"正在上传（{index}/{files.Length}）{name}…");
                total += await _host.UploadLocalFileAsync(file, relative, progress, CancellationToken.None);
            }

            var target = BuildRemoteDisplayPath(_host.Profile.RemoteRoot, _remoteBrowsePath);
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

    private void OnBrowseLocal(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择要同步的本地目录" };
        var current = LocalRootBox.Text.Trim();
        if (Directory.Exists(current))
        {
            dialog.InitialDirectory = current;
        }

        if (dialog.ShowDialog(this) == true)
        {
            LocalRootBox.Text = dialog.FolderName;
        }
    }

    private void OnBrowseKey(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 SSH 私钥",
            Filter = "所有文件|*.*",
        };

        if (dialog.ShowDialog(this) == true)
        {
            PrivateKeyBox.Text = dialog.FileName;
        }
    }

    private void OnAuthTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PasswordInput is null || PrivateKeyBox is null)
        {
            return;
        }

        var useKey = AuthTypeCombo.SelectedIndex == 1;
        PasswordInput.IsEnabled = !useKey;
        PrivateKeyBox.IsEnabled = useKey;
    }

    private async void OnTestConnection(object sender, RoutedEventArgs e)
    {
        var profile = BuildProfileFromUi();
        var errors = profile.Validate().ToList();
        if (errors.Count > 0)
        {
            TestResultText.Foreground = Brushes.Firebrick;
            TestResultText.Text = string.Join("；", errors);
            return;
        }

        if (!ConfirmProtectedRemoteRoot(profile))
        {
            TestResultText.Foreground = Brushes.DimGray;
            TestResultText.Text = "已取消（远端根目录位于系统目录）";
            return;
        }

        TestResultText.Foreground = Brushes.DimGray;
        TestResultText.Text = "连接中…";
        try
        {
            await _host.TestConnectionAsync(profile, PasswordInput.Password);
            await _host.RestartAsync(profile);
            TestResultText.Foreground = Brushes.Green;
            TestResultText.Text = $"连接成功（{DateTime.Now:HH:mm:ss}），配置已保存并生效";
            LoadProfileToUi();
            InvalidateFilePage();
        }
        catch (Exception ex)
        {
            TestResultText.Foreground = Brushes.Firebrick;
            TestResultText.Text = ex.Message;
            _host.RequestSync();
        }
    }

    private async void OnSaveConfig(object sender, RoutedEventArgs e)
    {
        var profile = BuildProfileFromUi();
        var errors = profile.Validate().ToList();
        if (errors.Count > 0)
        {
            SaveResultText.Foreground = Brushes.Firebrick;
            SaveResultText.Text = string.Join("；", errors);
            return;
        }

        if (!ConfirmProtectedRemoteRoot(profile))
        {
            SaveResultText.Foreground = Brushes.DimGray;
            SaveResultText.Text = "已取消（远端根目录位于系统目录）";
            return;
        }

        try
        {
            await _host.SaveAndRestartAsync(profile, PasswordInput.Password);
            StartupRegistration.Apply(profile.Sync.AutoStart);
            SaveResultText.Foreground = Brushes.Green;
            SaveResultText.Text = $"已保存并应用（{DateTime.Now:HH:mm:ss}）";
            LoadProfileToUi();
            InvalidateFilePage();
        }
        catch (Exception ex)
        {
            SaveResultText.Foreground = Brushes.Firebrick;
            SaveResultText.Text = ex.Message;
        }
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

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        var directory = _store.GetLogDirectory(_host.Profile.ProfileId);
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
    }

    private void OnClearLogs(object sender, RoutedEventArgs e)
    {
        _allLogs.Clear();
        _visibleLogs.Clear();
        UpdateEmptyLogHint();
    }

    private void UpdateEmptyLogHint()
    {
        EmptyLogHint.Visibility = _visibleLogs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnLogEmitted(LogEvent logEvent)
    {
        var line = $"[{logEvent.Timestamp:HH:mm:ss} {LevelTag(logEvent.Level)}] {logEvent.RenderMessage()}";
        if (logEvent.Exception is not null)
        {
            line += $"  ({logEvent.Exception.Message})";
        }

        Dispatcher.BeginInvoke(new Action(() => AppendLog(LogEntry.Create(logEvent.Level, line))));
    }

    private void AppendLog(LogEntry entry)
    {
        _allLogs.Add(entry);
        if (_allLogs.Count > MaxLogLines)
        {
            _allLogs.RemoveRange(0, MaxLogLines / 4);
        }

        if (entry.Level < _minLevel)
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
            if (entry.Level >= _minLevel)
            {
                _visibleLogs.Add(entry);
            }
        }

        UpdateEmptyLogHint();
    }

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
    private LogEntry(LogEventLevel level, string text, Brush foreground, FontWeight fontWeight)
    {
        Level = level;
        Text = text;
        Foreground = foreground;
        FontWeight = fontWeight;
    }

    public LogEventLevel Level { get; }

    public string Text { get; }

    public Brush Foreground { get; }

    public FontWeight FontWeight { get; }

    public static LogEntry Create(LogEventLevel level, string text) =>
        new(level, text, BrushFor(level), WeightFor(level));

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
