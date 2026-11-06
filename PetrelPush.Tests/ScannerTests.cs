using System;
using System.IO;
using System.Linq;
using PetrelPush.Core.Model;
using PetrelPush.Core.Scan;
using Xunit;

namespace PetrelPush.Tests;

public sealed class ScannerTests
{
    [Fact]
    public void ScanFindsFilesAndEmptyDirectories()
    {
        using var temp = new TempDirectory();
        temp.Write("a.txt", "hello");
        temp.Write("sub/b.txt", "world");
        Directory.CreateDirectory(Path.Combine(temp.Path, "empty"));

        var rules = IgnoreRules.Load(temp.Path, new SyncOptions());
        var scan = new Scanner().Scan(temp.Path, rules, new ScannerOptions());

        Assert.Contains(scan.Entries, entry => entry.Path == "a.txt" && entry.Kind == EntryKind.File);
        Assert.Contains(scan.Entries, entry => entry.Path == "sub/b.txt" && entry.Kind == EntryKind.File);
        Assert.Contains(scan.Entries, entry => entry.Path == "empty" && entry.Kind == EntryKind.Dir);
        Assert.Contains("sub", scan.NonEmptyDirectories);
        Assert.DoesNotContain("empty", scan.NonEmptyDirectories);
        Assert.Equal(2, scan.FileCount);
        Assert.Equal(2, scan.DirectoryCount);
    }

    [Fact]
    public void ScanRecordsFileSizeAndTimestamp()
    {
        using var temp = new TempDirectory();
        var full = temp.Write("a.txt", "hello");

        var rules = IgnoreRules.Load(temp.Path, new SyncOptions());
        var scan = new Scanner().Scan(temp.Path, rules, new ScannerOptions());

        var entry = Assert.Single(scan.Entries);
        Assert.Equal(5, entry.Size);
        Assert.Equal(File.GetLastWriteTimeUtc(full).Ticks, entry.MtimeTicks);
    }

    [Fact]
    public void ScanPrunesIgnoredSubtree()
    {
        using var temp = new TempDirectory();
        temp.Write(".syncignore", "**/node_modules/\n");
        temp.Write("node_modules/pkg/index.js", "x");
        temp.Write("keep.txt", "x");

        var rules = IgnoreRules.Load(temp.Path, new SyncOptions());
        var scan = new Scanner().Scan(temp.Path, rules, new ScannerOptions());

        Assert.DoesNotContain(scan.Entries, entry => entry.Path.StartsWith("node_modules", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(scan.Entries, entry => entry.Path == "keep.txt");
    }

    [Fact]
    public void ScanAppliesRootGitignore()
    {
        using var temp = new TempDirectory();
        temp.Write(".gitignore", "*.log\n");
        temp.Write("a.log", "x");
        temp.Write("b.txt", "x");

        var rules = IgnoreRules.Load(temp.Path, new SyncOptions());
        var scan = new Scanner().Scan(temp.Path, rules, new ScannerOptions());

        Assert.DoesNotContain(scan.Entries, entry => entry.Path == "a.log");
        Assert.Contains(scan.Entries, entry => entry.Path == "b.txt");
    }

    [Fact]
    public void ScanAppliesNestedGitignore()
    {
        using var temp = new TempDirectory();
        temp.Write("sub/.gitignore", "*.x\n");
        temp.Write("sub/a.x", "x");
        temp.Write("sub/b.txt", "x");

        var rules = IgnoreRules.Load(temp.Path, new SyncOptions());
        var scan = new Scanner().Scan(temp.Path, rules, new ScannerOptions());

        Assert.DoesNotContain(scan.Entries, entry => entry.Path == "sub/a.x");
        Assert.Contains(scan.Entries, entry => entry.Path == "sub/b.txt");
    }

    [Fact]
    public void ScanUsesForwardSlashesInRelativePaths()
    {
        using var temp = new TempDirectory();
        temp.Write("a/b/c.txt", "x");

        var rules = IgnoreRules.Load(temp.Path, new SyncOptions());
        var scan = new Scanner().Scan(temp.Path, rules, new ScannerOptions());

        Assert.Contains(scan.Entries, entry => entry.Path == "a/b/c.txt");
        Assert.All(scan.Entries, entry => Assert.DoesNotContain('\\', entry.Path));
    }
}
