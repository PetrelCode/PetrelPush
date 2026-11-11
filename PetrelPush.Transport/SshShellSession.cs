using System.Threading.Channels;
using Renci.SshNet;
using Renci.SshNet.Common;
using Serilog;
using PetrelPush.Core.Abstractions;
using PetrelPush.Core.Model;

namespace PetrelPush.Transport;

/// <summary>
/// 远端交互式终端会话：申请 PTY（<c>pty-req</c>）+ 启动登录 shell（<c>shell</c>），
/// 以原始字节流收发，支持窗口尺寸变更（<c>window-change</c>）。
///
/// 注意：<see cref="ShellStream"/> 不做终端仿真，只搬运字节；转义序列（光标、颜色、清屏）
/// 由前端 xterm.js 解析渲染。
/// </summary>
public sealed class SshShellSession : ITerminalSession
{
    private readonly SshClient _client;
    private readonly ShellStream _shell;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private int _disposed;
    private Task? _pump;

    private SshShellSession(SshClient client, ShellStream shell)
    {
        _client = client;
        _shell = shell;
    }

    /// <summary>远端输出的原始字节块（UTF-8 未解码，交由前端一次性解码，避免多字节字符被切断）。</summary>
    public ChannelReader<byte[]> Output => _inbound.Reader;

    /// <summary>会话结束（远端关闭、网络断开或被释放）。参数为可展示的原因。</summary>
    public event Action<string>? Closed;

    public bool IsConnected => !_disposed.Equals(1) && _client.IsConnected;

    /// <summary>
    /// 当前 SSH.NET 版本是否支持窗口尺寸变更。false 表示反射未命中（通常是 SSH.NET 升级改名），
    /// 此时终端仍可正常使用，只是缩放窗口后远端不会重排，调用方应记录告警。
    /// </summary>
    public static bool SupportsWindowResize => ShellWindowResizer.IsSupported;

    public static string Label(SyncProfile profile) =>
        $"{profile.Remote.User}@{profile.Remote.Host}:{profile.Remote.Port}";

    /// <summary>
    /// 建立连接并启动 shell。SSH.NET 为同步 API，整体放到线程池执行，避免阻塞 UI。
    /// </summary>
    public static Task<SshShellSession> ConnectAsync(
        SyncProfile profile,
        int columns,
        int rows,
        CancellationToken cancellationToken)
        => Task.Run(() => ConnectCore(profile, columns, rows), cancellationToken);

    /// <summary>resize 能力告警只记录一次，避免每次开终端都刷屏。</summary>
    private static int _resizeWarningLogged;

    private static SshShellSession ConnectCore(SyncProfile profile, int columns, int rows)
    {
        if (!ShellWindowResizer.IsSupported && Interlocked.Exchange(ref _resizeWarningLogged, 1) == 0)
        {
            Log.Warning("当前 SSH.NET 版本未暴露 ShellStream 窗口尺寸接口，缩放窗口不会触发远端重排");
        }

        var validator = new HostKeyValidator(profile.Remote);
        var connectionInfo = SshConnectionFactory.Create(profile.Remote);

        var client = new SshClient(connectionInfo)
        {
            KeepAliveInterval = TimeSpan.FromSeconds(Math.Max(1, profile.Remote.KeepAliveIntervalSec)),
        };

        // 终端与文件传输走同一套 TOFU 校验：指纹未确认或变更时直接拒绝，不做降级
        client.HostKeyReceived += (_, e) => e.CanTrust = validator.Verify(e);

        try
        {
            client.Connect();

            var shell = client.CreateShellStream(
                terminalName: "xterm-256color",
                columns: (uint)Math.Max(2, columns),
                rows: (uint)Math.Max(1, rows),
                width: 0,
                height: 0,
                bufferSize: 16 * 1024);

            // 注意：ShellStream 未重写 Stream.ReadTimeout，设置该属性会抛
            // InvalidOperationException("Timeouts are not supported on this stream")。
            // 因此读取泵改用 DataAvailable + 可取消等待的轮询方式（见 StartPump）。

            var session = new SshShellSession(client, shell);
            session.StartPump();
            return session;
        }
        catch (SshAuthenticationException ex)
        {
            client.Dispose();
            throw new SyncFatalException(
                $"SSH 认证失败：{Label(profile)}。请检查密码/私钥；"
                + "若使用 root + 密码，请确认 PermitRootLogin yes、PasswordAuthentication yes 且 root 密码未被锁定。",
                ex);
        }
        catch (SshConnectionException ex) when (validator.Mismatch && validator.Failure is not null)
        {
            client.Dispose();
            throw new HostKeyMismatchException(validator.Failure, ex);
        }
        catch (SshConnectionException ex) when (validator.Failure is not null)
        {
            client.Dispose();
            throw new SyncFatalException(validator.Failure, ex);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>把配置写入 shell：自动切换到指定工作目录（路径经单引号转义）。</summary>
    public void SendCommand(string command)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(command + "\n");
        Send(bytes);
    }

    public void Send(byte[] data)
    {
        if (data.Length == 0 || Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        try
        {
            _shell.Write(data, 0, data.Length);
            _shell.Flush();
        }
        catch (Exception ex) when (ex is SshException or IOException or ObjectDisposedException)
        {
            // 连接已断：由读取泵统一上报，这里不重复打扰用户
        }
    }

    public void SendText(string text) => Send(System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>窗口尺寸变更：让远端 TUI 程序（vim / htop）按新尺寸重排。</summary>
    public void Resize(int columns, int rows)
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        ShellWindowResizer.Resize(_shell, Math.Max(2, columns), Math.Max(1, rows));
    }

    private void StartPump()
    {
        var token = _cts.Token;
        _pump = Task.Run(() =>
        {
            var buffer = new byte[16 * 1024];
            var reason = "连接已断开";
            try
            {
                // 阻塞式读取：ShellStream.Read 内部按需从 Channel 取数据，
                // 剩余部分留在其内部缓冲区，因此不会漏读；空闲时阻塞不占 CPU。
                // 取消由 Dispose 中的 _shell.Dispose() 打断（抛异常或返回 0）。
                while (!token.IsCancellationRequested)
                {
                    var read = _shell.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        if (token.IsCancellationRequested)
                        {
                            break;
                        }

                        reason = "连接已断开";
                        break;
                    }

                    var chunk = buffer.AsSpan(0, read).ToArray();
                    if (!_inbound.Writer.TryWrite(chunk))
                    {
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is SshException
                                           or IOException
                                           or ObjectDisposedException
                                           or InvalidOperationException)
            {
                reason = Volatile.Read(ref _disposed) == 1 ? "会话已关闭" : $"连接中断：{ex.Message}";
            }
            finally
            {
                _inbound.Writer.TryComplete();
                if (Volatile.Read(ref _disposed) == 0)
                {
                    Closed?.Invoke(reason);
                }
            }
        });
    }

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

        try
        {
            _shell.Dispose();
        }
        catch (Exception ex) when (ex is SshException or IOException or ObjectDisposedException)
        {
        }

        try
        {
            if (_client.IsConnected)
            {
                _client.Disconnect();
            }
        }
        catch (Exception ex) when (ex is SshException or IOException or ObjectDisposedException)
        {
        }

        try
        {
            _client.Dispose();
        }
        catch (Exception ex) when (ex is SshException or IOException or ObjectDisposedException)
        {
        }

        _cts.Dispose();
        _inbound.Writer.TryComplete();
    }
}
