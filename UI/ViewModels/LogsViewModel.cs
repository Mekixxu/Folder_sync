using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Serilog;

namespace FolderSync.UI.ViewModels
{
    /// <summary>
    /// 日志文件列表视图模型，仅提供文件列表与外部打开入口。
    /// </summary>
    public class LogsViewModel : ViewModelBase
    {
        private readonly string _logDirectory;

        public ObservableCollection<LogFileItemViewModel> LogFiles { get; } = new();

        private LogFileItemViewModel? _selectedLogFile;
        public LogFileItemViewModel? SelectedLogFile
        {
            get => _selectedLogFile;
            set
            {
                if (SetProperty(ref _selectedLogFile, value))
                {
                    OnPropertyChanged(nameof(IsLogSelected));
                    OnPropertyChanged(nameof(SelectedLogTitle));
                    OnPropertyChanged(nameof(SelectedLogPath));
                }
            }
        }

        public bool IsLogSelected => SelectedLogFile != null;
        public string LogDirectory => _logDirectory;
        public string SelectedLogTitle => SelectedLogFile?.FileName ?? "请选择一个日志文件";
        public string SelectedLogPath => SelectedLogFile?.FullPath ?? Path.Combine(_logDirectory, "<日志文件名>");

        public ICommand RefreshCommand { get; }
        public ICommand OpenInEditorCommand { get; }

        public LogsViewModel()
        {
            _logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log");
            
            RefreshCommand = new RelayCommand(_ => LoadLogFiles());
            OpenInEditorCommand = new RelayCommand(_ => OpenSelectedLogFile(), _ => IsLogSelected);

            LoadLogFiles();
        }

        private void LoadLogFiles()
        {
            LogFiles.Clear();
            SelectedLogFile = null;

            if (Directory.Exists(_logDirectory))
            {
                var files = Directory.EnumerateFiles(_logDirectory)
                    .Where(f =>
                    {
                        var extension = Path.GetExtension(f);
                        return string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(extension, ".log", StringComparison.OrdinalIgnoreCase);
                    })
                    .Select(f => new FileInfo(f))
                    .Select(f => new LogFileItemViewModel
                    {
                        FileName = f.Name,
                        FullPath = f.FullName,
                        LastModified = f.LastWriteTime,
                        Kind = DetectLogKind(f.FullName)
                    })
                    .OrderBy(f => f.KindPriority)
                    .ThenByDescending(f => f.LastModified)
                    .ToList();

                foreach (var file in files)
                {
                    LogFiles.Add(file);
                }
            }
        }

        private void OpenSelectedLogFile()
        {
            if (SelectedLogFile != null && File.Exists(SelectedLogFile.FullPath))
            {
                try
                {
                    // 使用系统默认文本编辑器打开日志文件
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = SelectedLogFile.FullPath,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to open log file in external editor");
                }
            }
        }

        private string DetectLogKind(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                var firstLine = reader.ReadLine() ?? string.Empty;
                if (firstLine.StartsWith("TaskName:", StringComparison.OrdinalIgnoreCase))
                {
                    return "Report";
                }
            }
            catch
            {
                // ignore read failures for classification
            }

            return "Runtime";
        }
    }

    /// <summary>
    /// 日志文件列表项视图模型
    /// </summary>
    public class LogFileItemViewModel : ViewModelBase
    {
        public string FileName { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public DateTime LastModified { get; set; }
        public string Kind { get; set; } = "Runtime";
        public int KindPriority => string.Equals(Kind, "Report", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        public string FileExtension => Path.GetExtension(FileName).TrimStart('.').ToUpperInvariant();
    }
}
