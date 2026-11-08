using System;
using System.Windows;

namespace PetrelPush.App;

/// <summary>文件页「新建文件夹」的名称输入对话框：校验失败时留在窗口内提示，不关闭。</summary>
public partial class TextInputWindow : Window
{
    public TextInputWindow(string title, string hint, string initialValue)
    {
        InitializeComponent();

        Title = title;
        TitleText.Text = title;
        HintText.Text = hint;
        InputBox.Text = initialValue;

        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    /// <summary>名称校验：返回 null 表示通过，否则返回提示文案。</summary>
    public Func<string, string?>? Validate { get; init; }

    public string Value => InputBox.Text.Trim();

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        var error = Validate?.Invoke(Value);
        if (error is not null)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            InputBox.Focus();
            InputBox.SelectAll();
            return;
        }

        DialogResult = true;
    }
}
