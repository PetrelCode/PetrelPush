using System;
using System.Collections.Generic;
using System.Linq;
using PetrelPush.Core.Diff;
using PetrelPush.Core.Model;
using Xunit;

namespace PetrelPush.Tests;

public sealed class TaskOrdererTests
{
    private static readonly byte[] HashA = [1, 2, 3];

    private static readonly byte[] HashB = [4, 5, 6];

    private static SyncTask Upload(string path, byte[]? hash = null, long size = 5) => new()
    {
        Action = SyncAction.Upload,
        DstPath = path,
        WantKind = EntryKind.File,
        WantHash = hash,
        Size = size,
    };

    private static SyncTask Delete(string path, byte[]? hash = null, EntryKind kind = EntryKind.File) => new()
    {
        Action = SyncAction.Delete,
        DstPath = path,
        WantKind = kind,
        WantHash = hash,
    };

    private static SyncTask Rename(string source, string target) => new()
    {
        Action = SyncAction.Rename,
        SrcPath = source,
        DstPath = target,
        WantKind = EntryKind.File,
    };

    [Fact]
    public void UploadAndDeleteWithSameContentCancelEachOther()
    {
        var result = TaskOrderer.Normalize([Upload("a.txt", HashA), Delete("a.txt", HashA)]);

        Assert.Empty(result);
    }

    [Fact]
    public void DeleteWinsWhenContentDiffers()
    {
        var result = TaskOrderer.Normalize([Upload("a.txt", HashB), Delete("a.txt", HashA)]);

        var task = Assert.Single(result);
        Assert.Equal(SyncAction.Delete, task.Action);
    }

    [Fact]
    public void RenameChainsAreMerged()
    {
        var result = TaskOrderer.Normalize([Rename("a.txt", "b.txt"), Rename("b.txt", "c.txt")]);

        var task = Assert.Single(result);
        Assert.Equal(SyncAction.Rename, task.Action);
        Assert.Equal("a.txt", task.SrcPath);
        Assert.Equal("c.txt", task.DstPath);
    }

    [Fact]
    public void RenameIntoDeletedTargetBecomesDeleteOfSource()
    {
        var result = TaskOrderer.Normalize([Rename("a.txt", "b.txt"), Delete("b.txt")]);

        Assert.Contains(result, task => task.Action == SyncAction.Delete && task.DstPath == "a.txt");
        Assert.DoesNotContain(result, task => task.Action == SyncAction.Rename);
    }

    [Fact]
    public void DuplicateSameActionKeepsLastIntent()
    {
        var result = TaskOrderer.Normalize([Upload("a.txt", HashA, 5), Upload("a.txt", HashB, 6)]);

        var task = Assert.Single(result);
        Assert.Equal(HashB, task.WantHash);
    }

    [Fact]
    public void StagesFollowMkdirRenameUploadDeleteOrder()
    {
        var tasks = new List<SyncTask>
        {
            Delete("z.txt"),
            Upload("b.txt"),
            Rename("a.txt", "a2.txt"),
            new() { Action = SyncAction.Mkdir, DstPath = "dir", WantKind = EntryKind.Dir },
        };

        var stages = TaskOrderer.BuildStages(tasks, batchSize: 4, largeFileThresholdBytes: 0);
        var flattened = stages.SelectMany(batch => batch).ToList();

        Assert.Equal(
            [SyncAction.Mkdir, SyncAction.Rename, SyncAction.Upload, SyncAction.Delete],
            flattened.Select(task => task.Action).ToArray());
    }

    [Fact]
    public void DeleteStageOrdersChildrenBeforeParents()
    {
        var stages = TaskOrderer.BuildStages(
            [Delete("a", kind: EntryKind.Dir), Delete("a/b/c.txt"), Delete("a/b", kind: EntryKind.Dir)],
            batchSize: 10,
            largeFileThresholdBytes: 0);

        var flattened = stages.SelectMany(batch => batch).Select(task => task.DstPath).ToArray();

        Assert.Equal("a/b/c.txt", flattened[0]);
        Assert.Equal("a/b", flattened[1]);
        Assert.Equal("a", flattened[2]);
    }

    [Fact]
    public void LargeUploadsGoToTheirOwnBatch()
    {
        var tasks = new List<SyncTask>
        {
            Upload("small1.txt", HashA, 10),
            Upload("small2.txt", HashA, 10),
            Upload("big.bin", HashB, 10 * 1024 * 1024),
        };

        var stages = TaskOrderer.BuildStages(tasks, batchSize: 8, largeFileThresholdBytes: 1024 * 1024);

        Assert.Equal(2, stages.Count);
        Assert.Equal(2, stages[0].Count);
        Assert.Single(stages[1]);
        Assert.Equal("big.bin", stages[1][0].DstPath);
    }
}
