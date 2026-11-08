using System;
using Microsoft.Win32;

namespace PetrelPush.App;

/// <summary>开机自启（HKCU Run，无需管理员权限）。</summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PetrelPush";

    /// <summary>写入或删除登录项；返回是否真的写成功（组策略或权限限制时为 false）。</summary>
    public static bool Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null)
            {
                return false;
            }

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return true;
            }

            var executable = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executable))
            {
                return false;
            }

            key.SetValue(ValueName, $"\"{executable}\"");
            return true;
        }
        catch (Exception)
        {
            // 组策略或权限限制时忽略，不影响同步功能
            return false;
        }
    }
}
