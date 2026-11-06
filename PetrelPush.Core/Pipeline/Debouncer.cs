namespace PetrelPush.Core.Pipeline;

/// <summary>
/// 去抖合并：静默窗口到期触发；若持续有事件，最多等待上限后强制触发。
/// </summary>
public sealed class Debouncer : IDisposable
{
    private static readonly TimeSpan Infinite = Timeout.InfiniteTimeSpan;

    private readonly TimeSpan _quietWindow;
    private readonly TimeSpan _maxDelay;
    private readonly Action _onElapsed;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();

    private Timer? _timer;
    private DateTimeOffset? _firstPush;
    private bool _disposed;

    public Debouncer(TimeSpan quietWindow, TimeSpan maxDelay, Action onElapsed, Func<DateTimeOffset>? clock = null)
    {
        if (quietWindow < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(quietWindow));
        }

        if (maxDelay < quietWindow)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDelay), "上限必须不小于静默窗口");
        }

        _quietWindow = quietWindow;
        _maxDelay = maxDelay;
        _onElapsed = onElapsed;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public void Push()
    {
        Action? fire = null;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var now = _clock();
            _firstPush ??= now;
            var sinceFirst = now - _firstPush.Value;

            if (sinceFirst >= _maxDelay)
            {
                _firstPush = null;
                _timer?.Change(Infinite, Infinite);
                fire = _onElapsed;
            }
            else
            {
                var delay = _quietWindow;
                var remaining = _maxDelay - sinceFirst;
                if (delay > remaining)
                {
                    delay = remaining;
                }

                if (delay < TimeSpan.FromMilliseconds(1))
                {
                    delay = TimeSpan.FromMilliseconds(1);
                }

                _timer ??= new Timer(_ => OnTimer(), null, Infinite, Infinite);
                _timer.Change(delay, Infinite);
            }
        }

        fire?.Invoke();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer?.Dispose();
            _timer = null;
            _firstPush = null;
        }
    }

    private void OnTimer()
    {
        var fire = false;
        lock (_gate)
        {
            if (_disposed || _firstPush is null)
            {
                return;
            }

            _firstPush = null;
            _timer?.Change(Infinite, Infinite);
            fire = true;
        }

        if (fire)
        {
            _onElapsed();
        }
    }
}
