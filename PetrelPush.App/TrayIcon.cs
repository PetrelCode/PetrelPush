using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PetrelPush.App;

public enum TrayState
{
    Idle,
    Busy,
    Error,
    Paused,
}

/// <summary>系统托盘图标与菜单（Color 状态：绿/黄/红/灰）。</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _pauseItem;
    private Icon? _current;
    private IntPtr _iconHandle;
    private bool _disposed;

    public TrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开主窗口", null, (_, _) => ShowRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("立即同步", null, (_, _) => SyncNowRequested?.Invoke());
        _pauseItem = new ToolStripMenuItem("暂停", null, (_, _) => PauseResumeRequested?.Invoke());
        menu.Items.Add(_pauseItem);
        menu.Items.Add("深度校验", null, (_, _) => DeepVerifyRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke());

        _notifyIcon = new NotifyIcon
        {
            Text = "PetrelPush",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowRequested?.Invoke();

        SetState(TrayState.Idle, "PetrelPush 就绪");
    }

    public event Action? ShowRequested;

    public event Action? SyncNowRequested;

    public event Action? PauseResumeRequested;

    public event Action? DeepVerifyRequested;

    public event Action? ExitRequested;

    public void SetState(TrayState state, string tooltip)
    {
        if (_disposed)
        {
            return;
        }

        var color = state switch
        {
            TrayState.Busy => Color.FromArgb(255, 193, 7),
            TrayState.Error => Color.FromArgb(224, 49, 49),
            TrayState.Paused => Color.FromArgb(158, 158, 158),
            _ => Color.FromArgb(67, 160, 71),
        };

        SetIcon(CreateIcon(color));
        _notifyIcon.Text = Clip(tooltip);
    }

    public void SetPaused(bool paused)
    {
        _pauseItem.Text = paused ? "恢复同步" : "暂停同步";
    }

    public void ShowBalloon(string title, string message)
    {
        if (_disposed)
        {
            return;
        }

        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.ShowBalloonTip(3000);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _current?.Dispose();
        _current = null;
        if (_iconHandle != IntPtr.Zero)
        {
            DestroyIcon(_iconHandle);
            _iconHandle = IntPtr.Zero;
        }
    }

    private void SetIcon(Icon icon)
    {
        var previousIcon = _current;
        var previousHandle = _iconHandle;

        _current = icon;
        _iconHandle = icon.Handle;
        _notifyIcon.Icon = icon;

        previousIcon?.Dispose();
        if (previousHandle != IntPtr.Zero)
        {
            DestroyIcon(previousHandle);
        }
    }

    private static Icon CreateIcon(Color color)
    {
        using var bitmap = new Bitmap(16, 16);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            graphics.FillEllipse(brush, 1, 1, 14, 14);
            using var pen = new Pen(Color.FromArgb(90, 0, 0, 0));
            graphics.DrawEllipse(pen, 1, 1, 13, 13);
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }

    private static string Clip(string value) => value.Length <= 63 ? value : value[..60] + "…";

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
