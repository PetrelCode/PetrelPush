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

    /// <summary>设置页回填控件时屏蔽变更回调，避免把读回的值再写一遍注册表。</summary>
    private bool _suppressAutoStartChange;

    /// <summary>设置页回填「本地终端」下拉框时屏蔽变更回调。</summary>
    private bool _suppressLocalTerminalChange;

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
        PhaseDot.Fill = (Brush)FindResource(foregroundKey);
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
        SettingsLogPathText.Text = _store.GetLogDirectory(profile.ProfileId);
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

    /// <summary>以「已保存的配置」为基础做副本，各卡片只覆盖自己那几个字段。</summary>
    private SyncProfile CloneProfile() => _host.Profile.Clone();

    /// <summary>「基本」卡片：配置名称、本地目录、远端目录。</summary>
    private SyncProfile BuildBasicProfileFromUi()
    {
        var profile = CloneProfile();
        profile.ProfileName = ProfileNameBox.Text.Trim();
        profile.LocalRoot = LocalRootBox.Text.Trim();
        profile.RemoteRoot = RemoteRootBox.Text.Trim();
        return profile;
    }

    /// <summary>「远端连接」卡片：主机、端口、用户、认证方式与凭据。</summary>
    private SyncProfile BuildRemoteProfileFromUi()
    {
        var profile = CloneProfile();
        profile.Remote.Host = HostBox.Text.Trim();
        profile.Remote.Port = ReadInt(PortBox, 22);
        profile.Remote.User = UserBox.Text.Trim();
        profile.Remote.Auth.Type = AuthTypeCombo.SelectedIndex == 1 ? "key" : "password";
        profile.Remote.Auth.PrivateKeyPath = string.IsNullOrWhiteSpace(PrivateKeyBox.Text)
            ? null
            : PrivateKeyBox.Text.Trim();

        // 口令框留空表示沿用已保存的凭据，避免把 secrets.bin 清空
        var typedPassword = PasswordInput.Password;
        if (!string.IsNullOrEmpty(typedPassword))
        {
            profile.Remote.Password = typedPassword;
        }

        return profile;
    }

    /// <summary>「设置 · 同步参数」卡片：去抖、轮询、并发、限速、空目录等。</summary>
    private SyncProfile BuildSyncProfileFromUi()
    {
        var source = _host.Profile;
        var profile = CloneProfile();

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

        // 开机自启在「设置」页即时保存，这里沿用源 profile 的值，避免保存时被覆盖
        profile.Sync.AutoStart = source.Sync.AutoStart;

        return profile;
    }

    /// <summary>「基本」卡片校验：只看配置名称、本地目录、远端目录。</summary>
    private static List<string> ValidateBasicCard(SyncProfile profile)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(profile.LocalRoot))
        {
            errors.Add("请填写本地目录");
        }
        else if (!Directory.Exists(profile.LocalRoot))
        {
            errors.Add($"本地目录不存在：{profile.LocalRoot}");
        }

        if (!(profile.RemoteRoot ?? string.Empty).StartsWith('/'))
        {
            errors.Add("远端目录必须是绝对路径（以 / 开头）");
        }

        return errors;
    }

    /// <summary>「远端连接」卡片校验：只看主机、端口、用户与凭据。</summary>
    private static List<string> ValidateRemoteCard(SyncProfile profile)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(profile.Remote.Host))
        {
            errors.Add("请填写主机");
        }

        if (profile.Remote.Port is < 1 or > 65535)
        {
            errors.Add("端口必须在 1~65535 之间");
        }

        if (string.IsNullOrWhiteSpace(profile.Remote.User))
        {
            errors.Add("请填写用户名");
        }

        if (string.Equals(profile.Remote.Auth.Type, "key", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(profile.Remote.Auth.PrivateKeyPath))
            {
                errors.Add("密钥认证需要填写私钥路径");
            }
            else if (!File.Exists(profile.Remote.Auth.PrivateKeyPath))
            {
                errors.Add($"私钥文件不存在：{profile.Remote.Auth.PrivateKeyPath}");
            }
        }
        else if (string.IsNullOrEmpty(profile.Remote.Password))
        {
            errors.Add("密码认证需要填写密码");
        }

        return errors;
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

        try
        {
            var window = TerminalWindow.ForLocal(directory, preference);
            window.Owner = this;
            window.Show();
        }
        catch (Exception ex)
        {
            ShowMessage($"打开本地终端失败：{ex.Message}", isError: true);
        }
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

        try
        {
            var window = TerminalWindow.ForRemote(_host.Profile, absolute);
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

    /// <summary>
    /// 「远端连接」卡片右上角的「测试连接」：先保存本卡片的连接信息，再测试连接并重建引擎。
    /// </summary>
    private async void OnTestConnection(object sender, RoutedEventArgs e)
    {
        var profile = BuildRemoteProfileFromUi();
        var errors = ValidateRemoteCard(profile);
        if (errors.Count > 0)
        {
            ShowResult(TestResultText, string.Join("；", errors), Brushes.Firebrick);
            return;
        }

        if (!ConfirmProtectedRemoteRoot(profile))
        {
            ShowResult(TestResultText, "已取消（远端根目录位于系统目录）", Brushes.DimGray);
            return;
        }

        ShowResult(TestResultText, "连接中…", Brushes.DimGray);
        try
        {
            await _host.TestConnectionAsync(profile, null);
            await _host.RestartAsync(profile);
            ShowResult(TestResultText, $"连接成功（{DateTime.Now:HH:mm:ss}），远端连接配置已保存并生效", Brushes.Green);
            RefreshRemoteCard();
        }
        catch (Exception ex)
        {
            ShowResult(TestResultText, ex.Message, Brushes.Firebrick);
            _host.RequestSync();
        }
    }

    /// <summary>「基本」卡片右上角的「保存」：只保存配置名称与两个目录，然后重建引擎。</summary>
    private async void OnSaveBasicConfig(object sender, RoutedEventArgs e)
    {
        var profile = BuildBasicProfileFromUi();
        var errors = ValidateBasicCard(profile);
        if (errors.Count > 0)
        {
            ShowResult(BasicSaveResultText, string.Join("；", errors), Brushes.Firebrick);
            return;
        }

        if (!ConfirmProtectedRemoteRoot(profile))
        {
            ShowResult(BasicSaveResultText, "已取消（远端根目录位于系统目录）", Brushes.DimGray);
            return;
        }

        var previousLocalRoot = _host.Profile.LocalRoot;
        var previousRemoteRoot = _host.Profile.RemoteRoot;
        try
        {
            // 凭据不属于本卡片，传 null 沿用已保存的口令
            await _host.SaveAndRestartAsync(profile, null);

            var rootsChanged =
                !string.Equals(previousLocalRoot, profile.LocalRoot, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(previousRemoteRoot, profile.RemoteRoot, StringComparison.Ordinal);
            ShowResult(
                BasicSaveResultText,
                rootsChanged
                    ? $"已保存并应用（{DateTime.Now:HH:mm:ss}）；同步目录已变更，历史快照会被清空，本轮将全量上传到新的远端目录"
                    : $"已保存并应用（{DateTime.Now:HH:mm:ss}）",
                Brushes.Green);
            RefreshBasicCard();
        }
        catch (Exception ex)
        {
            ShowResult(BasicSaveResultText, ex.Message, Brushes.Firebrick);
        }
    }

    /// <summary>「设置 · 同步参数」卡片右上角的「应用」：只保存同步参数。</summary>
    private async void OnSaveConfig(object sender, RoutedEventArgs e)
    {
        var profile = BuildSyncProfileFromUi();
        var errors = profile.Validate().ToList();
        if (errors.Count > 0)
        {
            ShowResult(SettingsSaveResultText, string.Join("；", errors), Brushes.Firebrick);
            return;
        }

        try
        {
            await _host.SaveAndRestartAsync(profile, null);
            StartupRegistration.Apply(profile.Sync.AutoStart);
            ShowResult(SettingsSaveResultText, $"已保存并应用（{DateTime.Now:HH:mm:ss}）", Brushes.Green);
            RefreshSyncCard();
            InvalidateFilePage();
        }
        catch (Exception ex)
        {
            ShowResult(SettingsSaveResultText, ex.Message, Brushes.Firebrick);
        }
    }

    /// <summary>在卡片右上角的结果文本里显示提示。</summary>
    private static void ShowResult(System.Windows.Controls.TextBlock target, string message, Brush brush)
    {
        target.Foreground = brush;
        target.Text = message;
    }

    /// <summary>只刷新「基本」卡片，保留其他卡片里未保存的编辑。</summary>
    private void RefreshBasicCard()
    {
        var profile = _host.Profile;
        ProfileHeader.Text = string.IsNullOrWhiteSpace(profile.ProfileName)
            ? profile.ProfileId
            : $"{profile.ProfileName}（{profile.ProfileId}）";
        ProfileNameBox.Text = profile.ProfileName;
        LocalRootBox.Text = profile.LocalRoot;
        RemoteRootBox.Text = profile.RemoteRoot;
        LoadIgnoreEditor(profile);
        InvalidateFilePage();
    }

    /// <summary>只刷新「同步参数」卡片，保留其他卡片里未保存的编辑。</summary>
    private void RefreshSyncCard()
    {
        var profile = _host.Profile;
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
    }

    /// <summary>只刷新「远端连接」卡片，保留其他卡片里未保存的编辑。</summary>
    private void RefreshRemoteCard()
    {
        var profile = _host.Profile;
        HostBox.Text = profile.Remote.Host;
        PortBox.Text = profile.Remote.Port.ToString();
        UserBox.Text = profile.Remote.User;
        AuthTypeCombo.SelectedIndex = string.Equals(profile.Remote.Auth.Type, "key", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        PrivateKeyBox.Text = profile.Remote.Auth.PrivateKeyPath ?? string.Empty;
        PasswordInput.Password = profile.Remote.Password ?? string.Empty;
        FingerprintText.Text = string.IsNullOrWhiteSpace(profile.Remote.HostKeyFingerprint)
            ? "主机指纹：未确认（首次同步前请点“测试连接”完成确认）"
            : $"主机指纹：{profile.Remote.HostKeyFingerprint}";
        InvalidateFilePage();
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
            var applied = StartupRegistration.Apply(enabled);
            _store.Save(profile);

            if (!applied)
            {
                SettingsResultText.Foreground = (Brush)FindResource("StateWarningBrush");
                SettingsResultText.Text = "已保存到配置，但写入注册表被拒绝（可能受组策略限制），登录后可能不会自动启动";
                return;
            }

            SettingsResultText.Foreground = (Brush)FindResource("StateSuccessBrush");
            SettingsResultText.Text = enabled
                ? $"已开启开机自启（{DateTime.Now:HH:mm:ss} 生效）"
                : "已关闭开机自启";
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
            ? _store.GetLogDirectory(profileId)
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
