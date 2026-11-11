using System;
using System.IO;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using PetrelPush.Core.Config;
using PetrelPush.Core.Model;

namespace PetrelPush.App;

public static class LoggingSetup
{
    /// <summary>
    /// 实际写入的日志目录。多连接共用一份日志，落在启动时选中连接的目录下，
    /// 界面上的「日志」路径直接用它，避免误以为每条连接各有一份日志。
    /// </summary>
    public static string LogDirectory { get; private set; } = string.Empty;

    public static void Initialize(ProfileStore store, SyncProfile profile, UiLogSink sink)
    {
        var directory = store.GetLogDirectory(profile.ProfileId);
        Directory.CreateDirectory(directory);
        LogDirectory = directory;

        // 界面日志全量捕获（界面内可再按级别过滤）；文件日志按配置级别与保留天数滚动。
        // Connection 上下文用于区分多连接的日志：默认「应用」，SyncHost 会覆盖成连接名。
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.WithProperty("Connection", "应用")
            .WriteTo.Sink(sink)
            .WriteTo.File(
                Path.Combine(directory, "PetrelPush-.log"),
                restrictedToMinimumLevel: ParseLevel(profile.Log.Level),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: Math.Max(1, profile.Log.RetentionDays),
                shared: true,
                outputTemplate:
                "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] [{Connection}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    public static LogEventLevel ParseLevel(string? level) => level?.ToLowerInvariant() switch
    {
        "verbose" => LogEventLevel.Verbose,
        "debug" => LogEventLevel.Debug,
        "warn" or "warning" => LogEventLevel.Warning,
        "error" => LogEventLevel.Error,
        "fatal" => LogEventLevel.Fatal,
        _ => LogEventLevel.Information,
    };
}
