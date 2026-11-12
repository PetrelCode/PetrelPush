using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PetrelPush.Core.Model;

namespace PetrelPush.App;

/// <summary>
/// 终端顶栏「快速命令 / 快速路径」按钮下方弹出的浮层：列出条目，点一条把对应文本填到终端输入行（不自动回车）。
/// 命令列表在「设置」页维护，这里只读；路径列表仍可在这里增删 —— 设置页只管命名的命令。
/// 浮层是独立的无边框窗口：WebView2 是 HWND 宿主控件，同区域的 WPF 浮层会被遮挡，
/// 而顶栏面板又会把终端内容往下压。
/// </summary>
public partial class QuickAccessPickerWindow : Window
{
    private readonly QuickAccessOptions _quickAccess;
    private readonly bool _isRemoteSession;
    private readonly string _workingDirectory;
    private readonly Action<QuickAccessOptions>? _persist;

    /// <summary>弹窗里再打开子对话框时，焦点离开会把浮层当成"失焦关闭"，这里临时屏蔽。</summary>
    private bool _suppressDeactivate;

    public QuickAccessPickerWindow(
        QuickAccessOptions quickAccess,
        bool showPaths,
        bool isRemoteSession,
        string workingDirectory,
        Action<QuickAccessOptions>? persist)
    {
        InitializeComponent();

        _quickAccess = quickAccess;
        _isRemoteSession = isRemoteSession;
        _workingDirectory = workingDirectory;
        _persist = persist;
        ShowPaths = showPaths;

        HintText.Text = showPaths
            ? "点一条把 cd 命令填进终端输入行，不会自动回车。"
            : "点一条把命令填进终端输入行，不会自动回车。";
        AddPathButton.Visibility = showPaths ? Visibility.Visible : Visibility.Collapsed;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };
        Deactivated += OnDeactivated;

        RefreshItems();
    }

    /// <summary>本次打开的是路径列表（true）还是命令列表（false）。</summary>
    public bool ShowPaths { get; }

    /// <summary>是否因为失焦被关掉：顶栏按钮靠它区分"再次点击收起"与"重新打开"。</summary>
    public bool ClosedByDeactivate { get; private set; }

    /// <summary>用户选中了一条；参数为要填进终端的文本。</summary>
    public event Action<string>? ItemPicked;

    /// <summary>
    /// 贴着锚点按钮下方弹出，右边缘与按钮对齐。
    /// 边界按「虚拟屏幕」而不是主屏工作区计算，多显示器（含负坐标）时也不会跑到别的屏上。
    /// 注意不要给它设 Owner：带 Owner 的无边框浮层在激活状态切换时会把终端窗口一起带进
    /// 最小化 / 隐藏状态（Win32 会按属主窗口的显示状态联动）；这里靠自身 Deactivated 关闭。
    /// </summary>
    public void ShowUnder(FrameworkElement anchor)
    {
        var dpi = VisualTreeHelper.GetDpi(anchor);
        var screenPoint = anchor.PointToScreen(new Point(anchor.ActualWidth, anchor.ActualHeight));
        var bottomRight = new Point(screenPoint.X / dpi.DpiScaleX, screenPoint.Y / dpi.DpiScaleY);

        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

        Left = Math.Max(virtualLeft, Math.Min(bottomRight.X - Width, virtualRight - Width));
        Top = bottomRight.Y + 6;

        Show();

        // SizeToContent 的窗口高度要等显示后才知道；贴底放不下时翻到按钮上方
        if (Top + ActualHeight > virtualBottom)
        {
            Top = Math.Max(virtualTop, bottomRight.Y - anchor.ActualHeight - ActualHeight - 12);
        }
    }

    /// <summary>本会话使用的路径列表：远端终端看 RemotePaths，本地终端看 LocalPaths。</summary>
    private List<string> Paths => _isRemoteSession
        ? _quickAccess.RemotePaths ??= new List<string>()
        : _quickAccess.LocalPaths ??= new List<string>();

    private void RefreshItems()
    {
        var items = new List<PickerItem>();
        if (ShowPaths)
        {
            items.AddRange(Paths.Select(path => new PickerItem(string.Empty, path, path, removable: true)));
        }
        else
        {
            items.AddRange((_quickAccess.Commands ?? new List<QuickCommand>())
                .Where(command => !string.IsNullOrWhiteSpace(command.Command))
                .Select(command => new PickerItem(command.Name, command.Command, command.Command, removable: false)));
        }

        ItemList.ItemsSource = items;

        EmptyText.Text = ShowPaths
            ? "还没有快速路径：点下方「添加路径」，选中后会把 cd 命令填进终端。"
            : "还没有快速命令：到「设置」页的「快速命令」里添加，选择后会把命令填进终端。";
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPickRow(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PickerItem item })
        {
            return;
        }

        Close();
        ItemPicked?.Invoke(item.Value);
    }

    private void OnRemoveRow(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PickerItem item })
        {
            return;
        }

        if (!Paths.Remove(item.Value))
        {
            return;
        }

        Persist();
        RefreshItems();
    }

    private void OnAddPath(object sender, RoutedEventArgs e)
    {
        _suppressDeactivate = true;
        try
        {
            var value = _isRemoteSession ? PromptRemotePath() : PickLocalDirectory();
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            Paths.Add(value);
            Persist();
            RefreshItems();
        }
        finally
        {
            _suppressDeactivate = false;
        }
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_suppressDeactivate)
        {
            return;
        }

        ClosedByDeactivate = true;

        // 失焦发生在窗口激活切换的消息处理里，此时直接 Close 会让激活状态机来回抖
        // （实测表现为点一下终端，终端跟着被最小化），推迟到本次输入处理完再关。
        Dispatcher.BeginInvoke(new Action(Close), DispatcherPriority.Background);
    }

    private string? PickLocalDirectory()
    {
        var dialog = new OpenFolderDialog { Title = "选择要加入快速路径的目录" };
        if (Directory.Exists(_workingDirectory))
        {
            dialog.InitialDirectory = _workingDirectory;
        }

        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private string? PromptRemotePath()
    {
        var initial = _workingDirectory.StartsWith('/') ? _workingDirectory : "/";
        var dialog = new TextInputWindow("添加快速路径", "远端目录（Linux 绝对路径）", initial)
        {
            Owner = this,
            Validate = ValidateRemotePath,
        };

        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    private string? ValidateRemotePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "路径不能为空";
        }

        if (!value.StartsWith('/'))
        {
            return "远端路径必须以 / 开头";
        }

        if (value.IndexOfAny(['\r', '\n']) >= 0)
        {
            return "路径不能包含换行";
        }

        return Paths.Contains(value, StringComparer.Ordinal) ? "该路径已在列表中" : null;
    }

    private void Persist()
    {
        if (_persist is null)
        {
            return;
        }

        try
        {
            _persist(_quickAccess);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存快速路径失败：{ex.Message}", "PetrelPush",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

/// <summary>浮层里的一行：Title 为空时只显示 Detail（路径没有名称）。</summary>
internal sealed class PickerItem(string title, string detail, string value, bool removable)
{
    public string Title { get; } = title;

    public string Detail { get; } = detail;

    /// <summary>选中后填进终端的文本。</summary>
    public string Value { get; } = value;

    public Visibility TitleVisibility =>
        string.IsNullOrWhiteSpace(Title) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility DetailVisibility =>
        string.IsNullOrWhiteSpace(Detail) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility RemoveVisibility => removable ? Visibility.Visible : Visibility.Collapsed;
}
