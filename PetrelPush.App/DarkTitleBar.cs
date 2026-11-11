using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PetrelPush.App;

/// <summary>
/// 深色标题栏：主窗口与终端窗口的顶栏都是深色底，标题栏若保持系统浅色，
/// 窗口顶部会多出一条突兀的亮边，因此统一交给 DWM 切成深色。
/// </summary>
internal static class DarkTitleBar
{
    /// <summary>Windows 10 2004 及以后 / Windows 11 的深色模式属性编号。</summary>
    private const int DwmwaUseImmersiveDarkMode = 20;

    /// <summary>Windows 10 1809 ~ 1909 上同一属性用的是旧编号。</summary>
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>在窗口句柄就绪后调用；系统过旧时 DWM 会返回失败，此时保持默认外观即可。</summary>
    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        int enabled = 1;
        if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeLegacy, ref enabled, sizeof(int));
        }
    }
}
