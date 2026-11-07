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

    public SftpTransferExecutor(SyncProfile profile, ILogger logger, int? poolSize = null)
    {
        _logger = logger;
        var validator = new HostKeyValidator(profile.Remote.HostKeyFingerprint);
        validator.FingerprintAccepted += fingerprint => HostKeyTrusted?.Invoke(fingerprint);
        _pool = new SftpConnectionPool(
            profile.Remote,
            profile.Remote.Password,
            Math.Max(1, poolSize ?? profile.Sync.MaxParallelTransfers),
            validator,
            logger);
    }

    /// <summary>首次连接（测试连接）确认的主机指纹，调用方应写回配置。</summary>
    public event Action<string>? HostKeyTrusted;

    /// <summary>上传字节进度：目标相对路径、已上传字节、文件总字节。</summary>
    public event Action<string, long, long>? UploadProgress;

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

    /// <summary>
    /// 列出远端目录的一层条目（只读浏览）。路径经 <see cref="PathGuard.Combine"/> 规范化，
    /// 只能落在 remoteRoot 之内，不参与 diff 与任务队列。
    /// </summary>
    public async Task<IReadOnlyList<RemoteEntry>> ListDirectoryAsync(
        string relativeDirectory,
        SyncProfile profile,
        int maxEntries,
        CancellationToken cancellationToken)
    {
        using var lease = await _pool.AcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                () => ListDirectoryCore(lease.Client, profile, relativeDirectory, maxEntries, cancellationToken),
                cancellationToken).ConfigureAwait(false);
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

    /// <summary>把远端文件下载到本地：临时文件 + 大小校验 + 原子替换。</summary>
    public async Task<RemoteDownloadResult> DownloadFileAsync(
        string relativePath,
        string localDestinationPath,
        SyncProfile profile,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        using var lease = await _pool.AcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                () => DownloadCore(lease.Client, profile, relativePath, localDestinationPath, progress, cancellationToken),
                cancellationToken).ConfigureAwait(false);
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

    /// <summary>把本地文件直接上传到远端指定相对路径（拖拽上传用，不经过本地同步目录）。</summary>
    public async Task<long> UploadFileAsync(
        string localFilePath,
        string relativePath,
        SyncProfile profile,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        using var lease = await _pool.AcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                () => UploadCore(lease.Client, profile, localFilePath, relativePath, progress, cancellationToken),
                cancellationToken).ConfigureAwait(false);
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

    private List<RemoteEntry> ListDirectoryCore(
        SftpClient client,
        SyncProfile profile,
        string relativeDirectory,
        int maxEntries,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var root = PathGuard.NormalizeRemoteRoot(profile.RemoteRoot);
        var directory = PathGuard.Combine(profile.RemoteRoot, relativeDirectory);
        if (!client.Exists(directory))
        {
            throw new IOException($"远端目录不存在：{directory}");
        }

        var baseRelative = PathGuard.RelativeTo(root, directory);
        var limit = Math.Clamp(maxEntries, 1, 20000);
        var results = new List<RemoteEntry>();

        foreach (var file in client.ListDirectory(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Name is "." or "..")
            {
                continue;
            }

            if (results.Count >= limit)
            {
                _logger.Debug("目录条目超过 {Limit} 条，已截断：{Directory}", limit, directory);
                break;
            }

            var relative = baseRelative.Length == 0 ? file.Name : baseRelative + "/" + file.Name;
            results.Add(ToRemoteEntry(file, relative));
        }

        results.Sort(CompareRemoteEntries);
        return results;
    }

    private RemoteDownloadResult DownloadCore(
        SftpClient client,
        SyncProfile profile,
        string relativePath,
        string localDestinationPath,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var remotePath = PathGuard.Combine(profile.RemoteRoot, relativePath);
        var attributes = client.GetAttributes(remotePath);
        if (attributes.IsDirectory)
        {
            throw new IOException($"远端路径是目录，无法下载：{remotePath}");
        }

        var destination = Path.GetFullPath(localDestinationPath);
        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = destination + ".dl_tmp_" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       UploadBufferSize,
                       FileOptions.SequentialScan))
            {
                client.DownloadFile(remotePath, stream, downloaded => progress?.Report((long)downloaded));
                stream.Flush();

                if (stream.Length != attributes.Size)
                {
                    throw new IOException(
                        $"下载校验失败：远端 {attributes.Size} 字节，本地 {stream.Length} 字节（{relativePath}）");
                }
            }

            File.Move(temporaryPath, destination, true);
            _logger.Debug("下载 {Path}（{Size} 字节）→ {Destination}", relativePath, attributes.Size, destination);

            var modified = new DateTimeOffset(DateTime.SpecifyKind(attributes.LastWriteTimeUtc, DateTimeKind.Utc));
            return new RemoteDownloadResult(attributes.Size, modified);
        }
        catch
        {
            TryDeleteLocalFile(temporaryPath);
            throw;
        }
    }

    private long UploadCore(
        SftpClient client,
        SyncProfile profile,
        string localFilePath,
        string relativePath,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var info = new FileInfo(localFilePath);
        if (!info.Exists)
        {
            throw new FileNotFoundException($"本地文件不存在：{localFilePath}", localFilePath);
        }

        var remotePath = PathGuard.Combine(profile.RemoteRoot, relativePath);
        EnsureDirectory(client, PathGuard.ParentOf(remotePath));

        var temporaryPath = remotePath + ".sync_tmp_" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(
                       localFilePath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete,
                       UploadBufferSize,
                       FileOptions.SequentialScan))
            {
                client.UploadFile(stream, temporaryPath, true, uploaded => progress?.Report((long)uploaded));
            }

            var remoteSize = client.GetAttributes(temporaryPath).Size;
            if (remoteSize != info.Length)
            {
                throw new IOException(
                    $"上传校验失败：本地 {info.Length} 字节，远端 {remoteSize} 字节（{relativePath}）");
            }

            RenameWithOverwrite(client, temporaryPath, remotePath);
            ApplyExecutablePermissions(client, remotePath, relativePath, profile);
            _logger.Debug("直接上传 {Path}（{Size} 字节）", relativePath, info.Length);
            return info.Length;
        }
        catch
        {
            TryDeleteFile(client, temporaryPath);
            throw;
        }
    }

    private static RemoteEntry ToRemoteEntry(ISftpFile file, string relativePath)
    {
        try
        {
            var attributes = file.Attributes;
            var isDirectory = attributes?.IsDirectory ?? file.IsDirectory;
            var isSymbolicLink = attributes?.IsSymbolicLink ?? file.IsSymbolicLink;
            var isRegularFile = attributes?.IsRegularFile ?? file.IsRegularFile;
            var permissions = attributes is null ? string.Empty : FormatPermissions(attributes);
            var modified = new DateTimeOffset(DateTime.SpecifyKind(file.LastWriteTimeUtc, DateTimeKind.Utc));

            return new RemoteEntry(
                file.Name,
                relativePath,
                isDirectory,
                isSymbolicLink,
                !isDirectory && !isRegularFile && !isSymbolicLink,
                isDirectory ? 0 : file.Length,
                modified,
                permissions);
        }
        catch (Exception ex) when (ex is SshException or IOException or FormatException or ArgumentException or InvalidOperationException)
        {
            // 单个条目属性异常不影响整目录浏览
            return new RemoteEntry(
                file.Name,
                relativePath,
                file.IsDirectory,
                false,
                false,
                0,
                DateTimeOffset.MinValue,
                string.Empty);
        }
    }

    /// <summary>把 SSH.NET 的布尔权限位拼成八进制（如 644）。</summary>
    private static string FormatPermissions(SftpFileAttributes attributes)
    {
        var value = 0;
        if (attributes.OwnerCanRead)
        {
            value |= 0b100_000_000;
        }

        if (attributes.OwnerCanWrite)
        {
            value |= 0b010_000_000;
        }

        if (attributes.OwnerCanExecute)
        {
            value |= 0b001_000_000;
        }

        if (attributes.GroupCanRead)
        {
            value |= 0b000_100_000;
        }

        if (attributes.GroupCanWrite)
        {
            value |= 0b000_010_000;
        }

        if (attributes.GroupCanExecute)
        {
            value |= 0b000_001_000;
        }

        if (attributes.OthersCanRead)
        {
            value |= 0b000_000_100;
        }

        if (attributes.OthersCanWrite)
        {
            value |= 0b000_000_010;
        }

        if (attributes.OthersCanExecute)
        {
            value |= 0b000_000_001;
        }

        return Convert.ToString(value, 8).PadLeft(3, '0');
    }

    private static int CompareRemoteEntries(RemoteEntry left, RemoteEntry right)
    {
        if (left.IsDirectory != right.IsDirectory)
        {
            return left.IsDirectory ? -1 : 1;
        }

        return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
    }

    private void TryDeleteLocalFile(string localPath)
    {
        try
        {
            if (File.Exists(localPath))
            {
                File.Delete(localPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Debug(ex, "清理本地临时文件失败：{Path}", localPath);
        }
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

            // 按 1% 粒度上报，避免每个 SFTP 缓冲包都触发一次 UI 更新
            var totalBytes = info.Length;
            var lastStep = 0L;
            void ReportUpload(long done)
            {
                var step = totalBytes <= 0 ? 100 : (long)Math.Floor(done * 100.0 / totalBytes);
                if (step == lastStep)
                {
                    return;
                }

                lastStep = step;
                UploadProgress?.Invoke(task.DstPath, done, totalBytes);
            }

            using var uploadStream = new HashingUploadStream(file, hashing, limiter, cancellationToken, ReportUpload);

            client.UploadFile(uploadStream, temporaryPath, true);

            var remoteSize = client.GetAttributes(temporaryPath).Size;
            if (remoteSize != info.Length)
            {
                throw new IOException($"上传校验失败：本地 {info.Length} 字节，远端 {remoteSize} 字节（{task.DstPath}）");
            }

            var hash = hashing.GetHashAndReset();
            RenameWithOverwrite(client, temporaryPath, remotePath);
            ApplyExecutablePermissions(client, remotePath, task.DstPath, profile);

            // 补一次终态，保证 UI 收到 100%
            UploadProgress?.Invoke(task.DstPath, totalBytes, totalBytes);

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
