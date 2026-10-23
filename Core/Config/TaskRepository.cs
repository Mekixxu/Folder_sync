using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Serilog;

namespace FolderSync.Core.Config
{
    public class TaskRepository
    {
        private static readonly ConcurrentDictionary<string, object> PathGates =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private readonly string _filePath;
        private readonly object _gate;

        public TaskRepository(string? filePath = null)
        {
            var dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            Directory.CreateDirectory(dataDir);
            _filePath = Path.GetFullPath(filePath ?? Path.Combine(dataDir, "tasks.json"));
            _gate = PathGates.GetOrAdd(_filePath, _ => new object());
        }

        public List<SyncTaskDefinition> LoadAll()
        {
            lock (_gate)
            {
                return LoadAllCore();
            }
        }

        public void SaveAll(IEnumerable<SyncTaskDefinition> tasks)
        {
            lock (_gate)
            {
                var json = JsonSerializer.Serialize(tasks.ToList(), JsonOptions);
                AtomicWrite(json);
            }
        }

        public void Upsert(SyncTaskDefinition task)
        {
            lock (_gate)
            {
                var tasks = LoadAllCore();
                var index = tasks.FindIndex(t => string.Equals(t.Id, task.Id, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    tasks[index] = task;
                }
                else
                {
                    tasks.Add(task);
                }

                var json = JsonSerializer.Serialize(tasks, JsonOptions);
                AtomicWrite(json);
            }
        }

        /// <summary>
        /// 仅更新任务的分析字段。任务已被删除时返回 false，避免旧分析把已删除任务重新创建出来。
        /// </summary>
        public bool UpdateAnalysisSnapshot(string taskId, IEnumerable<SavedTaskAnalysisItem> items, DateTime? savedAtUtc)
        {
            lock (_gate)
            {
                var tasks = LoadAllCore();
                var task = tasks.FirstOrDefault(t => string.Equals(t.Id, taskId, StringComparison.OrdinalIgnoreCase));
                if (task == null)
                {
                    return false;
                }

                task.SavedAnalysisItems = items.ToList();
                task.AnalysisSavedAtUtc = savedAtUtc;
                var json = JsonSerializer.Serialize(tasks, JsonOptions);
                AtomicWrite(json);
                return true;
            }
        }

        public void DeleteById(string id)
        {
            lock (_gate)
            {
                var tasks = LoadAllCore();
                tasks.RemoveAll(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

                var json = JsonSerializer.Serialize(tasks, JsonOptions);
                AtomicWrite(json);
            }
        }

        private List<SyncTaskDefinition> LoadAllCore()
        {
            if (!File.Exists(_filePath))
            {
                return new List<SyncTaskDefinition>();
            }

            string json;
            try
            {
                json = File.ReadAllText(_filePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 暂时性读取失败不能退化为空列表，否则后续 Upsert 会覆盖原文件。
                throw new IOException($"无法读取任务配置：{_filePath}", ex);
            }

            try
            {
                return JsonSerializer.Deserialize<List<SyncTaskDefinition>>(json, JsonOptions) ?? new List<SyncTaskDefinition>();
            }
            catch (JsonException ex)
            {
                var backupPath = BackupCorruptFile();
                throw new InvalidDataException(
                    $"任务配置文件已损坏，已备份到 {backupPath ?? "(备份失败)"}，未覆盖原文件。",
                    ex);
            }
        }

        private void AtomicWrite(string json)
        {
            var tempPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                File.Move(tempPath, _filePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Failed to clean up temporary task file {TempPath}", tempPath);
                    }
                }
            }
        }

        private string? BackupCorruptFile()
        {
            try
            {
                var backupPath = _filePath + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}";
                File.Copy(_filePath, backupPath, overwrite: true);
                Log.Error("Task definitions file was corrupt and backed up to {BackupPath}", backupPath);
                return backupPath;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to back up corrupt file {FilePath}", _filePath);
                return null;
            }
        }
    }
}
