using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Serilog;

namespace FolderSync.Core.Config
{
    public class TaskRepository
    {
        private readonly string _filePath;
        private readonly object _gate = new();
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public TaskRepository(string? filePath = null)
        {
            var dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            Directory.CreateDirectory(dataDir);
            _filePath = filePath ?? Path.Combine(dataDir, "tasks.json");
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

            try
            {
                var json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<List<SyncTaskDefinition>>(json, JsonOptions) ?? new List<SyncTaskDefinition>();
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                BackupCorruptFile();
                Log.Error(ex, "Failed to load task definitions from {FilePath}. Corrupt file backed up; returning empty list.", _filePath);
                return new List<SyncTaskDefinition>();
            }
        }

        private void AtomicWrite(string json)
        {
            var tempPath = _filePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _filePath, overwrite: true);
        }

        private void BackupCorruptFile()
        {
            try
            {
                var backupPath = _filePath + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}";
                File.Copy(_filePath, backupPath, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to back up corrupt file {FilePath}", _filePath);
            }
        }
    }
}