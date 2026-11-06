using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using Serilog;
using PetrelPush.Core.Abstractions;
using PetrelPush.Core.Model;

namespace PetrelPush.Transport;

/// <summary>
/// SFTP 写入协议实现：
/// 上传 = 临时文件 + 大小校验 + posix-rename 原子覆盖；删除/改名/建目录均校验 remoteRoot 边界。
/// </summary>
public sealed class SftpTransferExecutor : ITransferExecutor
{
    private const int UploadBufferSize = 64 * 1024;

    private readonly SftpConnectionPool _pool;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, byte> _knownDirectories = new(StringComparer.Ordinal);

    public SftpTransferExecutor(SyncProfile profile, ILogger logger)
    {
        _logger = logger;
        var validator = new HostKeyValidator(profile.Remote.HostKeyFingerprint);
        validator.FingerprintAccepted += fingerprint => HostKeyTrusted?.Invoke(fingerprint);
        _pool = new SftpConnectionPool(
            profile.Remote,
            profile.Remote.Password,
            Math.Max(1, profile.Sync.MaxParallelTransfers),
            validator,
            logger);
    }

    /// <summary>首次连接（测试连接）确认的主机指纹，调用方应写回配置。</summary>
    public event Action<string>? HostKeyTrusted;

    public async Task<TransferOutcome> ExecuteAsync(SyncTask task, SyncProfile profile, CancellationToken cancellationToken)
    {
        using var lease = await _pool.AcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => ExecuteCore(lease.Client, task, profile, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SyncFatalException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is SshException or SocketException or IOException or ObjectDisposedException or TimeoutException)
        {
            lease.MarkBroken();
            throw;
        }
    }

