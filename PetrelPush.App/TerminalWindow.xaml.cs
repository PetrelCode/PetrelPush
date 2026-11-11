using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using PetrelPush.Core.Abstractions;
using PetrelPush.Core.Model;
using PetrelPush.Transport;
using Serilog;

namespace PetrelPush.App;

/// <summary>
/// 交互式终端窗口：WebView2 承载 xterm.js 负责渲染，SSH ShellStream 提供 PTY 字节流。
/// 窗口内不实现任何终端仿真逻辑，转义序列全部交给前端解析。
/// </summary>
public partial class TerminalWindow : Window
{
    private const string VirtualHost = "petrelpush.terminal";

    /// <summary>单次投递到前端的最大字节数，避免单条消息过大导致卡顿。</summary>
    private const int MaxFlushBytes = 256 * 1024;

    /// <summary>
    /// 全局共享的 WebView2 环境：所有终端窗口共用同一组 browser 进程。
    /// 每个窗口各自 CreateAsync 会拉起独立的一套 msedgewebview2.exe，关窗后残留明显。
    /// </summary>
    private static readonly SemaphoreSlim EnvironmentGate = new(1, 1);
    private static CoreWebView2Environment? _sharedEnvironment;
    private static int _openWindows;

    private readonly string _targetLabel;
    private readonly string _workingDirectory;
    private readonly Func<int, int, CancellationToken, Task<ITerminalSession>> _connectAsync;
    private readonly bool _isRemoteSession;
    private readonly ConcurrentQueue<byte[]> _pending = new();

    /// <summary>快速命令 / 快速路径（本窗口私有副本，改动后回写给 profile）。</summary>
    private readonly QuickAccessOptions _quickAccess;
    private readonly Action<QuickAccessOptions>? _persistQuickAccess;

    private ITerminalSession? _session;
    private CancellationTokenSource? _connectCts;
    private bool _closing;
    private int _flushScheduled;
    private int _firstChunkLogged;
    private int _lastColumns = 100;
    private int _lastRows = 30;

    /// <summary>面板当前显示的是路径（true）还是命令（false）。</summary>
    private bool _quickPanelShowsPaths;

    private TerminalWindow(
        string targetLabel,
        string workingDirectory,
        Func<int, int, CancellationToken, Task<ITerminalSession>> connectAsync,
        bool isRemoteSession,
        QuickAccessOptions? quickAccess,
        Action<QuickAccessOptions>? persistQuickAccess)
    {
        InitializeComponent();

        // 终端窗口整体是深色底，标题栏一并切成深色
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);

        _targetLabel = targetLabel;
        _workingDirectory = workingDirectory;
        _connectAsync = connectAsync;
        _isRemoteSession = isRemoteSession;
        _quickAccess = quickAccess?.Clone() ?? new QuickAccessOptions();
        _persistQuickAccess = persistQuickAccess;

        Title = $"终端 — {targetLabel}";
        TargetText.Text = targetLabel;
        PathText.Text = workingDirectory;

        Interlocked.Increment(ref _openWindows);

