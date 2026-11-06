using System.IO.Enumeration;
using Serilog;
using PetrelPush.Core.Model;

namespace PetrelPush.Core.Scan;

public sealed class ScannerOptions
{
    public bool SyncEmptyDirs { get; init; } = true;

    public bool FollowSymlinks { get; init; }
}

/// <summary>被跳过的条目（reparse point、不可访问等）。</summary>
public sealed record SkippedPath(string Path, string Reason);

public sealed class ScanResult
{
    public List<ScannedEntry> Entries { get; } = new();

    public List<SkippedPath> Skipped { get; } = new();

    /// <summary>有子项的目录集合（相对路径，大小写不敏感）。</summary>
    public HashSet<string> NonEmptyDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>扫描被异常中断（结果不完整）。此时不得据此做删除类决策。</summary>
    public bool Incomplete { get; internal set; }

    public int FileCount
    {
        get
        {
            var count = 0;
            foreach (var entry in Entries)
            {
                if (entry.Kind == EntryKind.File)
                {
                    count++;
                }
            }

            return count;
        }
    }

    public int DirectoryCount => Entries.Count - FileCount;
}

/// <summary>
/// 目录扫描器：单次遍历取回长度/时间/属性，按忽略规则整棵剪枝，跳过 reparse point。
/// </summary>
public sealed class Scanner
{
    public ScanResult Scan(
        string localRoot,
        IgnoreRules rules,
        ScannerOptions options,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(localRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"本地目录不存在：{root}");
        }

        var result = new ScanResult();
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
            BufferSize = 64 * 1024,
        };

        bool Include(ref FileSystemEntry entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var full = entry.ToSpecifiedFullPath();
            var relative = ToRelative(root, full);
            if (relative.Length == 0)
            {
                return false;
            }

            if (!options.FollowSymlinks && (entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                result.Skipped.Add(new SkippedPath(relative, "reparse point（符号链接 / junction / 云盘占位）"));
                return false;
            }

            return !rules.IsIgnored(relative, entry.IsDirectory);
        }

        bool Recurse(ref FileSystemEntry entry)
        {
            var full = entry.ToSpecifiedFullPath();
            var relative = ToRelative(root, full);
            if (relative.Length == 0)
            {
                return false;
            }

            if (!entry.IsDirectory)
            {
                return false;
            }

            if (!options.FollowSymlinks && (entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            if (rules.IsIgnored(relative, true))
            {
                return false;
            }

            // 进入目录前先把该目录的 .gitignore 纳入规则集
            rules.EnsureDirectoryRules(relative);
            return true;
        }

        ScannedEntry Transform(ref FileSystemEntry entry)
        {
            var full = entry.ToSpecifiedFullPath();
            var relative = ToRelative(root, full);
            var isDirectory = entry.IsDirectory;
            return new ScannedEntry(
                relative,
                isDirectory ? EntryKind.Dir : EntryKind.File,
                isDirectory ? 0 : entry.Length,
                entry.LastWriteTimeUtc.UtcTicks);
        }

        var enumerable = new FileSystemEnumerable<ScannedEntry>(root, Transform, enumerationOptions)
        {
            ShouldIncludePredicate = Include,
            ShouldRecursePredicate = Recurse,
        };

        try
        {
            foreach (var scanned in enumerable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Entries.Add(scanned);

                var parent = PathKeys.Parent(scanned.Path);
                if (parent is not null)
                {
                    result.NonEmptyDirectories.Add(parent);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 扫描中断：结果集不完整，本轮只做“不删除任何东西”的保守处理
            result.Incomplete = true;
            logger?.Warning(ex, "扫描中断（结果不完整），本轮不做删除决策，已收集 {Count} 个条目", result.Entries.Count);
        }

        return result;
    }

    private static string ToRelative(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative == ".")
        {
            return string.Empty;
        }

        return PathKeys.Normalize(relative);
    }
}
