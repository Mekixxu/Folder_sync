using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Core.Config;
using FolderSync.Core.Reporting;
using FolderSync.Core.Sync;
using FolderSync.UI.Services;

namespace FolderSync.UI.ViewModels
{
    public class TaskAnalysisRowViewModel : ViewModelBase
    {
        private bool _shouldSync;

        public string RelativePath { get; init; } = string.Empty;
        public bool IsDirectory { get; init; }
        public long? SourceSize { get; init; }
        public long? DestSize { get; init; }
        public DateTime? SourceLastWrite { get; init; }
        public DateTime? DestLastWrite { get; init; }
        public string DirectionLabel { get; init; } = "-";
        public string Reason { get; init; } = string.Empty;
        public FolderSync.Core.Diff.SyncActionType? ActionType { get; init; }
        public AnalysisDirection Direction { get; init; }
        public bool IsProtectedByDeliveredState { get; init; }
        public bool HasWarning { get; init; }

        public bool IsSelectable =>
            ActionType is not null
            && ActionType != FolderSync.Core.Diff.SyncActionType.Conflict
            && Direction != AnalysisDirection.None
            && !IsProtectedByDeliveredState;

        public bool ShouldSync
        {
            get => _shouldSync;
            set
            {
                // 无合法动作/方向的分析行不能被勾选执行，避免隐式反向覆盖。
                var normalized = value && IsSelectable;
                SetProperty(ref _shouldSync, normalized);
            }
        }
    }

    public class TaskAnalysisViewModel : ViewModelBase
    {
        private readonly SyncTaskDefinition _task;
        private readonly TaskAnalysisService _service;
        private readonly Action? _onSaved;
        private readonly System.Windows.Threading.DispatcherTimer _summaryDebounceTimer;
        private bool _summaryDirty;
        private int _selectedSyncFileCount;
        private string _totalSyncSizeText = string.Empty;

        public ObservableCollection<TaskAnalysisRowViewModel> Items { get; } = new();

        public ICommand ExecuteSelectedCommand { get; }
        public ICommand RefreshAnalysisCommand { get; }
        public ICommand SaveAnalysisCommand { get; }
        public ICommand StopCurrentOperationCommand { get; }
        private CancellationTokenSource? _currentOperationCts;
        private bool _isLoading;
        private bool _isExecuting;
        private bool _hasUnsavedChanges;
        private double _busyProgressValue;
        private double _busyProgressMaximum = 1;
        private bool _isBusyProgressIndeterminate = true;
        public bool IsLoading
        {
            get => _isLoading;
            set
            {
                if (SetProperty(ref _isLoading, value))
                {
                    OnPropertyChanged(nameof(IsBusy));
                }
            }
        }

        public bool IsExecuting
        {
            get => _isExecuting;
            set
            {
                if (SetProperty(ref _isExecuting, value))
                {
                    OnPropertyChanged(nameof(IsBusy));
                }
            }
        }

        public bool HasUnsavedChanges
        {
            get => _hasUnsavedChanges;
            set => SetProperty(ref _hasUnsavedChanges, value);
        }

        public double BusyProgressValue
        {
            get => _busyProgressValue;
            set => SetProperty(ref _busyProgressValue, value);
        }

        public double BusyProgressMaximum
        {
            get => _busyProgressMaximum;
            set => SetProperty(ref _busyProgressMaximum, value);
        }

        public bool IsBusyProgressIndeterminate
        {
            get => _isBusyProgressIndeterminate;
            set => SetProperty(ref _isBusyProgressIndeterminate, value);
        }

        public string TaskTitle => $"分析结果 - {_task.TaskName}";
        public bool IsBusy => IsLoading || IsExecuting;
        public bool IsStopRequested => _currentOperationCts?.IsCancellationRequested == true;
        public int SelectedSyncFileCount => _selectedSyncFileCount;
        public string TotalSyncSizeText => _totalSyncSizeText;

        public TaskAnalysisViewModel(SyncTaskDefinition task, TaskAnalysisService? service = null, Action? onSaved = null)
        {
            _task = task;
            _service = service ?? new TaskAnalysisService();
            _onSaved = onSaved;
            _summaryDebounceTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            _summaryDebounceTimer.Tick += (_, _) =>
            {
                _summaryDebounceTimer.Stop();
                if (_summaryDirty)
                {
                    RefreshSummaryCore();
                }
            };
            ExecuteSelectedCommand = new RelayCommand(async _ => await ExecuteSelectedAsync(), _ => !IsBusy && Items.Any(i => i.ShouldSync));
            RefreshAnalysisCommand = new RelayCommand(async _ => await LoadAnalysisAsync(useSavedIfAvailable: false), _ => !IsBusy);
            SaveAnalysisCommand = new RelayCommand(_ => SaveAnalysis(), _ => !IsBusy && Items.Count > 0);
            StopCurrentOperationCommand = new RelayCommand(_ => StopCurrentOperation(), _ => IsBusy && !IsStopRequested);
            _ = LoadAnalysisAsync(useSavedIfAvailable: true);
        }

