using System;
using Serilog.Core;
using Serilog.Events;

namespace PetrelPush.App;

/// <summary>把 Serilog 事件转发到界面（实时日志流）。</summary>
public sealed class UiLogSink : ILogEventSink
{
    public event Action<LogEvent>? LogEmitted;

    public void Emit(LogEvent logEvent)
    {
        try
        {
            LogEmitted?.Invoke(logEvent);
        }
        catch
        {
            // 界面已释放时忽略
        }
    }
}
