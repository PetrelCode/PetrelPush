using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Serilog.Events;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
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

    private LogEventLevel _minLevel = LogEventLevel.Verbose;
    private bool _allowClose;

    public MainWindow(SyncHost host, ProfileStore store, TrayIcon tray, UiLogSink sink, string initialPage = "status")
    {
        InitializeComponent();

        _host = host;
        _store = store;
        _tray = tray;

        StatusLogList.ItemsSource = _visibleLogs;
        LogList.ItemsSource = _visibleLogs;

        _host.StatusChanged += OnStatusChanged;
        _host.Progress += OnProgress;
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
        PageIgnore.Visibility = tag == "ignore" ? Visibility.Visible : Visibility.Collapsed;
        PageLog.Visibility = tag == "log" ? Visibility.Visible : Visibility.Collapsed;
        PageHelp.Visibility = tag == "help" ? Visibility.Visible : Visibility.Collapsed;
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

        PauseButton.Content = _host.IsPaused ? "恢复" : "暂停";
        _tray.SetPaused(_host.IsPaused);
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

    private void ApplyStatus(EngineStatus status)
    {
        ApplyPhaseBadge(status.Phase);

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

        PauseButton.Content = _host.IsPaused ? "恢复" : "暂停";
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
            EnginePhase.Paused => ("已暂停", "StateIdleBrush", "StateIdleSoftBrush"),
            EnginePhase.Error => ("异常", "StateErrorBrush", "StateErrorSoftBrush"),
            _ => ("已停止", "StateIdleBrush", "StateIdleSoftBrush"),
        };

        var foreground = (Brush)FindResource(foregroundKey);
        PhaseText.Text = text;
        PhaseText.Foreground = foreground;
        PhaseDot.Fill = foreground;
        PhaseBadge.Background = (Brush)FindResource(backgroundKey);
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
