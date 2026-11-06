using PetrelPush.Core.Model;

namespace PetrelPush.Core.Diff;

public sealed record DiffOptions(bool SyncEmptyDirs = true);

public sealed record DiffResult(IReadOnlyList<SyncTask> Tasks, bool SnapshotDirty);

/// <summary>
/// 期望态（扫描结果） vs 远端已知态（file_entry.synced_*） → 变更集。
/// 生成 A/M/D 与“仅大小写改名”的 R，随后交给 <see cref="RenameMatcher"/> 做精确重命名配对。
/// </summary>
public sealed class DiffEngine
{
    public DiffResult Compute(
        IReadOnlyDictionary<string, FileEntry> existing,
        IReadOnlyList<ScannedEntry> scanned,
        IReadOnlySet<string> nonEmptyDirectories,
        DiffOptions options)
    {
        var scannedByKey = new Dictionary<string, ScannedEntry>(scanned.Count, StringComparer.Ordinal);
        foreach (var entry in scanned)
        {
            scannedByKey[entry.PathKey] = entry;
        }

        var tasks = new List<SyncTask>();

        // ① 本地已消失 → 远端删除（远端本无则交给状态库清理行）
        foreach (var entry in existing.Values)
        {
            if (scannedByKey.ContainsKey(entry.PathKey) || entry.SyncedKind is null)
            {
                continue;
            }

            tasks.Add(new SyncTask
            {
                Action = SyncAction.Delete,
                DstPath = entry.Path,
                WantKind = entry.SyncedKind.Value,
                WantHash = entry.SyncedHash,
                Size = entry.Size,
            });
        }

        // ② 新增 / 修改 / 类型变化 / 仅大小写改名
        foreach (var scan in scannedByKey.Values)
        {
            existing.TryGetValue(scan.PathKey, out var previous);
            var syncedKind = previous?.SyncedKind;

            if (syncedKind is null)
            {
                AddCreate(tasks, scan, nonEmptyDirectories, options);
                continue;
            }

            if (syncedKind.Value != scan.Kind)
            {
                tasks.Add(CreateDelete(previous!));
                AddCreate(tasks, scan, nonEmptyDirectories, options);
                continue;
            }

            if (scan.Kind == EntryKind.Dir)
            {
                if (!string.Equals(scan.Path, previous!.Path, StringComparison.Ordinal))
                {
                    tasks.Add(new SyncTask
                    {
                        Action = SyncAction.Rename,
                        SrcPath = previous.Path,
                        DstPath = scan.Path,
                        WantKind = EntryKind.Dir,
                    });
                }

                continue;
            }

            if (scan.Hash is null)
            {
                // 内容读不到（被独占/权限），本轮跳过，下一轮重试
                continue;
            }

            if (!Hashes.Equal(scan.Hash, previous!.SyncedHash))
            {
                tasks.Add(new SyncTask
                {
                    Action = SyncAction.Upload,
                    DstPath = scan.Path,
                    WantKind = EntryKind.File,
                    WantHash = scan.Hash,
                    Size = scan.Size,
                });
            }
            else if (!string.Equals(scan.Path, previous.Path, StringComparison.Ordinal))
            {
                tasks.Add(new SyncTask
                {
                    Action = SyncAction.Rename,
                    SrcPath = previous.Path,
                    DstPath = scan.Path,
                    WantKind = EntryKind.File,
                    WantHash = scan.Hash,
                    Size = scan.Size,
                });
            }
        }

        var normalized = RenameMatcher.Detect(tasks);
        var dirty = IsSnapshotDirty(existing, scannedByKey);
        return new DiffResult(normalized, dirty);
    }

    private static void AddCreate(
        List<SyncTask> tasks,
        ScannedEntry scan,
        IReadOnlySet<string> nonEmptyDirectories,
        DiffOptions options)
    {
        if (scan.Kind == EntryKind.Dir)
        {
            if (!options.SyncEmptyDirs && !nonEmptyDirectories.Contains(scan.Path))
            {
                return;
            }

            tasks.Add(new SyncTask
            {
                Action = SyncAction.Mkdir,
                DstPath = scan.Path,
                WantKind = EntryKind.Dir,
            });
            return;
        }

        if (scan.Hash is null)
        {
            return;
        }

        tasks.Add(new SyncTask
        {
            Action = SyncAction.Upload,
            DstPath = scan.Path,
            WantKind = EntryKind.File,
            WantHash = scan.Hash,
            Size = scan.Size,
        });
    }

    private static SyncTask CreateDelete(FileEntry entry) => new()
    {
        Action = SyncAction.Delete,
        DstPath = entry.Path,
        WantKind = entry.SyncedKind ?? entry.Kind,
        WantHash = entry.SyncedHash,
        Size = entry.Size,
    };

    private static bool IsSnapshotDirty(
        IReadOnlyDictionary<string, FileEntry> existing,
        IReadOnlyDictionary<string, ScannedEntry> scanned)
    {
        foreach (var scan in scanned.Values)
        {
            if (!existing.TryGetValue(scan.PathKey, out var previous))
            {
                return true;
            }

            if (!string.Equals(previous.Path, scan.Path, StringComparison.Ordinal)
                || previous.Kind != scan.Kind
                || previous.Size != scan.Size
                || previous.MtimeTicks != scan.MtimeTicks
                || !Hashes.Equal(previous.Hash, scan.Hash))
            {
                return true;
            }
        }

        foreach (var key in existing.Keys)
        {
            if (!scanned.ContainsKey(key))
            {
                return true;
            }
        }

        return false;
    }
}
