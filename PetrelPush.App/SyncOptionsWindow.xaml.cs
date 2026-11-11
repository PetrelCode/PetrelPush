using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PetrelPush.Core.Model;

namespace PetrelPush.App;

/// <summary>
/// 同步参数弹窗：只覆盖当前连接的 sync 段（去抖、轮询、并发、限速、空目录等），
/// 其它字段沿用已保存的配置，保存后立即重建该连接的引擎。
/// </summary>
public partial class SyncOptionsWindow : Window
{
    private readonly SyncHost _host;
    private readonly SyncProfile _profile;

    public SyncOptionsWindow(SyncHost host)
    {
        InitializeComponent();

        _host = host;
        _profile = host.Profile.Clone();
        Title = $"同步参数 — {host.DisplayName}";
        LoadToUi();
    }

    /// <summary>本次是否已保存（用于调用方刷新界面）。</summary>
    public bool Saved { get; private set; }

    private void LoadToUi()
    {
        DebounceBox.Text = _profile.Sync.DebounceMs.ToString();
        MaxForceBox.Text = _profile.Sync.MaxForceTriggerMs.ToString();
        PollBox.Text = _profile.Sync.PollIntervalSec.ToString();
        DeepVerifyDaysBox.Text = _profile.Sync.DeepVerifyIntervalDays.ToString();
        ParallelBox.Text = _profile.Sync.MaxParallelTransfers.ToString();
        RateLimitBox.Text = _profile.Sync.RateLimitKbps.ToString();
        LargeFileBox.Text = _profile.Sync.LargeFileThresholdMB.ToString();
        ExecutableSuffixesBox.Text = string.Join(", ", _profile.Sync.ExecutableSuffixes ?? Array.Empty<string>());

        SyncEmptyDirsCheck.IsChecked = _profile.Sync.SyncEmptyDirs;
        DeleteEmptyDirsCheck.IsChecked = _profile.Sync.DeleteEmptyDirs;
        FollowSymlinksCheck.IsChecked = _profile.Sync.FollowSymlinks;
        DryRunCheck.IsChecked = _profile.Sync.DryRun;
    }

    private void CollectFromUi()
    {
        _profile.Sync.DebounceMs = Math.Max(0, ReadInt(DebounceBox, 500));
        _profile.Sync.MaxForceTriggerMs = Math.Max(_profile.Sync.DebounceMs, ReadInt(MaxForceBox, 5000));
        _profile.Sync.PollIntervalSec = Math.Max(1, ReadInt(PollBox, 30));
        _profile.Sync.DeepVerifyIntervalDays = Math.Max(1, ReadInt(DeepVerifyDaysBox, 7));
        _profile.Sync.MaxParallelTransfers = Math.Max(1, ReadInt(ParallelBox, 4));
        _profile.Sync.RateLimitKbps = Math.Max(0, ReadInt(RateLimitBox, 0));
        _profile.Sync.LargeFileThresholdMB = Math.Max(0, ReadInt(LargeFileBox, 100));

        var suffixes = (ExecutableSuffixesBox.Text ?? string.Empty)
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (suffixes.Length > 0)
        {
            _profile.Sync.ExecutableSuffixes = suffixes;
        }

        _profile.Sync.SyncEmptyDirs = SyncEmptyDirsCheck.IsChecked == true;
        _profile.Sync.DeleteEmptyDirs = DeleteEmptyDirsCheck.IsChecked == true;
        _profile.Sync.FollowSymlinks = FollowSymlinksCheck.IsChecked == true;
        _profile.Sync.DryRun = DryRunCheck.IsChecked == true;
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        CollectFromUi();

        try
        {
            await _host.SaveAndRestartAsync(_profile, null);
            Saved = true;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ShowResult(ex.Message, Brushes.Firebrick);
        }
    }

    private static int ReadInt(TextBox box, int fallback) =>
        int.TryParse((box.Text ?? string.Empty).Trim(), out var value) ? value : fallback;

    private void ShowResult(string message, Brush brush)
    {
        ResultText.Foreground = brush;
        ResultText.Text = message;
    }
}