        private async Task LoadAnalysisAsync(bool useSavedIfAvailable)
        {
            if (IsLoading)
            {
                return;
            }

            try
            {
                using var operationCts = BeginOperation();
                IsLoading = true;
                ResetBusyProgressDisplay();
                CommandManager.InvalidateRequerySuggested();
                var token = operationCts.Token;
                var rows = await Task.Run(async () =>
                {
                    List<TaskAnalysisRowViewModel>? result;
                    if (useSavedIfAvailable && _service.HasSavedAnalysis(_task))
                    {
                        result = _service.GetSavedAnalysis(_task).Select(MapToRow).ToList();
                    }
                    else
                    {
                        result = (await _service.AnalyzeAsync(_task, token)).Select(MapToRow).ToList();
                    }

                    return result;
                }, token);

                ClearItems();
                foreach (var row in rows)
                {
                    row.PropertyChanged += OnRowPropertyChanged;
                    Items.Add(row);
                }
                HasUnsavedChanges = false;
                RaiseSummaryPropertiesChanged();
            }
            catch (OperationCanceledException)
            {
                // 用户主动停止时保持当前列表不变。
            }
            catch (Exception ex)
            {
                MessageDialogService.ShowError("Msg.AnalysisFailed", "Title.AnalysisFailed", ex.Message);
            }
            finally
            {
                IsLoading = false;
                EndOperation();
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private void SaveAnalysis()
        {
            try
            {
                _service.SaveAnalysis(_task, BuildAnalysisItemsFromRows());
                _onSaved?.Invoke();
                HasUnsavedChanges = false;
                MessageDialogService.ShowInfo("Msg.AnalysisSaved", "Title.Saved");
            }
            catch (Exception ex)
            {
                MessageDialogService.ShowError("Msg.SaveFailed", "Title.SaveFailed", ex.Message);
            }
        }

        private async Task ExecuteSelectedAsync()
        {
            if (IsExecuting)
            {
                return;
            }

            try
            {
                using var operationCts = BeginOperation();
                IsExecuting = true;
                IsBusyProgressIndeterminate = true;
                BusyProgressValue = 0;
                BusyProgressMaximum = 1;
                CommandManager.InvalidateRequerySuggested();
                var selected = BuildAnalysisItemsFromRows();
                var token = operationCts.Token;
                var executionResult = await Task.Run(async () =>
                {
                    var report = await _service.ExecuteSelectedAsync(_task, selected, token);
                    var reportPath = SyncReportFileWriter.Write(_task.Id, _task.TaskName, report);

                    // 失败/取消时保留未成功项，避免把未执行内容当成已处理。
                    var successfulPaths = new HashSet<string>(
                        report.SuccessDetails.Select(d => d.ItemPath),
                        StringComparer.OrdinalIgnoreCase);
                    var remaining = selected
                        .Where(i => !successfulPaths.Contains(i.RelativePath))
                        .ToList();
                    if (!report.WasCancelled && remaining.Count < selected.Count)
                    {
                        _service.SaveAnalysis(_task, remaining);
                    }

                    return (report, reportPath, remainingCount: remaining.Count);
                }, token);

                if (!executionResult.report.WasCancelled
                    && executionResult.remainingCount < selected.Count)
                {
                    _onSaved?.Invoke();
                }

                HasUnsavedChanges = executionResult.remainingCount > 0;
                var succeeded = executionResult.report.CreatedFiles
                                + executionResult.report.UpdatedFiles
                                + executionResult.report.DeletedFiles;
                var failed = executionResult.report.FailedFiles;
                var skipped = executionResult.report.WarningDetails.Count;
                var reportFileName = Path.GetFileName(executionResult.reportPath);

                if (executionResult.report.WasCancelled)
                {
                    MessageDialogService.ShowInfo(
                        "Msg.ExecuteSelectedStopped",
                        "Title.Stopped",
                        succeeded,
                        failed,
                        reportFileName);
                }
                else if (!string.IsNullOrWhiteSpace(executionResult.report.ErrorMessage))
                {
                    MessageDialogService.ShowError(
                        "Msg.ExecuteSelectedFailed",
                        "Title.ExecuteFailed",
                        executionResult.report.ErrorMessage,
                        succeeded,
                        failed,
                        reportFileName);
                }
                else
                {
                    MessageDialogService.ShowInfo(
                        "Msg.ExecuteSelectedComplete",
                        "Title.ExecuteComplete",
                        succeeded,
                        failed,
                        skipped,
                        reportFileName);
                }
            }
            catch (OperationCanceledException)
            {
                MessageDialogService.ShowInfo("Msg.SyncStopped", "Title.Stopped");
            }
            catch (Exception ex)
            {
                MessageDialogService.ShowError("Msg.ExecuteFailed", "Title.ExecuteFailed", ex.Message);
            }
            finally
            {
                IsExecuting = false;
                EndOperation();
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private CancellationTokenSource BeginOperation()
        {
            EndOperation();
            _currentOperationCts = new CancellationTokenSource();
            OnPropertyChanged(nameof(IsStopRequested));
            return _currentOperationCts;
        }

        private void EndOperation()
        {
            _currentOperationCts?.Dispose();
            _currentOperationCts = null;
            OnPropertyChanged(nameof(IsStopRequested));
        }

        private void StopCurrentOperation()
        {
            if (_currentOperationCts == null || _currentOperationCts.IsCancellationRequested)
            {
                return;
            }

            _currentOperationCts.Cancel();
            OnPropertyChanged(nameof(IsStopRequested));
            CommandManager.InvalidateRequerySuggested();
        }

        /// <summary>
        /// 窗口关闭前取消正在进行的分析/执行，避免后台任务继续消费 FTP 连接。
        /// </summary>
        public void CancelPendingOperations()
        {
            try
            {
                _currentOperationCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 操作已结束，忽略。
            }
        }

        /// <summary>
        /// 关闭前保存分析结果（若存在未保存修改则触发保存，保存由 SaveAnalysis 自己处理异常）。
        /// </summary>
        public void SaveBeforeClose()
        {
            if (HasUnsavedChanges)
            {
                SaveAnalysis();
            }
        }

        private void ClearItems()
        {
            foreach (var row in Items)
            {
                row.PropertyChanged -= OnRowPropertyChanged;
            }

            Items.Clear();
        }

        private static TaskAnalysisRowViewModel MapToRow(TaskAnalysisItem i)
        {
            return new TaskAnalysisRowViewModel
            {
                RelativePath = i.RelativePath,
                IsDirectory = i.IsDirectory,
                SourceSize = i.SourceSize,
                DestSize = i.DestSize,
                SourceLastWrite = i.SourceLastWrite,
                DestLastWrite = i.DestLastWrite,
                DirectionLabel = i.DirectionLabel,
                Reason = i.Reason,
                ActionType = i.ActionType,
                Direction = i.Direction,
                IsProtectedByDeliveredState = i.IsProtectedByDeliveredState,
                HasWarning = i.HasWarning,
                ShouldSync = i.ShouldSync
                    && i.ActionType is not null
                    && i.ActionType != FolderSync.Core.Diff.SyncActionType.Conflict
                    && i.Direction != AnalysisDirection.None
            };
        }

        private List<TaskAnalysisItem> BuildAnalysisItemsFromRows()
        {
            return Items.Select(i => new TaskAnalysisItem
            {
                RelativePath = i.RelativePath,
                IsDirectory = i.IsDirectory,
                SourceSize = i.SourceSize,
                DestSize = i.DestSize,
                SourceLastWrite = i.SourceLastWrite,
                DestLastWrite = i.DestLastWrite,
                ActionType = i.ActionType,
                Direction = i.Direction,
                Reason = i.Reason,
                IsProtectedByDeliveredState = i.IsProtectedByDeliveredState,
                HasWarning = i.HasWarning,
                ShouldSync = i.ShouldSync && i.IsSelectable
            }).ToList();
        }

        private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TaskAnalysisRowViewModel.ShouldSync))
            {
                HasUnsavedChanges = true;
                ScheduleSummaryRefresh();
            }
        }

        private void ScheduleSummaryRefresh()
        {
            _summaryDirty = true;
            if (!_summaryDebounceTimer.IsEnabled)
            {
                _summaryDebounceTimer.Start();
            }
            else
            {
                _summaryDebounceTimer.Stop();
                _summaryDebounceTimer.Start();
            }
        }

        private void RaiseSummaryPropertiesChanged()
        {
            _summaryDirty = true;
            RefreshSummaryCore();
        }

        private void RefreshSummaryCore()
        {
            _summaryDirty = false;
            var selected = Items.Where(i => i.ShouldSync && !i.IsDirectory).ToList();
            _selectedSyncFileCount = selected.Count;
            var transferBytes = selected
                .Where(i => i.ActionType != FolderSync.Core.Diff.SyncActionType.Delete)
                .Sum(i => i.Direction == AnalysisDirection.BToA
                    ? i.DestSize ?? 0L
                    : i.SourceSize ?? 0L);
            _totalSyncSizeText = FormatBytes(transferBytes);
            OnPropertyChanged(nameof(SelectedSyncFileCount));
            OnPropertyChanged(nameof(TotalSyncSizeText));
        }

        private void ResetBusyProgressDisplay()
        {
            BusyProgressValue = 0;
            BusyProgressMaximum = 1;
            IsBusyProgressIndeterminate = true;
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            var unitIndex = 0;

            while (value >= 1024 && unitIndex < units.Length - 1)
            {
                value /= 1024;
                unitIndex++;
            }

            var format = unitIndex == 0 ? "0" : "0.##";
            return string.Format(CultureInfo.InvariantCulture, "{0:" + format + "} {1}", value, units[unitIndex]);
        }
    }
}
