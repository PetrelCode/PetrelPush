namespace PetrelPush.Transport;

/// <summary>远端路径穿越防护：所有路径规范化后必须位于 remoteRoot 之下。</summary>
public static class PathGuard
{
    private static readonly string[] ProtectedRoots =
    [
        "/",
        "/etc",
        "/bin",
        "/sbin",
        "/usr",
        "/boot",
        "/lib",
        "/lib64",
        "/dev",
        "/proc",
        "/sys",
        "/var/lib",
    ];

    public static string NormalizeRemoteRoot(string remoteRoot)
    {
        if (string.IsNullOrWhiteSpace(remoteRoot))
        {
            throw new ArgumentException("remoteRoot 不能为空", nameof(remoteRoot));
        }

        var normalized = remoteRoot.Replace('\\', '/').Trim();
        if (!normalized.StartsWith('/'))
        {
            throw new ArgumentException($"remoteRoot 必须是绝对路径：{remoteRoot}", nameof(remoteRoot));
        }

        var segments = new List<string>();
        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    throw new ArgumentException($"remoteRoot 包含越界路径：{remoteRoot}", nameof(remoteRoot));
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        return segments.Count == 0 ? "/" : "/" + string.Join('/', segments);
    }

    /// <summary>把本地相对路径拼到 remoteRoot 下；拒绝绝对路径、<c>..</c>、空段。</summary>
    public static string Combine(string remoteRoot, string relativePath)
    {
        var root = NormalizeRemoteRoot(remoteRoot);
        var raw = (relativePath ?? string.Empty).Replace('\\', '/');
        if (raw.StartsWith('/'))
        {
            throw new RemotePathException($"不允许绝对路径：{relativePath}");
        }

        var relative = raw.Trim('/');
        if (relative.Length == 0)
        {
            return root;
        }

        var segments = relative.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                throw new RemotePathException($"非法相对路径：{relativePath}");
            }

            if (segment.Contains('\0'))
            {
                throw new RemotePathException($"非法相对路径（含 NUL）：{relativePath}");
            }
        }

        var combined = root == "/" ? "/" + string.Join('/', segments) : root + "/" + string.Join('/', segments);
        if (!IsInside(root, combined))
        {
            throw new RemotePathException($"路径越界：{relativePath}");
        }

        return combined;
    }

    public static bool IsInside(string remoteRoot, string candidate)
    {
        var root = NormalizeRemoteRoot(remoteRoot);
        if (string.Equals(candidate, root, StringComparison.Ordinal))
        {
            return true;
        }

        var prefix = root == "/" ? "/" : root + "/";
        return candidate.StartsWith(prefix, StringComparison.Ordinal);
    }

    public static string ParentOf(string remotePath)
    {
        var normalized = remotePath.Replace('\\', '/').TrimEnd('/');
        if (normalized.Length == 0)
        {
            return "/";
        }

        var index = normalized.LastIndexOf('/');
        return index <= 0 ? "/" : normalized[..index];
    }

    public static string RelativeTo(string remoteRoot, string remotePath)
    {
        var root = NormalizeRemoteRoot(remoteRoot);
        if (string.Equals(remotePath, root, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var prefix = root == "/" ? "/" : root + "/";
        return remotePath.StartsWith(prefix, StringComparison.Ordinal) ? remotePath[prefix.Length..] : remotePath;
    }

    /// <summary>remoteRoot 是否直接等于系统目录或其上级（命中时客户端只做一次二次确认）。</summary>
    public static bool IsProtectedRoot(string remoteRoot)
    {
        var root = NormalizeRemoteRoot(remoteRoot);
        foreach (var protectedRoot in ProtectedRoots)
        {
            if (string.Equals(root, protectedRoot, StringComparison.Ordinal))
            {
                return true;
            }

            if (protectedRoot != "/" && protectedRoot.StartsWith(root + "/", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

public sealed class RemotePathException : Exception
{
    public RemotePathException(string message)
        : base(message)
    {
    }
}
