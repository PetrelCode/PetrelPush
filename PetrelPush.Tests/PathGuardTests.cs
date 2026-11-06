using PetrelPush.Transport;
using Xunit;

namespace PetrelPush.Tests;

public sealed class PathGuardTests
{
    [Theory]
    [InlineData("/srv/sync/app", "src/a.txt", "/srv/sync/app/src/a.txt")]
    [InlineData("/srv/sync/app/", "a.txt", "/srv/sync/app/a.txt")]
    [InlineData("/", "a.txt", "/a.txt")]
    [InlineData("/srv/sync/app", "中文 目录/a b.txt", "/srv/sync/app/中文 目录/a b.txt")]
    public void CombineBuildsRemotePath(string root, string relative, string expected)
    {
        Assert.Equal(expected, PathGuard.Combine(root, relative));
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("a/../../etc/passwd")]
    [InlineData("a/./b/../c")]
    [InlineData("/absolute/path")]
    public void CombineRejectsTraversal(string relative)
    {
        Assert.Throws<RemotePathException>(() => PathGuard.Combine("/srv/sync/app", relative));
    }

    [Fact]
    public void NormalizeRemoteRootCollapsesSegments()
    {
        Assert.Equal("/srv/sync/app", PathGuard.NormalizeRemoteRoot("/srv//sync/./app/"));
        Assert.Equal("/srv/app", PathGuard.NormalizeRemoteRoot("/srv/x/../app"));
        Assert.Equal("/", PathGuard.NormalizeRemoteRoot("/"));
    }

    [Fact]
    public void NormalizeRemoteRootRejectsRelativePath()
    {
        Assert.Throws<System.ArgumentException>(() => PathGuard.NormalizeRemoteRoot("srv/sync"));
    }

    [Theory]
    [InlineData("/srv/sync/app", "/srv/sync/app/src/a.txt", true)]
    [InlineData("/srv/sync/app", "/srv/sync/app", true)]
    [InlineData("/srv/sync/app", "/srv/sync/app2/a.txt", false)]
    [InlineData("/srv/sync/app", "/etc/passwd", false)]
    [InlineData("/", "/etc/passwd", true)]
    public void IsInsideChecksRootBoundary(string root, string candidate, bool expected)
    {
        Assert.Equal(expected, PathGuard.IsInside(root, candidate));
    }

    [Theory]
    [InlineData("/", true)]
    [InlineData("/etc", true)]
    [InlineData("/var", true)]
    [InlineData("/usr/local", false)]
    [InlineData("/etc/nginx", false)]
    [InlineData("/srv/sync/app", false)]
    [InlineData("/root/sync/app", false)]
    public void IsProtectedRootDetectsSystemDirectories(string root, bool expected)
    {
        Assert.Equal(expected, PathGuard.IsProtectedRoot(root));
    }

    [Fact]
    public void ParentOfReturnsParentPath()
    {
        Assert.Equal("/srv/sync", PathGuard.ParentOf("/srv/sync/app"));
        Assert.Equal("/", PathGuard.ParentOf("/srv"));
    }
}
