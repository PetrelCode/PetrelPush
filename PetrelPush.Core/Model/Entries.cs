namespace PetrelPush.Core.Model;

/// <summary>条目类型，数值与 file_entry.kind 一致。</summary>
public enum EntryKind
{
    File = 0,
    Dir = 1,
}

/// <summary>相对路径的规范化与大小写标识处理。</summary>
public static class PathKeys
{
    /// <summary>统一为 '/' 分隔、去掉首尾分隔符（根路径为 ""）。</summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var normalized = path.Replace('\\', '/');
        var start = 0;
        var end = normalized.Length;
        while (start < end && normalized[start] == '/')
        {
            start++;
        }

        while (end > start && normalized[end - 1] == '/')
        {
            end--;
        }

        return normalized.Substring(start, end - start);
    }

    /// <summary>Windows 大小写不敏感的比较键。</summary>
    public static string Key(string path) => Normalize(path).ToLowerInvariant();

    /// <summary>路径深度（根为 0，'a/b' 为 1）。</summary>
    public static int Depth(string path)
    {
        var normalized = Normalize(path);
        if (normalized.Length == 0)
        {
            return 0;
        }

        var depth = 0;
        foreach (var c in normalized)
        {
            if (c == '/')
            {
                depth++;
            }
        }

        return depth;
    }

    public static string? Parent(string path)
    {
        var normalized = Normalize(path);
        var index = normalized.LastIndexOf('/');
        if (index < 0)
        {
            return null;
        }

        return normalized[..index];
    }

    public static bool IsUnder(string path, string prefix)
    {
        var p = Normalize(path);
        var q = Normalize(prefix);
        if (q.Length == 0)
        {
            return p.Length > 0;
        }

        return p.Length > q.Length
               && p[q.Length] == '/'
               && p.StartsWith(q, StringComparison.OrdinalIgnoreCase);
    }

    public static string RelativeTo(string path, string prefix)
    {
        var p = Normalize(path);
        var q = Normalize(prefix);
        if (q.Length == 0)
        {
            return p;
        }

        return p.Length > q.Length + 1 ? p[(q.Length + 1)..] : string.Empty;
    }
}

/// <summary>扫描到的一条本地条目（期望态）。</summary>
public sealed class ScannedEntry
{
    public ScannedEntry(string path, EntryKind kind, long size, long mtimeTicks, byte[]? hash = null)
    {
        Path = PathKeys.Normalize(path);
        PathKey = PathKeys.Key(Path);
        Kind = kind;
        Size = size;
        MtimeTicks = mtimeTicks;
        Hash = hash;
    }

    public string Path { get; }

    public string PathKey { get; }

    public EntryKind Kind { get; }

    public long Size { get; }

    /// <summary>UTC FILETIME（100ns 精度）。</summary>
    public long MtimeTicks { get; }

    /// <summary>内容哈希；null 表示尚未计算或读取失败。</summary>
    public byte[]? Hash { get; set; }

    public override string ToString() => $"{Path} ({Kind}, {Size})";
}

/// <summary>file_entry 行：本地快照 + 远端已知态。</summary>
public sealed class FileEntry
{
    public FileEntry(
        string path,
        EntryKind kind,
        long size,
        long mtimeTicks,
        byte[]? hash,
        byte[]? syncedHash,
        EntryKind? syncedKind)
    {
        Path = PathKeys.Normalize(path);
        PathKey = PathKeys.Key(Path);
        Kind = kind;
        Size = size;
        MtimeTicks = mtimeTicks;
        Hash = hash;
        SyncedHash = syncedHash;
        SyncedKind = syncedKind;
    }

    public string Path { get; set; }

    public string PathKey { get; set; }

    public EntryKind Kind { get; set; }

    public long Size { get; set; }

    public long MtimeTicks { get; set; }

    public byte[]? Hash { get; set; }

    /// <summary>远端已同步内容哈希；null 表示远端不存在该路径。</summary>
    public byte[]? SyncedHash { get; set; }

    /// <summary>远端已知类型；null 表示远端不存在该路径。</summary>
    public EntryKind? SyncedKind { get; set; }

    public override string ToString() => $"{Path} ({Kind}, synced={SyncedKind?.ToString() ?? "none"})";
}

/// <summary>哈希比较/格式化的小工具。</summary>
public static class Hashes
{
    public static string ToHex(byte[]? hash) => hash is null ? string.Empty : Convert.ToHexString(hash).ToLowerInvariant();

    public static bool Equal(byte[]? left, byte[]? right)
    {
        if (left is null)
        {
            return right is null;
        }

        return right is not null && left.AsSpan().SequenceEqual(right);
    }

    public static byte[]? FromHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return null;
        }

        return Convert.FromHexString(hex);
    }
}
