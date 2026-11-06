using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;

namespace PetrelPush.App;

public partial class SplashWindow : Window
{
    private readonly DateTime _shownAt = DateTime.UtcNow;

    public SplashWindow()
    {
        InitializeComponent();

        var version = typeof(SplashWindow).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        VersionBadge.Text = $"v{version}";

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 系统开启「减少动态效果」时只保留静态形态（对应 prefers-reduced-motion）
        if (!SystemParameters.ClientAreaAnimation)
        {
            BrandPanel.Opacity = 1;
            NameText.Opacity = 1;
            NameTransform.Y = 0;
            EditionBorder.Opacity = 1;
            EditionTransform.Y = 0;
            TrackBorder.Opacity = 1;
            HintText.Opacity = 1;
            FillTransform.X = 49.4;
            return;
        }

        ((Storyboard)FindResource("SplashStoryboard")).Begin(this);
    }

    /// <summary>保证启动画面至少展示一段时间，避免一闪而过。</summary>
    public async Task WaitMinimumAsync(TimeSpan minimum)
    {
        var remaining = minimum - (DateTime.UtcNow - _shownAt);
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining);
        }
    }
}
