using System.Diagnostics;

namespace PetrelPush.Transport;

/// <summary>应用层令牌桶限速（rateLimitKbps，0 表示不限速）。</summary>
public sealed class TokenBucketRateLimiter
{
    private readonly double _bytesPerSecond;
    private readonly double _capacity;
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _clock;

    private double _tokens;
    private DateTimeOffset _lastRefill;

    public TokenBucketRateLimiter(int kiloBytesPerSecond, Func<DateTimeOffset>? clock = null)
    {
        _bytesPerSecond = Math.Max(0, kiloBytesPerSecond) * 1024.0;
        _capacity = Math.Max(_bytesPerSecond, 64 * 1024);
        _tokens = _capacity;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _lastRefill = _clock();
    }

    public bool Enabled => _bytesPerSecond > 0;

    /// <summary>消费（或欠下）指定字节数；不足时阻塞等待。</summary>
    public void Consume(int bytes)
    {
        if (!Enabled || bytes <= 0)
        {
            return;
        }

        var sw = Stopwatch.StartNew();
        while (true)
        {
            TimeSpan wait;
            lock (_gate)
            {
                var now = _clock();
                var elapsed = now - _lastRefill;
                _lastRefill = now;
                _tokens = Math.Min(_capacity, _tokens + (elapsed.TotalSeconds * _bytesPerSecond));
                _tokens -= bytes;
                if (_tokens >= 0)
                {
                    return;
                }

                wait = TimeSpan.FromSeconds(-_tokens / _bytesPerSecond);
            }

            if (wait > TimeSpan.FromSeconds(30))
            {
                wait = TimeSpan.FromSeconds(30);
            }

            Thread.Sleep(wait);
        }
    }
}
