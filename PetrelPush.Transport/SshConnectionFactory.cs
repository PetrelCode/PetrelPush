using Renci.SshNet;
using Renci.SshNet.Common;
using PetrelPush.Core.Abstractions;
using PetrelPush.Core.Model;

namespace PetrelPush.Transport;

/// <summary>
/// SSH 连接信息构造：统一认证方式（密码 + keyboard-interactive 回退、私钥）、超时与保活参数。
/// 供 <c>SftpClient</c>（文件传输）与 <c>SshClient</c>（终端会话）共用，避免认证逻辑分叉。
/// </summary>
public static class SshConnectionFactory
{
    /// <summary>按 profile 的远端设置构造连接信息（含认证方式）。</summary>
    public static ConnectionInfo Create(RemoteOptions options)
    {
        var authentication = BuildAuthentication(options);
        return new ConnectionInfo(options.Host, options.Port, options.User, authentication)
        {
            Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, options.ConnectTimeoutMs)),
        };
    }

    /// <summary>
    /// 密码认证先走 PasswordAuthentication，失败自动回退 KeyboardInteractive（RHEL / Rocky 常见）。
    /// </summary>
    public static AuthenticationMethod[] BuildAuthentication(RemoteOptions options)
    {
        if (string.Equals(options.Auth.Type, "key", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(options.Auth.PrivateKeyPath))
            {
                throw new SyncFatalException("密钥认证缺少 privateKeyPath");
            }

            try
            {
                var keyFile = string.IsNullOrEmpty(options.Auth.PrivateKeyPassphrase)
                    ? new PrivateKeyFile(options.Auth.PrivateKeyPath)
                    : new PrivateKeyFile(options.Auth.PrivateKeyPath, options.Auth.PrivateKeyPassphrase);
                return [new PrivateKeyAuthenticationMethod(options.User, keyFile)];
            }
            catch (Exception ex) when (ex is SshException or IOException or NotSupportedException)
            {
                throw new SyncFatalException($"读取私钥失败（口令错误或格式不支持）：{ex.Message}", ex);
            }
        }

        if (string.IsNullOrEmpty(options.Password))
        {
            throw new SyncFatalException("密码认证缺少密码（secrets.bin 未保存或 DPAPI 解密失败）");
        }

        var keyboardInteractive = new KeyboardInteractiveAuthenticationMethod(options.User);
        keyboardInteractive.AuthenticationPrompt += (_, e) =>
        {
            foreach (var prompt in e.Prompts)
            {
                prompt.Response = options.Password;
            }
        };

        return
        [
            new PasswordAuthenticationMethod(options.User, options.Password!),
            keyboardInteractive,
        ];
    }
}
