using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using PetrelPush.Core.Model;
using PetrelPush.State;
using Xunit;

namespace PetrelPush.Tests;

public sealed class StateStoreTests
{
    private static readonly byte[] HashA = [1, 2, 3];

    private static ILogger Logger => new LoggerConfiguration().CreateLogger();

    private static async Task<SqliteStateStore> OpenAsync(TempDirectory temp)
    {
        var store = new SqliteStateStore(Path.Combine(temp.Path, "state.db"), Logger);
        await store.InitializeAsync(CancellationToken.None);
        return store;
    }

    private static ScannedEntry Scan(string path, byte[]? hash = null, long size = 3) =>
        new(path, EntryKind.File, size, 100, hash ?? HashA);

    private static SyncTask UploadTask(string path, byte[]? hash = null) => new()
    {
        Action = SyncAction.Upload,
        DstPath = path,
        WantKind = EntryKind.File,
        WantHash = hash ?? HashA,
        Size = 3,
    };

    [Fact]
    public async Task CommitScanPersistsEntries()
    {
        using var temp = new TempDirectory();
        await using var store = await OpenAsync(temp);

        await store.CommitScanAsync([Scan("a.txt")], [], CancellationToken.None);

        var entries = await store.LoadEntriesAsync(CancellationToken.None);
        var entry = Assert.Single(entries).Value;
        Assert.Equal("a.txt", entry.Path);
        Assert.Equal(EntryKind.File, entry.Kind);
        Assert.Equal(3, entry.Size);
        Assert.Null(entry.SyncedKind);
        Assert.Equal(HashA, entry.Hash);
    }

    [Fact]
    public async Task ResetClearsEntriesAndTasks()
    {
        using var temp = new TempDirectory();
        await using var store = await OpenAsync(temp);

        await store.CommitScanAsync([Scan("a.txt")], [UploadTask("a.txt")], CancellationToken.None);
        Assert.Single(await store.LoadEntriesAsync(CancellationToken.None));
        Assert.Equal(1, await store.CountTasksAsync("pending", CancellationToken.None));

        await store.ResetAsync(CancellationToken.None);

        Assert.Empty(await store.LoadEntriesAsync(CancellationToken.None));
        Assert.Equal(0, await store.CountTasksAsync("pending", CancellationToken.None));
    }

