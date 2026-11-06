using PetrelPush.Core.Model;

namespace PetrelPush.Core.Diff;

/// <summary>
/// 任务归一化（防“复活”）与阶段排序（父目录先行）。
/// </summary>
public static class TaskOrderer
{
    /// <summary>同路径只保留一个最终意图，并合并 rename 链。</summary>
    public static IReadOnlyList<SyncTask> Normalize(IReadOnlyList<SyncTask> tasks)
    {
        var list = new List<SyncTask>(tasks.Count);
        var indexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            var key = $"{task.Action}|{task.SrcPath}|{task.DstPath}";
            if (indexByKey.TryGetValue(key, out var existing))
            {
                // 同一动作的重复任务保留最后一个（最新意图）
                list[existing] = task;
            }
            else
            {
                indexByKey[key] = list.Count;
                list.Add(task);
            }
        }

        MergeRenameChains(list);

        // rename(a→b) + delete(b) → delete(a)
        var deleteKeys = new HashSet<string>(
            list.Where(t => t.Action == SyncAction.Delete).Select(t => t.DstKey),
            StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < list.Count; i++)
        {
            var task = list[i];
            if (task.Action == SyncAction.Rename && task.SrcPath is not null && deleteKeys.Contains(task.DstKey))
            {
                list[i] = new SyncTask
                {
                    Action = SyncAction.Delete,
                    DstPath = task.SrcPath,
                    WantKind = task.WantKind,
                    WantHash = task.WantHash,
                    Size = task.Size,
                };
            }
        }

        // 同目标冲突消解
        var result = new List<SyncTask>(list.Count);
        foreach (var group in list.GroupBy(t => t.DstKey, StringComparer.OrdinalIgnoreCase))
        {
            var delete = group.FirstOrDefault(t => t.Action == SyncAction.Delete);
            var upload = group.FirstOrDefault(t => t.Action == SyncAction.Upload);

            if (delete is not null && upload is not null)
            {
                // 内容与远端一致：删除后又出现同内容文件，两者都取消
                if (Hashes.Equal(delete.WantHash, upload.WantHash))
                {
                    continue;
                }

                result.Add(delete);
                continue;
            }

            if (group.Count() == 1)
            {
                result.Add(group.First());
                continue;
            }

            // 同一路径同类动作保留最后一个（最新意图）
            foreach (var actionGroup in group.GroupBy(t => t.Action))
            {
                result.Add(actionGroup.Last());
            }
        }

        return result;
    }

    /// <summary>
    /// 分阶段批次：Mkdir → Rename → Upload → Delete。
    /// 删除按深度降序（子先于父），其余按深度升序（父先于子）；大文件单独串行。
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<SyncTask>> BuildStages(
        IReadOnlyList<SyncTask> tasks,
        int batchSize,
        long largeFileThresholdBytes)
    {
        var size = Math.Max(1, batchSize);
        var stages = new List<IReadOnlyList<SyncTask>>();

        foreach (var action in new[] { SyncAction.Mkdir, SyncAction.Rename, SyncAction.Upload, SyncAction.Delete })
        {
            var stage = tasks.Where(t => t.Action == action).ToList();
            if (stage.Count == 0)
            {
                continue;
            }

            if (action == SyncAction.Delete)
            {
                stage.Sort(static (left, right) =>
                {
                    var depth = right.Depth.CompareTo(left.Depth);
                    return depth != 0 ? depth : string.CompareOrdinal(left.DstPath, right.DstPath);
                });
            }
            else
            {
                stage.Sort(static (left, right) =>
                {
                    var depth = left.Depth.CompareTo(right.Depth);
                    return depth != 0 ? depth : string.CompareOrdinal(left.DstPath, right.DstPath);
                });
            }

            if (action == SyncAction.Upload && largeFileThresholdBytes > 0)
            {
                var large = stage.Where(t => t.Size > largeFileThresholdBytes).ToList();
                var small = stage.Where(t => t.Size <= largeFileThresholdBytes).ToList();

                foreach (var chunk in small.Chunk(size))
                {
                    stages.Add(chunk);
                }

                foreach (var task in large)
                {
                    stages.Add([task]);
                }
            }
            else
            {
                foreach (var chunk in stage.Chunk(size))
                {
                    stages.Add(chunk);
                }
            }
        }

        return stages;
    }

    private static void MergeRenameChains(List<SyncTask> list)
    {
        var guard = 0;
        var changed = true;
        while (changed && guard++ < 16)
        {
            changed = false;
            for (var i = 0; i < list.Count && !changed; i++)
            {
                if (list[i].Action != SyncAction.Rename)
                {
                    continue;
                }

                for (var j = 0; j < list.Count; j++)
                {
                    if (i == j || list[j].Action != SyncAction.Rename || list[j].SrcPath is null)
                    {
                        continue;
                    }

                    if (!string.Equals(PathKeys.Key(list[i].DstPath), PathKeys.Key(list[j].SrcPath!), StringComparison.Ordinal))
                    {
                        continue;
                    }

                    list[i] = list[i] with
                    {
                        DstPath = list[j].DstPath,
                        WantHash = list[j].WantHash,
                        WantKind = list[j].WantKind,
                        Size = list[j].Size,
                    };
                    list.RemoveAt(j);
                    changed = true;
                    break;
                }
            }
        }
    }
}
