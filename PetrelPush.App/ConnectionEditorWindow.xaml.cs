using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using PetrelPush.Core.Config;
using PetrelPush.Core.Model;
using PetrelPush.Transport;
using Serilog;

namespace PetrelPush.App;

/// <summary>
/// 连接配置弹窗：新建与编辑共用同一套表单，保存后立即重建该连接的引擎。
/// 表单以「已保存的配置」为底稿，通过 <see cref="SyncProfile.Clone"/> 做局部覆盖。
/// </summary>
public partial class ConnectionEditorWindow : Window
{
    private readonly SyncHost _host;
    private readonly ProfileStore _store;
    private readonly SyncProfile _profile;
    private readonly ILogger _logger;

    public ConnectionEditorWindow(SyncHost host, ProfileStore store, ILogger logger)
    {
        InitializeComponent();

        _host = host;
        _store = store;
        _logger = logger;
        _profile = host.Profile.Clone();

        Title = $"连接配置 — {host.DisplayName}";
        LoadToUi();
    }

    /// <summary>本次弹窗是否已把配置写入磁盘（保存成功或测试连接成功）。</summary>
    public bool Saved { get; private set; }

    /// <summary>该连接是否已有配置文件（用于取消新建时清理空白连接）。</summary>
    public bool Persisted => _store.Exists(_profile.ProfileId);

    private void LoadToUi()
    {
        ProfileNameBox.Text = _profile.ProfileName;
        LocalRootBox.Text = _profile.LocalRoot;
        RemoteRootBox.Text = _profile.RemoteRoot;

        HostBox.Text = _profile.Remote.Host;
        PortBox.Text = _profile.Remote.Port.ToString();
        UserBox.Text = _profile.Remote.User;
        AuthTypeCombo.SelectedIndex = string.Equals(
            _profile.Remote.Auth.Type,
            "key",
            StringComparison.OrdinalIgnoreCase)
            ? 1
            : 0;
        PrivateKeyBox.Text = _profile.Remote.Auth.PrivateKeyPath ?? string.Empty;
        PasswordInput.Password = _profile.Remote.Password ?? string.Empty;
        UpdateAuthEnabled();
        FingerprintText.Text = FormatFingerprint(_profile.Remote);

        // 同步参数不在这里改：沿用已有配置（新建连接用默认值），到「配置」页的「同步参数」卡片调整
        EnabledCheck.IsChecked = _profile.Enabled;
    }

    /// <summary>把表单写回工作副本（测试连接与保存共用）。</summary>
    private void CollectFromUi()
    {
        _profile.ProfileName = ProfileNameBox.Text.Trim();
        _profile.LocalRoot = LocalRootBox.Text.Trim();
        _profile.RemoteRoot = RemoteRootBox.Text.Trim();

        _profile.Remote.Host = HostBox.Text.Trim();
        _profile.Remote.Port = ReadInt(PortBox, 22);
        _profile.Remote.User = UserBox.Text.Trim();
        _profile.Remote.Auth.Type = AuthTypeCombo.SelectedIndex == 1 ? "key" : "password";
        _profile.Remote.Auth.PrivateKeyPath = string.IsNullOrWhiteSpace(PrivateKeyBox.Text)
            ? null
            : PrivateKeyBox.Text.Trim();

        // 口令框留空表示沿用已保存的凭据，避免把 secrets.bin 清空
        var typedPassword = PasswordInput.Password;
        if (!string.IsNullOrEmpty(typedPassword))
        {
            _profile.Remote.Password = typedPassword;
        }

        _profile.Enabled = EnabledCheck.IsChecked == true;
    }

    private void OnBrowseLocal(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择要同步的本地目录" };
        var current = LocalRootBox.Text.Trim();
        if (Directory.Exists(current))
        {
            dialog.InitialDirectory = current;
        }

        if (dialog.ShowDialog(this) == true)
        {
            LocalRootBox.Text = dialog.FolderName;
        }
    }

    private void OnBrowseKey(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 SSH 私钥",
            Filter = "所有文件|*.*",
        };

