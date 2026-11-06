using System.Text;
using System.Text.RegularExpressions;
using PetrelPush.Core.Model;

namespace PetrelPush.Core.Scan;

/// <summary>
/// 单条 gitignore 兼容规则。支持：注释、空行、<c>!</c> 取反、尾部 <c>/</c> 仅目录、
/// 前导 <c>/</c> 锚定、<c>**</c> 跨级、<c>*</c> <c>?</c> <c>[...]</c>、<c>\</c> 转义。
/// </summary>
public sealed class IgnoreRule
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private readonly Regex _regex;

    private IgnoreRule(
        string source,
        int lineNumber,
        string pattern,
        bool negated,
        bool dirOnly,
        bool anchored,
        string baseDir,
        Regex regex)
    {
        Source = source;
        LineNumber = lineNumber;
        Pattern = pattern;
        Negated = negated;
        DirOnly = dirOnly;
        Anchored = anchored;
        BaseDir = baseDir;
        _regex = regex;
    }

    /// <summary>规则来源文件（相对本地根，或 &lt;builtin&gt;）。</summary>
    public string Source { get; }

    public int LineNumber { get; }

    /// <summary>去掉注释/取反符/首尾斜杠后的模式。</summary>
    public string Pattern { get; }

    public bool Negated { get; }

    public bool DirOnly { get; }

    public bool Anchored { get; }

    /// <summary>规则所属目录（相对本地根，根为 ""）。</summary>
    public string BaseDir { get; }

    public int BaseDepth => PathKeys.Depth(BaseDir);

    /// <summary>解析一行；返回 null 表示该行是注释/空行/无效行。</summary>
    public static IgnoreRule? ParseLine(string rawLine, int lineNumber, string baseDir, string source)
    {
        if (rawLine is null)
        {
            return null;
        }

        var line = rawLine.TrimEnd('\r').TrimEnd('\n');
        if (line.Length == 0)
        {
            return null;
        }

        if (line[0] == '#')
        {
            return null;
        }

        // \# 与 \! 表示字面量（去掉反斜杠后不再解释为注释/取反）
        var escapedLeading = false;
        if (line.Length >= 2 && line[0] == '\\' && (line[1] == '#' || line[1] == '!'))
        {
            line = line[1..];
            escapedLeading = true;
        }

        line = TrimUnescapedTrailingSpaces(line);
        if (line.Length == 0)
        {
            return null;
        }

        var negated = false;
        if (!escapedLeading && line[0] == '!')
        {
            negated = true;
            line = line[1..];
            if (line.Length == 0)
            {
                return null;
            }
        }

        var dirOnly = false;
        if (line.EndsWith('/'))
        {
            dirOnly = true;
            line = line[..^1];
        }

        var anchored = line.Contains('/');
        if (line.StartsWith('/'))
        {
            anchored = true;
            line = line[1..];
        }

        if (line.Length == 0)
        {
            return null;
        }

        var normalizedBase = PathKeys.Normalize(baseDir);
        Regex regex;
        try
        {
            regex = Compile(line, anchored);
        }
        catch (ArgumentException)
        {
            return null;
        }

        return new IgnoreRule(source, lineNumber, line, negated, dirOnly, anchored, normalizedBase, regex);
    }

    /// <summary>规则是否命中该路径（路径为相对本地根、'/' 分隔）。</summary>
    public bool Matches(string relPath, bool isDir)
    {
        if (DirOnly && !isDir)
        {
            return false;
        }

        string relative;
        if (BaseDir.Length == 0)
        {
            relative = relPath;
        }
        else
        {
            if (relPath.Length <= BaseDir.Length + 1)
            {
                return false;
            }

            if (!relPath.StartsWith(BaseDir, StringComparison.OrdinalIgnoreCase) || relPath[BaseDir.Length] != '/')
            {
                return false;
            }

            relative = relPath[(BaseDir.Length + 1)..];
        }

        return _regex.IsMatch(relative);
    }

    public string Describe()
    {
        var location = Source == IgnoreRules.BuiltinSource ? "内置规则" : $"{Source}:{LineNumber}";
        var negation = Negated ? "!" : string.Empty;
        return $"{location} {negation}{Pattern}{(DirOnly ? "/" : string.Empty)}";
    }

    public override string ToString() => Describe();

    private static string TrimUnescapedTrailingSpaces(string line)
    {
        var end = line.Length;
        while (end > 0 && line[end - 1] == ' ')
        {
            // 倒数第二个字符是反斜杠则视为转义空格
            if (end >= 2 && line[end - 2] == '\\')
            {
                break;
            }

            end--;
        }

        return end == line.Length ? line : line[..end];
    }

    private static Regex Compile(string pattern, bool anchored)
    {
        var sb = new StringBuilder(pattern.Length + 16);
        sb.Append('^');
        if (!anchored)
        {
            sb.Append("(?:.*/)?");
        }

        var i = 0;
        while (i < pattern.Length)
        {
            var c = pattern[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                    {
                        var followedBySlash = i + 2 < pattern.Length && pattern[i + 2] == '/';
                        if (followedBySlash)
                        {
                            sb.Append("(?:[^/]+/)*");
                            i += 3;
                        }
                        else
                        {
                            sb.Append(".*");
                            i += 2;
                        }
                    }
                    else
                    {
                        sb.Append("[^/]*");
                        i++;
                    }

                    break;

                case '?':
                    sb.Append("[^/]");
                    i++;
                    break;

                case '[':
                {
                    var close = pattern.IndexOf(']', i + 1);
                    if (close < 0)
                    {
                        sb.Append("\\[");
                        i++;
                        break;
                    }

                    var body = pattern.Substring(i + 1, close - i - 1);
                    if (body.StartsWith('!'))
                    {
                        body = "^" + body[1..];
                    }

                    sb.Append('[');
                    foreach (var ch in body)
                    {
                        if (ch is '\\' or '[' or ']')
                        {
                            sb.Append('\\');
                        }

                        sb.Append(ch);
                    }

                    sb.Append(']');
                    i = close + 1;
                    break;
                }

                case '\\':
                    if (i + 1 < pattern.Length)
                    {
                        sb.Append(Regex.Escape(pattern[i + 1].ToString()));
                        i += 2;
                    }
                    else
                    {
                        i++;
                    }

                    break;

                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    i++;
                    break;
            }
        }

        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.Compiled | RegexOptions.CultureInvariant, MatchTimeout);
    }
}
