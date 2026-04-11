using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;

namespace FolderSync.UI.ViewModels
{
    /// <summary>
    /// 任务列表 ViewModel
    /// </summary>
    public class TasksViewModel : ViewModelBase
    {
        private readonly Action<object?> _navigateAction;

        public ObservableCollection<TaskListItemViewModel> Tasks { get; } = new();

        public ICommand CreateNewTaskCommand { get; }
        public ICommand RunTaskCommand { get; }
        public ICommand EditTaskCommand { get; }
        public ICommand DeleteTaskCommand { get; }

        public TasksViewModel(Action<object?> navigateAction)
        {
            _navigateAction = navigateAction;

            CreateNewTaskCommand = new RelayCommand(_ => NavigateToEditor());
            RunTaskCommand = new RelayCommand(RunTask);
            EditTaskCommand = new RelayCommand(EditTask);
            DeleteTaskCommand = new RelayCommand(DeleteTask);

            LoadMockData();
        }

        private void NavigateToEditor(TaskListItemViewModel? taskToEdit = null)
        {
            // 跳转到任务编辑器，并传入返回的回调
            _navigateAction(new TaskEditorViewModel(() => _navigateAction(new TasksViewModel(_navigateAction))));
        }

        private void RunTask(object? parameter)
        {
            if (parameter is TaskListItemViewModel task)
            {
                // TODO: 触发 SchedulerManager 执行一次
                task.StatusIcon = "PlayCircleOutline";
                task.StatusColor = new SolidColorBrush(Colors.Green);
            }
        }

        private void EditTask(object? parameter)
        {
            if (parameter is TaskListItemViewModel task)
            {
                NavigateToEditor(task);
            }
        }

        private void DeleteTask(object? parameter)
        {
            if (parameter is TaskListItemViewModel task)
            {
                Tasks.Remove(task);
                // TODO: 从数据库/配置文件中删除，并取消 Scheduler 中的调度
            }
        }

        private void LoadMockData()
        {
            Tasks.Add(new TaskListItemViewModel
            {
                Id = Guid.NewGuid().ToString(),
                TaskName = "工作文档备份",
                SourcePath = @"C:\WorkData",
                DestPath = @"\\NAS\Backup\WorkData",
                SyncMode = "单向更新",
                ScheduleInfo = "计划: 每天 18:00 (Cron: 0 0 18 * * ?)",
                StatusIcon = "CheckCircleOutline",
                StatusColor = new SolidColorBrush(Colors.DeepSkyBlue)
            });

            Tasks.Add(new TaskListItemViewModel
            {
                Id = Guid.NewGuid().ToString(),
                TaskName = "网站静态资源发布",
                SourcePath = @"D:\Projects\Web\Dist",
                DestPath = @"ftp://192.168.1.10/public_html",
                SyncMode = "单向镜像",
                ScheduleInfo = "计划: 每隔 10 分钟",
                StatusIcon = "ClockOutline",
                StatusColor = new SolidColorBrush(Colors.Gray)
            });
        }
    }

    /// <summary>
    /// 任务列表中的单个项视图模型
    /// </summary>
    public class TaskListItemViewModel : ViewModelBase
    {
        public string Id { get; set; } = string.Empty;
        public string TaskName { get; set; } = string.Empty;
        public string SourcePath { get; set; } = string.Empty;
        public string DestPath { get; set; } = string.Empty;
        public string SyncMode { get; set; } = string.Empty;
        public string ScheduleInfo { get; set; } = string.Empty;

        private string _statusIcon = "CircleOutline";
        public string StatusIcon
        {
            get => _statusIcon;
            set => SetProperty(ref _statusIcon, value);
        }

        private Brush _statusColor = new SolidColorBrush(Colors.Gray);
        public Brush StatusColor
        {
            get => _statusColor;
            set => SetProperty(ref _statusColor, value);
        }
    }
}
