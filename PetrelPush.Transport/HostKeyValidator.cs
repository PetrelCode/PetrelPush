using PetrelPush.Core.Abstractions;
using PetrelPush.Core.Model;
using Renci.SshNet.Common;

namespace PetrelPush.Transport;

/// <summary>已确认的主机指纹，以及它绑定的远端地址（host:port）。</summary>
public sealed record HostKeyTrust(string Fingerprint, string Target);

/// <summary>
/// 远端主机指纹与已确认的不一致（同一地址上换了密钥，例如远端重装系统 / 换机）。
/// 需要用户在界面上显式确认后才能覆盖，不能自动信任。
/// </summary>
public sealed class HostKeyMismatchException : SyncFatalException
{
    public HostKeyMismatchException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// 主机密钥校验（TOFU）：必须显式比对指纹，禁止“信任任意主机密钥”。
/// 指纹与确认时的 host:port 绑定：远端地址变更（改 IP / 换端口）后旧指纹自动作废，
/// 由「测试连接」重新确认；同一地址上指纹变化时，仅在用户显式确认后才覆盖。
/// </summary>
public sealed class HostKeyValidator
{
    private readonly string _target;
    private string? _expected;

    public HostKeyValidator(RemoteOptions remote)
    {
        _target = remote.CurrentHostKeyTarget;

        var fingerprint = string.IsNullOrWhiteSpace(remote.HostKeyFingerprint)
            ? null
            : remote.HostKeyFingerprint.Trim();

        // 指纹只对确认时的 host:port 有效：改了 IP / 端口后旧指纹作废，重新走 TOFU
        if (fingerprint is not null
            && !string.Equals(remote.HostKeyTarget, _target, StringComparison.OrdinalIgnoreCase))
        {
            PreviousTarget = remote.HostKeyTarget;
            fingerprint = null;
        }

        _expected = fingerprint;
    }

    /// <summary>仅由「测试连接」开启：尚未确认指纹时接受并记录。</summary>
    public bool TrustOnFirstUse { get; set; }

    /// <summary>用户已显式确认“换机 / 重装”：指纹与已确认值不同时允许覆盖。</summary>
    public bool AllowFingerprintRotation { get; set; }

    /// <summary>旧指纹绑定的地址；仅在远端地址变更导致旧指纹作废时有值。</summary>
    public string? PreviousTarget { get; }

    public string? ObservedFingerprint { get; private set; }

    public string? Failure { get; private set; }

    /// <summary>本次校验是否因“同一地址上指纹不同”而失败（需用户确认后才能覆盖）。</summary>
    public bool Mismatch { get; private set; }

    public event Action<HostKeyTrust>? FingerprintAccepted;

    public bool Verify(HostKeyEventArgs e)
    {
        Mismatch = false;

        var fingerprintBytes = e.FingerPrint;
        if (fingerprintBytes is null || fingerprintBytes.Length == 0)
        {
            Failure = "无法获取远端主机密钥指纹";
            return false;
        }

        var fingerprint = "SHA256:" + Convert.ToBase64String(fingerprintBytes).TrimEnd('=');
        ObservedFingerprint = fingerprint;

        if (_expected is null)
        {
            if (!TrustOnFirstUse)
            {
                Failure = PreviousTarget is null
                    ? $"远端主机指纹尚未确认（{Mask(fingerprint)}）。请先在配置页点击“测试连接”完成确认。"
                    : $"远端地址已变更（{PreviousTarget} → {_target}），旧指纹已作废（当前 {Mask(fingerprint)}）。"
                      + "请先在配置页点击“测试连接”重新确认。";
                return false;
            }

            Accept(fingerprint);
            return true;
        }

        if (string.Equals(_expected, fingerprint, StringComparison.Ordinal))
        {
            Failure = null;
            return true;
        }

        Mismatch = true;
        if (AllowFingerprintRotation)
        {
            Accept(fingerprint);
            return true;
        }

        Failure = $"远端主机指纹不匹配：期望 {Mask(_expected)}，实际 {Mask(fingerprint)}";
        return false;
    }

    private void Accept(string fingerprint)
    {
        _expected = fingerprint;
        Failure = null;
        Mismatch = false;
        FingerprintAccepted?.Invoke(new HostKeyTrust(fingerprint, _target));
    }

    /// <summary>日志脱敏：只保留前 8 位。</summary>
    public static string Mask(string fingerprint)
    {
        var prefix = fingerprint.StartsWith("SHA256:", StringComparison.Ordinal) ? "SHA256:" : string.Empty;
        var body = fingerprint[prefix.Length..];
        return body.Length <= 8 ? fingerprint : $"{prefix}{body[..8]}…";
    }
}
