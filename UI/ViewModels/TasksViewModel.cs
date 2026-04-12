using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FolderSync.Core.Config;
using FolderSync.Core.Reporting;
using FolderSync.Core.Scheduler;
using FolderSync.Core.Sync;
using FolderSync.UI.Views;

namespace FolderSync.UI.ViewModels
{
    /// <summary>
    /// 任务列表 ViewModel
    /// </summary>
    public class TasksViewModel : ViewModelBase
    {
        private readonly Action<object?> _navigateAction;
        private readonly TaskRepository _taskRepository = new();
        private readonly TaskAnalysisService _analysisService;
        private readonly ObservableCollection<SyncTaskDefinition> _definitions = new();
        private bool _isBusy;
        private bool _isAnalysisProgressVisible;
        private double _analysisProgressValue;
        private double _analysisProgressMaximum = 1;
        private string _analysisStatusText = "未开始分析";

        public ObservableCollection<TaskListItemViewModel> Tasks { get; } = new();

        public ICommand CreateNewTaskCommand { get; }
        public ICommand AnalyzeSelectedTasksCommand { get; }
        public ICommand ExecuteSelectedTasksCommand { get; }
        public ICommand SyncSelectedTasksCommand { get; }
        public ICommand OpenTaskAnalysisCommand { get; }
        public ICommand EditTaskCommand { get; }
        public ICommand DeleteTaskCommand { get; }

        public bool IsAnalysisProgressVisible
        {
            get => _isAnalysisProgressVisible;
            set => SetProperty(ref _isAnalysisProgressVisible, value);
        }

        public double AnalysisProgressValue
        {
            get => _analysisProgressValue;
            set => SetProperty(ref _analysisProgressValue, value);
        }

        public double AnalysisProgressMaximum
        {
            get => _analysisProgressMaximum;
            set => SetProperty(ref _analysisProgressMaximum, value);
        }

        public string AnalysisStatusText
        {
            get => _analysisStatusText;
            set => SetProperty(ref _analysisStatusText, value);
        }

        public TasksViewModel(Action<object?> navigateAction)
        {
            _navigateAction = navigateAction;
            _analysisService = new TaskAnalysisService(_taskRepository);

            CreateNewTaskCommand = new RelayCommand(_ => NavigateToEditor());
            AnalyzeSelectedTasksCommand = new RelayCommand(_ => _ = AnalyzeSelectedTasksAsync(), _ => CanRunBulkActions());
            ExecuteSelectedTasksCommand = new RelayCommand(_ => _ = ExecuteSelectedTasksAsync(), _ => CanRunBulkActions());
            SyncSelectedTasksCommand = new RelayCommand(_ => _ = SyncSelectedTasksAsync(), _ => CanRunBulkActions());
            OpenTaskAnalysisCommand = new RelayCommand(OpenTaskAnalysis, CanOpenTaskAnalysis);
            EditTaskCommand = new RelayCommand(EditTask);
            DeleteTaskCommand = new RelayCommand(DeleteTask);

            LoadTasks();
        }

        private void NavigateToEditor(TaskListItemViewModel? taskToEdit = null)
        {
            var editDef = taskToEdit == null
                ? null
                : _definitions.FirstOrDefault(t => string.Equals(t.Id, taskToEdit.Id, StringComparison.OrdinalIgnoreCase));
            _navigateAction(new TaskEditorViewModel(() => _navigateAction(new TasksViewModel(_navigateAction)), editDef));
        }

        private bool CanRunBulkActions()
        {
            return !_isBusy && Tasks.Any(t => t.IsSelected);
        }

        private bool CanOpenTaskAnalysis(object? parameter)
        {
            return parameter is TaskListItemViewModel taskVm && taskVm.IsAnalysisCompleted;
        }

