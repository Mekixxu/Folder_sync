using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FolderSync.Core.Config;
using FolderSync.Core.Reporting;
using FolderSync.Core.Scheduler;
using FolderSync.Core.Sync;
using FolderSync.UI.Services;
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
        private readonly OneWayDeliveryStateStore _deliveryStateStore = new();
        private readonly ObservableCollection<SyncTaskDefinition> _definitions = new();
        private CancellationTokenSource? _currentOperationCts;
        private bool _isBusy;
        private bool _isAnalysisProgressVisible;
        private double _analysisProgressValue;
        private double _analysisProgressMaximum = 1;
        private bool _isAnalysisProgressIndeterminate = true;
        private string _analysisStatusText = "未开始分析";
        private string _currentOperationDisplayName = string.Empty;

        public ObservableCollection<TaskListItemViewModel> Tasks { get; } = new();

        public ICommand CreateNewTaskCommand { get; }
        public ICommand AnalyzeSelectedTasksCommand { get; }
        public ICommand ExecuteSelectedTasksCommand { get; }
        public ICommand SyncSelectedTasksCommand { get; }
        public ICommand StopCurrentOperationCommand { get; }
        public ICommand OpenTaskAnalysisCommand { get; }
        public ICommand EditTaskCommand { get; }
        public ICommand DeleteTaskCommand { get; }
        public ICommand ResetSendOnceStateCommand { get; }
        public ICommand RunNowCommand { get; }

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

        public bool IsAnalysisProgressIndeterminate
        {
            get => _isAnalysisProgressIndeterminate;
            set => SetProperty(ref _isAnalysisProgressIndeterminate, value);
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
            StopCurrentOperationCommand = new RelayCommand(_ => StopCurrentOperation(), _ => CanStopCurrentOperation());
            OpenTaskAnalysisCommand = new RelayCommand(OpenTaskAnalysis, CanOpenTaskAnalysis);
            EditTaskCommand = new RelayCommand(EditTask);
            DeleteTaskCommand = new RelayCommand(async _ => await DeleteTaskAsync(_));
            ResetSendOnceStateCommand = new RelayCommand(async _ => await ResetSendOnceStateAsync(_));
            RunNowCommand = new RelayCommand(async _ => await RunNowAsync(_));

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

        private bool CanStopCurrentOperation()
        {
            return _isBusy && _currentOperationCts != null && !_currentOperationCts.IsCancellationRequested;
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
                MessageDialogService.ShowError("Msg.TaskNotFound", "Title.AnalysisFailed");
                return;
            }

            OpenTaskAnalysisWindow(taskVm, def);
        }

        private void OpenTaskAnalysisWindow(TaskListItemViewModel taskVm, SyncTaskDefinition def)
        {
            var vm = new TaskAnalysisViewModel(def, _analysisService, () => MarkTaskAnalysisCompleted(taskVm, true));
            var window = new TaskAnalysisWindow
            {
                DataContext = vm,
                Owner = System.Windows.Application.Current?.MainWindow
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

        private async Task DeleteTaskAsync(object? parameter)
        {
            if (parameter is TaskListItemViewModel task)
            {
                var confirmed = MessageDialogService.Confirm("Msg.ConfirmDeleteTask", "Title.ConfirmDelete", task.TaskName);
                if (!confirmed)
                {
                    return;
                }

                try
                {
                    await SchedulerManager.Instance.RemoveJobAsync(task.Id);
                    _taskRepository.DeleteById(task.Id);
                    _definitions.Remove(_definitions.First(t => t.Id == task.Id));
                    task.PropertyChanged -= TaskItemOnPropertyChanged;
                    Tasks.Remove(task);
                    await _deliveryStateStore.InitializeAsync();
                    await _deliveryStateStore.ResetTaskAsync(task.Id);
                    CommandManager.InvalidateRequerySuggested();
                }
                catch (Exception ex)
                {
                    MessageDialogService.ShowError("Msg.DeleteTaskFailed", "Title.DeleteFailed", ex.Message);
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
                SyncMode = FormatSyncMode(def.SyncMode),
                ScheduleInfo = def.IsManualTrigger ? "计划: 手动触发" : $"计划: {ResolveCronExpressionSafe(def)}",
                IsAnalysisCompleted = hasSavedAnalysis,
                IsOneWaySendOnce = def.SyncMode == SyncMode.OneWaySendOnce
            };
        }

        private static string ResolveCronExpressionSafe(SyncTaskDefinition def)
        {
            try
            {
                return SyncTaskFactory.ResolveCronExpression(def);
            }
            catch (Exception)
            {
                // 历史遗留的非法调度配置不应阻断任务列表显示，仅展示原始配置文本。
                return string.IsNullOrWhiteSpace(def.CronExpression) ? "配置无效" : def.CronExpression.Trim();
            }
        }

        private static string FormatSyncMode(SyncMode mode)
        {
            return mode switch
            {
                SyncMode.OneWayIncremental => "单向增量",
                SyncMode.OneWayUpdate => "单向更新",
                SyncMode.OneWaySendOnce => "单向一次性同步",
                SyncMode.OneWayMirror => "单向镜像",
                SyncMode.TwoWay => "双向同步",
                _ => mode.ToString()
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
            using var operationCts = BeginOperation("分析");
            try
            {
                var token = operationCts.Token;
                IsAnalysisProgressVisible = true;
                ResetAnalysisProgressDisplay();

                for (var i = 0; i < selected.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var current = selected[i];
                    AnalysisStatusText = $"正在分析 ({i + 1}/{selected.Count})：{current.Definition.TaskName}";
                    var analysis = await AnalyzeTaskAsync(current.Definition, token);
                    _analysisService.SaveAnalysis(current.Definition, analysis);
                    MarkTaskAnalysisCompleted(current.TaskVm, true);
                }

                AnalysisStatusText = selected.Count == 1
                    ? "分析完成，已打开文件列表。"
                    : $"分析完成，共 {selected.Count} 个任务。点击任务左侧状态图标查看文件列表。";
                CommandManager.InvalidateRequerySuggested();

                if (selected.Count == 1)
                {
                    OpenTaskAnalysisWindow(selected[0].TaskVm, selected[0].Definition);
                }
            }
            catch (OperationCanceledException)
            {
                AnalysisStatusText = "分析已停止。";
                MessageDialogService.ShowInfo("Msg.AnalysisStopped", "Title.Stopped");
            }
            catch (Exception ex)
            {
                AnalysisStatusText = "分析失败";
                MessageDialogService.ShowError("Msg.BatchAnalysisFailed", "Title.AnalysisFailed", ex.Message);
            }
            finally
            {
                _isBusy = false;
                EndOperation();
                IsAnalysisProgressVisible = false;
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
            using var operationCts = BeginOperation("执行");
            try
            {
                var token = operationCts.Token;
                IsAnalysisProgressVisible = true;
                IsAnalysisProgressIndeterminate = false;
                AnalysisProgressMaximum = selected.Count;
                AnalysisProgressValue = 0;
                var reportCount = 0;
                var reportFileNames = new List<string>();
                for (var i = 0; i < selected.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var task = selected[i];
                    AnalysisStatusText = $"正在执行 ({i + 1}/{selected.Count})：{task.Definition.TaskName}";
                    var analysisItems = _analysisService.HasSavedAnalysis(task.Definition)
                        ? _analysisService.GetSavedAnalysis(task.Definition)
                        : await AnalyzeTaskAsync(task.Definition, token);

                    if (!_analysisService.HasSavedAnalysis(task.Definition))
                    {
                        _analysisService.SaveAnalysis(task.Definition, analysisItems);
                    }

                    MarkTaskAnalysisCompleted(task.TaskVm, true);
                    var report = await ExecuteSelectedItemsAsync(task.Definition, analysisItems, token);
                    var reportPath = SyncReportFileWriter.Write(task.Definition.Id, task.Definition.TaskName, report);
                    reportFileNames.Add(Path.GetFileName(reportPath));
                    reportCount++;
                    AnalysisProgressValue = i + 1;
                }

                AnalysisStatusText = $"批量执行完成，共处理 {reportCount} 个任务。";
                MessageDialogService.ShowInfo("Msg.ExecuteComplete", "Title.ExecuteComplete", reportCount, BuildReportPreview(reportFileNames), BuildReportMoreSuffix(reportFileNames));
            }
            catch (OperationCanceledException)
            {
                AnalysisStatusText = "执行已停止。";
                MessageDialogService.ShowInfo("Msg.ExecuteStopped", "Title.Stopped");
            }
            catch (Exception ex)
            {
                MessageDialogService.ShowError("Msg.BatchExecuteFailed", "Title.ExecuteFailed", ex.Message);
            }
            finally
            {
                _isBusy = false;
                EndOperation();
                IsAnalysisProgressVisible = false;
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
            using var operationCts = BeginOperation("同步");
            try
            {
                var token = operationCts.Token;
                IsAnalysisProgressVisible = true;
                ResetAnalysisProgressDisplay();
                var reportFileNames = new List<string>();

                for (var i = 0; i < selected.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var current = selected[i];
                    AnalysisStatusText = $"正在分析 ({i + 1}/{selected.Count})：{current.Definition.TaskName}";
                    var analysis = await AnalyzeTaskAsync(current.Definition, token);
                    _analysisService.SaveAnalysis(current.Definition, analysis);
                    MarkTaskAnalysisCompleted(current.TaskVm, true);
                }

                AnalysisStatusText = "分析完成，开始执行同步...";
                IsAnalysisProgressIndeterminate = false;
                AnalysisProgressMaximum = selected.Count;
                AnalysisProgressValue = 0;

                for (var i = 0; i < selected.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var task = selected[i];
                    AnalysisStatusText = $"正在同步 ({i + 1}/{selected.Count})：{task.Definition.TaskName}";
                    var analysisItems = _analysisService.GetSavedAnalysis(task.Definition);
                    var report = await ExecuteSelectedItemsAsync(task.Definition, analysisItems, token);
                    var reportPath = SyncReportFileWriter.Write(task.Definition.Id, task.Definition.TaskName, report);
                    reportFileNames.Add(Path.GetFileName(reportPath));
                    AnalysisProgressValue = i + 1;
                }

                AnalysisStatusText = $"同步完成，共处理 {selected.Count} 个任务。";
                MessageDialogService.ShowInfo("Msg.SyncComplete", "Title.SyncComplete", selected.Count, BuildReportPreview(reportFileNames), BuildReportMoreSuffix(reportFileNames));
            }
            catch (OperationCanceledException)
            {
                AnalysisStatusText = "同步已停止。";
                MessageDialogService.ShowInfo("Msg.SyncStopped", "Title.Stopped");
            }
            catch (Exception ex)
            {
                AnalysisStatusText = "同步失败";
                MessageDialogService.ShowError("Msg.BatchSyncFailed", "Title.SyncFailed", ex.Message);
            }
            finally
            {
                _isBusy = false;
                EndOperation();
                IsAnalysisProgressVisible = false;
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

        private Task<List<TaskAnalysisItem>> AnalyzeTaskAsync(
            SyncTaskDefinition definition,
            CancellationToken cancellationToken)
        {
            return Task.Run(
                async () => await _analysisService.AnalyzeAsync(definition, cancellationToken),
                cancellationToken);
        }

        private Task<SyncReport> ExecuteSelectedItemsAsync(
            SyncTaskDefinition definition,
            IEnumerable<TaskAnalysisItem> analysisItems,
            CancellationToken cancellationToken)
        {
            var items = analysisItems.ToList();
            return Task.Run(async () => await _analysisService.ExecuteSelectedAsync(definition, items, cancellationToken), cancellationToken);
        }

        private CancellationTokenSource BeginOperation(string operationDisplayName)
        {
            EndOperation();
            _currentOperationDisplayName = operationDisplayName;
            _currentOperationCts = new CancellationTokenSource();
            CommandManager.InvalidateRequerySuggested();
            return _currentOperationCts;
        }

        private void EndOperation()
        {
            _currentOperationCts?.Dispose();
            _currentOperationCts = null;
            _currentOperationDisplayName = string.Empty;
            CommandManager.InvalidateRequerySuggested();
        }

        private void StopCurrentOperation()
        {
            if (_currentOperationCts == null || _currentOperationCts.IsCancellationRequested)
            {
                return;
            }

            _currentOperationCts.Cancel();
            AnalysisStatusText = string.IsNullOrWhiteSpace(_currentOperationDisplayName)
                ? "正在停止当前操作..."
                : $"正在停止{_currentOperationDisplayName}...";
            CommandManager.InvalidateRequerySuggested();
        }

        private SyncTaskDefinition? FindDefinition(string taskId)
        {
            return _definitions.FirstOrDefault(t => string.Equals(t.Id, taskId, StringComparison.OrdinalIgnoreCase));
        }

        private void ResetAnalysisProgressDisplay()
        {
            AnalysisProgressValue = 0;
            AnalysisProgressMaximum = 1;
            IsAnalysisProgressIndeterminate = true;
        }

        private static void MarkTaskAnalysisCompleted(TaskListItemViewModel taskVm, bool completed)
        {
            taskVm.IsAnalysisCompleted = completed;
        }

        private static string BuildReportPreview(IReadOnlyList<string> reportFileNames)
        {
            if (reportFileNames.Count == 0)
            {
                return string.Empty;
            }

            return string.Join(Environment.NewLine, reportFileNames.Take(5).Select(name => $"- {name}"));
        }

        private static string BuildReportMoreSuffix(IReadOnlyList<string> reportFileNames)
        {
            if (reportFileNames.Count <= 5)
            {
                return string.Empty;
            }

            return MessageDialogService.GetString("Msg.ReportMoreFiles", reportFileNames.Count - 5);
        }

        private void TaskItemOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TaskListItemViewModel.IsSelected))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private async Task RunNowAsync(object? parameter)
        {
            if (parameter is not TaskListItemViewModel taskVm)
            {
                return;
            }

            var definition = FindDefinition(taskVm.Id);
            if (definition == null)
            {
                MessageDialogService.ShowError("Msg.TaskNotFound", "Title.ExecuteFailed");
                return;
            }

            if (_isBusy)
            {
                MessageDialogService.ShowInfo("Msg.BusyRunning", "Title.Unavailable");
                return;
            }

            _isBusy = true;
            using var operationCts = BeginOperation("立即运行");
            try
            {
                var token = operationCts.Token;
                IsAnalysisProgressVisible = true;
                ResetAnalysisProgressDisplay();
                AnalysisStatusText = $"正在分析：{definition.TaskName}";

                var analysis = await AnalyzeTaskAsync(definition, token);
                if (analysis.Count == 0)
                {
                    MessageDialogService.ShowInfo("Msg.NothingToSync", "Title.NothingToSync");
                    AnalysisStatusText = "分析完成，无待同步内容。";
                    return;
                }

                _analysisService.SaveAnalysis(definition, analysis);
                MarkTaskAnalysisCompleted(taskVm, true);
                CommandManager.InvalidateRequerySuggested();

                AnalysisStatusText = $"正在执行：{definition.TaskName}";
                IsAnalysisProgressIndeterminate = false;
                AnalysisProgressMaximum = 1;
                AnalysisProgressValue = 0;

                var report = await ExecuteSelectedItemsAsync(definition, analysis, token);
                var reportPath = SyncReportFileWriter.Write(definition.Id, definition.TaskName, report);
                var reportFileName = Path.GetFileName(reportPath);

                AnalysisStatusText = $"执行完成：{definition.TaskName}";
                MessageDialogService.ShowInfo("Msg.RunNowComplete", "Title.ExecuteComplete", definition.TaskName, reportFileName);
            }
            catch (OperationCanceledException)
            {
                AnalysisStatusText = "执行已停止。";
                MessageDialogService.ShowInfo("Msg.ExecuteStopped", "Title.Stopped");
            }
            catch (Exception ex)
            {
                AnalysisStatusText = "执行失败";
                MessageDialogService.ShowError("Msg.RunNowFailed", "Title.ExecuteFailed", ex.Message);
            }
            finally
            {
                _isBusy = false;
                EndOperation();
                IsAnalysisProgressVisible = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private async Task ResetSendOnceStateAsync(object? parameter)
        {
            if (parameter is not TaskListItemViewModel taskVm)
            {
                return;
            }

            var definition = FindDefinition(taskVm.Id);
            if (definition == null || definition.SyncMode != SyncMode.OneWaySendOnce)
            {
                MessageDialogService.ShowInfo("Msg.ResetUnavailable", "Title.Unavailable");
                return;
            }

            var confirmed = MessageDialogService.Confirm("Msg.ConfirmResetSendOnce", "Title.ConfirmReset", definition.TaskName);

            if (!confirmed)
            {
                return;
            }

            try
            {
                await _deliveryStateStore.InitializeAsync();
                await _deliveryStateStore.ResetTaskAsync(definition.Id);
                definition.SavedAnalysisItems.Clear();
                definition.AnalysisSavedAtUtc = null;
                _taskRepository.Upsert(definition);
                taskVm.IsAnalysisCompleted = false;
                MessageDialogService.ShowInfo("Msg.ResetDone", "Title.ResetDone");
            }
            catch (Exception ex)
            {
                MessageDialogService.ShowError("Msg.ResetFailed", "Title.ResetFailed", ex.Message);
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
        public bool IsOneWaySendOnce { get; set; }

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
        private static readonly System.Windows.Media.Brush _analysisReadyBrush = new SolidColorBrush(Colors.ForestGreen);
        private static readonly System.Windows.Media.Brush _analysisPendingBrush = new SolidColorBrush(Colors.Gray);
        public System.Windows.Media.Brush AnalysisStatusColor
        {
            get => IsAnalysisCompleted ? _analysisReadyBrush : _analysisPendingBrush;
        }
    }
}
