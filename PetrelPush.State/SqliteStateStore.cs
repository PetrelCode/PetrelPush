using System.Globalization;
using Microsoft.Data.Sqlite;
using Serilog;
using PetrelPush.Core.Abstractions;
using PetrelPush.Core.Model;

namespace PetrelPush.State;

/// <summary>
/// SQLite 状态库：file_entry（快照 + 远端已知态）、sync_task（任务队列）、sync_meta（KV）。
/// 单文件、WAL、批量事务；打开时做 quick_check，损坏则备份重建（幂等全量重传）。
/// </summary>
public sealed class SqliteStateStore : IStateStore
{
    private const string SchemaVersion = "1";

    private const string UpsertEntrySql = """
        INSERT INTO file_entry (path, path_key, kind, size, mtime_ticks, hash_sha256, updated_at)
        VALUES ($path, $key, $kind, $size, $mtime, $hash, $now)
        ON CONFLICT(path_key) DO UPDATE SET
            path = excluded.path,
            kind = excluded.kind,
            size = excluded.size,
            mtime_ticks = excluded.mtime_ticks,
            hash_sha256 = excluded.hash_sha256,
            updated_at = excluded.updated_at;
        """;

    private const string InsertTaskSql = """
        INSERT INTO sync_task
            (created_at, action, src_path, src_key, dst_path, dst_key, want_hash, want_kind,
             state, attempts, next_retry_at, last_error, updated_at)
        VALUES
            ($created, $action, $src, $srcKey, $dst, $dstKey, $hash, $kind,
             'pending', 0, NULL, NULL, $created);
        """;

    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _initialized;

