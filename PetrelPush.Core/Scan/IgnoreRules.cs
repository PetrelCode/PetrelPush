using Serilog;
using PetrelPush.Core.Model;

namespace PetrelPush.Core.Scan;

/// <summary>一次忽略判定的解释，用于“该文件为何未同步”查询。</summary>
public sealed record IgnoreExplanation(bool Ignored, string Path, bool IsDirectory, IgnoreRule? MatchedRule, string? IgnoredByAncestor, string Summary);

/// <summary>
/// 三层忽略规则：L0 内置硬编码 / L1 逐目录 .gitignore / L2 根 .syncignore（优先级最高）。
/// 规则变更立即生效（每个周期重新加载实例）。
/// </summary>
public sealed class IgnoreRules
{
    public const string BuiltinSource = "<builtin>";

    private static readonly string[] BuiltinPatterns =
    [
        ".sync_tmp_*",
        "*.tmp",
        "~$*",
        "*.swp",
        ".DS_Store",
        "Thumbs.db",
        "desktop.ini",
        ".git/",
    ];

    private readonly List<IgnoreRule> _builtin = new();
    private readonly List<IgnoreRule> _repo = new();
    private readonly List<IgnoreRule> _project = new();
    private readonly Dictionary<string, bool> _evalCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loadedDirectoryRules = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _extraIgnorePath;
    private readonly ILogger? _logger;

    private IgnoreRules(string localRoot, string extraIgnorePath, ILogger? logger)
    {
        LocalRoot = localRoot;
        _extraIgnorePath = extraIgnorePath;
        _logger = logger;
    }

    public string LocalRoot { get; }

    public int RuleCount => _builtin.Count + _repo.Count + _project.Count;

    public int RepoRuleCount => _repo.Count;

    /// <summary>加载 L0 + 根 .gitignore + L2 .syncignore。</summary>
    public static IgnoreRules Load(string localRoot, SyncOptions options, ILogger? logger = null)
    {
        var root = Path.GetFullPath(localRoot);
        var extra = string.IsNullOrWhiteSpace(options.ExtraIgnoreFile) ? ".syncignore" : options.ExtraIgnoreFile;
        var rules = new IgnoreRules(root, Path.Combine(root, extra.Replace('/', Path.DirectorySeparatorChar)), logger);

        var line = 0;
        foreach (var pattern in BuiltinPatterns)
        {
            line++;
            var rule = IgnoreRule.ParseLine(pattern, line, string.Empty, BuiltinSource);
            if (rule is not null)
            {
                rules._builtin.Add(rule);
            }
        }

        rules.EnsureDirectoryRules(string.Empty);

        var extraFull = Path.GetFullPath(rules._extraIgnorePath);
        if (File.Exists(extraFull))
        {
            var baseDir = PathKeys.Normalize(Path.GetRelativePath(root, Path.GetDirectoryName(extraFull) ?? root));
            if (baseDir == ".")
            {
                baseDir = string.Empty;
            }

            rules.AddRulesFromFile(extraFull, baseDir, rules._project);
        }

        return rules;
    }

    /// <summary>加载某目录下的 .gitignore（幂等）。Scanner 在进入目录时调用。</summary>
    public void EnsureDirectoryRules(string directoryRelativePath)
    {
        var dir = PathKeys.Normalize(directoryRelativePath);
        if (!_loadedDirectoryRules.Add(dir))
        {
            return;
        }

        var full = dir.Length == 0 ? LocalRoot : Path.Combine(LocalRoot, dir.Replace('/', Path.DirectorySeparatorChar));
        var gitignore = Path.Combine(full, ".gitignore");
        if (!File.Exists(gitignore))
        {
            return;
        }

        AddRulesFromFile(gitignore, dir, _repo);
    }

    /// <summary>路径是否被忽略（含“父目录被排除时子项无法重新包含”的 git 语义）。</summary>
    public bool IsIgnored(string relativePath, bool isDirectory)
    {
        var path = PathKeys.Normalize(relativePath);
        if (path.Length == 0)
        {
            return false;
        }

        var index = 0;
        while (true)
        {
            index = path.IndexOf('/', index);
            if (index < 0)
            {
                break;
            }

            var ancestor = path[..index];
            index++;
            if (Evaluate(ancestor, true))
            {
                return true;
            }
        }

        return Evaluate(path, isDirectory);
    }

