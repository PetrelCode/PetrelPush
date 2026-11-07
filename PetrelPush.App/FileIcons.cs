using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PetrelPush.App;

/// <summary>
/// 按扩展名取 Windows 系统文件图标（与资源管理器一致）。
/// 使用 SHGFI_USEFILEATTRIBUTES 按扩展名查询，不要求文件真实存在，本地与远端条目共用。
/// </summary>
public static class FileIcons
{
    private const uint FileAttributeNormal = 0x80;
    private const uint FileAttributeDirectory = 0x10;
    private const uint ShgfiIcon = 0x100;
    private const uint ShgfiUseFileAttributes = 0x10;

    private static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    private static ImageSource? _folder;
    private static ImageSource? _genericFile;
    private static bool _folderResolved;
    private static bool _genericResolved;

    public static ImageSource? FolderIcon
    {
        get
        {
            if (!_folderResolved)
            {
                // 伪文件名不能是 "."：会被解析成驱动器根，取到磁盘图标
                _folder = Query("folder", FileAttributeDirectory);
                _folderResolved = true;
            }

            return _folder;
        }
    }

    public static ImageSource? GenericFileIcon => ResolveGeneric();

    /// <summary>按文件名的扩展名取图标；未知类型回退到通用文件图标。</summary>
    public static ImageSource? ForFile(string name)
    {
        var extension = Path.GetExtension(name);
        if (string.IsNullOrEmpty(extension))
        {
            return ResolveGeneric();
        }

        lock (Gate)
        {
            if (Cache.TryGetValue(extension, out var cached))
            {
                return cached;
            }
        }

        var icon = Query("a" + extension, FileAttributeNormal);
        if (icon is null)
        {
            return ResolveGeneric();
        }

        lock (Gate)
        {
            Cache[extension] = icon;
        }

        return icon;
    }

    private static ImageSource? ResolveGeneric()
    {
        if (!_genericResolved)
        {
            _genericFile = Query("a.txt", FileAttributeNormal);
            _genericResolved = true;
        }

        return _genericFile;
    }

    private static ImageSource? Query(string pseudoPath, uint attributes)
    {
        var info = new ShFileInfo();
        try
        {
            var result = SHGetFileInfo(
                pseudoPath,
                attributes,
                ref info,
                (uint)Marshal.SizeOf<ShFileInfo>(),
                ShgfiIcon | ShgfiUseFileAttributes);

            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            {
                return null;
            }

            var source = Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (info.hIcon != IntPtr.Zero)
            {
                DestroyIcon(info.hIcon);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref ShFileInfo psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