        private void OpenTaskAnalysis(object? parameter)
        {
            if (parameter is not TaskListItemViewModel taskVm || !taskVm.IsAnalysisCompleted)
            {
                return;
            }

            var def = FindDefinition(taskVm.Id);
            if (def == null)
            {
                MessageBox.Show("未找到任务定义。", "分析失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var vm = new TaskAnalysisViewModel(def, _analysisService, () => MarkTaskAnalysisCompleted(taskVm, true));
            var window = new TaskAnalysisWindow
            {
                DataContext = vm,
                Owner = Application.Current?.MainWindow
            };
            window.ShowDialog();
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
                try
                {
                    _taskRepository.DeleteById(task.Id);
                    _definitions.Remove(_definitions.First(t => t.Id == task.Id));
                    task.PropertyChanged -= TaskItemOnPropertyChanged;
                    Tasks.Remove(task);
                    SchedulerManager.Instance.RemoveJobAsync(task.Id).GetAwaiter().GetResult();
                    CommandManager.InvalidateRequerySuggested();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"删除任务失败：{ex.Message}", "删除失败", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void LoadTasks()
        {
            Tasks.Clear();
            _definitions.Clear();
            var all = _taskRepository.LoadAll();
            foreach (var def in all)
            {
                _definitions.Add(def);
                var vm = MapToListItem(def);
                vm.PropertyChanged += TaskItemOnPropertyChanged;
                Tasks.Add(vm);
            }
        }

        private static TaskListItemViewModel MapToListItem(SyncTaskDefinition def)
        {
            var hasSavedAnalysis = def.SavedAnalysisItems.Count > 0;
            return new TaskListItemViewModel
            {
                Id = def.Id,
                TaskName = def.TaskName,
                SourcePath = def.SourcePath,
                DestPath = def.DestPath,
                SyncMode = def.SyncMode.ToString(),
                ScheduleInfo = def.IsManualTrigger ? "计划: 手动触发" : $"计划: {SyncTaskFactory.ResolveCronExpression(def)}",
                IsAnalysisCompleted = hasSavedAnalysis
            };
        }

        private async Task AnalyzeSelectedTasksAsync()
        {
            var selected = GetSelectedTasks();
            if (selected.Count == 0 || _isBusy)
            {
                return;
            }

            _isBusy = true;
            try
            {
                IsAnalysisProgressVisible = true;
                AnalysisProgressMaximum = selected.Count;
                AnalysisProgressValue = 0;

                for (var i = 0; i < selected.Count; i++)
                {
                    var current = selected[i];
                    AnalysisStatusText = $"正在分析 ({i + 1}/{selected.Count})：{current.Definition.TaskName}";
                    var analysis = await _analysisService.AnalyzeAsync(current.Definition);
                    _analysisService.SaveAnalysis(current.Definition, analysis);
                    MarkTaskAnalysisCompleted(current.TaskVm, true);
                    AnalysisProgressValue = i + 1;
                }

                AnalysisStatusText = $"分析完成，共 {selected.Count} 个任务。";
            }
            catch (Exception ex)
            {
                AnalysisStatusText = "分析失败";
                MessageBox.Show($"批量分析失败：{ex.Message}", "分析失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isBusy = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private async Task ExecuteSelectedTasksAsync()
        {
            var selected = GetSelectedTasks();
            if (selected.Count == 0 || _isBusy)
            {
                return;
            }

            _isBusy = true;
            try
            {
                var reportCount = 0;
                foreach (var task in selected)
                {
                    var analysisItems = _analysisService.HasSavedAnalysis(task.Definition)
                        ? _analysisService.GetSavedAnalysis(task.Definition)
                        : await _analysisService.AnalyzeAsync(task.Definition);

                    if (!_analysisService.HasSavedAnalysis(task.Definition))
                    {
                        _analysisService.SaveAnalysis(task.Definition, analysisItems);
                    }

                    MarkTaskAnalysisCompleted(task.TaskVm, true);
                    var report = await _analysisService.ExecuteSelectedAsync(task.Definition, analysisItems);
                    SyncReportFileWriter.Write(task.Definition.Id, task.Definition.TaskName, report);
                    reportCount++;
                }

                MessageBox.Show($"批量执行完成，共处理 {reportCount} 个任务。", "执行完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"批量执行失败：{ex.Message}", "执行失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isBusy = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private async Task SyncSelectedTasksAsync()
        {
            var selected = GetSelectedTasks();
            if (selected.Count == 0 || _isBusy)
            {
                return;
            }

            _isBusy = true;
            try
            {
                IsAnalysisProgressVisible = true;
                AnalysisProgressMaximum = selected.Count;
                AnalysisProgressValue = 0;

                for (var i = 0; i < selected.Count; i++)
                {
                    var current = selected[i];
                    AnalysisStatusText = $"正在分析 ({i + 1}/{selected.Count})：{current.Definition.TaskName}";
                    var analysis = await _analysisService.AnalyzeAsync(current.Definition);
                    _analysisService.SaveAnalysis(current.Definition, analysis);
                    MarkTaskAnalysisCompleted(current.TaskVm, true);
                    AnalysisProgressValue = i + 1;
                }

                AnalysisStatusText = "分析完成，开始执行同步...";

                foreach (var task in selected)
                {
                    var analysisItems = _analysisService.GetSavedAnalysis(task.Definition);
                    var report = await _analysisService.ExecuteSelectedAsync(task.Definition, analysisItems);
                    SyncReportFileWriter.Write(task.Definition.Id, task.Definition.TaskName, report);
                }

                AnalysisStatusText = $"同步完成，共处理 {selected.Count} 个任务。";
                MessageBox.Show(AnalysisStatusText, "同步完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AnalysisStatusText = "同步失败";
                MessageBox.Show($"批量同步失败：{ex.Message}", "同步失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isBusy = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private List<SelectedTaskPair> GetSelectedTasks()
        {
            var selected = new List<SelectedTaskPair>();
            foreach (var taskVm in Tasks.Where(t => t.IsSelected))
            {
                var definition = FindDefinition(taskVm.Id);
                if (definition != null)
                {
                    selected.Add(new SelectedTaskPair(taskVm, definition));
                }
            }

            return selected;
        }

        private SyncTaskDefinition? FindDefinition(string taskId)
        {
            return _definitions.FirstOrDefault(t => string.Equals(t.Id, taskId, StringComparison.OrdinalIgnoreCase));
        }

        private static void MarkTaskAnalysisCompleted(TaskListItemViewModel taskVm, bool completed)
        {
            taskVm.IsAnalysisCompleted = completed;
        }

        private void TaskItemOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TaskListItemViewModel.IsSelected))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private sealed record SelectedTaskPair(TaskListItemViewModel TaskVm, SyncTaskDefinition Definition);
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

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        private bool _isAnalysisCompleted;
        public bool IsAnalysisCompleted
        {
            get => _isAnalysisCompleted;
            set
            {
                if (SetProperty(ref _isAnalysisCompleted, value))
                {
                    OnPropertyChanged(nameof(AnalysisStatusIcon));
                    OnPropertyChanged(nameof(AnalysisStatusColor));
                }
            }
        }

        public string AnalysisStatusIcon => IsAnalysisCompleted ? "CheckCircle" : "HelpCircleOutline";
        private static readonly Brush _analysisReadyBrush = new SolidColorBrush(Colors.ForestGreen);
        private static readonly Brush _analysisPendingBrush = new SolidColorBrush(Colors.Gray);
        public Brush AnalysisStatusColor
        {
            get => IsAnalysisCompleted ? _analysisReadyBrush : _analysisPendingBrush;
        }
    }
}