        if (dialog.ShowDialog(this) == true)
        {
            PrivateKeyBox.Text = dialog.FileName;
        }
    }

    private void OnAuthTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PasswordInput is null || PrivateKeyBox is null)
        {
            return;
        }

        UpdateAuthEnabled();
    }

    private void UpdateAuthEnabled()
    {
        var useKey = AuthTypeCombo.SelectedIndex == 1;
        PasswordInput.IsEnabled = !useKey;
        PrivateKeyBox.IsEnabled = useKey;
    }

    /// <summary>测试连接：成功后按配置完整度决定是否自动启用，并写回配置。</summary>
    private async void OnTestConnection(object sender, RoutedEventArgs e)
    {
        CollectFromUi();

        var errors = ValidateRemote(_profile);
        if (errors.Count > 0)
        {
            ShowResult(string.Join("；", errors), Brushes.Firebrick);
            return;
        }

        if (!ConfirmProtectedRemoteRoot())
        {
            ShowResult("已取消（远端根目录位于系统目录）", Brushes.DimGray);
            return;
        }

        ShowResult("连接中…", Brushes.DimGray);
        try
        {
            await _host.TestConnectionAsync(_profile, null);
        }
        catch (HostKeyMismatchException ex)
        {
            // 同一地址上指纹变了（远端重装 / 换机）：必须由用户显式确认后才能覆盖
            if (!ConfirmHostKeyRotation(ex.Message))
            {
                ShowResult("已取消：主机指纹保持原值，未连接远端", Brushes.DimGray);
                return;
            }

            try
            {
                await _host.TestConnectionAsync(_profile, null, trustNewHostKey: true);
            }
            catch (Exception retryEx)
            {
                ShowResult(retryEx.Message, Brushes.Firebrick);
                _host.RequestSync();
                return;
            }
        }
        catch (Exception ex)
        {
            ShowResult(ex.Message, Brushes.Firebrick);
            _host.RequestSync();
            return;
        }

        // 配置完整时，测试通过即自动启用；目录没填全就只保存连接信息，等补全后再启用
        var autoEnabled = _profile.Enabled || _profile.Validate().ToList().Count == 0;
        if (autoEnabled && !_profile.Enabled)
        {
            _profile.Enabled = true;
            EnabledCheck.IsChecked = true;
            _store.Save(_profile);
        }

        try
        {
            await _host.RestartAsync(_profile);
            Saved = true;
            FingerprintText.Text = FormatFingerprint(_profile.Remote);
            ShowResult(
                autoEnabled
                    ? $"连接成功（{DateTime.Now:HH:mm:ss}），已保存并启用"
                    : $"连接成功（{DateTime.Now:HH:mm:ss}），连接信息已保存；补全本地 / 远端目录并保存后再启用",
                autoEnabled ? Brushes.Green : Brushes.DimGray);
        }
        catch (Exception ex)
        {
            ShowResult(ex.Message, Brushes.Firebrick);
            _host.RequestSync();
        }
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        CollectFromUi();

        var errors = _profile.Validate().ToList();
        if (errors.Count > 0)
        {
            ShowResult(string.Join("；", errors), Brushes.Firebrick);
            return;
        }

        if (!ConfirmProtectedRemoteRoot())
        {
            ShowResult("已取消（远端根目录位于系统目录）", Brushes.DimGray);
            return;
        }

        try
        {
            // 凭据已随 profile 传入（口令框留空时沿用已保存的值）
            await _host.SaveAndRestartAsync(_profile, null);
            Saved = true;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "保存连接 {Profile} 失败", _profile.ProfileId);
            ShowResult(ex.Message, Brushes.Firebrick);
        }
    }

    /// <summary>远端根目录命中系统目录（或其上级）时，只做一次二次确认，不构成对用法的限制。</summary>
    private bool ConfirmProtectedRemoteRoot()
    {
        if (string.IsNullOrWhiteSpace(_profile.RemoteRoot) || _profile.ProtectedRootAcknowledged)
        {
            return true;
        }

        bool isProtected;
        try
        {
            isProtected = PathGuard.IsProtectedRoot(_profile.RemoteRoot);
        }
        catch (ArgumentException)
        {
            return true;
        }

        if (!isProtected)
        {
            return true;
        }

        var answer = MessageBox.Show(
            this,
            $"远端根目录 \"{_profile.RemoteRoot}\" 位于系统目录或其上级。\n继续同步可能覆盖系统文件，确认使用这个目录？",
            "PetrelPush — 远端根目录确认",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            return false;
        }

        _profile.ProtectedRootAcknowledged = true;
        return true;
    }

    /// <summary>同一远端地址上指纹变化时的显式确认（默认不信任）。</summary>
    private bool ConfirmHostKeyRotation(string message)
    {
        var answer = MessageBox.Show(
            this,
            message + Environment.NewLine + Environment.NewLine
            + "如果远端主机刚刚重装系统或更换了主机密钥，点「是」信任新指纹并继续；否则点「否」中止。",
            "远端主机指纹已变化",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        return answer == MessageBoxResult.Yes;
    }

    /// <summary>「远端连接」部分的校验：只看主机、端口、用户与凭据。</summary>
    private static List<string> ValidateRemote(SyncProfile profile)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(profile.Remote.Host))
        {
            errors.Add("请填写主机");
        }

        if (profile.Remote.Port is < 1 or > 65535)
        {
            errors.Add("端口必须在 1~65535 之间");
        }

        if (string.IsNullOrWhiteSpace(profile.Remote.User))
        {
            errors.Add("请填写用户名");
        }

        if (string.Equals(profile.Remote.Auth.Type, "key", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(profile.Remote.Auth.PrivateKeyPath))
            {
                errors.Add("密钥认证需要填写私钥路径");
            }
            else if (!File.Exists(profile.Remote.Auth.PrivateKeyPath))
            {
                errors.Add($"私钥文件不存在：{profile.Remote.Auth.PrivateKeyPath}");
            }
        }
        else if (string.IsNullOrEmpty(profile.Remote.Password))
        {
            errors.Add("密码认证需要填写密码");
        }

        return errors;
    }

    /// <summary>指纹展示：已确认、未确认、以及“远端地址变更导致旧指纹作废”三种状态。</summary>
    private static string FormatFingerprint(RemoteOptions remote)
    {
        if (string.IsNullOrWhiteSpace(remote.HostKeyFingerprint))
        {
            return "主机指纹：未确认（点「测试连接」完成确认）";
        }

        return remote.HasConfirmedHostKey
            ? $"主机指纹：{remote.HostKeyFingerprint}"
            : $"主机指纹：{remote.HostKeyFingerprint}（绑定的远端地址为 {remote.HostKeyTarget}，地址已变更，请重新测试连接）";
    }

    private static int ReadInt(TextBox box, int fallback) =>
        int.TryParse((box.Text ?? string.Empty).Trim(), out var value) ? value : fallback;

    private void ShowResult(string message, Brush brush)
    {
        ResultText.Foreground = brush;
        ResultText.Text = message;
    }
}
