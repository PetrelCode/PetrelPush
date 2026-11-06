using System;
using System.Collections.Generic;
using System.Linq;
using PetrelPush.Core.Diff;
using PetrelPush.Core.Model;
using Xunit;

namespace PetrelPush.Tests;

public sealed class DiffEngineTests
{
    private static readonly byte[] HashA = [1, 2, 3];

    private static readonly byte[] HashB = [4, 5, 6];

    private static Dictionary<string, FileEntry> Existing(params FileEntry[] entries) =>
        entries.ToDictionary(entry => entry.PathKey, StringComparer.Ordinal);

    private static FileEntry SyncedFile(string path, byte[] hash, long size = 5) =>
        new(path, EntryKind.File, size, 100, hash, hash, EntryKind.File);

    private static FileEntry SyncedDirectory(string path) =>
        new(path, EntryKind.Dir, 0, 100, null, null, EntryKind.Dir);

    private static ScannedEntry ScannedFile(string path, byte[]? hash, long size = 5) =>
        new(path, EntryKind.File, size, 100, hash);

    private static DiffResult Run(
        IReadOnlyDictionary<string, FileEntry> existing,
        IReadOnlySet<string> nonEmptyDirectories,
        params ScannedEntry[] scanned) =>
        new DiffEngine().Compute(existing, scanned, nonEmptyDirectories, new DiffOptions(true));

    private static IReadOnlySet<string> NoDirectories() =>
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void NewFileProducesUpload()
    {
        var result = Run(Existing(), NoDirectories(), ScannedFile("a.txt", HashA));

        var task = Assert.Single(result.Tasks);
        Assert.Equal(SyncAction.Upload, task.Action);
        Assert.Equal("a.txt", task.DstPath);
        Assert.True(result.SnapshotDirty);
    }

    [Fact]
    public void NewDirectoryProducesMkdir()
    {
        var result = Run(Existing(), NoDirectories(), new ScannedEntry("empty", EntryKind.Dir, 0, 100));

        var task = Assert.Single(result.Tasks);
        Assert.Equal(SyncAction.Mkdir, task.Action);
        Assert.Equal(EntryKind.Dir, task.WantKind);
    }

    [Fact]
    public void EmptyDirectorySkippedWhenSyncEmptyDirsDisabled()
    {
        var result = new DiffEngine().Compute(
            Existing(),
            [new ScannedEntry("empty", EntryKind.Dir, 0, 100)],
            NoDirectories(),
            new DiffOptions(SyncEmptyDirs: false));

        Assert.Empty(result.Tasks);
    }

    [Fact]
    public void ModifiedFileProducesUpload()
    {
        var result = Run(Existing(SyncedFile("a.txt", HashA)), NoDirectories(), ScannedFile("a.txt", HashB));

        var task = Assert.Single(result.Tasks);
        Assert.Equal(SyncAction.Upload, task.Action);
        Assert.Equal(HashB, task.WantHash);
    }

    [Fact]
    public void UnchangedFileProducesNothing()
    {
        var result = Run(Existing(SyncedFile("a.txt", HashA)), NoDirectories(), ScannedFile("a.txt", HashA));

        Assert.Empty(result.Tasks);
        Assert.False(result.SnapshotDirty);
    }

    [Fact]
    public void MissingLocalFileProducesDelete()
    {
        var result = Run(Existing(SyncedFile("a.txt", HashA)), NoDirectories());

        var task = Assert.Single(result.Tasks);
        Assert.Equal(SyncAction.Delete, task.Action);
        Assert.Equal(EntryKind.File, task.WantKind);
    }

    [Fact]
    public void MissingLocalEntryWithoutRemoteStateProducesNoTask()
    {
        var entry = new FileEntry("a.txt", EntryKind.File, 5, 100, HashA, null, null);
        var result = Run(Existing(entry), NoDirectories());

        Assert.Empty(result.Tasks);
        Assert.True(result.SnapshotDirty);
    }

    [Fact]
    public void CaseOnlyRenameProducesRename()
    {
        var existing = new FileEntry("foo.txt", EntryKind.File, 5, 100, HashA, HashA, EntryKind.File);
        var result = Run(Existing(existing), NoDirectories(), ScannedFile("Foo.txt", HashA));

        var task = Assert.Single(result.Tasks);
        Assert.Equal(SyncAction.Rename, task.Action);
        Assert.Equal("foo.txt", task.SrcPath);
        Assert.Equal("Foo.txt", task.DstPath);
    }

    [Fact]
    public void TypeChangeProducesDeleteAndCreate()
    {
        var result = Run(Existing(SyncedDirectory("data")), NoDirectories(), ScannedFile("data", HashA));

        Assert.Equal(2, result.Tasks.Count);
        Assert.Contains(result.Tasks, task => task.Action == SyncAction.Delete && task.WantKind == EntryKind.Dir);
        Assert.Contains(result.Tasks, task => task.Action == SyncAction.Upload && task.WantKind == EntryKind.File);
    }

    [Fact]
    public void FileRenameIsPairedPrecisely()
    {
        var result = Run(Existing(SyncedFile("old.txt", HashA, 5)), NoDirectories(), ScannedFile("new.txt", HashA, 5));

        var task = Assert.Single(result.Tasks);
        Assert.Equal(SyncAction.Rename, task.Action);
        Assert.Equal("old.txt", task.SrcPath);
        Assert.Equal("new.txt", task.DstPath);
    }

    [Fact]
    public void DirectoryRenameIsPairedIntoOneTask()
    {
        var existing = Existing(
            SyncedDirectory("old"),
            SyncedFile("old/a.txt", HashA, 5),
            SyncedFile("old/b.txt", HashB, 6));
        var scanned = new[]
        {
            new ScannedEntry("new", EntryKind.Dir, 0, 100),
            ScannedFile("new/a.txt", HashA, 5),
            ScannedFile("new/b.txt", HashB, 6),
        };
        var nonEmpty = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "new" };

        var result = new DiffEngine().Compute(existing, scanned, nonEmpty, new DiffOptions(true));

        var task = Assert.Single(result.Tasks);
        Assert.Equal(SyncAction.Rename, task.Action);
        Assert.Equal("old", task.SrcPath);
        Assert.Equal("new", task.DstPath);
        Assert.Equal(EntryKind.Dir, task.WantKind);
    }

    [Fact]
    public void DirectoryRenameWithModifiedChildFallsBackToPerFileTasks()
    {
        var existing = Existing(
            SyncedDirectory("old"),
            SyncedFile("old/a.txt", HashA, 5),
            SyncedFile("old/b.txt", HashB, 6));
        var scanned = new[]
        {
            new ScannedEntry("new", EntryKind.Dir, 0, 100),
            ScannedFile("new/a.txt", HashA, 5),
            ScannedFile("new/b.txt", [9, 9, 9], 7),
        };
        var nonEmpty = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "new" };

        var result = new DiffEngine().Compute(existing, scanned, nonEmpty, new DiffOptions(true));

        Assert.Contains(result.Tasks, task => task.Action == SyncAction.Mkdir);
        Assert.Contains(result.Tasks, task => task.Action == SyncAction.Upload);
        Assert.Contains(result.Tasks, task => task.Action == SyncAction.Delete);
        Assert.DoesNotContain(result.Tasks, task => task.Action == SyncAction.Rename && task.WantKind == EntryKind.Dir);
    }

    [Fact]
    public void UnreadableFileIsSkippedThisCycle()
    {
        var result = Run(Existing(SyncedFile("a.txt", HashA)), NoDirectories(), ScannedFile("a.txt", null));

        Assert.Empty(result.Tasks);
    }
}
