using PetrelPush.Core.Model;
using PetrelPush.Transport;
using Xunit;

namespace PetrelPush.Tests;

public sealed class FileBrowseTests
{
    [Theory]
    [InlineData("a.txt", "a.txt")]
    [InlineData("a:b?.txt", "a_b_.txt")]
    [InlineData("na*me\"|.log", "na_me__.log")]
    [InlineData("name.", "name")]
    [InlineData("name   ", "name")]
    [InlineData("CON", "_CON")]
    [InlineData("con.txt", "_con.txt")]
    [InlineData("LPT9.log", "_LPT9.log")]
    [InlineData("中文 文件.md", "中文 文件.md")]
    public void SanitizeProducesWindowsSafeName(string remote, string expected)
    {
        Assert.Equal(expected, LocalFileName.Sanitize(remote));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    public void SanitizeFallsBackForUnusableName(string? remote)
    {
        Assert.Equal("_", LocalFileName.Sanitize(remote));
    }

    [Fact]
    public void LocalStateFollowsRemoteKnownState()
    {
        var entry = new FileEntry("a.txt", EntryKind.File, 12, 0, null, [1], EntryKind.File);

        Assert.Equal(FileEntryState.Synced, FileEntryStates.ForLocal(entry, EntryKind.File, false));
        Assert.Equal(FileEntryState.PendingUpload, FileEntryStates.ForLocal(entry, EntryKind.Dir, false));
        Assert.Equal(FileEntryState.PendingUpload, FileEntryStates.ForLocal(null, EntryKind.File, false));
        Assert.Equal(FileEntryState.Ignored, FileEntryStates.ForLocal(entry, EntryKind.File, true));
    }

    [Theory]
    [InlineData(true, FileEntryState.LocalExists)]
    [InlineData(false, FileEntryState.RemoteOnly)]
    public void RemoteStateFollowsLocalPresence(bool localExists, FileEntryState expected)
    {
        Assert.Equal(expected, FileEntryStates.ForRemote(localExists));
    }

    [Fact]
    public void RemoteEntryExposesBrowsableFlags()
    {
        var directory = new RemoteEntry("src", "src", true, false, false, 0, DateTimeOffset.UnixEpoch, "755");
        Assert.True(directory.CanEnter);
        Assert.False(directory.CanDownload);

        var linkedDirectory = new RemoteEntry("lib", "lib", true, true, false, 0, DateTimeOffset.UnixEpoch, "777");
        Assert.False(linkedDirectory.CanEnter);

        var file = new RemoteEntry("a.txt", "a.txt", false, false, false, 10, DateTimeOffset.UnixEpoch, "644");
        Assert.True(file.CanDownload);
        Assert.False(file.CanEnter);

        var pipe = new RemoteEntry("pipe", "pipe", false, false, true, 0, DateTimeOffset.UnixEpoch, "000");
        Assert.False(pipe.CanDownload);
    }

    [Fact]
    public void RemoteEntryNormalizesRelativePath()
    {
        var entry = new RemoteEntry("a.txt", "src\\a.txt", false, false, false, 1, DateTimeOffset.UnixEpoch, "644");
        Assert.Equal("src/a.txt", entry.RelativePath);
    }

    [Theory]
    [InlineData("644", "rw-r--r--")]
    [InlineData("755", "rwxr-xr-x")]
    [InlineData("600", "rw-------")]
    [InlineData("777", "rwxrwxrwx")]
    [InlineData("000", "---------")]
    public void UnixPermissionsDescribeSymbolicForm(string octal, string expected)
    {
        Assert.Equal(expected, UnixPermissions.Describe(octal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("64")]
    [InlineData("64x")]
    [InlineData("9999")]
    public void UnixPermissionsRejectsInvalidInput(string? octal)
    {
        Assert.Equal(string.Empty, UnixPermissions.Describe(octal));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../etc")]
    [InlineData("src/../../etc")]
    [InlineData("/etc")]
    public void BrowsePathIsAlwaysInsideRemoteRoot(string relative)
    {
        Assert.Throws<RemotePathException>(() => PathGuard.Combine("/srv/sync/app", relative));
    }
}
