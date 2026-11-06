using PetrelPush.Core.Model;

namespace PetrelPush.Core.Diff;

/// <summary>
/// 精确重命名检测（不做相似度猜测）：
/// 1. 目录级：A 中前缀 X/ 下的全部条目与 D 中前缀 Y/ 下的条目在“去掉前缀后的相对路径 + 哈希”上一一对应且数量 ≥ 2；
/// 2. 文件级：(hash, size) 完全相同的一对一配对。
/// </summary>
public static class RenameMatcher
{
    public static List<SyncTask> Detect(List<SyncTask> tasks)
    {
        var consumed = new HashSet<SyncTask>(ReferenceEqualityComparer.Instance);
        var produced = new List<SyncTask>();

        DetectDirectoryRenames(tasks, consumed, produced);
        DetectFileRenames(tasks, consumed, produced);

        var result = new List<SyncTask>(tasks.Count);
        foreach (var task in tasks)
        {
            if (!consumed.Contains(task))
            {
                result.Add(task);
            }
        }

        result.AddRange(produced);
        return result;
    }

    private static void DetectDirectoryRenames(
        List<SyncTask> tasks,
        HashSet<SyncTask> consumed,
        List<SyncTask> produced)
    {
        while (true)
        {
            var dirDeletes = tasks
                .Where(t => !consumed.Contains(t) && t.Action == SyncAction.Delete && t.WantKind == EntryKind.Dir)
                .ToList();
            var dirCreates = tasks
                .Where(t => !consumed.Contains(t) && t.Action == SyncAction.Mkdir)
                .ToList();

            if (dirDeletes.Count == 0 || dirCreates.Count == 0)
            {
                return;
            }

            (SyncTask Delete, SyncTask Create, List<SyncTask> DeleteChildren, List<SyncTask> CreateChildren)? best = null;

            foreach (var delete in dirDeletes)
            {
                foreach (var create in dirCreates)
                {
                    if (string.Equals(delete.DstPath, create.DstPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var deleteChildren = tasks
                        .Where(t => !consumed.Contains(t)
                                    && t.Action == SyncAction.Delete
                                    && t.WantKind == EntryKind.File
                                    && PathKeys.IsUnder(t.DstPath, delete.DstPath))
                        .ToList();
                    var createChildren = tasks
                        .Where(t => !consumed.Contains(t)
                                    && t.Action == SyncAction.Upload
                                    && PathKeys.IsUnder(t.DstPath, create.DstPath))
                        .ToList();

                    if (deleteChildren.Count < 2 || deleteChildren.Count != createChildren.Count)
                    {
                        continue;
                    }

                    if (!SubtreeMatches(deleteChildren, createChildren, delete.DstPath, create.DstPath))
                    {
                        continue;
                    }

                    if (best is null || deleteChildren.Count > best.Value.DeleteChildren.Count)
                    {
                        best = (delete, create, deleteChildren, createChildren);
                    }
                }
            }

            if (best is null)
            {
                return;
            }

            var (dirDelete, dirCreate, deleteKids, createKids) = best.Value;
            consumed.Add(dirDelete);
            consumed.Add(dirCreate);
            foreach (var task in deleteKids)
            {
                consumed.Add(task);
            }

            foreach (var task in createKids)
            {
                consumed.Add(task);
            }

            produced.Add(new SyncTask
            {
                Action = SyncAction.Rename,
                SrcPath = dirDelete.DstPath,
                DstPath = dirCreate.DstPath,
                WantKind = EntryKind.Dir,
            });
        }
    }

    private static bool SubtreeMatches(
        List<SyncTask> deleteChildren,
        List<SyncTask> createChildren,
        string sourceRoot,
        string targetRoot)
    {
        var byRelativePath = new Dictionary<string, SyncTask>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in deleteChildren)
        {
            var relative = PathKeys.RelativeTo(task.DstPath, sourceRoot);
            if (relative.Length == 0 || !byRelativePath.TryAdd(relative, task))
            {
                return false;
            }
        }

        foreach (var task in createChildren)
        {
            var relative = PathKeys.RelativeTo(task.DstPath, targetRoot);
            if (relative.Length == 0 || !byRelativePath.TryGetValue(relative, out var counterpart))
            {
                return false;
            }

            if (counterpart.WantHash is null || task.WantHash is null)
            {
                return false;
            }

            if (!Hashes.Equal(counterpart.WantHash, task.WantHash))
            {
                return false;
            }

            if (counterpart.Size != 0 && task.Size != 0 && counterpart.Size != task.Size)
            {
                return false;
            }
        }

        return true;
    }

    private static void DetectFileRenames(
        List<SyncTask> tasks,
        HashSet<SyncTask> consumed,
        List<SyncTask> produced)
    {
        var deleteCandidates = tasks
            .Where(t => !consumed.Contains(t) && t.Action == SyncAction.Delete && t.WantKind == EntryKind.File && t.WantHash is not null)
            .ToList();
        var uploadCandidates = tasks
            .Where(t => !consumed.Contains(t) && t.Action == SyncAction.Upload && t.WantHash is not null)
            .ToList();

        if (deleteCandidates.Count == 0 || uploadCandidates.Count == 0)
        {
            return;
        }

        var bySignature = new Dictionary<(string Hash, long Size), Queue<SyncTask>>();
        foreach (var task in deleteCandidates)
        {
            var signature = (Hashes.ToHex(task.WantHash), task.Size);
            if (!bySignature.TryGetValue(signature, out var queue))
            {
                queue = new Queue<SyncTask>();
                bySignature[signature] = queue;
            }

            queue.Enqueue(task);
        }

        foreach (var upload in uploadCandidates)
        {
            var signature = (Hashes.ToHex(upload.WantHash), upload.Size);
            if (!bySignature.TryGetValue(signature, out var queue) || queue.Count == 0)
            {
                continue;
            }

            var delete = queue.Dequeue();
            if (consumed.Contains(delete) || consumed.Contains(upload))
            {
                continue;
            }

            consumed.Add(delete);
            consumed.Add(upload);
            produced.Add(upload with
            {
                Id = 0,
                Action = SyncAction.Rename,
                SrcPath = delete.DstPath,
                State = "pending",
                Attempts = 0,
                NextRetryAt = null,
                LastError = null,
            });
        }
    }
}
