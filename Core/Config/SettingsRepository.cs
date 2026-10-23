using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using Serilog;

namespace FolderSync.Core.Config
{
    public class SettingsRepository
    {
        private static readonly ConcurrentDictionary<string, object> PathGates =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private readonly string _filePath;
        private readonly object _gate;

        public SettingsRepository(string? filePath = null)
        {
            var dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            Directory.CreateDirectory(dataDir);
            _filePath = Path.GetFullPath(filePath ?? Path.Combine(dataDir, "settings.json"));
            _gate = PathGates.GetOrAdd(_filePath, _ => new object());
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
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
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
                        Log.Warning(ex, "Failed to clean up temporary settings file {TempPath}", tempPath);
                    }
                }
            }
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
