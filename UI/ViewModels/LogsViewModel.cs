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
    /// 日志查看器的视图模型
    /// </summary>
    public class LogsViewModel : ViewModelBase
    {
        private readonly string _logsDirectory;

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
                    LoadLogContent();
                }
            }
        }

        public bool IsLogSelected => SelectedLogFile != null;

        private string _logContent = string.Empty;
        public string LogContent
        {
            get => _logContent;
            set => SetProperty(ref _logContent, value);
        }

        public ICommand RefreshCommand { get; }
        public ICommand OpenInEditorCommand { get; }

        public LogsViewModel()
        {
            _logsDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            
            RefreshCommand = new RelayCommand(_ => LoadLogFiles());
            OpenInEditorCommand = new RelayCommand(_ => OpenSelectedLogFile(), _ => IsLogSelected);

            LoadLogFiles();
        }

        private void LoadLogFiles()
        {
            LogFiles.Clear();
            SelectedLogFile = null;
            LogContent = string.Empty;

            if (Directory.Exists(_logsDirectory))
            {
                var files = Directory.GetFiles(_logsDirectory, "*.txt")
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

        private void LoadLogContent()
        {
            if (SelectedLogFile != null && File.Exists(SelectedLogFile.FullPath))
            {
                try
                {
                    // 使用 FileShare.ReadWrite 打开以允许在写入日志时读取
                    using var stream = new FileStream(SelectedLogFile.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    LogContent = reader.ReadToEnd();
                }
                catch (Exception ex)
                {
                    LogContent = $"无法读取日志文件内容: {ex.Message}";
                    Log.Error(ex, "Failed to read log file {FileName}", SelectedLogFile.FileName);
                }
            }
            else
            {
                LogContent = string.Empty;
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
    }
}
