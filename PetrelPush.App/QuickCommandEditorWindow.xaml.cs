using System;
using System.Windows;

namespace PetrelPush.App;

/// <summary>
/// 设置页「快速命令」的添加 / 编辑对话框：名称与命令两个字段，校验失败时留在窗口内提示。
/// </summary>
public partial class QuickCommandEditorWindow : Window
{
    public QuickCommandEditorWindow(string title, string name, string command)
    {
        InitializeComponent();

        Title = title;
        NameBox.Text = name;
        CommandBox.Text = command;

        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    /// <summary>校验：返回 null 表示通过，否则返回提示文案。参数依次为名称与命令。</summary>
    public Func<string, string, string?>? Validate { get; init; }

    public string CommandName => NameBox.Text.Trim();

    public string CommandText => CommandBox.Text.Trim();

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        var error = Validate?.Invoke(CommandName, CommandText);
        if (error is not null)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;

            if (string.IsNullOrWhiteSpace(CommandName))
            {
                NameBox.Focus();
            }
            else
            {
                CommandBox.Focus();
                CommandBox.SelectAll();
            }

            return;
        }

        DialogResult = true;
    }
}
