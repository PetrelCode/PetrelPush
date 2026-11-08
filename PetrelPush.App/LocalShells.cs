using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace PetrelPush.App;

/// <summary>候选类型：能装进 ConPTY 的 shell，或只能独立窗口启动的终端程序。</summary>
public enum LocalTerminalKind
{
    /// <summary>在 ConPTY 伪控制台里运行，字节流交给内置 xterm.js 渲染。</summary>
    Shell = 0,

    /// <summary>独立的终端模拟器（Windows Terminal），只能新开窗口，无法嵌入内置终端。</summary>
    External = 1,
}

/// <summary>一个本地终端候选。</summary>
public sealed record LocalShellOption(
    string Key,
    string Name,
    string Executable,
    string Arguments,
    LocalTerminalKind Kind = LocalTerminalKind.Shell)
{
    public bool IsExternal => Kind == LocalTerminalKind.External;

    /// <summary>完整命令行：可执行文件路径加引号，兼容 Program Files 这类含空格的路径。</summary>
    public string CommandLine => string.IsNullOrEmpty(Arguments)
        ? $"\"{Executable}\""
        : $"\"{Executable}\" {Arguments}";
}

/// <summary>
/// 本地终端候选的探测与选择：设置页用 <see cref="Detect"/> 枚举本机可用项让用户挑，
/// 会话启动时用 <see cref="Resolve"/> 取一个 shell（外部终端不参与 ConPTY 启动）。
/// </summary>
public static class LocalShells
{
    /// <summary>自动：PowerShell 7 → Windows PowerShell → cmd。</summary>
    public const string AutoKey = "auto";

    public const string AutoDisplayName = "自动（优先 PowerShell 7）";

    /// <summary>Windows Terminal，外部窗口，不是 shell。</summary>
    public const string WindowsTerminalKey = "wt";

    /// <summary>枚举本机真正可用的候选项，按默认优先级排列（shell 在前，外部终端在后）。</summary>
    public static IReadOnlyList<LocalShellOption> Detect()
    {
        var shells = new List<LocalShellOption>();

        // PowerShell 7：先看 PATH，再看常见安装位置
        AddShell(shells, "pwsh", FindPowerShell7(), "-NoLogo -NoExit");

        // Windows PowerShell 5.1：系统自带，显式把控制台编解码切到 UTF-8
        AddShell(
            shells,
            "powershell",
            Environment.ExpandEnvironmentVariables(
                @"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"),
            "-NoLogo -NoExit -Command \"[Console]::OutputEncoding=[System.Text.Encoding]::UTF8;"
            + "[Console]::InputEncoding=[System.Text.Encoding]::UTF8\"");

        var comspec = Environment.GetEnvironmentVariable("COMSPEC");
        if (string.IsNullOrWhiteSpace(comspec) || !File.Exists(comspec))
        {
            comspec = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\cmd.exe");
        }

        AddShell(shells, "cmd", comspec, "/k chcp 65001>nul");

        // Git Bash：只认 Git for Windows 的标准安装位置，避免误把 System32\bash.exe（WSL 启动器）当成它
        AddShell(shells, "git-bash", FindGitBash(), "--login -i");

        // WSL：系统自带的 wsl.exe 在没有安装任何发行版时只会打印帮助，这种机器上不列出来
        if (HasWslDistribution())
        {
            AddShell(
                shells,
                "wsl",
                Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\wsl.exe"),
                string.Empty);
        }

        // Windows Terminal 是终端模拟器而不是 shell，只能独立窗口打开，所以排在最后
        var windowsTerminal = FindWindowsTerminal();
        if (windowsTerminal is not null)
        {
            shells.Add(new LocalShellOption(
                WindowsTerminalKey,
                "Windows Terminal（外部窗口）",
                windowsTerminal,
                string.Empty,
                LocalTerminalKind.External));
        }

        return shells;
    }

    /// <summary>按用户偏好取一个可嵌入内置终端的 shell；偏好不可用时回退到自动顺序。</summary>
    public static LocalShellOption Resolve(string? preference)
    {
        var key = NormalizeKey(preference);
        var shells = new List<LocalShellOption>();
        foreach (var option in Detect())
        {
            if (!option.IsExternal)
            {
                shells.Add(option);
            }
        }

        if (shells.Count == 0)
        {
            throw new NotSupportedException("未找到可用的本地 shell：pwsh / powershell / cmd 均不存在");
        }

        foreach (var shell in shells)
        {
            if (string.Equals(shell.Key, key, StringComparison.Ordinal))
            {
                return shell;
            }
        }

        return shells[0];
    }

