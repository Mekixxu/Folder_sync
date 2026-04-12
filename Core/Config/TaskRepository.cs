using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FolderSync.Core.Config
{
    public class TaskRepository
    {
        private readonly string _filePath;
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public TaskRepository(string? filePath = null)
        {
            var dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            Directory.CreateDirectory(dataDir);
            _filePath = filePath ?? Path.Combine(dataDir, "tasks.json");
        }

        public List<SyncTaskDefinition> LoadAll()
        {
            if (!File.Exists(_filePath))
            {
                return new List<SyncTaskDefinition>();
            }

            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<List<SyncTaskDefinition>>(json, JsonOptions) ?? new List<SyncTaskDefinition>();
        }

        public void SaveAll(IEnumerable<SyncTaskDefinition> tasks)
        {
            var json = JsonSerializer.Serialize(tasks.ToList(), JsonOptions);
            File.WriteAllText(_filePath, json);
        }

        public void Upsert(SyncTaskDefinition task)
        {
            var tasks = LoadAll();
            var index = tasks.FindIndex(t => string.Equals(t.Id, task.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                tasks[index] = task;
            }
            else
            {
                tasks.Add(task);
            }

            SaveAll(tasks);
        }

        public void DeleteById(string id)
        {
            var tasks = LoadAll();
            tasks.RemoveAll(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
            SaveAll(tasks);
        }
    }
}
