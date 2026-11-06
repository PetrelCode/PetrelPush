using PetrelPush.Core.Model;

namespace PetrelPush.Core.Abstractions;

public static class RetryBackoff
{
    /// <summary>指数退避：attempts=1 → baseDelay，逐次翻倍，封顶 maxDelay。</summary>
    public static TimeSpan Compute(int attempts, RetryOptions options)
    {
        var exponent = Math.Clamp(attempts - 1, 0, 30);
        var milliseconds = options.BaseDelayMs * Math.Pow(2, exponent);
        var capped = Math.Min(milliseconds, options.MaxDelayMs);
        return TimeSpan.FromMilliseconds(Math.Max(options.BaseDelayMs, capped));
    }
}