    /// <summary>取外部终端程序（Windows Terminal）；未安装时返回 null。</summary>
    public static LocalShellOption? ResolveExternal(string? preference)
    {
        var key = NormalizeKey(preference);
        foreach (var option in Detect())
        {
            if (option.IsExternal && string.Equals(option.Key, key, StringComparison.Ordinal))
            {
                return option;
            }
        }

        return null;
    }

    /// <summary>该偏好是否指向外部终端（需要独立窗口启动，不能进 ConPTY）。</summary>
    public static bool IsExternalKey(string? preference) =>
        string.Equals(NormalizeKey(preference), WindowsTerminalKey, StringComparison.Ordinal);

    public static string NormalizeKey(string? key) =>
        string.IsNullOrWhiteSpace(key) ? AutoKey : key.Trim().ToLowerInvariant();

    /// <summary>键到显示名的映射；未知键原样返回，便于设置页展示"配置了但本机没有"的项。</summary>
    public static string Describe(string? key) => NormalizeKey(key) switch
    {
        AutoKey => AutoDisplayName,
        "pwsh" => "PowerShell 7",
        "powershell" => "Windows PowerShell",
        "cmd" => "cmd",
        "git-bash" => "Git Bash",
        "wsl" => "WSL（默认发行版）",
        WindowsTerminalKey => "Windows Terminal（外部窗口）",
        var other => other,
    };

    /// <summary>设置页提示文案：说明这一项选中后到底会启动什么。</summary>
    public static string DescribeLaunch(string? preference)
    {
        var key = NormalizeKey(preference);

        if (IsExternalKey(key))
        {
            var external = ResolveExternal(key);
            return external is null
                ? "未检测到 Windows Terminal（wt.exe）：请改用内置终端，或安装后重试"
                : $"将在独立窗口中打开：{external.Executable} -d <当前目录>（不使用内置终端窗口）";
        }

        try
        {
            var shell = Resolve(key);
            var fallback = string.Equals(shell.Key, key, StringComparison.Ordinal)
                           || string.Equals(key, AutoKey, StringComparison.Ordinal)
                ? string.Empty
                : $"（未检测到 {Describe(key)}，已回退到 {shell.Name}）";

            return $"将启动：{shell.CommandLine}{fallback}";
        }
        catch (NotSupportedException ex)
        {
            return ex.Message;
        }
    }

    private static void AddShell(
        List<LocalShellOption> shells,
        string key,
        string? executable,
        string arguments)
    {
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            return;
        }

        shells.Add(new LocalShellOption(key, Describe(key), executable, arguments));
    }

    private static string? FindPowerShell7()
    {
        var fromPath = FindOnPath("pwsh.exe");
        if (fromPath is not null)
        {
            return fromPath;
        }

        string[] candidates =
        [
            @"%ProgramFiles%\PowerShell\7\pwsh.exe",
            @"%ProgramFiles%\PowerShell\7-preview\pwsh.exe",
            @"%LOCALAPPDATA%\Microsoft\WindowsApps\pwsh.exe",
        ];

        return FirstExisting(candidates);
    }

    private static string? FindGitBash()
    {
        string[] candidates =
        [
            @"%ProgramFiles%\Git\bin\bash.exe",
            @"%ProgramFiles(x86)%\Git\bin\bash.exe",
            @"%LOCALAPPDATA%\Programs\Git\bin\bash.exe",
        ];

        return FirstExisting(candidates);
    }

    private static string? FindWindowsTerminal()
    {
        var fromPath = FindOnPath("wt.exe");
        if (fromPath is not null)
        {
            return fromPath;
        }

        string[] candidates =
        [
            @"%LOCALAPPDATA%\Microsoft\WindowsApps\wt.exe",
        ];

        return FirstExisting(candidates);
    }

    /// <summary>
    /// WSL 是否有可用发行版：wsl.exe 在没装发行版时只会打印帮助（用户看到的就是这种），
    /// 所以读注册表 HKCU\...\Lxss 下的发行版子键，而不是只看 wsl.exe 是否存在。
    /// </summary>
    private static bool HasWslDistribution()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
            if (key is null)
            {
                return false;
            }

            return key.GetSubKeyNames().Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? FirstExisting(IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            var expanded = Environment.ExpandEnvironmentVariables(candidate);
            if (File.Exists(expanded))
            {
                return expanded;
            }
        }

        return null;
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // PATH 里有非法片段，跳过
            }
        }

        return null;
    }
}
