using System;
using System.IO;
using System.Text.Json;
using Serilog;

namespace FolderSync.Core.Config
{
    public class SettingsRepository
    {
        private readonly string _filePath;
        private readonly object _gate = new();
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public SettingsRepository(string? filePath = null)
        {
            var dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            Directory.CreateDirectory(dataDir);
            _filePath = filePath ?? Path.Combine(dataDir, "settings.json");
        }

        public AppSettings Load()
        {
            lock (_gate)
            {
                if (!File.Exists(_filePath))
                {
                    return new AppSettings();
                }

                try
                {
                    var json = File.ReadAllText(_filePath);
                    return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                    BackupCorruptFile();
                    Log.Error(ex, "Failed to load settings from {FilePath}. Corrupt file backed up; using defaults.", _filePath);
                    return new AppSettings();
                }
            }
        }

        public void Save(AppSettings settings)
        {
            lock (_gate)
            {
                var json = JsonSerializer.Serialize(settings, JsonOptions);
                AtomicWrite(json);
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