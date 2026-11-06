using System;
using System.IO;
using System.Linq;
using PetrelPush.Core.Config;
using PetrelPush.Core.Model;
using Xunit;

namespace PetrelPush.Tests;

public sealed class ProfileTests
{
    [Fact]
    public void JsonRoundTripKeepsChineseAndCamelCase()
    {
        using var temp = new TempDirectory();
        var profile = new SyncProfile
        {
            ProfileId = "local",
            ProfileName = "测试项目 → 服务器",
            LocalRoot = temp.Path,
            RemoteRoot = "/srv/sync/app",
            Remote = new RemoteOptions
            {
                Host = "192.168.1.10",
                User = "root",
                Password = "should-not-be-serialized",
            },
        };

        var json = SyncProfileJson.Serialize(profile);

        Assert.Contains("\"profileName\"", json);
        Assert.Contains("测试项目", json);
        Assert.DoesNotContain("should-not-be-serialized", json);

        var roundTrip = SyncProfileJson.Parse(json);
        Assert.Equal(profile.ProfileName, roundTrip.ProfileName);
        Assert.Equal(profile.RemoteRoot, roundTrip.RemoteRoot);
        Assert.Null(roundTrip.Remote.Password);
    }

    [Fact]
    public void ValidateReportsMissingLocalRoot()
    {
        var profile = new SyncProfile
        {
            ProfileId = "p",
            LocalRoot = Path.Combine(Path.GetTempPath(), "PetrelPush-does-not-exist-" + Guid.NewGuid().ToString("N")),
            RemoteRoot = "/srv/x",
            Remote = new RemoteOptions { Host = "host", User = "root", Password = "x" },
        };

        Assert.Contains(profile.Validate(), error => error.Contains("本地目录不存在"));
    }

    [Fact]
    public void ValidateAcceptsCompleteProfile()
    {
        using var temp = new TempDirectory();
        var profile = new SyncProfile
        {
            ProfileId = "p1",
            LocalRoot = temp.Path,
            RemoteRoot = "/srv/x",
            Remote = new RemoteOptions
            {
                Host = "host",
                User = "root",
                Password = "secret",
            },
        };

        Assert.Empty(profile.Validate());
    }

    [Fact]
    public void ValidateRejectsRelativeRemoteRoot()
    {
        using var temp = new TempDirectory();
        var profile = new SyncProfile
        {
            ProfileId = "p1",
            LocalRoot = temp.Path,
            RemoteRoot = "srv/x",
            Remote = new RemoteOptions { Host = "host", User = "root", Password = "secret" },
        };

        Assert.Contains(profile.Validate(), error => error.Contains("remoteRoot"));
    }

    [Fact]
    public void ProfileStoreEncryptsPasswordWithDpapi()
    {
        using var temp = new TempDirectory();
        var store = new ProfileStore(temp.Path);
        var profile = new SyncProfile
        {
            ProfileId = "demo",
            ProfileName = "演示",
            LocalRoot = temp.Path,
            RemoteRoot = "/srv/demo",
            Remote = new RemoteOptions { Host = "10.0.0.1", User = "root" },
        };

        store.Save(profile, "p@ssw0rd");
        var secretsFile = store.GetSecretsPath("demo");

        Assert.True(File.Exists(secretsFile));
        Assert.DoesNotContain("p@ssw0rd", File.ReadAllText(store.GetConfigPath("demo")));

        var loaded = store.Load("demo");
        Assert.Equal("p@ssw0rd", loaded.Remote.Password);
        Assert.Equal("/srv/demo", loaded.RemoteRoot);
    }

    [Fact]
    public void CreateDefaultProducesProfileName()
    {
        using var temp = new TempDirectory();
        var store = new ProfileStore(temp.Path);

        var profile = store.CreateDefault("id", temp.Path, "/srv/app", "host", "root");

        Assert.Equal("id", profile.ProfileId);
        Assert.Contains("→", profile.ProfileName);
        Assert.Equal("password", profile.Remote.Auth.Type);
    }
}
