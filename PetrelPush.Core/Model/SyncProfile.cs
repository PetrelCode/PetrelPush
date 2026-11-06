using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PetrelPush.Core.Model;

/// <summary>profile 配置（config.json）。秘密不在此文件中，另存 secrets.bin（DPAPI）。</summary>
public sealed class SyncProfile
{
    public string ProfileId { get; set; } = "default";

    public string ProfileName { get; set; } = string.Empty;

    public string LocalRoot { get; set; } = string.Empty;

    public RemoteOptions Remote { get; set; } = new();

    public string RemoteRoot { get; set; } = string.Empty;

    public SyncOptions Sync { get; set; } = new();

    public RetryOptions Retry { get; set; } = new();

    public LogOptions Log { get; set; } = new();

    /// <summary>remoteRoot 位于系统目录（或其上级）时，用户已做过的二次确认标记，避免反复打扰。</summary>
    public bool ProtectedRootAcknowledged { get; set; }

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(ProfileId))
        {
            yield return "ProfileId 不能为空";
        }
        else if (ProfileId.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_' or '.')))
        {
            yield return "ProfileId 只能包含字母、数字、'-'、'_'、'.'";
        }

        if (string.IsNullOrWhiteSpace(LocalRoot))
        {
            yield return "localRoot 不能为空";
        }
        else if (!Directory.Exists(LocalRoot))
        {
            yield return $"本地目录不存在：{LocalRoot}";
        }

        if (!RemoteRoot.StartsWith('/'))
        {
            yield return "remoteRoot 必须是绝对路径（以 / 开头）";
        }

        if (string.IsNullOrWhiteSpace(Remote.Host))
        {
            yield return "remote.host 不能为空";
        }

        if (Remote.Port is < 1 or > 65535)
        {
            yield return "remote.port 必须在 1~65535 之间";
        }

        if (string.IsNullOrWhiteSpace(Remote.User))
        {
            yield return "remote.user 不能为空";
        }

        if (Remote.Auth.Type is not ("password" or "key"))
        {
            yield return "remote.auth.type 只能是 password 或 key";
        }

        if (Remote.Auth.Type == "key" && string.IsNullOrWhiteSpace(Remote.Auth.PrivateKeyPath))
        {
            yield return "密钥认证需要 remote.auth.privateKeyPath";
        }

        if (Remote.Auth.Type == "key" && !string.IsNullOrWhiteSpace(Remote.Auth.PrivateKeyPath)
            && !File.Exists(Remote.Auth.PrivateKeyPath))
        {
            yield return $"私钥文件不存在：{Remote.Auth.PrivateKeyPath}";
        }

        if (Remote.Auth.Type == "password" && string.IsNullOrEmpty(Remote.Password))
        {
            yield return "密码认证需要提供密码（secrets.bin 中未找到或解密失败）";
        }

        if (Sync.MaxParallelTransfers < 1)
        {
            yield return "sync.maxParallelTransfers 必须 >= 1";
        }

        if (Sync.DebounceMs < 0 || Sync.MaxForceTriggerMs < Sync.DebounceMs)
        {
            yield return "sync.debounceMs / maxForceTriggerMs 参数不合法";
        }

        if (Sync.PollIntervalSec < 1)
        {
            yield return "sync.pollIntervalSec 必须 >= 1";
        }

        if (Retry.MaxAttempts < 1)
        {
            yield return "retry.maxAttempts 必须 >= 1";
        }

        if (Retry.BaseDelayMs < 1 || Retry.MaxDelayMs < Retry.BaseDelayMs)
        {
            yield return "retry.baseDelayMs / maxDelayMs 参数不合法";
        }
    }
}

public sealed class RemoteOptions
{
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 22;

    public string User { get; set; } = "root";

    public AuthOptions Auth { get; set; } = new();

    /// <summary>SHA256:xxxx 形式的主机指纹；为空表示尚未完成 TOFU 确认。</summary>
    public string? HostKeyFingerprint { get; set; }

    public int ConnectTimeoutMs { get; set; } = 10000;

    public int KeepAliveIntervalSec { get; set; } = 15;

    /// <summary>密码（仅内存，来自 secrets.bin）。</summary>
    [JsonIgnore]
    public string? Password { get; set; }
}

public sealed class AuthOptions
{
    /// <summary>password | key</summary>
    public string Type { get; set; } = "password";

    public string? PrivateKeyPath { get; set; }

    /// <summary>私钥口令（仅内存，来自 secrets.bin）。</summary>
    [JsonIgnore]
    public string? PrivateKeyPassphrase { get; set; }
}

public sealed class SyncOptions
{
    public int DebounceMs { get; set; } = 500;

    public int MaxForceTriggerMs { get; set; } = 5000;

    public int PollIntervalSec { get; set; } = 30;

    public int DeepVerifyIntervalDays { get; set; } = 7;

    public int MaxParallelTransfers { get; set; } = 4;

    public int RateLimitKbps { get; set; }

    public bool DeleteEmptyDirs { get; set; } = true;

    public bool SyncEmptyDirs { get; set; } = true;

    public string ExtraIgnoreFile { get; set; } = ".syncignore";

    public bool FollowSymlinks { get; set; }

    public string[] ExecutableSuffixes { get; set; } = [".sh", ".bash", ".py"];

    public int LargeFileThresholdMB { get; set; } = 100;

    public bool AutoStart { get; set; } = true;

    public bool PausedOnMeteredNetwork { get; set; } = true;

    /// <summary>只计算并展示变更集，不做任何远端写入（首次验收用）。</summary>
    public bool DryRun { get; set; }
}

public sealed class RetryOptions
{
    public int MaxAttempts { get; set; } = 8;

    public int BaseDelayMs { get; set; } = 1000;

    public int MaxDelayMs { get; set; } = 300000;
}

public sealed class LogOptions
{
    public string Level { get; set; } = "Info";

    public int RetentionDays { get; set; } = 14;
}

/// <summary>config.json 读写。使用 camelCase、不转义中文。</summary>
public static class SyncProfileJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static SyncProfile Parse(string json)
    {
        var profile = JsonSerializer.Deserialize<SyncProfile>(json, Options)
                      ?? throw new InvalidDataException("配置文件内容为空或格式不正确");
        return profile;
    }

    public static string Serialize(SyncProfile profile) => JsonSerializer.Serialize(profile, Options);

    public static SyncProfile Load(string path) => Parse(File.ReadAllText(path));

    public static void Save(string path, SyncProfile profile)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, Serialize(profile));
    }
}
