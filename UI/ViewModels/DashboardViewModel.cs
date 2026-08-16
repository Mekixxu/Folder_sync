using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FolderSync.Core.Config;

namespace FolderSync.UI.ViewModels
{
    /// <summary>
    /// Dashboard 的视图模型
    /// </summary>
    public class DashboardViewModel : ViewModelBase
    {
        private int _activeTaskCount;
        public int ActiveTaskCount
        {
            get => _activeTaskCount;
            set => SetProperty(ref _activeTaskCount, value);
        }

        private int _todaySyncCount;
        public int TodaySyncCount
        {
            get => _todaySyncCount;
            set => SetProperty(ref _todaySyncCount, value);
        }

        private int _todayErrorCount;
        public int TodayErrorCount
        {
            get => _todayErrorCount;
            set => SetProperty(ref _todayErrorCount, value);
        }

        public ObservableCollection<TaskItemViewModel> ActiveTasks { get; } = new();

        public DashboardViewModel()
        {
            _ = LoadDashboardAsync();
        }

        private async Task LoadDashboardAsync()
        {
            try
            {
                var (activeTaskCount, activeTasks, todaySyncCount, todayErrorCount) = await Task.Run(LoadDashboardSnapshot);
                ActiveTaskCount = activeTaskCount;
                TodaySyncCount = todaySyncCount;
                TodayErrorCount = todayErrorCount;
                ActiveTasks.Clear();
                foreach (var task in activeTasks)
                {
                    ActiveTasks.Add(task);
                }
            }
            catch (Exception ex)
            {
                // 统计失败不应导致页面崩溃，回退为空数据
                System.Diagnostics.Debug.WriteLine($"Dashboard load failed: {ex}");
            }
        }

        private static (int ActiveTaskCount, System.Collections.Generic.List<TaskItemViewModel> Tasks, int TodaySyncCount, int TodayErrorCount) LoadDashboardSnapshot()
        {
            var repository = new TaskRepository();
            var allTasks = repository.LoadAll();
            var activeTasks = allTasks.Where(t => !t.IsManualTrigger).ToList();

            var tasks = activeTasks
                .Select(t => new TaskItemViewModel
                {
                    TaskName = t.TaskName,
                    SourcePath = t.SourcePath,
                    DestinationPath = t.DestPath,
                    Status = "已调度"
                })
                .ToList();

            var logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log");
            var todayReports = new System.Collections.Generic.List<FileInfo>();
            if (Directory.Exists(logDirectory))
            {
                todayReports = Directory.EnumerateFiles(logDirectory, "*.txt")
                    .Select(f => new FileInfo(f))
                    .Where(f => f.LastWriteTime.Date == DateTime.Today)
                    .ToList();
            }

            var todaySyncCount = 0;
            var todayErrorCount = 0;
            foreach (var report in todayReports)
            {
                try
                {
                    var content = File.ReadAllText(report.FullName);
                    var actionMatch = Regex.Match(
                        content,
                        @"Actions: total=\d+, create=(\d+), update=(\d+), delete=(\d+), skippedDelivered=\d+, failed=(\d+)");
                    if (actionMatch.Success)
                    {
                        todaySyncCount += int.Parse(actionMatch.Groups[1].Value)
                                           + int.Parse(actionMatch.Groups[2].Value)
                                           + int.Parse(actionMatch.Groups[3].Value);
                        if (int.Parse(actionMatch.Groups[4].Value) > 0)
                        {
                            todayErrorCount++;
                        }
                    }
                    else if (content.Contains("ErrorDetails:", StringComparison.Ordinal))
                    {
                        todayErrorCount++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to read report {report.FullName}: {ex.Message}");
                }
            }

            return (activeTasks.Count, tasks, todaySyncCount, todayErrorCount);
        }
    }

    /// <summary>
    /// 简化的任务条目 ViewModel
    /// </summary>
    public class TaskItemViewModel : ViewModelBase
    {
        public string TaskName { get; set; } = string.Empty;
        public string SourcePath { get; set; } = string.Empty;
        public string DestinationPath { get; set; } = string.Empty;
        public string Status { get; set; } = "Idle";
        
        private double _progressPercentage;
        public double ProgressPercentage
        {
            get => _progressPercentage;
            set => SetProperty(ref _progressPercentage, value);
        }
    }
}