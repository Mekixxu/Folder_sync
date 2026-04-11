using System.Collections.ObjectModel;

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

        // 模拟绑定的任务列表数据模型
        public ObservableCollection<TaskItemViewModel> ActiveTasks { get; } = new();

        public DashboardViewModel()
        {
            // 初始化模拟数据
            ActiveTaskCount = 0;
            TodaySyncCount = 0;
            TodayErrorCount = 0;
            
            // 可以添加一些假数据用于 UI 预览
            // ActiveTasks.Add(new TaskItemViewModel { TaskName = "Backup to NAS", SourcePath = @"C:\Data", DestinationPath = @"\\NAS\Backup", Status = "Running", ProgressPercentage = 45 });
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
