using System;
using System.Threading.Tasks;
using PetrelPush.Core.Pipeline;
using Xunit;

namespace PetrelPush.Tests;

public sealed class DebouncerTests
{
    [Fact]
    public void PushBeforeMaxDelayDoesNotFire()
    {
        var now = DateTimeOffset.UtcNow;
        var fired = 0;
        using var debouncer = new Debouncer(
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(5),
            () => fired++,
            () => now);

        debouncer.Push();

        Assert.Equal(0, fired);
    }

    [Fact]
    public void PushAfterMaxDelayFiresImmediately()
    {
        var now = DateTimeOffset.UtcNow;
        var fired = 0;
        using var debouncer = new Debouncer(
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(5),
            () => fired++,
            () => now);

        debouncer.Push();
        now = now.AddSeconds(5);
        debouncer.Push();

        Assert.Equal(1, fired);
    }

    [Fact]
    public async Task QuietWindowElapsedFiresCallback()
    {
        var fired = new TaskCompletionSource();
        using var debouncer = new Debouncer(
            TimeSpan.FromMilliseconds(80),
            TimeSpan.FromSeconds(5),
            () => fired.TrySetResult());

        debouncer.Push();

        var completed = await Task.WhenAny(fired.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(fired.Task, completed);
    }

    [Fact]
    public void InvalidArgumentsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new Debouncer(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), () => { }));
    }
}
