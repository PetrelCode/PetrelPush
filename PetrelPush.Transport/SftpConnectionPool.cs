using System.Collections.Concurrent;
using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;
using Serilog;
using PetrelPush.Core.Abstractions;
using PetrelPush.Core.Model;

namespace PetrelPush.Transport;

/// <summary>
/// SFTP 连接池：一线程一连接（SftpClient 非线程安全），重连串行化并指数退避，避免触发 fail2ban。
/// </summary>
public sealed class SftpConnectionPool : IAsyncDisposable
{
    private readonly RemoteOptions _options;
    private readonly string? _password;
    private readonly HostKeyValidator _validator;
    private readonly int _maxSize;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly ConcurrentBag<SftpClient> _idle = new();

    private int _consecutiveFailures;
    private DateTimeOffset _nextAttemptAt = DateTimeOffset.MinValue;
    private bool _disposed;

    public SftpConnectionPool(
        RemoteOptions options,
        string? password,
        int maxSize,
        HostKeyValidator validator,
        ILogger logger)
    {
        _options = options;
        _password = password;
        _validator = validator;
        _maxSize = Math.Max(1, maxSize);
        _logger = logger;
        _slots = new SemaphoreSlim(_maxSize, _maxSize);
    }

    public HostKeyValidator Validator => _validator;

    public async Task<SftpLease> AcquireAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            while (_idle.TryTake(out var idleClient))
            {
                if (idleClient.IsConnected)
                {
                    return new SftpLease(this, idleClient);
                }

                SafeDispose(idleClient);
            }

            var fresh = await ConnectWithGateAsync(cancellationToken).ConfigureAwait(false);
            return new SftpLease(this, fresh);
        }
        catch
        {
            _slots.Release();
            throw;
        }
    }

    internal void Release(SftpClient client, bool healthy)
    {
        if (_disposed || !healthy || !client.IsConnected)
        {
            SafeDispose(client);
        }
        else
        {
            _idle.Add(client);
        }

        try
        {
            _slots.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        while (_idle.TryTake(out var client))
        {
            SafeDispose(client);
        }

        _connectGate.Dispose();
        _slots.Dispose();
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    private async Task<SftpClient> ConnectWithGateAsync(CancellationToken cancellationToken)
    {
        // 长退避期间不阻塞任务线程：本周期快速失败，由任务重试队列按退避时间重新排期
        var pending = _nextAttemptAt - DateTimeOffset.UtcNow;
        if (pending > TimeSpan.FromSeconds(5))
        {
            throw new SshConnectionException($"连接处于退避期（{pending.TotalSeconds:F0}s 后重试）");
        }

        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wait = _nextAttemptAt - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                _logger.Debug("等待重连退避 {Delay}", wait);
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }

            var client = await Task.Run(Connect, cancellationToken).ConfigureAwait(false);
            _consecutiveFailures = 0;
            _nextAttemptAt = DateTimeOffset.MinValue;
            return client;
        }
        catch (SyncFatalException)
        {
            throw;
        }
        catch (Exception ex) when (ex is SshException or SocketException or IOException or TimeoutException)
        {
            _consecutiveFailures++;
            var delay = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(8, _consecutiveFailures - 1))));
            _nextAttemptAt = DateTimeOffset.UtcNow + delay;
            _logger.Warning(
                "连接 {User}@{Host}:{Port} 失败（连续 {Failures} 次），{Delay} 后重试：{Message}",
                _options.User,
                _options.Host,
                _options.Port,
                _consecutiveFailures,
                delay,
                ex.Message);
            throw;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private SftpClient Connect()
    {
        var authentication = BuildAuthentication();
        var connectionInfo = new ConnectionInfo(_options.Host, _options.Port, _options.User, authentication)
        {
            Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, _options.ConnectTimeoutMs)),
        };

        var client = new SftpClient(connectionInfo)
        {
            KeepAliveInterval = TimeSpan.FromSeconds(Math.Max(1, _options.KeepAliveIntervalSec)),
            OperationTimeout = TimeSpan.FromMinutes(10),
            BufferSize = 32 * 1024,
        };

        client.HostKeyReceived += (_, e) => e.CanTrust = _validator.Verify(e);

        try
        {
            client.Connect();
            _logger.Debug("已连接 {User}@{Host}:{Port}", _options.User, _options.Host, _options.Port);
            return client;
        }
        catch (SshAuthenticationException ex)
        {
            client.Dispose();
            throw new SyncFatalException(
                $"SSH 认证失败：{_options.User}@{_options.Host}:{_options.Port}。请检查密码/私钥；"
                + "若使用 root + 密码，请确认 PermitRootLogin yes、PasswordAuthentication yes 且 root 密码未被锁定。",
                ex);
        }
        catch (SshConnectionException ex) when (_validator.Failure is not null)
        {
            client.Dispose();
            throw new SyncFatalException(_validator.Failure, ex);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private AuthenticationMethod[] BuildAuthentication()
    {
        if (string.Equals(_options.Auth.Type, "key", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(_options.Auth.PrivateKeyPath))
            {
                throw new SyncFatalException("密钥认证缺少 privateKeyPath");
            }

            try
            {
                var keyFile = string.IsNullOrEmpty(_options.Auth.PrivateKeyPassphrase)
                    ? new PrivateKeyFile(_options.Auth.PrivateKeyPath)
                    : new PrivateKeyFile(_options.Auth.PrivateKeyPath, _options.Auth.PrivateKeyPassphrase);
                return [new PrivateKeyAuthenticationMethod(_options.User, keyFile)];
            }
            catch (Exception ex) when (ex is SshException or IOException or NotSupportedException)
            {
                throw new SyncFatalException($"读取私钥失败（口令错误或格式不支持）：{ex.Message}", ex);
            }
        }

        if (string.IsNullOrEmpty(_password))
        {
            throw new SyncFatalException("密码认证缺少密码（secrets.bin 未保存或 DPAPI 解密失败）");
        }

        // 先 PasswordAuthentication，失败自动回退 KeyboardInteractive（RHEL/Rocky 常见）
        var keyboardInteractive = new KeyboardInteractiveAuthenticationMethod(_options.User);
        keyboardInteractive.AuthenticationPrompt += (_, e) =>
        {
            foreach (var prompt in e.Prompts)
            {
                prompt.Response = _password;
            }
        };

        return
        [
            new PasswordAuthenticationMethod(_options.User, _password),
            keyboardInteractive,
        ];
    }

    private void SafeDispose(SftpClient client)
    {
        try
        {
            client.Dispose();
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "释放 SFTP 连接失败");
        }
    }
}

/// <summary>池租约：Dispose 即归还；传输异常时调用 <see cref="MarkBroken"/> 丢弃连接。</summary>
public sealed class SftpLease : IDisposable
{
    private readonly SftpConnectionPool _pool;
    private int _disposed;
    private bool _broken;

    internal SftpLease(SftpConnectionPool pool, SftpClient client)
    {
        _pool = pool;
        Client = client;
    }

    public SftpClient Client { get; }

    public void MarkBroken() => _broken = true;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _pool.Release(Client, !_broken);
    }
}
