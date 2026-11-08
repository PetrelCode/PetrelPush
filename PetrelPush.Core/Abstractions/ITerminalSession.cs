using System.Threading.Channels;

namespace PetrelPush.Core.Abstractions;

/// <summary>
/// 终端会话：本地 ConPTY 与远端 SSH ShellStream 的统一抽象。
///
/// 只搬运原始字节（UTF-8 未解码），终端仿真（转义序列、光标、备用屏、宽字符）
/// 全部由前端 xterm.js 负责，本层不做任何解析。
/// </summary>
public interface ITerminalSession : IDisposable
{
    /// <summary>会话输出的原始字节块；生产者结束后通道完成。</summary>
    ChannelReader<byte[]> Output { get; }

    /// <summary>会话结束（本地进程退出 / 远端断开 / 被释放）。参数为可展示的原因。</summary>
    event Action<string>? Closed;

    bool IsConnected { get; }

    /// <summary>写入原始字节（键盘输入，已由前端编码为 UTF-8）。</summary>
    void Send(byte[] data);

    /// <summary>写入文本（按 UTF-8 编码）。</summary>
    void SendText(string text);

    /// <summary>
    /// 执行一条命令。回车符由实现按平台约定补齐：SSH 用 LF，Windows ConPTY 用单个 CR
    /// （用 CRLF 会让 PowerShell 多解析一个换行而进入续行模式）。
    /// </summary>
    void SendCommand(string command);

    /// <summary>窗口尺寸变更：本地为 ResizePseudoConsole，远端为 SSH window-change。</summary>
    void Resize(int columns, int rows);
}
