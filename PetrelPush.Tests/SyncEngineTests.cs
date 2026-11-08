using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using PetrelPush.Core.Abstractions;
using PetrelPush.Core.Model;
using PetrelPush.Core.Pipeline;
using PetrelPush.State;
using Xunit;

namespace PetrelPush.Tests;

public sealed class SyncEngineTests
{
    private static ILogger Logger => new LoggerConfiguration().CreateLogger();

    [Fact]
    public async Task ChangingRemoteRootDropsSnapshotAndUploadsAgain()
    {
        using var local = new TempDirectory();
        using var stateDirectory = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(local.Path, "a.txt"), "hello");

        await using var store = new SqliteStateStore(Path.Combine(stateDirectory.Path, "state.db"), Logger);
        await store.InitializeAsync(CancellationToken.None);

        var profile = new SyncProfile
        {
            LocalRoot = local.Path,
            RemoteRoot = "/srv/first",
        };

        var transport = new CountingTransport();
        await using var engine = new SyncEngine(() => profile, store, transport, Logger);

        await engine.RunOnceAsync(deepVerify: false);
        Assert.Equal(["a.txt"], transport.UploadedTargets);
        var firstSignature = await store.GetMetaAsync("roots_signature", CancellationToken.None);
        Assert.NotNull(firstSignature);
        Assert.Contains("/srv/first", firstSignature);

        // 远端根目录换到别处：旧快照记录的是另一个目录的已知态，必须清空后全量重传
        profile.RemoteRoot = "/srv/second";
        await engine.RunOnceAsync(deepVerify: false);
        Assert.Equal(["a.txt", "a.txt"], transport.UploadedTargets);
        var secondSignature = await store.GetMetaAsync("roots_signature", CancellationToken.None);
        Assert.NotNull(secondSignature);
        Assert.Contains("/srv/second", secondSignature);
    }

    private sealed class CountingTransport : ITransferExecutor
    {
        public List<string> UploadedTargets { get; } = [];

        public event Action<string, long, long>? UploadProgress
        {
            add { }
            remove { }
        }

        public Task<TransferOutcome> ExecuteAsync(
            SyncTask task,
            SyncProfile profile,
            CancellationToken cancellationToken)
        {
            UploadedTargets.Add(task.DstPath);
            return Task.FromResult(new TransferOutcome(task.WantHash, task.WantKind));
        }

        public Task TestConnectionAsync(SyncProfile profile, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<int> CleanupEmptyDirectoriesAsync(
            SyncProfile profile,
            IReadOnlySet<string> protectedDirectories,
            CancellationToken cancellationToken) => Task.FromResult(0);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
