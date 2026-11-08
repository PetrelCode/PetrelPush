using System.Reflection;
using Renci.SshNet;

namespace PetrelPush.Transport;

/// <summary>
/// SSH.NET 的 <see cref="ShellStream"/> 没有公开窗口尺寸变更 API：
/// pty-req 在创建时发送，但后续的 window-change 只能由内部 <c>IChannelSession</c> 发。
/// 这里通过反射取出 channel 并发送 window-change 请求，让远端 TUI 程序（vim / htop）在窗口缩放后重排。
///
/// 反射失败时静默降级：会话仍然可用，只是远端不会随窗口重排。这样 SSH.NET 升级改名也不至于崩溃。
/// </summary>
internal static class ShellWindowResizer
{
    private static readonly FieldInfo? ChannelField =
        typeof(ShellStream).GetField("_channel", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly MethodInfo? WindowChangeMethod = ChannelField?.FieldType.GetMethod(
        "SendWindowChangeRequest",
        BindingFlags.Public | BindingFlags.Instance,
        binder: null,
        types: [typeof(uint), typeof(uint), typeof(uint), typeof(uint)],
        modifiers: null);

    public static bool IsSupported => ChannelField is not null && WindowChangeMethod is not null;

    public static void Resize(ShellStream shell, int columns, int rows)
    {
        if (!IsSupported)
        {
            return;
        }

        try
        {
            var channel = ChannelField!.GetValue(shell);
            if (channel is null)
            {
                return;
            }

            WindowChangeMethod!.Invoke(channel, [(uint)columns, (uint)rows, 0u, 0u]);
        }
        catch (Exception ex) when (ex is TargetInvocationException
                                       or ArgumentException
                                       or MemberAccessException
                                       or InvalidOperationException)
        {
        }
    }
}
