using System;
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
    private SyncHost? _host;
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
            var profile = LoadOrCreateProfile(_store);
            if (_store.Exists(profile.ProfileId))
            {
                StartupRegistration.Apply(profile.Sync.AutoStart);
            }

            _uiSink = new UiLogSink();
            LoggingSetup.Initialize(_store, profile, _uiSink);
            Log.Information("PetrelPush 启动，profile：{Profile}", profile.ProfileId);

            _host = new SyncHost(_store, profile, Log.Logger);
            _tray = new TrayIcon();
            _window = new MainWindow(_host, _store, _tray, _uiSink, ReadInitialPage(e.Args));
            MainWindow = _window;

            _tray.ShowRequested += ShowMainWindow;
            _tray.SyncNowRequested += () => _host?.RequestSync();
            _tray.PauseResumeRequested += () => _window?.TogglePause();
            _tray.DeepVerifyRequested += () => _host?.RequestDeepVerify();
            _tray.ExitRequested += ExitApplication;

            await splash.WaitMinimumAsync(TimeSpan.FromMilliseconds(1200));

            // 主界面就绪后直接关闭启动画面（对应 splash.ts 的 destroySplashWindow）
            _window.Show();
            splash.Close();

            _ = InitializeHostAsync();
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

    private static SyncProfile LoadOrCreateProfile(ProfileStore store)
    {
        var ids = store.ListProfileIds();
        foreach (var id in ids)
        {
            try
            {
                return store.Load(id);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "读取 profile {Profile} 失败，尝试下一个", id);
            }
        }

        return new SyncProfile
        {
            ProfileId = "default",
            ProfileName = "新建同步配置",
        };
    }

    private async Task InitializeHostAsync()
    {
        try
        {
            if (_host is null || _window is null)
            {
                return;
            }

            var errors = _host.Profile.Validate().ToList();
            if (errors.Count > 0)
            {
                _window.ShowHint($"尚未开始同步：{errors[0]}（请在「配置」页补全后保存）");
                return;
            }

            await _host.StartAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动同步失败");
            _window?.ShowHint($"启动失败：{ex.Message}");
        }
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
            if (_host is not null)
            {
                await _host.DisposeAsync().ConfigureAwait(true);
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
