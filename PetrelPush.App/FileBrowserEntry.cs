using System;
using System.Windows.Media;
using PetrelPush.Core.Model;

namespace PetrelPush.App;

/// <summary>文件页的一行：本地 / 远端条目 + 差异标记的展示投影。</summary>
public sealed class FileBrowserEntry
{
    private static readonly SolidColorBrush SyncedBrush = Frozen(0x2E, 0x9E, 0x4F);
    private static readonly SolidColorBrush PendingBrush = Frozen(0xB7, 0x79, 0x1F);
    private static readonly SolidColorBrush MutedBrush = Frozen(0x9C, 0x95, 0xAB);
    private static readonly SolidColorBrush RemoteOnlyBrush = Frozen(0x73, 0x33, 0xC6);
    private static readonly SolidColorBrush SpecialBrush = Frozen(0xD0, 0x34, 0x2C);

    private FileBrowserEntry()
    {
    }

    public string Name { get; private init; } = string.Empty;

    /// <summary>相对同步根的路径，'/' 分隔。</summary>
    public string RelativePath { get; private init; } = string.Empty;

    public bool IsDirectory { get; private init; }

    /// <summary>是否可进入（目录，且不是符号链接目录）。</summary>
    public bool CanEnter { get; private init; }

    /// <summary>是否可下载（远端普通文件）。</summary>
    public bool CanDownload { get; private init; }

    /// <summary>系统文件图标（按扩展名，与资源管理器一致）。</summary>
    public ImageSource? Icon { get; private init; }

    public string SizeText { get; private init; } = "—";

    /// <summary>原始字节数（目录为 0），用于计算下载进度。</summary>
    public long SizeBytes { get; private init; }

    public string TimeText { get; private init; } = "—";

    public string Permissions { get; private init; } = string.Empty;

    /// <summary>权限的符号形式，如 rw-r--r--。</summary>
    public string PermissionsHint { get; private init; } = string.Empty;

    public string StateText { get; private init; } = string.Empty;

    public Brush StateBrush { get; private init; } = MutedBrush;

    public string ToolTip { get; private init; } = string.Empty;

    public static FileBrowserEntry FromLocal(
        string name,
        string relativePath,
        bool isDirectory,
        bool isSymbolicLink,
        bool isSpecialFile,
        long size,
        DateTime modifiedAt,
        FileEntryState state)
    {
        var sizeText = isDirectory ? "—" : FormatSize(size);
        return new FileBrowserEntry
        {
            Name = name,
            RelativePath = relativePath,
            IsDirectory = isDirectory,
            CanEnter = isDirectory && !isSymbolicLink,
            CanDownload = false,
            Icon = isDirectory ? FileIcons.FolderIcon : FileIcons.ForFile(name),
            SizeText = sizeText,
            SizeBytes = isDirectory ? 0 : size,
            TimeText = FormatTime(modifiedAt),
            StateText = FileEntryStates.Describe(state),
            StateBrush = BrushFor(state),
            ToolTip = BuildToolTip(relativePath, sizeText, modifiedAt, string.Empty),
        };
    }

    public static FileBrowserEntry FromRemote(RemoteEntry entry, bool localExists)
    {
        var state = FileEntryStates.ForRemote(localExists);
        var sizeText = entry.IsDirectory ? "—" : FormatSize(entry.Size);
        var permissionsHint = UnixPermissions.Describe(entry.Permissions);
        return new FileBrowserEntry
        {
            Name = entry.Name,
            RelativePath = entry.RelativePath,
            IsDirectory = entry.IsDirectory,
            CanEnter = entry.CanEnter,
            CanDownload = entry.CanDownload,
            Icon = entry.IsDirectory ? FileIcons.FolderIcon : FileIcons.ForFile(entry.Name),
            SizeText = sizeText,
            SizeBytes = entry.Size,
            TimeText = FormatTime(entry.ModifiedAt.LocalDateTime),
            Permissions = entry.Permissions,
            PermissionsHint = permissionsHint,
            StateText = entry.IsSpecialFile ? "特殊文件" : FileEntryStates.Describe(state),
            StateBrush = entry.IsSpecialFile ? SpecialBrush : BrushFor(state),
            ToolTip = BuildToolTip(entry.RelativePath, sizeText, entry.ModifiedAt.LocalDateTime, permissionsHint),
        };
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 0)
        {
            return "—";
        }

        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        string[] units = ["KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = -1;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

    private static string BuildToolTip(string relativePath, string sizeText, DateTime time, string permissionsHint)
    {
        var timeText = time == DateTime.MinValue || time.Year < 1900
            ? "未知"
            : time.ToString("yyyy-MM-dd HH:mm:ss");

        var tail = string.IsNullOrEmpty(permissionsHint)
            ? $"{sizeText} · 修改于 {timeText}"
            : $"{sizeText} · 修改于 {timeText} · 权限 {permissionsHint}";

        return $"{relativePath}\n{tail}";
    }

    private static Brush BrushFor(FileEntryState state) => state switch
    {
        FileEntryState.Synced => SyncedBrush,
        FileEntryState.PendingUpload => PendingBrush,
        FileEntryState.RemoteOnly => RemoteOnlyBrush,
        _ => MutedBrush,
    };

    private static string FormatTime(DateTime time)
    {
        if (time == DateTime.MinValue || time.Year < 1900)
        {
            return "—";
        }

        return time.ToString("yyyy-MM-dd HH:mm");
    }

    private static SolidColorBrush Frozen(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
