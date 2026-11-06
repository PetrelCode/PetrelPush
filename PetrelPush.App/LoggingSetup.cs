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
    public static void Initialize(ProfileStore store, SyncProfile profile, UiLogSink sink)
    {
        var directory = store.GetLogDirectory(profile.ProfileId);
        Directory.CreateDirectory(directory);

        // 界面日志全量捕获（界面内可再按级别过滤）；文件日志按配置级别与保留天数滚动
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(sink)
            .WriteTo.File(
                Path.Combine(directory, "PetrelPush-.log"),
                restrictedToMinimumLevel: ParseLevel(profile.Log.Level),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: Math.Max(1, profile.Log.RetentionDays),
                shared: true)
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
