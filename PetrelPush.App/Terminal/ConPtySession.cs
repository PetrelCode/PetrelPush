using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using PetrelPush.Core.Abstractions;
using Serilog;

namespace PetrelPush.App;

/// <summary>
/// 本地终端会话：Windows ConPTY（伪控制台）承载 <c>pwsh</c> / <c>powershell</c> / <c>cmd</c>。
///
/// 与远端 <c>SshShellSession</c> 对齐：双方都是"PTY + ANSI 字节流"，除数据源外完全同构，
/// 因此前端 xterm.js 渲染层可以原样复用。
///
/// 编码约定：启动 shell 时强制 UTF-8（PowerShell 设 Console 编解码，cmd 执行 chcp 65001），
/// 否则中文系统默认 OEM 代码页（CP936）的输出会被前端按 UTF-8 解码成乱码。
/// </summary>
internal sealed class ConPtySession : ITerminalSession
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const int StartfUseStdHandles = 0x00000100;

    private static readonly IntPtr InvalidHandleValue = new(-1);

    /// <summary>PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE 常量值。</summary>
    private static readonly IntPtr ProcThreadAttributePseudoConsole = new(0x00020016);

    private readonly IntPtr _pseudoConsole;
    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _cts = new();

    private int _disposed;
    private int _exited;

    private ConPtySession(IntPtr pseudoConsole, FileStream input, FileStream output)
    {
        _pseudoConsole = pseudoConsole;
        _input = input;
        _output = output;
    }

    public ChannelReader<byte[]> Output => _inbound.Reader;

    public event Action<string>? Closed;

    public bool IsConnected =>
        Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _exited) == 0;

    /// <summary>
    /// ConPTY 需要 Windows 10 1809（build 17763）及以上。
    /// 用 IsWindowsVersionAtLeast 而非 Environment.OSVersion —— 后者在未在 manifest 中声明
    /// supportedOS 时会返回兼容模式版本号（6.2 / build 9200），导致误判为不支持。
    /// </summary>
    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);

    /// <summary>
    /// 创建伪控制台并启动 shell。整个过程是同步 Win32 调用，调用方应放到线程池执行。
    /// <paramref name="shellPreference"/> 来自设置页（auto / pwsh / powershell / cmd / git-bash / wsl）。
    /// </summary>
    public static ConPtySession Start(
        int columns,
        int rows,
        string? workingDirectory,
        string? shellPreference = null)
    {
        var size = ToCoord(columns, rows);

        if (!CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "创建控制台输入管道失败");
        }

        if (!CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0))
        {
            CloseHandle(inputRead);
            CloseHandle(inputWrite);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "创建控制台输出管道失败");
        }

        IntPtr pseudoConsole;
        try
        {
            var hr = CreatePseudoConsole(size, inputRead, outputWrite, 0, out pseudoConsole);
            if (hr != 0)
            {
                CloseHandle(inputRead);
                CloseHandle(inputWrite);
                CloseHandle(outputRead);
                CloseHandle(outputWrite);
                throw new Win32Exception(hr, "创建伪控制台失败");
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            CloseHandle(inputRead);
            CloseHandle(inputWrite);
            CloseHandle(outputRead);
            CloseHandle(outputWrite);
            throw new NotSupportedException(
                "当前系统不支持 ConPTY，本地终端需要 Windows 10 1809 或更高版本。",
                ex);
        }

        // ConPTY 已复制并持有这两端，本地侧句柄可以立即关闭
        CloseHandle(inputRead);
        CloseHandle(outputWrite);

        IntPtr attributeList = IntPtr.Zero;
        try
        {
            StartShell(pseudoConsole, workingDirectory, shellPreference, ref attributeList);
        }
        catch
        {
            ClosePseudoConsole(pseudoConsole);
            CloseHandle(inputWrite);
            CloseHandle(outputRead);
            throw;
        }
        finally
        {
            if (attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
        }

        var input = new FileStream(new SafeFileHandle(inputWrite, ownsHandle: true), FileAccess.Write, 4096, isAsync: false);
        var output = new FileStream(new SafeFileHandle(outputRead, ownsHandle: true), FileAccess.Read, 4096, isAsync: false);

        var session = new ConPtySession(pseudoConsole, input, output);
        session.StartPump();
        return session;
    }

    private static void StartShell(
        IntPtr pseudoConsole,
        string? workingDirectory,
        string? shellPreference,
        ref IntPtr attributeList)
    {
        var startupInfo = new STARTUPINFOEX();
        startupInfo.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();

        // 与官方示例一致：显式声明使用标准句柄，并置为 INVALID_HANDLE_VALUE，
        // 让子进程的标准流走伪控制台而非继承父进程的控制台。
        startupInfo.StartupInfo.dwFlags = StartfUseStdHandles;
        startupInfo.StartupInfo.hStdInput = InvalidHandleValue;
        startupInfo.StartupInfo.hStdOutput = InvalidHandleValue;
        startupInfo.StartupInfo.hStdError = InvalidHandleValue;

        var attributeListSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
        if (attributeListSize == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "获取进程属性列表大小失败");
        }

        attributeList = Marshal.AllocHGlobal(attributeListSize);
        if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "初始化进程属性列表失败");
        }

        if (!UpdateProcThreadAttribute(
                attributeList,
                0,
                ProcThreadAttributePseudoConsole,
                pseudoConsole,
                new IntPtr(IntPtr.Size),
                IntPtr.Zero,
                IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "绑定伪控制台到子进程失败");
        }

        startupInfo.lpAttributeList = attributeList;

        var shell = LocalShells.Resolve(shellPreference);

        if (!CreateProcess(
                shell.Executable,
                shell.CommandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                ExtendedStartupInfoPresent,
                IntPtr.Zero,
                string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory,
                ref startupInfo,
                out var processInfo))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"启动 {shell.Name} 失败");
        }

        CloseHandle(processInfo.hThread);
        CloseHandle(processInfo.hProcess);
        Log.Debug("本地终端已启动：{Shell} {CommandLine}（PID {Pid}）", shell.Name, shell.CommandLine, processInfo.dwProcessId);
    }

    private void StartPump()
    {
        var token = _cts.Token;
        _ = Task.Run(() =>
        {
            var buffer = new byte[16 * 1024];
            var reason = "终端进程已退出";
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var read = _output.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    if (!_inbound.Writer.TryWrite(buffer.AsSpan(0, read).ToArray()))
                    {
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                reason = Volatile.Read(ref _disposed) == 1 ? "会话已关闭" : $"读取终端输出失败：{ex.Message}";
            }
            finally
            {
                Volatile.Write(ref _exited, 1);
                _inbound.Writer.TryComplete();
                if (Volatile.Read(ref _disposed) == 0)
                {
                    Closed?.Invoke(reason);
                }
            }
        });
    }

    public void Send(byte[] data)
    {
        if (data.Length == 0 || Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        try
        {
            _input.Write(data, 0, data.Length);
            _input.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // 子进程已退出，由读取端统一上报
        }
    }

    public void SendText(string text) => Send(System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// 本地 shell 的回车必须是单个 CR（与 xterm.js 按 Enter 发出的字节一致）。
    /// 若用 CRLF，CR 已触发执行，多出的 LF 会让 PowerShell / PSReadLine 进入续行模式（出现 &gt;&gt; 提示符）。
    /// </summary>
    public void SendCommand(string command) => SendText(command + "\r");

    public void Resize(int columns, int rows)
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        try
        {
            ResizePseudoConsole(_pseudoConsole, ToCoord(columns, rows));
        }
        catch (Exception ex) when (ex is ObjectDisposedException or EntryPointNotFoundException)
        {
        }
    }

    private static COORD ToCoord(int columns, int rows) => new()
    {
        X = (short)Math.Clamp(columns, 2, 500),
        Y = (short)Math.Clamp(rows, 1, 300),
    };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        // 关闭伪控制台会终止挂在其上的子进程，并关闭输出管道的写端
        try
        {
            ClosePseudoConsole(_pseudoConsole);
        }
        catch (EntryPointNotFoundException)
        {
        }

        try
        {
            _input.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }

        try
        {
            _output.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }

        _cts.Dispose();
        _inbound.Writer.TryComplete();
    }

    // ---- Win32 互操作 ----

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, IntPtr lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList,
        int dwAttributeCount,
        int dwFlags,
        ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList,
        uint dwFlags,
        IntPtr attribute,
        IntPtr lpValue,
        IntPtr cbSize,
        IntPtr lpPreviousValue,
        IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
