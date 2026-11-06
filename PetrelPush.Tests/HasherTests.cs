using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PetrelPush.Core.Model;
using PetrelPush.Core.Scan;
using Xunit;

namespace PetrelPush.Tests;

public sealed class HasherTests
{
    private static readonly byte[] HashA = [1, 2, 3];

    [Fact]
    public void CacheIsReusableWhenSizeAndTimestampMatchAndNotRacy()
    {
        var scan = new ScannedEntry("a.txt", EntryKind.File, 10, 100);
        var previous = new FileEntry("a.txt", EntryKind.File, 10, 100, HashA, HashA, EntryKind.File);

        Assert.True(SnapshotHasher.CanReuseCache(scan, previous, cycleStartTicks: 200, deepVerify: false));
    }

    [Fact]
    public void CacheIsNotReusableWhenTimestampFallsInsideCycle()
    {
        var scan = new ScannedEntry("a.txt", EntryKind.File, 10, 150);
        var previous = new FileEntry("a.txt", EntryKind.File, 10, 150, HashA, HashA, EntryKind.File);

        Assert.False(SnapshotHasher.CanReuseCache(scan, previous, cycleStartTicks: 150, deepVerify: false));
        Assert.False(SnapshotHasher.CanReuseCache(scan, previous, cycleStartTicks: 100, deepVerify: false));
    }

    [Fact]
    public void CacheIsNotReusableWhenSizeChanged()
    {
        var scan = new ScannedEntry("a.txt", EntryKind.File, 11, 100);
        var previous = new FileEntry("a.txt", EntryKind.File, 10, 100, HashA, HashA, EntryKind.File);

        Assert.False(SnapshotHasher.CanReuseCache(scan, previous, cycleStartTicks: 200, deepVerify: false));
    }

    [Fact]
    public void CacheIsNotReusableDuringDeepVerify()
    {
        var scan = new ScannedEntry("a.txt", EntryKind.File, 10, 100);
        var previous = new FileEntry("a.txt", EntryKind.File, 10, 100, HashA, HashA, EntryKind.File);

        Assert.False(SnapshotHasher.CanReuseCache(scan, previous, cycleStartTicks: 200, deepVerify: true));
    }

    [Fact]
    public void HashFileMatchesKnownSha256()
    {
        using var temp = new TempDirectory();
        var full = temp.Write("a.txt", "abc");

        var hash = SnapshotHasher.HashFile(full);

        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            Hashes.ToHex(hash));
    }

    [Fact]
    public async Task FillAsyncReusesHashesAndComputesChangedFiles()
    {
        using var temp = new TempDirectory();
        var full = temp.Write("a.txt", "hello");
        var mtime = File.GetLastWriteTimeUtc(full).Ticks;

        var entries = new List<ScannedEntry>
        {
            new("a.txt", EntryKind.File, 5, mtime),
            new("b.txt", EntryKind.File, 1, 100),
        };
        temp.Write("b.txt", "x");

        var previous = new Dictionary<string, FileEntry>(StringComparer.Ordinal)
        {
            ["a.txt"] = new FileEntry("a.txt", EntryKind.File, 5, mtime, HashA, HashA, EntryKind.File),
        };

        var hasher = new SnapshotHasher();
        var stats = await hasher.FillAsync(
            temp.Path,
            entries,
            previous,
            cycleStartTicks: mtime + 1000,
            deepVerify: false,
            parallelism: 2,
            CancellationToken.None);

        Assert.Equal(1, stats.Reused);
        Assert.Equal(1, stats.Computed);
        Assert.Equal(HashA, entries[0].Hash);
        Assert.NotNull(entries[1].Hash);
    }

    [Fact]
    public async Task FillAsyncMarksFailedHashAsNull()
    {
        using var temp = new TempDirectory();
        var entries = new List<ScannedEntry>
        {
            new("missing.txt", EntryKind.File, 1, 100),
        };

        var hasher = new SnapshotHasher();
        var stats = await hasher.FillAsync(
            temp.Path,
            entries,
            new Dictionary<string, FileEntry>(),
            cycleStartTicks: 1000,
            deepVerify: false,
            parallelism: 1,
            CancellationToken.None);

        Assert.Equal(1, stats.Failed);
        Assert.Null(entries[0].Hash);
    }
}
