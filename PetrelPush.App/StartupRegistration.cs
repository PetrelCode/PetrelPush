using System;
using Microsoft.Win32;

namespace PetrelPush.App;

/// <summary>开机自启（HKCU Run，无需管理员权限）。</summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PetrelPush";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null)
            {
                return;
            }

            if (enabled)
            {
                var executable = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(executable))
                {
                    key.SetValue(ValueName, $"\"{executable}\"");
                }
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception)
        {
            // 组策略或权限限制时忽略，不影响同步功能
        }
    }
}
