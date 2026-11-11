using System.Text;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using PetrelPush.Core.Config;
using PetrelPush.Core.Model;
using PetrelPush.Core.Pipeline;
using PetrelPush.Core.Scan;
using PetrelPush.State;
using PetrelPush.Transport;

namespace PetrelPush.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length > 0 && args[0] is "--version" or "-v" or "version")
        {
            var productVersion = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
            Console.WriteLine($"PetrelPush {productVersion}");
            return 0;
        }

        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var command = args[0].ToLowerInvariant();
        var options = ParseOptions(args[1..]);

        try
        {
            return command switch
            {
                "run" => await RunAsync(options).ConfigureAwait(false),
                "init" => Init(options),
                "test" => await TestConnectionAsync(options).ConfigureAwait(false),
                "check-ignore" => CheckIgnore(options),
                "profiles" => ListProfiles(options),
                "help" => PrintUsage(),
                _ => UnknownCommand(command),
            };
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            PrintUsage();
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"失败：{ex.Message}");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static async Task<int> RunAsync(Dictionary<string, string?> options)
    {
        var store = new ProfileStore(options.GetValueOrDefault("data-dir"));
        var profile = LoadProfile(store, options);

        if (options.ContainsKey("dry-run"))
        {
            profile.Sync.DryRun = true;
        }

        if (int.TryParse(options.GetValueOrDefault("parallel"), out var parallel))
        {
            profile.Sync.MaxParallelTransfers = Math.Max(1, parallel);
        }

        var logger = CreateLogger(store, profile);

        var errors = profile.Validate().ToList();
        if (errors.Count > 0)
        {
            foreach (var error in errors)
            {
                logger.Error("{Error}", error);
            }

            return 3;
        }

        var state = new SqliteStateStore(store.GetStateDatabasePath(profile.ProfileId), logger);
        await state.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        await using var transport = new SftpTransferExecutor(profile, logger);
        transport.HostKeyTrusted += trust =>
        {
            profile.Remote.HostKeyFingerprint = trust.Fingerprint;
            profile.Remote.HostKeyTarget = trust.Target;
            store.Save(profile);
            logger.Information("已固定主机指纹 {Fingerprint}（{Target}）", trust.Fingerprint, trust.Target);
        };

        var deep = options.ContainsKey("deep");

        if (options.ContainsKey("once"))
        {
            var runner = new CycleRunner(state, transport, logger);
            runner.Progress += (stage, message) => logger.Information("[{Stage}] {Message}", stage, message);
            var rules = IgnoreRules.Load(profile.LocalRoot, profile.Sync, logger);
            var result = await runner.RunOnceAsync(profile, rules, deep, CancellationToken.None).ConfigureAwait(false);
            PrintResult(result);
            await state.DisposeAsync().ConfigureAwait(false);
            return result.FailedTasks > 0 ? 4 : 0;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        await using var engine = new SyncEngine(() => profile, state, transport, logger);
        engine.StatusChanged += (_, status) =>
        {
            var error = status.LastError is null ? string.Empty : $"（{status.LastError}）";
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {status.Phase} {status.Message}{error}");
        };

        await engine.StartAsync(cancellation.Token).ConfigureAwait(false);
        try
        {
            await Task.Delay(Timeout.Infinite, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await engine.StopAsync().ConfigureAwait(false);
        await state.DisposeAsync().ConfigureAwait(false);
        return 0;
    }

    private static int Init(Dictionary<string, string?> options)
    {
        var store = new ProfileStore(options.GetValueOrDefault("data-dir"));
        var id = Require(options, "profile");
        var local = Require(options, "local");
        var remoteRoot = Require(options, "remote-root");
        var host = Require(options, "host");
        var user = options.GetValueOrDefault("user") ?? "root";

        var profile = store.CreateDefault(id, local, remoteRoot, host, user);
        if (int.TryParse(options.GetValueOrDefault("port"), out var port))
        {
            profile.Remote.Port = port;
        }

        var key = options.GetValueOrDefault("key");
        if (!string.IsNullOrWhiteSpace(key))
        {
            profile.Remote.Auth.Type = "key";
            profile.Remote.Auth.PrivateKeyPath = key;
        }

        if (options.TryGetValue("name", out var name) && !string.IsNullOrWhiteSpace(name))
        {
            profile.ProfileName = name;
        }

        var password = options.GetValueOrDefault("password");
        store.Save(profile, password);

        Console.WriteLine($"已创建 profile：{profile.ProfileId}");
        Console.WriteLine($"  配置：{store.GetConfigPath(profile.ProfileId)}");
        Console.WriteLine($"  状态库：{store.GetStateDatabasePath(profile.ProfileId)}");
        if (string.IsNullOrEmpty(password) && profile.Remote.Auth.Type == "password")
        {
            Console.WriteLine("  提示：未提供 --password，请在托盘界面「配置」页填写密码后保存。");
        }

        return 0;
    }

    private static async Task<int> TestConnectionAsync(Dictionary<string, string?> options)
    {
        var store = new ProfileStore(options.GetValueOrDefault("data-dir"));
        var profile = LoadProfile(store, options);
        var logger = CreateLogger(store, profile);

        await using var transport = new SftpTransferExecutor(profile, logger);
        transport.HostKeyTrusted += trust =>
        {
            profile.Remote.HostKeyFingerprint = trust.Fingerprint;
            profile.Remote.HostKeyTarget = trust.Target;
            store.Save(profile);
            Console.WriteLine($"已固定主机指纹：{trust.Fingerprint}（{trust.Target}）");
        };

        try
        {
            await transport.TestConnectionAsync(profile, CancellationToken.None).ConfigureAwait(false);
        }
        catch (HostKeyMismatchException ex)
        {
            Console.Error.WriteLine($"失败：{ex.Message}");
            Console.Error.WriteLine("如确认远端主机已重装或更换密钥，请删除配置中的 hostKeyFingerprint / hostKeyTarget 后重试。");
            return 1;
        }

        Console.WriteLine("连接测试成功。");
        return 0;
    }

    private static int CheckIgnore(Dictionary<string, string?> options)
    {
        var store = new ProfileStore(options.GetValueOrDefault("data-dir"));
        var profile = LoadProfile(store, options);
        var path = Require(options, "path");
        var isDirectory = options.ContainsKey("dir");
        var rules = IgnoreRules.Load(profile.LocalRoot, profile.Sync);

        // 加载祖先目录的 .gitignore，保证嵌套规则参与判定
        var segments = PathKeys.Normalize(path).Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = string.Empty;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            rules.EnsureDirectoryRules(current);
            current = current.Length == 0 ? segments[i] : current + "/" + segments[i];
        }

        var explanation = rules.Explain(path, isDirectory);
        Console.WriteLine($"路径：{explanation.Path}");
        Console.WriteLine($"类型：{(explanation.IsDirectory ? "目录" : "文件")}");
        Console.WriteLine($"结果：{(explanation.Ignored ? "不同步（被忽略）" : "会同步")}");
        Console.WriteLine($"原因：{explanation.Summary}");
        return 0;
    }

    private static int ListProfiles(Dictionary<string, string?> options)
    {
        var store = new ProfileStore(options.GetValueOrDefault("data-dir"));
        var ids = store.ListProfileIds();
        if (ids.Count == 0)
        {
            Console.WriteLine($"未找到 profile（目录：{store.BaseDirectory}）");
            return 0;
        }

        foreach (var id in ids)
        {
            Console.WriteLine($"{id}\t{store.GetConfigPath(id)}");
        }

        return 0;
    }

    private static int PrintUsage()
    {
        Console.WriteLine("""
            PetrelPush — Windows → Linux 项目文件实时同步工具

            用法：
              PetrelPush init --profile <id> --local <目录> --remote-root <远端目录> --host <主机>
                           [--port 22] [--user root] [--password <密码> | --key <私钥路径>] [--name <名称>]
              PetrelPush run --profile <id> [--once] [--deep] [--dry-run] [--parallel N]
              PetrelPush test --profile <id>
              PetrelPush check-ignore --profile <id> --path <相对路径> [--dir]
              PetrelPush profiles [--data-dir <目录>]

            说明：
              --once          只跑一轮后退出（用于首次验收）
              --deep          忽略 mtime 捷径，全部重算哈希
              --dry-run       只展示变更集，不做任何远端写入
              --parallel N    覆盖并发传输数（连接池大小）
              --data-dir      覆盖 profile 根目录（默认 %APPDATA%\PetrelPush\profiles）
            """);
        return 0;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"未知命令：{command}");
        PrintUsage();
        return 1;
    }

    private static void PrintResult(CycleResult result)
    {
        Console.WriteLine($"扫描：{result.ScannedFiles} 个文件 / {result.ScannedDirectories} 个目录，跳过 {result.SkippedReparsePoints} 个 reparse point");
        Console.WriteLine($"变更：{result.ChangedEntries} 个动作，完成 {result.ExecutedTasks}，失败 {result.FailedTasks}，耗时 {result.ElapsedMilliseconds} ms");
        if (result.DryRun)
        {
            Console.WriteLine("dry-run：以下动作未执行");
            foreach (var task in result.PlannedTasks)
            {
                Console.WriteLine($"  {task}");
            }
        }
    }

    private static SyncProfile LoadProfile(ProfileStore store, Dictionary<string, string?> options)
    {
        var id = Require(options, "profile");
        if (!store.Exists(id))
        {
            throw new UsageException($"profile 不存在：{id}（{store.GetConfigPath(id)}）");
        }

        return store.Load(id);
    }

    private static ILogger CreateLogger(ProfileStore store, SyncProfile profile)
    {
        var logDirectory = store.GetLogDirectory(profile.ProfileId);
        Directory.CreateDirectory(logDirectory);

        return new LoggerConfiguration()
            .MinimumLevel.ControlledBy(new LoggingLevelSwitch(ParseLevel(profile.Log.Level)))
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                Path.Combine(logDirectory, "PetrelPush-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: Math.Max(1, profile.Log.RetentionDays),
                shared: true)
            .CreateLogger();
    }

    private static LogEventLevel ParseLevel(string? level) => level?.ToLowerInvariant() switch
    {
        "verbose" => LogEventLevel.Verbose,
        "debug" => LogEventLevel.Debug,
        "warn" or "warning" => LogEventLevel.Warning,
        "error" => LogEventLevel.Error,
        "fatal" => LogEventLevel.Fatal,
        _ => LogEventLevel.Information,
    };

    private static Dictionary<string, string?> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var name = arg[2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                options[name] = args[i + 1];
                i++;
            }
            else
            {
                options[name] = null;
            }
        }

        return options;
    }

    private static string Require(Dictionary<string, string?> options, string name)
    {
        if (!options.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new UsageException($"缺少参数 --{name}");
        }

        return value;
    }

    private sealed class UsageException : Exception
    {
        public UsageException(string message)
            : base(message)
        {
        }
    }
}
