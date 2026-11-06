using System.Linq;
using PetrelPush.Core.Model;
using PetrelPush.Core.Scan;
using Xunit;

namespace PetrelPush.Tests;

public sealed class IgnoreRulesTests
{
    private static SyncOptions DefaultOptions => new();

    [Theory]
    [InlineData(".git", true)]
    [InlineData("Thumbs.db", false)]
    [InlineData("desktop.ini", false)]
    [InlineData("a.tmp", false)]
    [InlineData("~$report.docx", false)]
    [InlineData("a.swp", false)]
    public void BuiltinRulesAreIgnored(string path, bool isDirectory)
    {
        using var temp = new TempDirectory();
        var rules = IgnoreRules.Load(temp.Path, DefaultOptions);

        Assert.True(rules.IsIgnored(path, isDirectory));
    }

    [Fact]
    public void AnchoredRootRuleOnlyMatchesRoot()
    {
        using var temp = new TempDirectory();
        temp.Write(".syncignore", "/dist/\n");

        var rules = IgnoreRules.Load(temp.Path, DefaultOptions);

        Assert.True(rules.IsIgnored("dist", true));
        Assert.True(rules.IsIgnored("dist/a.js", false));
        Assert.False(rules.IsIgnored("sub/dist", true));
        Assert.False(rules.IsIgnored("sub/dist/a.js", false));
    }

    [Fact]
    public void DoubleStarMatchesAnyDepth()
    {
        using var temp = new TempDirectory();
        temp.Write(".syncignore", "**/node_modules/\n");

        var rules = IgnoreRules.Load(temp.Path, DefaultOptions);

        Assert.True(rules.IsIgnored("node_modules", true));
        Assert.True(rules.IsIgnored("a/node_modules", true));
        Assert.True(rules.IsIgnored("a/b/node_modules/x.js", false));
    }

    [Fact]
    public void NegationReincludesMatchingFile()
    {
        using var temp = new TempDirectory();
        temp.Write(".syncignore", "*.log\n!keep.log\n");

        var rules = IgnoreRules.Load(temp.Path, DefaultOptions);

        Assert.True(rules.IsIgnored("a.log", false));
        Assert.False(rules.IsIgnored("keep.log", false));
    }

    [Fact]
    public void NegationCannotReincludeInsideExcludedParent()
    {
        using var temp = new TempDirectory();
        temp.Write(".syncignore", "/dist/\n!/dist/runtime.json\n");

        var rules = IgnoreRules.Load(temp.Path, DefaultOptions);

        Assert.True(rules.IsIgnored("dist", true));
        Assert.True(rules.IsIgnored("dist/runtime.json", false));

        var explanation = rules.Explain("dist/runtime.json", false);
        Assert.True(explanation.Ignored);
        Assert.Equal("dist", explanation.IgnoredByAncestor);
    }

    [Fact]
    public void DirectoryOnlyRuleDoesNotMatchSameNamedFile()
    {
        using var temp = new TempDirectory();
        temp.Write(".syncignore", "dist/\n");

        var rules = IgnoreRules.Load(temp.Path, DefaultOptions);

        Assert.True(rules.IsIgnored("dist", true));
        Assert.False(rules.IsIgnored("dist", false));
    }

    [Fact]
    public void CharacterClassAndQuestionMarkAreSupported()
    {
        using var temp = new TempDirectory();
        temp.Write(".syncignore", "file[0-9].txt\nlog?.md\n");

        var rules = IgnoreRules.Load(temp.Path, DefaultOptions);

        Assert.True(rules.IsIgnored("file1.txt", false));
        Assert.False(rules.IsIgnored("filea.txt", false));
        Assert.True(rules.IsIgnored("log1.md", false));
        Assert.False(rules.IsIgnored("log10.md", false));
    }

    [Fact]
    public void CommentAndBlankLinesAreIgnored()
    {
        using var temp = new TempDirectory();
        temp.Write(".syncignore", "# comment\n\n   \n#*.log\n");

        var rules = IgnoreRules.Load(temp.Path, DefaultOptions);

        Assert.False(rules.IsIgnored("a.log", false));
    }

    [Fact]
    public void ProjectRulesOverrideGitignore()
    {
        using var temp = new TempDirectory();
        temp.Write(".gitignore", "*.log\n");
        temp.Write(".syncignore", "!*.log\n");

        var rules = IgnoreRules.Load(temp.Path, DefaultOptions);

        Assert.False(rules.IsIgnored("a.log", false));
    }

    [Fact]
    public void NestedGitignoreAppliesToItsSubtreeOnly()
    {
        using var temp = new TempDirectory();
        temp.Write("sub/.gitignore", "*.x\n");

        var rules = IgnoreRules.Load(temp.Path, DefaultOptions);
        rules.EnsureDirectoryRules("sub");

        Assert.True(rules.IsIgnored("sub/a.x", false));
        Assert.False(rules.IsIgnored("a.x", false));
    }

    [Fact]
    public void ExplainReportsMatchedRule()
    {
        using var temp = new TempDirectory();
        temp.Write(".syncignore", "*.log\n");

        var rules = IgnoreRules.Load(temp.Path, DefaultOptions);
        var explanation = rules.Explain("logs/a.log", false);

        Assert.True(explanation.Ignored);
        Assert.NotNull(explanation.MatchedRule);
        Assert.Contains("*.log", explanation.Summary);
        Assert.Equal(1, explanation.MatchedRule!.LineNumber);
    }

    [Fact]
    public void EscapedHashAndBangAreLiterals()
    {
        using var temp = new TempDirectory();
        temp.Write(".syncignore", "\\#hash.txt\n\\!bang.txt\n");

        var rules = IgnoreRules.Load(temp.Path, DefaultOptions);

        Assert.True(rules.IsIgnored("#hash.txt", false));
        Assert.True(rules.IsIgnored("!bang.txt", false));
        Assert.False(rules.IsIgnored("bang.txt", false));
    }
}