    [Fact]
    public async Task LocalDeletionWithoutRemoteStateDropsRow()
    {
        using var temp = new TempDirectory();
        await using var store = await OpenAsync(temp);

        await store.CommitScanAsync([Scan("a.txt")], [], CancellationToken.None);
        await store.CommitScanAsync([], [], CancellationToken.None);

        Assert.Empty(await store.LoadEntriesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task UploadDoneWritesSyncedHash()
    {
        using var temp = new TempDirectory();
        await using var store = await OpenAsync(temp);
        var scan = new[] { Scan("a.txt") };

        await store.CommitScanAsync(scan, [], CancellationToken.None);
        await store.CommitScanAsync(scan, [UploadTask("a.txt")], CancellationToken.None);

        var task = Assert.Single(await store.LoadRunnableTasksAsync(DateTimeOffset.UtcNow, 8, CancellationToken.None));
        await store.MarkTaskDoneAsync(task, HashA, CancellationToken.None);

        var entries = await store.LoadEntriesAsync(CancellationToken.None);
        Assert.Equal(EntryKind.File, entries["a.txt"].SyncedKind);
        Assert.Equal(HashA, entries["a.txt"].SyncedHash);
        Assert.Empty(await store.LoadRunnableTasksAsync(DateTimeOffset.UtcNow, 8, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteDoneRemovesRow()
    {
        using var temp = new TempDirectory();
        await using var store = await OpenAsync(temp);

        await store.CommitScanAsync([Scan("a.txt")], [], CancellationToken.None);
        await store.CommitScanAsync([Scan("a.txt")], [UploadTask("a.txt")], CancellationToken.None);
        var upload = Assert.Single(await store.LoadRunnableTasksAsync(DateTimeOffset.UtcNow, 8, CancellationToken.None));
        await store.MarkTaskDoneAsync(upload, HashA, CancellationToken.None);

        var deleteTask = new SyncTask
        {
            Action = SyncAction.Delete,
            DstPath = "a.txt",
            WantKind = EntryKind.File,
            WantHash = HashA,
        };
        await store.CommitScanAsync([], [deleteTask], CancellationToken.None);

        var delete = Assert.Single(await store.LoadRunnableTasksAsync(DateTimeOffset.UtcNow, 8, CancellationToken.None));
        Assert.Equal(SyncAction.Delete, delete.Action);
        await store.MarkTaskDoneAsync(delete, null, CancellationToken.None);

        Assert.Empty(await store.LoadEntriesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RenameDoneMovesRowAndKeepsSyncedState()
    {
        using var temp = new TempDirectory();
        await using var store = await OpenAsync(temp);

        await store.CommitScanAsync([Scan("old.txt")], [], CancellationToken.None);
        await store.CommitScanAsync([Scan("old.txt")], [UploadTask("old.txt")], CancellationToken.None);
        var upload = Assert.Single(await store.LoadRunnableTasksAsync(DateTimeOffset.UtcNow, 8, CancellationToken.None));
        await store.MarkTaskDoneAsync(upload, HashA, CancellationToken.None);

        var rename = new SyncTask
        {
            Action = SyncAction.Rename,
            SrcPath = "old.txt",
            DstPath = "new.txt",
            WantKind = EntryKind.File,
            WantHash = HashA,
        };
        await store.CommitScanAsync([Scan("new.txt")], [rename], CancellationToken.None);

        var loaded = Assert.Single(await store.LoadRunnableTasksAsync(DateTimeOffset.UtcNow, 8, CancellationToken.None));
        await store.MarkTaskDoneAsync(loaded, HashA, CancellationToken.None);

        var entries = await store.LoadEntriesAsync(CancellationToken.None);
        Assert.True(entries.ContainsKey("new.txt"));
        Assert.False(entries.ContainsKey("old.txt"));
        Assert.Equal(HashA, entries["new.txt"].SyncedHash);
        Assert.Equal(EntryKind.File, entries["new.txt"].SyncedKind);
    }

    [Fact]
    public async Task FailedTaskHonoursRetrySchedule()
    {
        using var temp = new TempDirectory();
        await using var store = await OpenAsync(temp);
        var scan = new[] { Scan("a.txt") };

        await store.CommitScanAsync(scan, [], CancellationToken.None);
        await store.CommitScanAsync(scan, [UploadTask("a.txt")], CancellationToken.None);
        var task = Assert.Single(await store.LoadRunnableTasksAsync(DateTimeOffset.UtcNow, 8, CancellationToken.None));

        var retryAt = DateTimeOffset.UtcNow.AddMinutes(1);
        await store.MarkTaskFailedAsync(task.Id, 1, retryAt, "boom", CancellationToken.None);

        Assert.Empty(await store.LoadRunnableTasksAsync(DateTimeOffset.UtcNow, 8, CancellationToken.None));
        var later = Assert.Single(await store.LoadRunnableTasksAsync(DateTimeOffset.UtcNow.AddMinutes(2), 8, CancellationToken.None));
        Assert.Equal(1, later.Attempts);
        Assert.Equal("boom", later.LastError);
        Assert.Equal(1, await store.CountTasksAsync("failed", CancellationToken.None));

        await store.ResetFailedTasksAsync(CancellationToken.None);
        Assert.Equal(1, await store.CountTasksAsync("pending", CancellationToken.None));
    }

    [Fact]
    public async Task NewIntentReplacesStalePendingTask()
    {
        using var temp = new TempDirectory();
        await using var store = await OpenAsync(temp);
        var scan = new[] { Scan("a.txt") };

        await store.CommitScanAsync(scan, [], CancellationToken.None);
        await store.CommitScanAsync(scan, [UploadTask("a.txt", HashA)], CancellationToken.None);
        await store.CommitScanAsync(scan, [UploadTask("a.txt", [7, 8, 9])], CancellationToken.None);

        var task = Assert.Single(await store.LoadRunnableTasksAsync(DateTimeOffset.UtcNow, 8, CancellationToken.None));
        Assert.Equal([7, 8, 9], task.WantHash);
    }

    [Fact]
    public async Task DirectoryRenameDoneMigratesChildRows()
    {
        using var temp = new TempDirectory();
        await using var store = await OpenAsync(temp);
        var now = DateTimeOffset.UtcNow;

        var initialScan = new[]
        {
            new ScannedEntry("old", EntryKind.Dir, 0, 100),
            Scan("old/a.txt"),
        };
        await store.CommitScanAsync(initialScan, [], CancellationToken.None);

        var mkdir = new SyncTask { Action = SyncAction.Mkdir, DstPath = "old", WantKind = EntryKind.Dir };
        await store.CommitScanAsync(initialScan, [mkdir, UploadTask("old/a.txt")], CancellationToken.None);
        var initialTasks = await store.LoadRunnableTasksAsync(now, 8, CancellationToken.None);
        await store.MarkTaskDoneAsync(initialTasks.Single(t => t.Action == SyncAction.Mkdir), null, CancellationToken.None);
        await store.MarkTaskDoneAsync(initialTasks.Single(t => t.Action == SyncAction.Upload), HashA, CancellationToken.None);

        var renamedScan = new[]
        {
            new ScannedEntry("new", EntryKind.Dir, 0, 100),
            Scan("new/a.txt"),
        };
        var rename = new SyncTask
        {
            Action = SyncAction.Rename,
            SrcPath = "old",
            DstPath = "new",
            WantKind = EntryKind.Dir,
        };
        await store.CommitScanAsync(renamedScan, [rename], CancellationToken.None);

        var loaded = Assert.Single(await store.LoadRunnableTasksAsync(now, 8, CancellationToken.None));
        await store.MarkTaskDoneAsync(loaded, null, CancellationToken.None);

        var entries = await store.LoadEntriesAsync(CancellationToken.None);
        Assert.True(entries.ContainsKey("new"));
        Assert.Equal(EntryKind.Dir, entries["new"].SyncedKind);
        Assert.True(entries.ContainsKey("new/a.txt"));
        Assert.Equal(HashA, entries["new/a.txt"].SyncedHash);
        Assert.False(entries.ContainsKey("old"));
        Assert.False(entries.ContainsKey("old/a.txt"));
    }

    [Fact]
    public async Task RetryCountSurvivesUnchangedIntent()
    {
        using var temp = new TempDirectory();
        await using var store = await OpenAsync(temp);
        var scan = new[] { Scan("a.txt") };
        var now = DateTimeOffset.UtcNow;

        await store.CommitScanAsync(scan, [], CancellationToken.None);
        await store.CommitScanAsync(scan, [UploadTask("a.txt")], CancellationToken.None);
        var task = Assert.Single(await store.LoadRunnableTasksAsync(now, 8, CancellationToken.None));
        await store.MarkTaskFailedAsync(task.Id, 3, now.AddMinutes(1), "boom", CancellationToken.None);

        // 下一轮扫描得到同样的意图：不应重建任务、不应丢重试次数
        await store.CommitScanAsync(scan, [UploadTask("a.txt")], CancellationToken.None);

        var reloaded = Assert.Single(await store.LoadRunnableTasksAsync(now.AddMinutes(2), 8, CancellationToken.None));
        Assert.Equal(task.Id, reloaded.Id);
        Assert.Equal(3, reloaded.Attempts);
    }

    [Fact]
    public async Task StaleTaskIsDroppedWhenIntentDisappears()
    {
        using var temp = new TempDirectory();
        await using var store = await OpenAsync(temp);
        var scan = new[] { Scan("a.txt") };

        await store.CommitScanAsync(scan, [], CancellationToken.None);
        await store.CommitScanAsync(scan, [UploadTask("a.txt")], CancellationToken.None);
        Assert.Single(await store.LoadRunnableTasksAsync(DateTimeOffset.UtcNow, 8, CancellationToken.None));

        // 本地恢复原状：本轮没有动作 → 旧任务作废（避免“删除后又复活”或相反）
        await store.CommitScanAsync(scan, [], CancellationToken.None);

        Assert.Empty(await store.LoadRunnableTasksAsync(DateTimeOffset.UtcNow, 8, CancellationToken.None));
    }

    [Fact]
    public async Task ExhaustedTaskIsNotRunnable()
    {
        using var temp = new TempDirectory();
        await using var store = await OpenAsync(temp);
        var scan = new[] { Scan("a.txt") };

        await store.CommitScanAsync(scan, [], CancellationToken.None);
        await store.CommitScanAsync(scan, [UploadTask("a.txt")], CancellationToken.None);
        var task = Assert.Single(await store.LoadRunnableTasksAsync(DateTimeOffset.UtcNow, 8, CancellationToken.None));
        await store.MarkTaskFailedAsync(task.Id, 8, DateTimeOffset.UtcNow.AddSeconds(-1), "boom", CancellationToken.None);

        Assert.Empty(await store.LoadRunnableTasksAsync(DateTimeOffset.UtcNow, 8, CancellationToken.None));
        Assert.False(await store.HasRunnableTasksAsync(DateTimeOffset.UtcNow, 8, CancellationToken.None));
        Assert.Equal(1, await store.CountTasksAsync("failed", CancellationToken.None));
    }

    [Fact]
    public async Task MetaValuesRoundTrip()
    {
        using var temp = new TempDirectory();
        await using var store = await OpenAsync(temp);

        Assert.Null(await store.GetMetaAsync("last_cycle_at", CancellationToken.None));
        await store.SetMetaAsync("last_cycle_at", "2026-01-01T00:00:00.0000000+00:00", CancellationToken.None);
        Assert.Equal("2026-01-01T00:00:00.0000000+00:00", await store.GetMetaAsync("last_cycle_at", CancellationToken.None));
    }
}
