using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Serilog;
using PetrelPush.Core.Config;
using PetrelPush.Core.Model;

namespace PetrelPush.App;

public partial class App : Application
{
    private Mutex? _mutex;
    private ProfileStore? _store;
    private ConnectionManager? _connections;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private UiLogSink? _uiSink;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _mutex = new Mutex(true, @"Local\PetrelPush.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("PetrelPush 已在运行，请查看系统托盘。", "PetrelPush", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "界面线程未处理异常");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Error(args.ExceptionObject as Exception, "未处理异常");
        };

        var splash = new SplashWindow();
        splash.Show();

        try
        {
            // 让启动画面先完成首帧绘制
            await Task.Delay(60);

            _store = new ProfileStore();

            // 日志必须先初始化：SyncHost / SyncEngine 在构造时就会持有当时的 Log.Logger 实例，
            // 若晚于它们创建，引擎日志会写进初始化前的空 logger，界面与文件都看不到。
            _uiSink = new UiLogSink();
            LoggingSetup.Initialize(_store, LoadStartupProfile(_store), _uiSink);

            _connections = new ConnectionManager(_store, Log.Logger);
            var profile = _connections.Current.Profile;

            if (_connections.Hosts.Any(host => _store.Exists(host.Profile.ProfileId)))
            {
                // 开机自启是应用级开关：任一连接开启即注册
                StartupRegistration.Apply(_connections.Hosts.Any(host => host.Profile.Sync.AutoStart));
            }

            Log.Information("PetrelPush 启动，共 {Count} 条连接（{Enabled} 条启用）", _connections.Hosts.Count, _connections.EnabledCount);

            _tray = new TrayIcon();
            _window = new MainWindow(_connections, _store, _tray, _uiSink, ReadInitialPage(e.Args));
            MainWindow = _window;

            _tray.ShowRequested += ShowMainWindow;
            _tray.SyncNowRequested += () => _connections?.RequestSyncAll();
            _tray.PauseResumeRequested += () => _connections?.TogglePauseAll();
            _tray.DeepVerifyRequested += () => _connections?.RequestDeepVerifyAll();
            _tray.ExitRequested += ExitApplication;

            // 任一连接的状态 / 列表变化都重新聚合托盘展示
            _connections.StateChanged += (_, _) => RefreshTray();
            _connections.Changed += (_, _) => RefreshTray();

            await splash.WaitMinimumAsync(TimeSpan.FromMilliseconds(1200));

            // 主界面就绪后直接关闭启动画面（对应 splash.ts 的 destroySplashWindow）
            _window.Show();
            splash.Close();

            _ = InitializeHostsAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "启动失败");
            try
            {
                splash.Close();
            }
            catch
            {
                // 忽略关闭异常
            }

            MessageBox.Show($"PetrelPush 启动失败：{ex.Message}", "PetrelPush", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    /// <summary>支持 --page status|config|files|ignore|log|settings|help|about 直接打开指定页面。</summary>
    /// <summary>
    /// 日志初始化必须早于 ConnectionManager（SyncHost / SyncEngine 会持有初始化时的 logger 实例），
    /// 这里先用第一条已保存的连接决定日志目录、级别与保留天数；一条都没有时退回默认 profile。
    /// </summary>
    private static SyncProfile LoadStartupProfile(ProfileStore store)
    {
        foreach (var id in store.ListProfileIds())
        {
            try
            {
                return store.Load(id);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "读取连接 {Profile} 失败，尝试下一个", id);
            }
        }

        return new SyncProfile
        {
            ProfileId = "default",
            ProfileName = "新建同步配置",
        };
    }

    private static string ReadInitialPage(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--page", StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1].ToLowerInvariant();
            }
        }

        return "status";
    }

    private async Task InitializeHostsAsync()
    {
        if (_connections is null || _window is null)
        {
            return;
        }

        try
        {
            var reports = new List<string>();
            await _connections.StartEnabledAsync(reports.Add).ConfigureAwait(true);

            if (reports.Count == 1)
            {
                _window.ShowHint(reports[0]);
            }
            else if (reports.Count > 1)
            {
                _window.ShowHint($"{reports[0]}（另有 {reports.Count - 1} 条连接未启动，详见日志页）");
            }

            RefreshTray();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动同步失败");
            _window.ShowHint($"启动失败：{ex.Message}");
        }
    }

    /// <summary>把各连接状态聚合成托盘图标与提示（引擎状态可能来自后台线程）。</summary>
    private void RefreshTray()
    {
        if (_connections is null || _tray is null)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshTray));
            return;
        }

        var (state, tooltip) = _connections.DescribeTray();
        _tray.SetState(state, tooltip);
        _tray.SetPaused(_connections.IsAllPaused);
    }

    private void ShowMainWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    private async void ExitApplication()
    {
        try
        {
            if (_connections is not null)
            {
                await _connections.DisposeAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "退出时释放同步引擎失败");
        }

        _window?.AllowClose();
        _tray?.Dispose();
        Log.Information("PetrelPush 退出");
        Log.CloseAndFlush();
        Shutdown();
    }
}
