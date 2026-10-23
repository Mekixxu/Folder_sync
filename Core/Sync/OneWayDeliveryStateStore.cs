using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Core.VFS;
using Microsoft.Data.Sqlite;

namespace FolderSync.Core.Sync
{
    /// <summary>
    /// 单向一次性同步状态存储。
    /// 记录某任务下某相对路径是否已经成功投递过一次。
    /// </summary>
    public class OneWayDeliveryStateStore
    {
        private readonly string _dbPath;
        private readonly string _connectionString;

        public OneWayDeliveryStateStore(string? dbPath = null)
        {
            var dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            Directory.CreateDirectory(dataDir);
            _dbPath = dbPath ?? Path.Combine(dataDir, "one-way-delivery-state.db");
            _connectionString = $"Data Source={_dbPath}";
        }

        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(cancellationToken);
            var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                CREATE TABLE IF NOT EXISTS one_way_delivery_state (
                    task_id TEXT NOT NULL,
                    relative_path TEXT NOT NULL,
                    is_directory INTEGER NOT NULL,
                    source_size_bytes INTEGER NOT NULL,
                    source_last_write_utc TEXT NULL,
                    source_hash TEXT NULL,
                    delivered_utc TEXT NOT NULL,
                    PRIMARY KEY (task_id, relative_path)
                );
                """;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task<Dictionary<string, OneWayDeliveryRecord>> LoadAsync(string taskId, CancellationToken cancellationToken = default)
        {
            var result = new Dictionary<string, OneWayDeliveryRecord>(StringComparer.OrdinalIgnoreCase);

            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(cancellationToken);
            var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                SELECT relative_path, is_directory, source_size_bytes, source_last_write_utc, source_hash, delivered_utc
                FROM one_way_delivery_state
                WHERE task_id = $taskId;
                """;
            cmd.Parameters.AddWithValue("$taskId", taskId);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var record = new OneWayDeliveryRecord
                {
                    RelativePath = reader.GetString(0),
                    IsDirectory = reader.GetInt64(1) == 1,
                    SourceSize = reader.GetInt64(2),
                    SourceLastWriteUtc = reader.IsDBNull(3)
                        ? null
                        : DateTime.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind),
                    SourceHash = reader.IsDBNull(4) ? null : reader.GetString(4),
                    DeliveredUtc = DateTime.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind)
                };

                result[record.RelativePath] = record;
            }

            return result;
        }

        public async Task UpsertAsync(string taskId, OneWayDeliveryRecord record, CancellationToken cancellationToken = default)
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(cancellationToken);
            var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO one_way_delivery_state (
                    task_id, relative_path, is_directory, source_size_bytes, source_last_write_utc, source_hash, delivered_utc
                ) VALUES (
                    $taskId, $path, $isDirectory, $size, $lastWrite, $hash, $deliveredUtc
                )
                ON CONFLICT(task_id, relative_path) DO UPDATE SET
                    is_directory = excluded.is_directory,
                    source_size_bytes = excluded.source_size_bytes,
                    source_last_write_utc = excluded.source_last_write_utc,
                    source_hash = excluded.source_hash,
                    delivered_utc = excluded.delivered_utc;
                """;
            cmd.Parameters.AddWithValue("$taskId", taskId);
            cmd.Parameters.AddWithValue("$path", record.RelativePath);
            cmd.Parameters.AddWithValue("$isDirectory", record.IsDirectory ? 1 : 0);
            cmd.Parameters.AddWithValue("$size", record.SourceSize);
            cmd.Parameters.AddWithValue("$lastWrite", record.SourceLastWriteUtc?.ToString("O") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$hash", record.SourceHash ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$deliveredUtc", record.DeliveredUtc.ToString("O"));
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        /// <summary>
        /// 批量写入投递状态：单连接 + 单事务，避免对每条记录都开启连接。
        /// </summary>
        public async Task UpsertRangeAsync(string taskId, IReadOnlyCollection<OneWayDeliveryRecord> records, CancellationToken cancellationToken = default)
        {
            if (records.Count == 0)
            {
                return;
            }

            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken);
            var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText =
                """
                INSERT INTO one_way_delivery_state (
                    task_id, relative_path, is_directory, source_size_bytes, source_last_write_utc, source_hash, delivered_utc
                ) VALUES (
                    $taskId, $path, $isDirectory, $size, $lastWrite, $hash, $deliveredUtc
                )
                ON CONFLICT(task_id, relative_path) DO UPDATE SET
                    is_directory = excluded.is_directory,
                    source_size_bytes = excluded.source_size_bytes,
                    source_last_write_utc = excluded.source_last_write_utc,
                    source_hash = excluded.source_hash,
                    delivered_utc = excluded.delivered_utc;
                """;
            var pTaskId = cmd.Parameters.Add("$taskId", SqliteType.Text);
            var pPath = cmd.Parameters.Add("$path", SqliteType.Text);
            var pIsDirectory = cmd.Parameters.Add("$isDirectory", SqliteType.Integer);
            var pSize = cmd.Parameters.Add("$size", SqliteType.Integer);
            var pLastWrite = cmd.Parameters.Add("$lastWrite", SqliteType.Text);
            var pHash = cmd.Parameters.Add("$hash", SqliteType.Text);
            var pDeliveredUtc = cmd.Parameters.Add("$deliveredUtc", SqliteType.Text);

            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pTaskId.Value = taskId;
                pPath.Value = record.RelativePath;
                pIsDirectory.Value = record.IsDirectory ? 1 : 0;
                pSize.Value = record.SourceSize;
                pLastWrite.Value = record.SourceLastWriteUtc?.ToString("O") ?? (object)DBNull.Value;
                pHash.Value = record.SourceHash ?? (object)DBNull.Value;
                pDeliveredUtc.Value = record.DeliveredUtc.ToString("O");
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        public async Task ResetTaskAsync(string taskId, CancellationToken cancellationToken = default)
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(cancellationToken);
            var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM one_way_delivery_state WHERE task_id = $taskId;";
            cmd.Parameters.AddWithValue("$taskId", taskId);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public class OneWayDeliveryRecord
    {
        public string RelativePath { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
        public long SourceSize { get; set; }
        public DateTime? SourceLastWriteUtc { get; set; }
        public string? SourceHash { get; set; }
        public DateTime DeliveredUtc { get; set; } = DateTime.UtcNow;
    }

    public static class OneWayDeliverySupport
    {
        public static async Task<OneWayDeliveryRecord> CreateDeliveredRecordAsync(
            string path,
            FileItem sourceItem,
            IFileSystem sourceFs,
            CancellationToken cancellationToken = default)
        {
            return new OneWayDeliveryRecord
            {
                RelativePath = path,
                IsDirectory = sourceItem.IsDirectory,
                SourceSize = sourceItem.Size,
                SourceLastWriteUtc = sourceItem.LastWriteTime,
                SourceHash = sourceItem.IsDirectory ? null : await ComputeXxHash64Async(sourceFs, path, cancellationToken),
                DeliveredUtc = DateTime.UtcNow
            };
        }

        public static OneWayDeliveryRecord CreateDeliveredRecordFromCopy(
            string path,
            FileItem sourceItem,
            string? sourceHash)
        {
            return new OneWayDeliveryRecord
            {
                RelativePath = path,
                IsDirectory = sourceItem.IsDirectory,
                SourceSize = sourceItem.Size,
                SourceLastWriteUtc = sourceItem.LastWriteTime,
                SourceHash = sourceItem.IsDirectory ? null : sourceHash,
                DeliveredUtc = DateTime.UtcNow
            };
        }

        public static async Task<bool> HasSourceChangedAsync(
            OneWayDeliveryRecord deliveredRecord,
            FileItem currentSourceItem,
            IFileSystem sourceFs,
            CancellationToken cancellationToken = default)
        {
            if (deliveredRecord.IsDirectory || currentSourceItem.IsDirectory)
            {
                return false;
            }

            var sizeChanged = deliveredRecord.SourceSize != currentSourceItem.Size;
            var lastWriteChanged = deliveredRecord.SourceLastWriteUtc != currentSourceItem.LastWriteTime;
            if (!sizeChanged && !lastWriteChanged)
            {
                return false;
            }

            if (sizeChanged)
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(deliveredRecord.SourceHash))
            {
                return true;
            }

            var currentHash = await ComputeXxHash64Async(sourceFs, currentSourceItem.Path, cancellationToken);
            return !string.Equals(deliveredRecord.SourceHash, currentHash, StringComparison.OrdinalIgnoreCase);
        }

        public static async Task<string> ComputeXxHash64Async(
            IFileSystem fileSystem,
            string path,
            CancellationToken cancellationToken = default)
        {
            using var stream = await fileSystem.OpenReadAsync(path, cancellationToken);
            var hasher = new XxHash64();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                hasher.Append(buffer.AsSpan(0, read));
            }

            return Convert.ToHexString(hasher.GetCurrentHash());
        }

        public static Task<CopyFileResult> CopyFileAndComputeHashAsync(
            IFileSystem fromFs,
            IFileSystem toFs,
            string path,
            FileItem? sourceItem = null,
            CancellationToken cancellationToken = default)
        {
            return CopyFileCoreAsync(fromFs, toFs, path, sourceItem, computeHash: true, cancellationToken);
        }

        public static Task<CopyFileResult> CopyFileAsync(
            IFileSystem fromFs,
            IFileSystem toFs,
            string path,
            FileItem? sourceItem = null,
            CancellationToken cancellationToken = default)
        {
            return CopyFileCoreAsync(fromFs, toFs, path, sourceItem, computeHash: false, cancellationToken);
        }

        private static async Task<CopyFileResult> CopyFileCoreAsync(
            IFileSystem fromFs,
            IFileSystem toFs,
            string path,
            FileItem? sourceItem,
            bool computeHash,
            CancellationToken cancellationToken)
        {
            var tempPath = VfsPath.CreateSiblingTemporaryPath(path, "tmp");
            Stream? readStream = null;
            Stream? writeStream = null;

            try
            {
                // 先打开源：源不可读时目标仍然完全未被触碰。
                readStream = await fromFs.OpenReadForCopyAsync(path, cancellationToken);
                writeStream = await toFs.OpenWriteToTempAsync(tempPath, cancellationToken);

                var hasher = computeHash ? new XxHash64() : null;
                var buffer = new byte[81920];
                long bytesCopied = 0;
                int read;
                while ((read = await readStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                {
                    hasher?.Append(buffer.AsSpan(0, read));
                    await writeStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    bytesCopied += read;
                }

                await writeStream.FlushAsync(cancellationToken);

                // 必须在提交前关闭上传流；FTP 的最终响应/关闭失败也不能记成功。
                await writeStream.DisposeAsync();
                writeStream = null;
                await readStream.DisposeAsync();
                readStream = null;

                await toFs.CommitTempFileAsync(tempPath, path, cancellationToken);

                if (sourceItem != null && !sourceItem.IsDirectory)
                {
                    await TryPreserveLastWriteTimeAsync(toFs, path, sourceItem.LastWriteTime);
                }

                return new CopyFileResult(
                    bytesCopied,
                    hasher == null ? null : Convert.ToHexString(hasher.GetCurrentHash()));
            }
            catch (OperationCanceledException)
            {
                await TryCleanupTempAsync(toFs, tempPath);
                throw;
            }
            catch (Exception)
            {
                await TryCleanupTempAsync(toFs, tempPath);
                throw;
            }
            finally
            {
                if (writeStream != null)
                {
                    try { await writeStream.DisposeAsync(); } catch { /* 保留原始异常 */ }
                }
                if (readStream != null)
                {
                    try { await readStream.DisposeAsync(); } catch { /* 保留原始异常 */ }
                }
            }
        }

        private static async Task TryPreserveLastWriteTimeAsync(IFileSystem toFs, string path, DateTime lastWriteTime)
        {
            try
            {
                await toFs.SetLastWriteTimeAsync(path, lastWriteTime, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "复制成功但保留目标最后修改时间失败：{Path}", path);
            }
        }

        private static async Task TryCleanupTempAsync(IFileSystem toFs, string tempPath)
        {
            try
            {
                await toFs.CleanupTempFileAsync(tempPath, CancellationToken.None);
            }
            catch
            {
                // 清理应尽力而为，保留原始异常；绝不触碰最终目标。
            }
        }
    }

    public readonly record struct CopyFileResult(long BytesCopied, string? Hash);
}