    public async Task TestConnectionAsync(SyncProfile profile, CancellationToken cancellationToken)
    {
        var validator = _pool.Validator;
        validator.TrustOnFirstUse = true;
        try
        {
            using var lease = await _pool.AcquireAsync(cancellationToken).ConfigureAwait(false);
            await Task.Run(
                () =>
                {
                    var client = lease.Client;
                    var root = PathGuard.NormalizeRemoteRoot(profile.RemoteRoot);
                    if (!client.Exists(root))
                    {
                        var parent = PathGuard.ParentOf(root);
                        if (!client.Exists(parent))
                        {
                            throw new IOException($"远端父目录不存在或无权限：{parent}");
                        }

                        client.CreateDirectory(root);
                        _logger.Information("已创建远端根目录：{Root}", root);
                    }

                    _knownDirectories.TryAdd(root, 0);
                    _logger.Information(
                        "连接测试成功：{User}@{Host}:{Port} → {Root}",
                        profile.Remote.User,
                        profile.Remote.Host,
                        profile.Remote.Port,
                        root);
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            validator.TrustOnFirstUse = false;
        }
    }

    public async Task<int> CleanupEmptyDirectoriesAsync(
        SyncProfile profile,
        IReadOnlySet<string> protectedDirectories,
        CancellationToken cancellationToken)
    {
        using var lease = await _pool.AcquireAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run(
            () => CleanupCore(lease.Client, profile, protectedDirectories, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _pool.DisposeAsync().ConfigureAwait(false);
    }

    private TransferOutcome ExecuteCore(
        SftpClient client,
        SyncTask task,
        SyncProfile profile,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return task.Action switch
        {
            SyncAction.Mkdir => ExecuteMkdir(client, task, profile),
            SyncAction.Upload => ExecuteUpload(client, task, profile, cancellationToken),
            SyncAction.Delete => ExecuteDelete(client, task, profile),
            SyncAction.Rename => ExecuteRename(client, task, profile),
            _ => throw new NotSupportedException($"未知动作：{task.Action}"),
        };
    }

    private TransferOutcome ExecuteMkdir(SftpClient client, SyncTask task, SyncProfile profile)
    {
        var remotePath = PathGuard.Combine(profile.RemoteRoot, task.DstPath);
        EnsureDirectory(client, remotePath);
        _logger.Debug("建目录 {Path}", task.DstPath);
        return new TransferOutcome(null, EntryKind.Dir);
    }

    private TransferOutcome ExecuteUpload(
        SftpClient client,
        SyncTask task,
        SyncProfile profile,
        CancellationToken cancellationToken)
    {
        var remotePath = PathGuard.Combine(profile.RemoteRoot, task.DstPath);
        EnsureDirectory(client, PathGuard.ParentOf(remotePath));

        var localPath = Path.Combine(profile.LocalRoot, task.DstPath.Replace('/', Path.DirectorySeparatorChar));
        var info = new FileInfo(localPath);
        if (!info.Exists)
        {
            throw new FileNotFoundException($"本地文件已消失：{task.DstPath}", localPath);
        }

        var temporaryPath = remotePath + ".sync_tmp_" + Guid.NewGuid().ToString("N");
        var limiter = profile.Sync.RateLimitKbps > 0 ? new TokenBucketRateLimiter(profile.Sync.RateLimitKbps) : null;

        try
        {
            using var file = new FileStream(
                localPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                UploadBufferSize,
                FileOptions.SequentialScan);
            using var hashing = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var uploadStream = new HashingUploadStream(file, hashing, limiter, cancellationToken);

            client.UploadFile(uploadStream, temporaryPath, true);

            var remoteSize = client.GetAttributes(temporaryPath).Size;
            if (remoteSize != info.Length)
            {
                throw new IOException($"上传校验失败：本地 {info.Length} 字节，远端 {remoteSize} 字节（{task.DstPath}）");
            }

            var hash = hashing.GetHashAndReset();
            RenameWithOverwrite(client, temporaryPath, remotePath);
            ApplyExecutablePermissions(client, remotePath, task.DstPath, profile);

            _logger.Debug("上传 {Path}（{Size} 字节）", task.DstPath, info.Length);
            return new TransferOutcome(hash, EntryKind.File);
        }
        catch
        {
            TryDeleteFile(client, temporaryPath);
            throw;
        }
    }

    private TransferOutcome ExecuteDelete(SftpClient client, SyncTask task, SyncProfile profile)
    {
        var remotePath = PathGuard.Combine(profile.RemoteRoot, task.DstPath);

        if (task.WantKind == EntryKind.Dir)
        {
            if (!client.Exists(remotePath))
            {
                return new TransferOutcome(null, EntryKind.Dir);
            }

            client.DeleteDirectory(remotePath);
            _knownDirectories.TryRemove(remotePath, out _);
            _logger.Debug("删除目录 {Path}", task.DstPath);
            return new TransferOutcome(null, EntryKind.Dir);
        }

        if (client.Exists(remotePath))
        {
            client.DeleteFile(remotePath);
            _logger.Debug("删除文件 {Path}", task.DstPath);
        }

        return new TransferOutcome(null, EntryKind.File);
    }

    private TransferOutcome ExecuteRename(SftpClient client, SyncTask task, SyncProfile profile)
    {
        if (task.SrcPath is null)
        {
            throw new InvalidOperationException($"rename 缺少源路径：{task.DstPath}");
        }

        var source = PathGuard.Combine(profile.RemoteRoot, task.SrcPath);
        var target = PathGuard.Combine(profile.RemoteRoot, task.DstPath);

        if (string.Equals(source, target, StringComparison.Ordinal))
        {
            return new TransferOutcome(task.WantHash, task.WantKind);
        }

        EnsureDirectory(client, PathGuard.ParentOf(target));
        RenameWithOverwrite(client, source, target);
        if (task.WantKind == EntryKind.Dir)
        {
            _knownDirectories.TryRemove(source, out _);
        }

        _logger.Debug("改名 {Source} → {Target}", task.SrcPath, task.DstPath);
        return new TransferOutcome(task.WantHash, task.WantKind);
    }

    private void RenameWithOverwrite(SftpClient client, string source, string target)
    {
        try
        {
            client.RenameFile(source, target, true);
            return;
        }
        catch (SshException ex)
        {
            _logger.Debug(ex, "posix-rename 不可用，回退为先删后改名：{Source} → {Target}", source, target);
        }

        if (client.Exists(target))
        {
            try
            {
                client.DeleteFile(target);
            }
            catch (SshException)
            {
                try
                {
                    client.DeleteDirectory(target);
                }
                catch (SshException deleteDirectoryError)
                {
                    _logger.Warning(deleteDirectoryError, "目标已存在且无法删除：{Target}", target);
                }
            }
        }

        client.RenameFile(source, target);
    }

    private void ApplyExecutablePermissions(SftpClient client, string remotePath, string relativePath, SyncProfile profile)
    {
        var suffixes = profile.Sync.ExecutableSuffixes;
        if (suffixes is null || suffixes.Length == 0)
        {
            return;
        }

        var extension = Path.GetExtension(relativePath);
        var shouldChmod = false;
        foreach (var suffix in suffixes)
        {
            if (string.Equals(suffix, extension, StringComparison.OrdinalIgnoreCase))
            {
                shouldChmod = true;
                break;
            }
        }

        if (!shouldChmod)
        {
            return;
        }

        try
        {
            client.ChangePermissions(remotePath, Convert.ToInt16("755", 8));
        }
        catch (Exception ex) when (ex is SshException or IOException)
        {
            _logger.Warning(ex, "设置可执行位失败：{Path}", relativePath);
        }
    }

    private void EnsureDirectory(SftpClient client, string remotePath)
    {
        var normalized = remotePath.Replace('\\', '/');
        if (normalized.Length == 0 || normalized == "/")
        {
            return;
        }

        if (_knownDirectories.ContainsKey(normalized))
        {
            return;
        }

        var current = string.Empty;
        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + segment;
            if (_knownDirectories.ContainsKey(current))
            {
                continue;
            }

            if (!client.Exists(current))
            {
                try
                {
                    client.CreateDirectory(current);
                }
                catch (SshException) when (client.Exists(current))
                {
                    // 并发创建，幂等
                }
            }

            _knownDirectories.TryAdd(current, 0);
        }
    }

    private int CleanupCore(
        SftpClient client,
        SyncProfile profile,
        IReadOnlySet<string> protectedDirectories,
        CancellationToken cancellationToken)
    {
        var root = PathGuard.NormalizeRemoteRoot(profile.RemoteRoot);
        if (!client.Exists(root))
        {
            return 0;
        }

        var directories = new List<string>();
        CollectDirectories(client, root, directories, 0, cancellationToken);
        directories.Sort((left, right) => CountSegments(right).CompareTo(CountSegments(left)));

        var removed = 0;
        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(directory, root, StringComparison.Ordinal))
            {
                continue;
            }

            var relative = PathGuard.RelativeTo(root, directory);
            if (protectedDirectories.Contains(relative))
            {
                continue;
            }

            try
            {
                var hasChildren = client.ListDirectory(directory)
                    .Any(entry => entry.Name is not ("." or ".."));
                if (hasChildren)
                {
                    continue;
                }

                client.DeleteDirectory(directory);
                _knownDirectories.TryRemove(directory, out _);
                removed++;
            }
            catch (Exception ex) when (ex is SshException or IOException)
            {
                _logger.Debug(ex, "清理空目录失败：{Directory}", directory);
            }
        }

        return removed;
    }

    private void CollectDirectories(
        SftpClient client,
        string directory,
        List<string> output,
        int depth,
        CancellationToken cancellationToken)
    {
        if (depth > 32 || output.Count >= 20000)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        output.Add(directory);

        List<ISftpFile> entries;
        try
        {
            entries = client.ListDirectory(directory).ToList();
        }
        catch (Exception ex) when (ex is SshException or IOException)
        {
            return;
        }

        foreach (var entry in entries)
        {
            if (!entry.IsDirectory || entry.Name is "." or "..")
            {
                continue;
            }

            CollectDirectories(client, directory.TrimEnd('/') + "/" + entry.Name, output, depth + 1, cancellationToken);
        }
    }

    private void TryDeleteFile(SftpClient client, string remotePath)
    {
        try
        {
            if (client.Exists(remotePath))
            {
                client.DeleteFile(remotePath);
            }
        }
        catch (Exception ex) when (ex is SshException or IOException)
        {
            _logger.Debug(ex, "清理临时文件失败：{Path}", remotePath);
        }
    }

    private static int CountSegments(string path) => path.Count(c => c == '/');
}
