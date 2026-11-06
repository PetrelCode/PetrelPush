using System;
using PetrelPush.Core.Abstractions;
using PetrelPush.Core.Model;
using Xunit;

namespace PetrelPush.Tests;

public sealed class RetryBackoffTests
{
    private static RetryOptions Options => new() { BaseDelayMs = 1000, MaxDelayMs = 300000, MaxAttempts = 8 };

    [Theory]
    [InlineData(1, 1000)]
    [InlineData(2, 2000)]
    [InlineData(3, 4000)]
    [InlineData(4, 8000)]
    public void DelayDoublesPerAttempt(int attempts, int expectedMs)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), RetryBackoff.Compute(attempts, Options));
    }

    [Fact]
    public void DelayIsCappedAtMaximum()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(300000), RetryBackoff.Compute(20, Options));
    }

    [Fact]
    public void ZeroAttemptsUsesBaseDelay()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(1000), RetryBackoff.Compute(0, Options));
    }
}