        Loaded += OnWindowLoaded;
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
    }

    /// <summary>本会话使用的快速路径列表：本地终端看 LocalPaths，远端终端看 RemotePaths。</summary>
    private List<string> QuickPaths =>
        _isRemoteSession
            ? _quickAccess.RemotePaths ??= new List<string>()
            : _quickAccess.LocalPaths ??= new List<string>();

    /// <summary>远端终端：SSH PTY，连上后自动 cd 到文件页当前目录。</summary>
    public static TerminalWindow ForRemote(
        SyncProfile profile,
        string workingDirectory,
        Action<QuickAccessOptions>? persistQuickAccess = null) =>
        new(
            SshShellSession.Label(profile),
            workingDirectory,
            (columns, rows, token) => ConnectRemoteAsync(profile, columns, rows, token),
            isRemoteSession: true,
            profile.QuickAccess,
            persistQuickAccess);

    /// <summary>本地终端：ConPTY，工作目录在进程启动参数里指定，不需要再发 cd。</summary>
    public static TerminalWindow ForLocal(
        string workingDirectory,
        string? shellPreference = null,
        QuickAccessOptions? quickAccess = null,
        Action<QuickAccessOptions>? persistQuickAccess = null) =>
        new(
            "本地终端",
            workingDirectory,
            (columns, rows, token) => ConnectLocalAsync(workingDirectory, shellPreference, columns, rows, token),
            isRemoteSession: false,
            quickAccess,
            persistQuickAccess);

    private static async Task<ITerminalSession> ConnectRemoteAsync(
        SyncProfile profile,
        int columns,
        int rows,
        CancellationToken cancellationToken)
        => await SshShellSession.ConnectAsync(profile, columns, rows, cancellationToken).ConfigureAwait(true);

    private static Task<ITerminalSession> ConnectLocalAsync(
        string workingDirectory,
        string? shellPreference,
        int columns,
        int rows,
        CancellationToken cancellationToken)
        => Task.Run<ITerminalSession>(
            () => ConPtySession.Start(columns, rows, workingDirectory, shellPreference),
            cancellationToken);

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnWindowLoaded;

        // 深色底：避免 WebView2 初始化期间闪白
        TerminalView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x1B, 0x1B, 0x26);
        SetStatus("正在初始化…", "#B7791F");

        try
        {
            await InitializeWebViewAsync();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            SetStatus("缺少运行时", "#D0342C");
            Log.Error("WebView2 运行时缺失");
            MessageBox.Show(
                this,
                "终端渲染依赖 Microsoft Edge WebView2 Runtime，当前系统未安装。\n\n"
                + "Windows 11 已预装；Windows 10 可通过 Edge 更新自动安装，"
                + "或从微软官网下载 Evergreen Runtime 安装后重新打开本窗口。",
                "PetrelPush — 缺少 WebView2 运行时",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Close();
        }
        catch (Exception ex)
        {
            SetStatus("初始化失败", "#D0342C");
            Log.Error(ex, "终端渲染层初始化失败");
            MessageBox.Show(
                this,
                ex.Message,
                "PetrelPush — 终端渲染层初始化失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Close();
        }
    }

    private async Task InitializeWebViewAsync()
    {
        var environment = await GetSharedEnvironmentAsync();
        await TerminalView.EnsureCoreWebView2Async(environment);

        var core = TerminalView.CoreWebView2;
        if (core is null)
        {
            throw new InvalidOperationException("WebView2 初始化未返回有效实例");
        }

        var settings = core.Settings;
        // 关闭浏览器加速键：让 Ctrl+C / Ctrl+V 走 DOM，由 xterm.js 与 shell 自己处理
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        // 关掉 WebView2 自带的右键菜单（表情符号 / 剪切 / 复制 / 粘贴…）：
        // 右键由 app.js 拦截，实现「有选中内容则复制，否则直接粘贴」
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDevToolsEnabled = true;
        settings.IsWebMessageEnabled = true;

        core.WebMessageReceived += OnWebMessageReceived;

        var webRoot = Path.Combine(AppContext.BaseDirectory, "Terminal", "wwwroot");
        if (!Directory.Exists(webRoot))
        {
            throw new DirectoryNotFoundException($"终端资源目录缺失：{webRoot}");
        }

        core.SetVirtualHostNameToFolderMapping(
            VirtualHost,
            webRoot,
            CoreWebView2HostResourceAccessKind.Allow);

        core.Navigate($"https://{VirtualHost}/index.html");
        Log.Information("终端渲染层已初始化（WebView2 + xterm.js）：{Root}", webRoot);
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string type;
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            root = document.RootElement.Clone();
            type = root.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            return;
        }

        switch (type)
        {
            case "ready":
                await ConnectAsync(ReadInt(root, "cols", 100), ReadInt(root, "rows", 30));
                break;

            case "retry":
                await ConnectAsync(_lastColumns, _lastRows);
                break;

            case "input":
                if (root.TryGetProperty("data", out var dataElement) &&
                    dataElement.GetString() is { Length: > 0 } text)
                {
                    _session?.SendText(text);
                }

                break;

            case "paste":
                PasteFromClipboard();
                break;

            case "copy":
                if (root.TryGetProperty("text", out var copyElement) &&
                    copyElement.GetString() is { Length: > 0 } copyText)
                {
                    CopyToClipboard(copyText);
                }

                break;

            case "resize":
                var cols = ReadInt(root, "cols", _lastColumns);
                var rows = ReadInt(root, "rows", _lastRows);
                if (cols == _lastColumns && rows == _lastRows)
                {
                    break;
                }

                _lastColumns = cols;
                _lastRows = rows;
                _session?.Resize(cols, rows);
                break;
        }
    }

    private static int ReadInt(JsonElement root, string name, int fallback) =>
        root.TryGetProperty(name, out var element) && element.TryGetInt32(out var value) && value > 0
            ? value
            : fallback;

    /// <summary>
    /// 右键粘贴：剪贴板由宿主读取，避免 WebView2 的剪贴板权限提示；
    /// 内容回传给前端后交给 xterm 的 paste，仍走正常的输入通道。
    /// </summary>
    private void PasteFromClipboard()
    {
        string text;
        try
        {
            text = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            Log.Debug(ex, "读取剪贴板失败");
            return;
        }

        if (text.Length > 0)
        {
            PostToWeb(new { type = "paste", text });
        }
    }

    /// <summary>右键复制：写回系统剪贴板（前端 navigator.clipboard 在 WebView2 中可能受权限限制）。</summary>
    private void CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException
                                       or ArgumentException
                                       or InvalidOperationException)
        {
            Log.Debug(ex, "写入剪贴板失败");
        }
    }

    private async Task ConnectAsync(int columns, int rows)
    {
        CloseSession();

        _connectCts?.Cancel();
        _connectCts?.Dispose();
        _connectCts = new CancellationTokenSource();

        _lastColumns = columns;
        _lastRows = rows;

        SetStatus("正在连接…", "#B7791F");
        Notify($"正在连接 {_targetLabel} …", NoticeColor.Info);

        Log.Information(
            "终端会话连接：{Target}，工作目录 {Directory}，PTY {Columns}x{Rows}",
            _targetLabel,
            _workingDirectory,
            columns,
            rows);

        ITerminalSession session;
        try
        {
            session = await _connectAsync(columns, rows, _connectCts.Token);
        }
        catch (SyncFatalException ex)
        {
            Log.Warning("终端会话连接失败：{Message}", ex.Message);
            FailSession("连接失败", ex.Message);
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "终端会话连接失败");
            FailSession("连接失败", ex.Message);
            return;
        }

        if (_closing)
        {
            session.Dispose();
            return;
        }

        _session = session;
        SetStatus("已连接", "#2E9E4F");
        Log.Information("终端会话已建立：{Target}", _targetLabel);

        // 清掉「正在连接」等临时输出，让 shell 从干净屏幕开始
        PostToWeb(new { type = "state", value = "connected" });
        PostToWeb(new { type = "reset" });

        // 远端：切到文件页当前目录（目录不存在时回落到 home）
        // 本地：工作目录已在 CreateProcess 参数里指定，无需再发命令
        if (_isRemoteSession)
        {
            session.SendCommand($"cd {QuotePosix(_workingDirectory)} 2>/dev/null || cd ~");
        }

        _ = PumpOutputAsync(session);
    }

    /// <summary>
    /// 连接失败：把原因写进终端内容区并提供按键重试。
    /// 不使用 WPF 覆盖层 —— WebView2 是 HWND 控件，会遮挡同一区域内的所有 WPF 元素。
    /// </summary>
    private void FailSession(string title, string detail)
    {
        SetStatus(title, "#D0342C");
        PostToWeb(new { type = "state", value = "failed" });
        Notify(title, NoticeColor.Error);
        Notify(detail, NoticeColor.Error);
        Notify("按 Enter 重新连接。", NoticeColor.Hint);
    }

    private enum NoticeColor
    {
        Info,
        Error,
        Hint,
    }

    private void Notify(string text, NoticeColor color)
    {
        var value = color switch
        {
            NoticeColor.Error => "error",
            NoticeColor.Hint => "hint",
            _ => "info",
        };

        PostToWeb(new { type = "notice", text, color = value });
    }

    /// <summary>POSIX 单引号转义：路径中的单引号写成 '\'' 形式。</summary>
    private static string QuotePosix(string path) => "'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    // ── 快速命令 / 快速路径 ────────────────────────────────────────────────
    // 面板放在顶栏下方独立一行（不用 Popup）：WebView2 是 HWND 宿主控件，浮层会被遮挡。

    private void OnToggleQuickCommands(object sender, RoutedEventArgs e)
    {
        if (QuickCommandButton.IsChecked == true)
        {
            ShowQuickPanel(showPaths: false);
        }
        else
        {
            HideQuickPanel();
        }
    }

    private void OnToggleQuickPaths(object sender, RoutedEventArgs e)
    {
        if (QuickPathButton.IsChecked == true)
        {
            ShowQuickPanel(showPaths: true);
        }
        else
        {
            HideQuickPanel();
        }
    }

    private void OnCloseQuickPanel(object sender, RoutedEventArgs e) => HideQuickPanel();

    private void ShowQuickPanel(bool showPaths)
    {
        _quickPanelShowsPaths = showPaths;
        QuickCommandButton.IsChecked = !showPaths;
        QuickPathButton.IsChecked = showPaths;

        QuickPanelTitle.Text = showPaths
            ? (_isRemoteSession ? "快速路径 · 远端" : "快速路径 · 本地")
            : "快速命令";
        QuickAddButton.Content = showPaths ? "＋ 添加路径" : "＋ 添加命令";
        QuickPanel.Visibility = Visibility.Visible;

        RefreshQuickPanel();
    }

    private void HideQuickPanel()
    {
        QuickPanel.Visibility = Visibility.Collapsed;
        QuickCommandButton.IsChecked = false;
        QuickPathButton.IsChecked = false;
    }

    private void RefreshQuickPanel()
    {
        var showPaths = _quickPanelShowsPaths;
        var items = (showPaths ? QuickPaths : _quickAccess.Commands) ?? new List<string>();

        QuickItemList.ItemsSource = items.Select(text => new QuickItem(text)).ToArray();

        QuickEmptyText.Text = showPaths
            ? (_isRemoteSession
                ? "还没有快速路径：点「添加路径」填一个远端目录，选中后终端会 cd 过去。"
                : "还没有快速路径：点「添加路径」挑一个本地目录，选中后终端会 cd 过去。")
            : "还没有快速命令：点「添加命令」写下要发送的内容，选中即发送到终端。";
        QuickEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>选中一条：命令原样发送，路径做一次 cd（回车符由会话按平台补齐）。</summary>
    private void OnQuickItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: QuickItem item })
        {
            return;
        }

        if (_session is null)
        {
            HideQuickPanel();
            Notify("终端尚未连接，快速入口暂不可用。", NoticeColor.Hint);
            return;
        }

        var showPaths = _quickPanelShowsPaths;
        try
        {
            if (showPaths)
            {
                _session.SendCommand(_isRemoteSession
                    ? $"cd {QuotePosix(item.Text)}"
                    : $"cd \"{item.Text}\"");
            }
            else
            {
                _session.SendCommand(item.Text);
            }

            Log.Debug("快速入口已发送：{Kind} {Text}", showPaths ? "路径" : "命令", item.Text);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "快速入口发送失败");
            Notify($"发送失败：{ex.Message}", NoticeColor.Error);
        }

        HideQuickPanel();
        PostToWeb(new { type = "focus" });
    }

    private void OnQuickRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: QuickItem item })
        {
            return;
        }

        var list = _quickPanelShowsPaths ? QuickPaths : _quickAccess.Commands;
        if (list is null || !list.Remove(item.Text))
        {
            return;
        }

        PersistQuickAccess();
        RefreshQuickPanel();
    }

    private void OnQuickAdd(object sender, RoutedEventArgs e)
    {
        if (_quickPanelShowsPaths)
        {
            AddQuickPath();
        }
        else
        {
            AddQuickCommand();
        }
    }

    private void AddQuickCommand()
    {
        var dialog = new TextInputWindow(
            "添加快速命令",
            "命令会原样发送到终端（相当于输入后回车），例如 docker compose ps",
            string.Empty)
        {
            Owner = this,
            Validate = ValidateQuickCommand,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        (_quickAccess.Commands ??= new List<string>()).Add(dialog.Value);
        PersistQuickAccess();
        RefreshQuickPanel();
    }

    private string? ValidateQuickCommand(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "命令不能为空";
        }

        if (value.IndexOfAny(['\r', '\n']) >= 0)
        {
            return "命令不能包含换行";
        }

        return _quickAccess.Commands is not null && _quickAccess.Commands.Contains(value, StringComparer.Ordinal)
            ? "该命令已在列表中"
            : null;
    }

    private void AddQuickPath()
    {
        string value;
        if (_isRemoteSession)
        {
            var initial = _workingDirectory.StartsWith('/') ? _workingDirectory : "/";
            var dialog = new TextInputWindow("添加快速路径", "远端目录（Linux 绝对路径）", initial)
            {
                Owner = this,
                Validate = ValidateQuickPath,
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            value = dialog.Value;
        }
        else
        {
            var dialog = new OpenFolderDialog { Title = "选择要加入快速路径的目录" };
            if (Directory.Exists(_workingDirectory))
            {
                dialog.InitialDirectory = _workingDirectory;
            }

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            value = dialog.FolderName;
        }

        QuickPaths.Add(value);
        PersistQuickAccess();
        RefreshQuickPanel();
    }

    private string? ValidateQuickPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "路径不能为空";
        }

        if (!value.StartsWith('/'))
        {
            return "远端路径必须以 / 开头";
        }

        if (value.IndexOfAny(['\r', '\n']) >= 0)
        {
            return "路径不能包含换行";
        }

        return QuickPaths.Contains(value, StringComparer.Ordinal) ? "该路径已在列表中" : null;
    }

    private void PersistQuickAccess()
    {
        if (_persistQuickAccess is null)
        {
            return;
        }

        try
        {
            _persistQuickAccess(_quickAccess);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "保存快速命令 / 快速路径失败");
            Notify($"保存快速入口失败：{ex.Message}", NoticeColor.Error);
        }
    }

    private async Task PumpOutputAsync(ITerminalSession session)
    {
        try
        {
            await foreach (var chunk in session.Output.ReadAllAsync().ConfigureAwait(false))
            {
                if (!ReferenceEquals(_session, session))
                {
                    return;
                }

                _pending.Enqueue(chunk);
                ScheduleFlush();
            }
        }
        catch (ChannelClosedException)
        {
        }
        catch (OperationCanceledException)
        {
        }

        if (!ReferenceEquals(_session, session) || _closing)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closing || !ReferenceEquals(_session, session))
            {
                return;
            }

            Log.Warning("终端会话已断开：{Target}", _targetLabel);
            FailSession(
                _isRemoteSession ? "连接已断开" : "终端已退出",
                _isRemoteSession ? "远端会话已结束。" : "终端进程已退出。");
        }));
    }

    private void ScheduleFlush()
    {
        if (Interlocked.Exchange(ref _flushScheduled, 1) != 0)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(new Action(FlushPending), DispatcherPriority.Background);
    }

    private void FlushPending()
    {
        Interlocked.Exchange(ref _flushScheduled, 0);

        using var buffer = new MemoryStream();
        while (_pending.TryDequeue(out var chunk))
        {
            // 只记首批输出的规模与是否带 ANSI 转义序列，不记内容（避免把终端文字写进日志）
            if (Interlocked.Exchange(ref _firstChunkLogged, 1) == 0)
            {
                Log.Information(
                    "终端首批输出：{Bytes} 字节，含 ANSI 转义序列：{HasEsc}",
                    chunk.Length,
                    Array.IndexOf(chunk, (byte)0x1b) >= 0);
            }

            buffer.Write(chunk, 0, chunk.Length);
            if (buffer.Length >= MaxFlushBytes)
            {
                break;
            }
        }

        if (buffer.Length == 0)
        {
            return;
        }

        var base64 = Convert.ToBase64String(buffer.GetBuffer(), 0, (int)buffer.Length);
        PostToWeb(new { type = "data", b64 = base64 });

        if (!_pending.IsEmpty)
        {
            ScheduleFlush();
        }
    }

    private void PostToWeb(object payload)
    {
        var core = TerminalView.CoreWebView2;
        if (core is null)
        {
            return;
        }

        try
        {
            core.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
        }
        catch (InvalidOperationException)
        {
            // WebView2 正在销毁
        }
    }

    private void SetStatus(string text, string colorHex)
    {
        StatusText.Text = text;
        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
            brush.Freeze();
            StatusDot.Fill = brush;
        }
        catch (FormatException)
        {
        }
    }

    private void CloseSession()
    {
        var session = Interlocked.Exchange(ref _session, null);
        session?.Dispose();
        _pending.Clear();
    }

    private static async Task<CoreWebView2Environment> GetSharedEnvironmentAsync()
    {
        if (_sharedEnvironment is not null)
        {
            return _sharedEnvironment;
        }

        await EnvironmentGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_sharedEnvironment is null)
            {
                var userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PetrelPush",
                    "WebView2");
                Directory.CreateDirectory(userDataFolder);
                _sharedEnvironment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            }

            return _sharedEnvironment;
        }
        finally
        {
            EnvironmentGate.Release();
        }
    }

    /// <summary>关闭阶段：只停会话，不动 WebView2（此时控件仍在视觉树中）。</summary>
    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        _closing = true;

        _connectCts?.Cancel();
        CloseSession();

        if (TerminalView.CoreWebView2 is not null)
        {
            TerminalView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
        }
    }

    /// <summary>已关闭阶段：释放 WebView2 控制器与其 renderer 进程。</summary>
    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _closing = true;

        try
        {
            TerminalView.Dispose();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            Log.Debug(ex, "释放 WebView2 控件时出现异常（可忽略）");
        }

        _connectCts?.Dispose();
        _connectCts = null;

        // 最后一个终端窗口关闭后断开共享环境引用，让 WebView2 Runtime 回收 browser 进程
        if (Interlocked.Decrement(ref _openWindows) <= 0)
        {
            _sharedEnvironment = null;
            Log.Debug("已关闭最后一个终端窗口，释放 WebView2 共享环境");
        }
    }
}

/// <summary>快速入口面板的一行（ItemsControl 的数据项，全文即内容）。</summary>
internal sealed class QuickItem(string text)
{
    public string Text { get; } = text;
}
