using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PetrelPush.Core.Model;

namespace PetrelPush.Core.Config;

public sealed class ProfileSecrets
{
    public string? Password { get; set; }

    public string? PrivateKeyPassphrase { get; set; }
}

/// <summary>
/// profile 目录管理：%APPDATA%\PetrelPush\profiles\&lt;id&gt;\{config.json, secrets.bin, state.db, logs\}。
/// 口令与私钥口令经 DPAPI（CurrentUser）加密后写入 secrets.bin。
/// </summary>
public sealed class ProfileStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PetrelPush.v1");

    public ProfileStore(string? baseDirectory = null)
    {
        BaseDirectory = string.IsNullOrWhiteSpace(baseDirectory) ? DefaultBaseDirectory : Path.GetFullPath(baseDirectory);
    }

    public static string DefaultBaseDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PetrelPush",
        "profiles");

    public string BaseDirectory { get; }

    public string GetProfileDirectory(string id) => Path.Combine(BaseDirectory, id);

    public string GetConfigPath(string id) => Path.Combine(GetProfileDirectory(id), "config.json");

    public string GetSecretsPath(string id) => Path.Combine(GetProfileDirectory(id), "secrets.bin");

    public string GetStateDatabasePath(string id) => Path.Combine(GetProfileDirectory(id), "state.db");

    public string GetLogDirectory(string id) => Path.Combine(GetProfileDirectory(id), "logs");

    public IReadOnlyList<string> ListProfileIds()
    {
        if (!Directory.Exists(BaseDirectory))
        {
            return Array.Empty<string>();
        }

        return Directory.GetDirectories(BaseDirectory)
            .Where(dir => File.Exists(Path.Combine(dir, "config.json")))
            .Select(dir => Path.GetFileName(dir))
            .Where(id => !string.IsNullOrEmpty(id))
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray()!;
    }

    public bool Exists(string id) => File.Exists(GetConfigPath(id));

    public SyncProfile Load(string id)
    {
        var path = GetConfigPath(id);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"找不到 profile 配置：{path}", path);
        }

        var profile = SyncProfileJson.Load(path);
        if (string.IsNullOrWhiteSpace(profile.ProfileId))
        {
            profile.ProfileId = id;
        }

        var secrets = ReadSecrets(id);
        if (secrets is not null)
        {
            profile.Remote.Password = secrets.Password;
            profile.Remote.Auth.PrivateKeyPassphrase = secrets.PrivateKeyPassphrase;
        }

        return profile;
    }

    public void Save(SyncProfile profile, string? password = null, string? privateKeyPassphrase = null)
    {
        if (string.IsNullOrWhiteSpace(profile.ProfileId))
        {
            throw new InvalidOperationException("profile.ProfileId 不能为空");
        }

        var directory = GetProfileDirectory(profile.ProfileId);
        Directory.CreateDirectory(directory);
        SyncProfileJson.Save(GetConfigPath(profile.ProfileId), profile);

        var secrets = new ProfileSecrets
        {
            Password = password ?? profile.Remote.Password,
            PrivateKeyPassphrase = privateKeyPassphrase ?? profile.Remote.Auth.PrivateKeyPassphrase,
        };

        if (string.IsNullOrEmpty(secrets.Password) && string.IsNullOrEmpty(secrets.PrivateKeyPassphrase))
        {
            var existing = GetSecretsPath(profile.ProfileId);
            if (File.Exists(existing))
            {
                File.Delete(existing);
            }

            return;
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(secrets, SyncProfileJson.Options);
        var encrypted = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(GetSecretsPath(profile.ProfileId), encrypted);
    }

    public ProfileSecrets? ReadSecrets(string id)
    {
        var path = GetSecretsPath(id);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var encrypted = File.ReadAllBytes(path);
            var json = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<ProfileSecrets>(json, SyncProfileJson.Options);
        }
        catch (CryptographicException)
        {
            // 换了 Windows 用户或机器：无法解密，按“未保存口令”处理
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public SyncProfile CreateDefault(string id, string localRoot, string remoteRoot, string host, string user)
    {
        return new SyncProfile
        {
            ProfileId = id,
            ProfileName = $"{Path.GetFileName(localRoot.TrimEnd('\\', '/'))} → {host}",
            LocalRoot = localRoot,
            RemoteRoot = remoteRoot,
            Remote = new RemoteOptions
            {
                Host = host,
                Port = 22,
                User = user,
                Auth = new AuthOptions { Type = "password" },
            },
        };
    }
}
