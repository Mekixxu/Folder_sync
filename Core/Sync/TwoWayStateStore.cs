using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace FolderSync.Core.Sync
{
    /// <summary>
    /// 双向同步状态基线存储（SQLite）。
    /// 记录上次同步完成后的 A/B 快照，用于可靠判定“删除/新增/冲突”。
    /// </summary>
    public class TwoWayStateStore
    {
        private readonly string _dbPath;
        private readonly string _connectionString;

        public TwoWayStateStore(string? dbPath = null)
        {
            var dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            Directory.CreateDirectory(dataDir);
            _dbPath = dbPath ?? Path.Combine(dataDir, "sync-state.db");
            _connectionString = $"Data Source={_dbPath}";
        }

        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(cancellationToken);
            var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                CREATE TABLE IF NOT EXISTS sync_state (
                    task_id TEXT NOT NULL,
                    side TEXT NOT NULL,
                    relative_path TEXT NOT NULL,
                    exists_flag INTEGER NOT NULL,
                    is_directory INTEGER NOT NULL,
                    size_bytes INTEGER NOT NULL,
                    last_write_utc TEXT NULL,
                    content_hash TEXT NULL,
                    PRIMARY KEY (task_id, side, relative_path)
                );
                """;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task<Dictionary<string, (StateSnapshot? A, StateSnapshot? B)>> LoadAsync(string taskId, CancellationToken cancellationToken = default)
        {
            var result = new Dictionary<string, (StateSnapshot? A, StateSnapshot? B)>(StringComparer.OrdinalIgnoreCase);

            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(cancellationToken);
            var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                SELECT side, relative_path, exists_flag, is_directory, size_bytes, last_write_utc, content_hash
                FROM sync_state
                WHERE task_id = $taskId;
                """;
            cmd.Parameters.AddWithValue("$taskId", taskId);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var side = reader.GetString(0);
                var path = reader.GetString(1);
                var snap = new StateSnapshot
                {
                    Exists = reader.GetInt64(2) == 1,
                    IsDirectory = reader.GetInt64(3) == 1,
                    Size = reader.GetInt64(4),
                    LastWriteUtc = reader.IsDBNull(5) ? null : DateTime.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
                    Hash = reader.IsDBNull(6) ? null : reader.GetString(6)
                };

                result.TryGetValue(path, out var pair);
                if (side == "A")
                {
                    pair.A = snap;
                }
                else
                {
                    pair.B = snap;
                }
                result[path] = pair;
            }

            return result;
        }

        public async Task SaveAsync(
            string taskId,
            IReadOnlyDictionary<string, StateSnapshot> sourceSnapshots,
            IReadOnlyDictionary<string, StateSnapshot> destSnapshots,
            CancellationToken cancellationToken = default)
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(cancellationToken);
            await using var tx = conn.BeginTransaction();

            var deleteCmd = conn.CreateCommand();
            deleteCmd.Transaction = tx;
            deleteCmd.CommandText = "DELETE FROM sync_state WHERE task_id = $taskId;";
            deleteCmd.Parameters.AddWithValue("$taskId", taskId);
            await deleteCmd.ExecuteNonQueryAsync(cancellationToken);

            foreach (var kv in sourceSnapshots)
            {
                await InsertSnapshotAsync(conn, tx, taskId, "A", kv.Key, kv.Value, cancellationToken);
            }
            foreach (var kv in destSnapshots)
            {
                await InsertSnapshotAsync(conn, tx, taskId, "B", kv.Key, kv.Value, cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
        }

        private static async Task InsertSnapshotAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            string taskId,
            string side,
            string path,
            StateSnapshot snap,
            CancellationToken cancellationToken)
        {
            var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                INSERT INTO sync_state (
                    task_id, side, relative_path, exists_flag, is_directory, size_bytes, last_write_utc, content_hash
                ) VALUES (
                    $taskId, $side, $path, $exists, $isDir, $size, $lastWrite, $hash
                );
                """;
            cmd.Parameters.AddWithValue("$taskId", taskId);
            cmd.Parameters.AddWithValue("$side", side);
            cmd.Parameters.AddWithValue("$path", path);
            cmd.Parameters.AddWithValue("$exists", snap.Exists ? 1 : 0);
            cmd.Parameters.AddWithValue("$isDir", snap.IsDirectory ? 1 : 0);
            cmd.Parameters.AddWithValue("$size", snap.Size);
            cmd.Parameters.AddWithValue("$lastWrite", snap.LastWriteUtc?.ToString("O") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$hash", snap.Hash ?? (object)DBNull.Value);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
