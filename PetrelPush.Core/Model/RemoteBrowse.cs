using System.Text;

namespace PetrelPush.Core.Model;

/// <summary>远端目录中的一条记录。只读浏览用，不参与 diff 与任务队列。</summary>
public sealed class RemoteEntry
{
    public RemoteEntry(
        string name,
        string relativePath,
        bool isDirectory,
        bool isSymbolicLink,
        bool isSpecialFile,
        long size,
        DateTimeOffset modifiedAt,
        string permissions)
    {
        Name = name;
        RelativePath = PathKeys.Normalize(relativePath);
        IsDirectory = isDirectory;
        IsSymbolicLink = isSymbolicLink;
        IsSpecialFile = isSpecialFile;
        Size = size;
        ModifiedAt = modifiedAt;
        Permissions = permissions;
    }

    public string Name { get; }

    /// <summary>相对 remoteRoot 的路径，'/' 分隔。</summary>
    public string RelativePath { get; }

    public bool IsDirectory { get; }

    /// <summary>符号链接；目录类型的链接不进入，避免环路。</summary>
    public bool IsSymbolicLink { get; }

    /// <summary>socket / fifo / 块设备等特殊文件，不支持下载。</summary>
    public bool IsSpecialFile { get; }

    public long Size { get; }

    public DateTimeOffset ModifiedAt { get; }

    /// <summary>八进制权限位，例如 644；无法获取时为空。</summary>
    public string Permissions { get; }

    public bool CanEnter => IsDirectory && !IsSymbolicLink;

    public bool CanDownload => !IsDirectory && !IsSpecialFile;
}

/// <summary>下载完成后的实际结果。</summary>
public sealed record RemoteDownloadResult(long Size, DateTimeOffset ModifiedAt);

/// <summary>远端删除结果：删掉的文件数与目录数。</summary>
public sealed record RemoteDeleteResult(int Files, int Directories);

/// <summary>文件页条目的差异状态。</summary>
public enum FileEntryState
{
    /// <summary>本地与远端已知态一致。</summary>
    Synced,

    /// <summary>本地有，远端已知态缺失。</summary>
    PendingUpload,

    /// <summary>被忽略规则排除，不参与同步。</summary>
    Ignored,

    /// <summary>远端存在，本地也存在。</summary>
    LocalExists,

    /// <summary>远端存在，本地没有（本地已删除或远端被手工新增）。</summary>
    RemoteOnly,
}

/// <summary>差异状态判定：本地侧看远端已知态，远端侧看本地是否存在。</summary>
public static class FileEntryStates
{
    public static FileEntryState ForLocal(FileEntry? snapshotEntry, EntryKind kind, bool ignored)
    {
        if (ignored)
        {
            return FileEntryState.Ignored;
        }

        return snapshotEntry?.SyncedKind == kind ? FileEntryState.Synced : FileEntryState.PendingUpload;
    }

    public static FileEntryState ForRemote(bool localExists) =>
        localExists ? FileEntryState.LocalExists : FileEntryState.RemoteOnly;

    public static string Describe(FileEntryState state) => state switch
    {
        FileEntryState.Synced => "已同步",
        FileEntryState.PendingUpload => "待上传",
        FileEntryState.Ignored => "已忽略",
        FileEntryState.LocalExists => "本地存在",
        FileEntryState.RemoteOnly => "仅远端",
        _ => string.Empty,
    };
}

/// <summary>八进制权限位与符号形式互转（644 → rw-r--r--）。</summary>
public static class UnixPermissions
{
    public static string Describe(string? octal)
    {
        if (string.IsNullOrWhiteSpace(octal) || octal.Length != 3)
        {
            return string.Empty;
        }

        var chars = new char[9];
        for (var i = 0; i < 3; i++)
        {
            var digit = octal[i] - '0';
            if (digit is < 0 or > 7)
            {
                return string.Empty;
            }

            chars[i * 3] = (digit & 4) != 0 ? 'r' : '-';
            chars[i * 3 + 1] = (digit & 2) != 0 ? 'w' : '-';
            chars[i * 3 + 2] = (digit & 1) != 0 ? 'x' : '-';
        }

        return new string(chars);
    }
}

/// <summary>把远端文件名转换为 Windows 可用的本地文件名（Linux 允许 ':' '*' '?' 等字符）。</summary>
public static class LocalFileName
{
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    public static string Sanitize(string? remoteName, string fallback = "_")
    {
        if (string.IsNullOrWhiteSpace(remoteName))
        {
            return fallback;
        }

        var builder = new StringBuilder(remoteName.Length);
        foreach (var c in remoteName)
        {
            builder.Append(char.IsControl(c) || Array.IndexOf(InvalidChars, c) >= 0 ? '_' : c);
        }

        // Windows 不允许文件名以空格或点结尾
        var name = builder.ToString().TrimEnd(' ', '.');
        if (name.Length == 0)
        {
            return fallback;
        }

        var stem = Path.GetFileNameWithoutExtension(name);
        foreach (var reserved in ReservedNames)
        {
            if (string.Equals(stem, reserved, StringComparison.OrdinalIgnoreCase))
            {
                return "_" + name;
            }
        }

        return name;
    }
}