    /// <summary>仅用内置规则判断（Watcher 快速过滤，不触碰磁盘）。</summary>
    public bool IsBuiltinIgnored(string relativePath, bool isDirectory)
    {
        var path = PathKeys.Normalize(relativePath);
        if (path.Length == 0)
        {
            return false;
        }

        foreach (var rule in _builtin)
        {
            if (rule.Matches(path, isDirectory))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>给出“为何未同步”的完整解释。</summary>
    public IgnoreExplanation Explain(string relativePath, bool isDirectory)
    {
        var path = PathKeys.Normalize(relativePath);
        if (path.Length == 0)
        {
            return new IgnoreExplanation(false, path, isDirectory, null, null, "根目录本身不参与同步判定");
        }

        var index = 0;
        while (true)
        {
            index = path.IndexOf('/', index);
            if (index < 0)
            {
                break;
            }

            var ancestor = path[..index];
            index++;
            var (ancestorIgnored, ancestorRule) = EvaluateDetailed(ancestor, true);
            if (ancestorIgnored)
            {
                return new IgnoreExplanation(
                    true,
                    path,
                    isDirectory,
                    ancestorRule,
                    ancestor,
                    $"父目录 {ancestor} 被规则 {ancestorRule?.Describe() ?? "(未知)"} 排除，子项无法被重新包含（与 git 语义一致）");
            }
        }

        var (ignored, rule) = EvaluateDetailed(path, isDirectory);
        if (!ignored)
        {
            return new IgnoreExplanation(false, path, isDirectory, rule, null, "未被任何规则排除");
        }

        var summary = rule is null
            ? "被排除（未命中具体规则，可能由父目录规则导致）"
            : $"被 {rule.Describe()} 排除";
        return new IgnoreExplanation(true, path, isDirectory, rule, null, summary);
    }

    private void AddRulesFromFile(string fullPath, string baseDir, List<IgnoreRule> target)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(fullPath);
        }
        catch (Exception ex)
        {
            _logger?.Warning(ex, "读取忽略规则文件失败：{File}", fullPath);
            return;
        }

        var source = PathKeys.Normalize(Path.GetRelativePath(LocalRoot, fullPath));
        for (var i = 0; i < lines.Length; i++)
        {
            var rule = IgnoreRule.ParseLine(lines[i], i + 1, baseDir, source);
            if (rule is not null)
            {
                target.Add(rule);
            }
        }

        // 深层 .gitignore 优先级更高：按所属目录深度稳定排序
        if (ReferenceEquals(target, _repo))
        {
            var ordered = _repo.OrderBy(r => r.BaseDepth).ToList();
            _repo.Clear();
            _repo.AddRange(ordered);
        }

        _evalCache.Clear();
    }

    private bool Evaluate(string relativePath, bool isDirectory)
    {
        var cacheKey = isDirectory ? relativePath + "|d" : relativePath + "|f";
        if (_evalCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var ignored = false;
        foreach (var rule in _builtin)
        {
            if (rule.Matches(relativePath, isDirectory))
            {
                ignored = !rule.Negated;
            }
        }

        foreach (var rule in _repo)
        {
            if (rule.Matches(relativePath, isDirectory))
            {
                ignored = !rule.Negated;
            }
        }

        foreach (var rule in _project)
        {
            if (rule.Matches(relativePath, isDirectory))
            {
                ignored = !rule.Negated;
            }
        }

        _evalCache[cacheKey] = ignored;
        return ignored;
    }

    private (bool Ignored, IgnoreRule? LastMatch) EvaluateDetailed(string relativePath, bool isDirectory)
    {
        var ignored = false;
        IgnoreRule? last = null;

        foreach (var rule in _builtin)
        {
            if (rule.Matches(relativePath, isDirectory))
            {
                ignored = !rule.Negated;
                last = rule;
            }
        }

        foreach (var rule in _repo)
        {
            if (rule.Matches(relativePath, isDirectory))
            {
                ignored = !rule.Negated;
                last = rule;
            }
        }

        foreach (var rule in _project)
        {
            if (rule.Matches(relativePath, isDirectory))
            {
                ignored = !rule.Negated;
                last = rule;
            }
        }

        return (ignored, last);
    }
}
