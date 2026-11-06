using Renci.SshNet.Common;

namespace PetrelPush.Transport;

/// <summary>
/// 主机密钥校验（TOFU）：必须显式比对指纹，禁止“信任任意主机密钥”。
/// 首次使用需由用户显式触发（测试连接）确认，确认后的指纹写入配置。
/// </summary>
public sealed class HostKeyValidator
{
    private string? _expected;

    public HostKeyValidator(string? expectedFingerprint)
    {
        _expected = string.IsNullOrWhiteSpace(expectedFingerprint) ? null : expectedFingerprint.Trim();
    }

    /// <summary>仅由“测试连接”开启：首次连接接受并记录指纹。</summary>
    public bool TrustOnFirstUse { get; set; }

    public string? ObservedFingerprint { get; private set; }

    public string? Failure { get; private set; }

    public event Action<string>? FingerprintAccepted;

    public bool Verify(HostKeyEventArgs e)
    {
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
                Failure = $"远端主机指纹尚未确认（{Mask(fingerprint)}）。请先在配置页点击“测试连接”完成确认。";
                return false;
            }

            _expected = fingerprint;
            Failure = null;
            FingerprintAccepted?.Invoke(fingerprint);
            return true;
        }

        if (!string.Equals(_expected, fingerprint, StringComparison.Ordinal))
        {
            Failure = $"远端主机指纹不匹配：期望 {Mask(_expected)}，实际 {Mask(fingerprint)}";
            return false;
        }

        Failure = null;
        return true;
    }

    /// <summary>日志脱敏：只保留前 8 位。</summary>
    public static string Mask(string fingerprint)
    {
        var prefix = fingerprint.StartsWith("SHA256:", StringComparison.Ordinal) ? "SHA256:" : string.Empty;
        var body = fingerprint[prefix.Length..];
        return body.Length <= 8 ? fingerprint : $"{prefix}{body[..8]}…";
    }
}