    public SqliteStateStore(string databasePath, ILogger logger)
    {
        _databasePath = Path.GetFullPath(databasePath);
        _logger = logger;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    public string DatabasePath => _databasePath;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _initialized) == 1)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA synchronous=NORMAL;", cancellationToken).ConfigureAwait(false);

            if (!await IsHealthyAsync(connection, cancellationToken).ConfigureAwait(false))
            {
                _logger.Error("状态库校验失败，备份后重建：{Path}", _databasePath);
                await connection.CloseAsync().ConfigureAwait(false);
                await connection.DisposeAsync().ConfigureAwait(false);
                BackupCorruptDatabase();
                connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
            }

            await CreateSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                "UPDATE sync_task SET state = 'pending', updated_at = $now WHERE state = 'running';",
                cancellationToken,
                ("$now", Iso(DateTimeOffset.UtcNow))).ConfigureAwait(false);
            await SetMetaInternalAsync(connection, "schema_version", SchemaVersion, cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref _initialized, 1);
        }
        finally
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyDictionary<string, FileEntry>> LoadEntriesAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, FileEntry>(StringComparer.Ordinal);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT path, kind, size, mtime_ticks, hash_sha256, synced_hash, synced_kind
            FROM file_entry;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var path = reader.GetString(0);
            var kind = (EntryKind)reader.GetInt32(1);
            var size = reader.GetInt64(2);
            var mtime = reader.GetInt64(3);
            var hash = reader.IsDBNull(4) ? null : (byte[])reader.GetValue(4);
            var syncedHash = reader.IsDBNull(5) ? null : (byte[])reader.GetValue(5);
            var syncedKind = reader.IsDBNull(6) ? (EntryKind?)null : (EntryKind)reader.GetInt32(6);

            var entry = new FileEntry(path, kind, size, mtime, hash, syncedHash, syncedKind);
            result[entry.PathKey] = entry;
        }

        return result;
    }

    public async Task<bool> HasRunnableTasksAsync(DateTimeOffset now, int maxAttempts, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM sync_task
                WHERE attempts < $max
                  AND (state = 'pending'
                       OR (state = 'failed' AND (next_retry_at IS NULL OR next_retry_at <= $now)))
            );
            """;
        command.Parameters.AddWithValue("$now", Iso(now));
        command.Parameters.AddWithValue("$max", Math.Max(1, maxAttempts));
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture) == 1;
    }

    public async Task<IReadOnlyList<SyncTask>> LoadRunnableTasksAsync(
        DateTimeOffset now,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        var result = new List<SyncTask>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, action, src_path, dst_path, want_hash, want_kind, state, attempts, next_retry_at, last_error
            FROM sync_task
            WHERE attempts < $max
              AND (state = 'pending'
                   OR (state = 'failed' AND (next_retry_at IS NULL OR next_retry_at <= $now)))
            ORDER BY id;
            """;
        command.Parameters.AddWithValue("$now", Iso(now));
        command.Parameters.AddWithValue("$max", Math.Max(1, maxAttempts));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new SyncTask
            {
                Id = reader.GetInt64(0),
                Action = SyncActionExtensions.Parse(reader.GetString(1)),
                SrcPath = reader.IsDBNull(2) ? null : reader.GetString(2),
                DstPath = reader.GetString(3),
                WantHash = reader.IsDBNull(4) ? null : (byte[])reader.GetValue(4),
                WantKind = (EntryKind)reader.GetInt32(5),
                State = reader.GetString(6),
                Attempts = reader.GetInt32(7),
                NextRetryAt = reader.IsDBNull(8) ? null : ParseIso(reader.GetString(8)),
                LastError = reader.IsDBNull(9) ? null : reader.GetString(9),
            });
        }

        return result;
    }

    public async Task CommitScanAsync(
        IReadOnlyList<ScannedEntry> scan,
        IReadOnlyList<SyncTask> tasks,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();

            await using (var setup = connection.CreateCommand())
            {
                setup.Transaction = transaction;
                setup.CommandText = """
                    CREATE TEMP TABLE IF NOT EXISTS _scan_key (path_key TEXT PRIMARY KEY);
                    DELETE FROM _scan_key;
                    """;
                await setup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var upsert = connection.CreateCommand())
            {
                upsert.Transaction = transaction;
                upsert.CommandText = UpsertEntrySql;
                var pPath = upsert.Parameters.Add("$path", SqliteType.Text);
                var pKey = upsert.Parameters.Add("$key", SqliteType.Text);
                var pKind = upsert.Parameters.Add("$kind", SqliteType.Integer);
                var pSize = upsert.Parameters.Add("$size", SqliteType.Integer);
                var pMtime = upsert.Parameters.Add("$mtime", SqliteType.Integer);
                var pHash = upsert.Parameters.Add("$hash", SqliteType.Blob);
                var pNow = upsert.Parameters.Add("$now", SqliteType.Text);
                pNow.Value = Iso(DateTimeOffset.UtcNow);

                foreach (var entry in scan)
                {
                    pPath.Value = entry.Path;
                    pKey.Value = entry.PathKey;
                    pKind.Value = (int)entry.Kind;
                    pSize.Value = entry.Size;
                    pMtime.Value = entry.MtimeTicks;
                    pHash.Value = (object?)entry.Hash ?? DBNull.Value;
                    await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            await using (var insertKey = connection.CreateCommand())
            {
                insertKey.Transaction = transaction;
                insertKey.CommandText = "INSERT OR IGNORE INTO _scan_key (path_key) VALUES ($key);";
                var pKey = insertKey.Parameters.Add("$key", SqliteType.Text);
                foreach (var entry in scan)
                {
                    pKey.Value = entry.PathKey;
                    await insertKey.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            await using (var cleanup = connection.CreateCommand())
            {
                cleanup.Transaction = transaction;
                cleanup.CommandText = """
                    DELETE FROM file_entry
                    WHERE synced_kind IS NULL
                      AND path_key NOT IN (SELECT path_key FROM _scan_key);
                    """;
                await cleanup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // 任务对账：意图相同的旧任务保留（不丢重试次数与退避），
            // 本轮未覆盖的过时任务作废（防止“本地已恢复原状、旧删除任务仍执行”）。
            var activeTasks = await LoadActiveTasksAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

            var newSignatures = new HashSet<string>(StringComparer.Ordinal);
            foreach (var task in tasks)
            {
                newSignatures.Add(IntentSignature(task.Action, task.SrcKey, task.DstKey, task.WantHash, task.WantKind));
            }

            var oldSignatures = new HashSet<string>(StringComparer.Ordinal);
            foreach (var active in activeTasks)
            {
                oldSignatures.Add(IntentSignature(active.Action, active.SrcKey, active.DstKey, active.WantHash, active.WantKind));
            }

            await using (var deleteById = connection.CreateCommand())
            {
                deleteById.Transaction = transaction;
                deleteById.CommandText = "DELETE FROM sync_task WHERE id = $id;";
                var pId = deleteById.Parameters.Add("$id", SqliteType.Integer);

                foreach (var active in activeTasks)
                {
                    var signature = IntentSignature(active.Action, active.SrcKey, active.DstKey, active.WantHash, active.WantKind);
                    if (newSignatures.Contains(signature))
                    {
                        continue;
                    }

                    pId.Value = active.Id;
                    await deleteById.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            if (tasks.Count > 0)
            {
                await using var deleteOld = connection.CreateCommand();
                deleteOld.Transaction = transaction;
                deleteOld.CommandText = "DELETE FROM sync_task WHERE dst_key = $dst AND state IN ('pending', 'failed');";
                var pOldKey = deleteOld.Parameters.Add("$dst", SqliteType.Text);

                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = InsertTaskSql;
                var pCreated = insert.Parameters.Add("$created", SqliteType.Text);
                var pAction = insert.Parameters.Add("$action", SqliteType.Text);
                var pSrc = insert.Parameters.Add("$src", SqliteType.Text);
                var pSrcKey = insert.Parameters.Add("$srcKey", SqliteType.Text);
                var pDst = insert.Parameters.Add("$dst", SqliteType.Text);
                var pDstKey = insert.Parameters.Add("$dstKey", SqliteType.Text);
                var pHash = insert.Parameters.Add("$hash", SqliteType.Blob);
                var pKind = insert.Parameters.Add("$kind", SqliteType.Integer);
                pCreated.Value = Iso(DateTimeOffset.UtcNow);

                foreach (var task in tasks)
                {
                    var signature = IntentSignature(task.Action, task.SrcKey, task.DstKey, task.WantHash, task.WantKind);
                    if (oldSignatures.Contains(signature))
                    {
                        continue;
                    }

                    pOldKey.Value = task.DstKey;
                    await deleteOld.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                    pAction.Value = task.Action.ToDbString();
                    pSrc.Value = (object?)task.SrcPath ?? DBNull.Value;
                    pSrcKey.Value = (object?)task.SrcKey ?? DBNull.Value;
                    pDst.Value = task.DstPath;
                    pDstKey.Value = task.DstKey;
                    pHash.Value = (object?)task.WantHash ?? DBNull.Value;
                    pKind.Value = (int)task.WantKind;
                    await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            await using (var cleanupDone = connection.CreateCommand())
            {
                cleanupDone.Transaction = transaction;
                cleanupDone.CommandText = "DELETE FROM sync_task WHERE state = 'done' AND updated_at < $cutoff;";
                cleanupDone.Parameters.AddWithValue("$cutoff", Iso(DateTimeOffset.UtcNow.AddDays(-1)));
                await cleanupDone.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task MarkTaskRunningAsync(long id, CancellationToken cancellationToken)
    {
        await ExecuteWriteAsync(
            "UPDATE sync_task SET state = 'running', updated_at = $now WHERE id = $id;",
            cancellationToken,
            ("$id", id),
            ("$now", Iso(DateTimeOffset.UtcNow))).ConfigureAwait(false);
    }

    public async Task MarkTaskDoneAsync(SyncTask task, byte[]? actualHash, CancellationToken cancellationToken)
    {
        var now = Iso(DateTimeOffset.UtcNow);
        var hash = actualHash ?? task.WantHash;

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();

            switch (task.Action)
            {
                case SyncAction.Upload:
                    await ExecuteAsync(
                        connection,
                        "UPDATE file_entry SET synced_hash = $hash, synced_kind = $kind, updated_at = $now WHERE path_key = $key;",
                        cancellationToken,
                        transaction,
                        ("$hash", (object?)hash ?? DBNull.Value),
                        ("$kind", (int)EntryKind.File),
                        ("$now", now),
                        ("$key", task.DstKey)).ConfigureAwait(false);
                    break;

                case SyncAction.Mkdir:
                    await ExecuteAsync(
                        connection,
                        "UPDATE file_entry SET synced_hash = NULL, synced_kind = $kind, updated_at = $now WHERE path_key = $key;",
                        cancellationToken,
                        transaction,
                        ("$kind", (int)EntryKind.Dir),
                        ("$now", now),
                        ("$key", task.DstKey)).ConfigureAwait(false);
                    break;

                case SyncAction.Delete:
                    await ExecuteAsync(
                        connection,
                        "DELETE FROM file_entry WHERE path_key = $key;",
                        cancellationToken,
                        transaction,
                        ("$key", task.DstKey)).ConfigureAwait(false);
                    break;

                case SyncAction.Rename:
                    await ApplyRenameAsync(connection, transaction, task, hash, now, cancellationToken).ConfigureAwait(false);
                    break;
            }

            if (task.Id > 0)
            {
                await ExecuteAsync(
                    connection,
                    "DELETE FROM sync_task WHERE id = $id;",
                    cancellationToken,
                    transaction,
                    ("$id", task.Id)).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task MarkTaskFailedAsync(
        long id,
        int attempts,
        DateTimeOffset nextRetryAt,
        string error,
        CancellationToken cancellationToken)
    {
        await ExecuteWriteAsync(
            """
            UPDATE sync_task
            SET state = 'failed', attempts = $attempts, next_retry_at = $next, last_error = $error, updated_at = $now
            WHERE id = $id;
            """,
            cancellationToken,
            ("$attempts", attempts),
            ("$next", Iso(nextRetryAt)),
            ("$error", Truncate(error, 2000)),
            ("$now", Iso(DateTimeOffset.UtcNow)),
            ("$id", id)).ConfigureAwait(false);
    }

    public async Task ResetFailedTasksAsync(CancellationToken cancellationToken)
    {
        await ExecuteWriteAsync(
            "UPDATE sync_task SET state = 'pending', attempts = 0, next_retry_at = NULL, last_error = NULL, updated_at = $now WHERE state = 'failed';",
            cancellationToken,
            ("$now", Iso(DateTimeOffset.UtcNow))).ConfigureAwait(false);
    }

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();

            await using (var clearTasks = connection.CreateCommand())
            {
                clearTasks.Transaction = transaction;
                clearTasks.CommandText = "DELETE FROM sync_task;";
                await clearTasks.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var clearEntries = connection.CreateCommand())
            {
                clearEntries.Transaction = transaction;
                clearEntries.CommandText = "DELETE FROM file_entry;";
                await clearEntries.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<int> CountTasksAsync(string state, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM sync_task WHERE state = $state;";
        command.Parameters.AddWithValue("$state", state);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    public async Task<string?> GetMetaAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM sync_meta WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? null : Convert.ToString(result, CultureInfo.InvariantCulture);
    }

    public async Task SetMetaAsync(string key, string value, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await SetMetaInternalAsync(connection, key, value, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _writeGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task ApplyRenameAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncTask task,
        byte[]? hash,
        string now,
        CancellationToken cancellationToken)
    {
        if (task.SrcPath is null)
        {
            return;
        }

        var srcKey = task.SrcKey!;
        var dstKey = task.DstKey;

        if (string.Equals(srcKey, dstKey, StringComparison.Ordinal))
        {
            // 仅大小写改名：同一行，改 path 即可（子项路径前缀大小写随之变化，逐行修正）
            await ExecuteAsync(
                connection,
                "UPDATE file_entry SET path = $path, updated_at = $now WHERE path_key = $key;",
                cancellationToken,
                transaction,
                ("$path", task.DstPath),
                ("$now", now),
                ("$key", srcKey)).ConfigureAwait(false);

            if (task.WantKind == EntryKind.Dir)
            {
                await ExecuteAsync(
                    connection,
                    """
                    UPDATE file_entry
                    SET path = $dstPath || substr(path, $srcLen + 1), updated_at = $now
                    WHERE substr(path_key, 1, $srcLen) = $srcKey
                      AND substr(path_key, $srcLen + 1, 1) = '/';
                    """,
                    cancellationToken,
                    transaction,
                    ("$dstPath", task.DstPath),
                    ("$now", now),
                    ("$srcKey", srcKey),
                    ("$srcLen", srcKey.Length)).ConfigureAwait(false);
            }

            return;
        }

        if (task.WantKind == EntryKind.Dir)
        {
            await ApplyDirectoryRenameAsync(connection, transaction, task, srcKey, dstKey, hash, now, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var dstHasSynced = false;
        await using (var probe = connection.CreateCommand())
        {
            probe.Transaction = transaction;
            probe.CommandText = "SELECT 1 FROM file_entry WHERE path_key = $key AND synced_kind IS NOT NULL;";
            probe.Parameters.AddWithValue("$key", dstKey);
            var value = await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            dstHasSynced = value is not null and not DBNull;
        }

        if (dstHasSynced)
        {
            // 目标位置远端已有内容（被 rename 覆盖）：目标行承接同步状态，源行丢弃
            await ExecuteAsync(
                connection,
                "UPDATE file_entry SET synced_hash = $hash, synced_kind = $kind, updated_at = $now WHERE path_key = $key;",
                cancellationToken,
                transaction,
                ("$hash", (object?)hash ?? DBNull.Value),
                ("$kind", (int)task.WantKind),
                ("$now", now),
                ("$key", dstKey)).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                "DELETE FROM file_entry WHERE path_key = $key;",
                cancellationToken,
                transaction,
                ("$key", srcKey)).ConfigureAwait(false);
            return;
        }

        await ExecuteAsync(
            connection,
            "DELETE FROM file_entry WHERE path_key = $key;",
            cancellationToken,
            transaction,
            ("$key", dstKey)).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            """
            UPDATE file_entry
            SET path = $path, path_key = $dstKey, synced_hash = $hash, synced_kind = $kind, updated_at = $now
            WHERE path_key = $srcKey;
            """,
            cancellationToken,
            transaction,
            ("$path", task.DstPath),
            ("$dstKey", dstKey),
            ("$hash", (object?)hash ?? DBNull.Value),
            ("$kind", (int)task.WantKind),
            ("$now", now),
            ("$srcKey", srcKey)).ConfigureAwait(false);
    }

    /// <summary>
    /// 目录改名成功：远端整棵子树已搬走，file_entry 中源前缀下的所有行必须级联迁移，
    /// 否则下一轮会把“旧子路径消失 / 新子路径不存在”误判成删除 + 重传。
    /// </summary>
    private async Task ApplyDirectoryRenameAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncTask task,
        string srcKey,
        string dstKey,
        byte[]? hash,
        string now,
        CancellationToken cancellationToken)
    {
        var nested = dstKey.StartsWith(srcKey + "/", StringComparison.Ordinal)
                     || srcKey.StartsWith(dstKey + "/", StringComparison.Ordinal);

        if (nested)
        {
            // 目标与源互相包含（极罕见）：只迁移目录行，子项交后续周期逐条修正
            await ExecuteAsync(
                connection,
                "DELETE FROM file_entry WHERE path_key = $dstKey;",
                cancellationToken,
                transaction,
                ("$dstKey", dstKey)).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                """
                UPDATE file_entry
                SET path = $path, path_key = $dstKey, synced_hash = $hash, synced_kind = $kind, updated_at = $now
                WHERE path_key = $srcKey;
                """,
                cancellationToken,
                transaction,
                ("$path", task.DstPath),
                ("$dstKey", dstKey),
                ("$hash", (object?)hash ?? DBNull.Value),
                ("$kind", (int)EntryKind.Dir),
                ("$now", now),
                ("$srcKey", srcKey)).ConfigureAwait(false);
            return;
        }

        var srcLen = srcKey.Length;
        var dstLen = dstKey.Length;

        // ① 远端目标子树已被源覆盖：丢弃目标侧所有行（含本地新建的占位行）
        await ExecuteAsync(
            connection,
            """
            DELETE FROM file_entry
            WHERE path_key = $dstKey
               OR (substr(path_key, 1, $dstLen) = $dstKey AND substr(path_key, $dstLen + 1, 1) = '/');
            """,
            cancellationToken,
            transaction,
            ("$dstKey", dstKey),
            ("$dstLen", dstLen)).ConfigureAwait(false);

        // ② 目录行迁移
        await ExecuteAsync(
            connection,
            """
            UPDATE file_entry
            SET path = $path, path_key = $dstKey, synced_hash = $hash, synced_kind = $kind, updated_at = $now
            WHERE path_key = $srcKey;
            """,
            cancellationToken,
            transaction,
            ("$path", task.DstPath),
            ("$dstKey", dstKey),
            ("$hash", (object?)hash ?? DBNull.Value),
            ("$kind", (int)EntryKind.Dir),
            ("$now", now),
            ("$srcKey", srcKey)).ConfigureAwait(false);

        // ③ 子项级联迁移（保留各自 synced_hash / synced_kind）
        await ExecuteAsync(
            connection,
            """
            UPDATE file_entry
            SET path = $path || substr(path, $srcLen + 1),
                path_key = $dstKey || substr(path_key, $srcLen + 1),
                updated_at = $now
            WHERE substr(path_key, 1, $srcLen) = $srcKey
              AND substr(path_key, $srcLen + 1, 1) = '/';
            """,
            cancellationToken,
            transaction,
            ("$path", task.DstPath),
            ("$dstKey", dstKey),
            ("$srcKey", srcKey),
            ("$srcLen", srcLen),
            ("$now", now)).ConfigureAwait(false);
    }

    private static async Task<List<ActiveTask>> LoadActiveTasksAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var result = new List<ActiveTask>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, action, src_key, dst_key, want_hash, want_kind
            FROM sync_task
            WHERE state IN ('pending', 'failed');
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new ActiveTask(
                reader.GetInt64(0),
                SyncActionExtensions.Parse(reader.GetString(1)),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : (byte[])reader.GetValue(4),
                (EntryKind)reader.GetInt32(5)));
        }

        return result;
    }

    private static string IntentSignature(SyncAction action, string? srcKey, string dstKey, byte[]? hash, EntryKind kind) =>
        $"{action.ToDbString()}|{srcKey}|{dstKey}|{Hashes.ToHex(hash)}|{(int)kind}";

    private static async Task CreateSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        const string schema = """
            CREATE TABLE IF NOT EXISTS file_entry (
                path         TEXT NOT NULL,
                path_key     TEXT NOT NULL PRIMARY KEY,
                kind         INTEGER NOT NULL,
                size         INTEGER NOT NULL DEFAULT 0,
                mtime_ticks  INTEGER NOT NULL DEFAULT 0,
                hash_sha256  BLOB,
                synced_hash  BLOB,
                synced_kind  INTEGER,
                updated_at   TEXT NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_file_entry_path ON file_entry(path);
            CREATE INDEX IF NOT EXISTS idx_entry_hash ON file_entry(hash_sha256);

            CREATE TABLE IF NOT EXISTS sync_task (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                created_at    TEXT NOT NULL,
                action        TEXT NOT NULL CHECK (action IN ('mkdir','upload','delete','rename')),
                src_path      TEXT,
                src_key       TEXT,
                dst_path      TEXT NOT NULL,
                dst_key       TEXT NOT NULL,
                want_hash     BLOB,
                want_kind     INTEGER NOT NULL,
                state         TEXT NOT NULL DEFAULT 'pending',
                attempts      INTEGER NOT NULL DEFAULT 0,
                next_retry_at TEXT,
                last_error    TEXT,
                updated_at    TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_task_state ON sync_task(state, next_retry_at);
            CREATE INDEX IF NOT EXISTS idx_task_dst ON sync_task(dst_key);

            CREATE TABLE IF NOT EXISTS sync_meta (
                key   TEXT PRIMARY KEY,
                value TEXT
            );
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = schema;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SetMetaInternalAsync(
        SqliteConnection connection,
        string key,
        string value,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sync_meta (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ExecuteWriteAsync(
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, sql, cancellationToken, null, parameters).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await ExecuteAsync(connection, sql, cancellationToken, null, parameters).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> IsHealthyAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return string.Equals(Convert.ToString(value, CultureInfo.InvariantCulture), "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private void BackupCorruptDatabase()
    {
        var suffix = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        try
        {
            if (File.Exists(_databasePath))
            {
                File.Move(_databasePath, $"{_databasePath}.corrupt-{suffix}", overwrite: true);
            }

            foreach (var sidecar in new[] { _databasePath + "-wal", _databasePath + "-shm" })
            {
                if (File.Exists(sidecar))
                {
                    File.Delete(sidecar);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "备份损坏状态库失败，将尝试直接重建：{Path}", _databasePath);
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseIso(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private sealed record ActiveTask(
        long Id,
        SyncAction Action,
        string? SrcKey,
        string DstKey,
        byte[]? WantHash,
        EntryKind WantKind);
}
