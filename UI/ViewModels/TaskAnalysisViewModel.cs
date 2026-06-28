using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.ComponentModel;
using System.Threading.Tasks;
using FolderSync.Core.Config;
using FolderSync.Core.Reporting;
using FolderSync.Core.Sync;

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

        public bool ShouldSync
        {
            get => _shouldSync;
            set => SetProperty(ref _shouldSync, value);
        }
    }

    public class TaskAnalysisViewModel : ViewModelBase
    {
        private readonly SyncTaskDefinition _task;
        private readonly TaskAnalysisService _service;
        private readonly Action? _onSaved;

        public ObservableCollection<TaskAnalysisRowViewModel> Items { get; } = new();

        public ICommand ExecuteSelectedCommand { get; }
        public ICommand RefreshAnalysisCommand { get; }
        public ICommand SaveAnalysisCommand { get; }
        private bool _isLoading;
        private bool _isExecuting;
        private bool _hasUnsavedChanges;
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

        public string TaskTitle => $"分析结果 - {_task.TaskName}";
        public bool IsBusy => IsLoading || IsExecuting;
        public int SelectedSyncFileCount => Items.Count(i => i.ShouldSync && !i.IsDirectory);
        public string TotalSyncSizeText => FormatBytes(Items
            .Where(i => i.ShouldSync && !i.IsDirectory)
            .Sum(i => i.SourceSize ?? 0L));

        public TaskAnalysisViewModel(SyncTaskDefinition task, TaskAnalysisService? service = null, Action? onSaved = null)
        {
            _task = task;
            _service = service ?? new TaskAnalysisService();
            _onSaved = onSaved;
            ExecuteSelectedCommand = new RelayCommand(async _ => await ExecuteSelectedAsync(), _ => !IsBusy && Items.Any(i => i.ShouldSync));
            RefreshAnalysisCommand = new RelayCommand(async _ => await LoadAnalysisAsync(useSavedIfAvailable: false), _ => !IsBusy);
            SaveAnalysisCommand = new RelayCommand(_ => SaveAnalysis(), _ => !IsBusy && Items.Count > 0);
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
                IsLoading = true;
                CommandManager.InvalidateRequerySuggested();
                var results = await Task.Run(async () =>
                {
                    return useSavedIfAvailable && _service.HasSavedAnalysis(_task)
                        ? _service.GetSavedAnalysis(_task)
                        : await _service.AnalyzeAsync(_task);
                });

                ClearItems();
                foreach (var i in results)
                {
                    var row = MapToRow(i);
                    row.PropertyChanged += OnRowPropertyChanged;
                    Items.Add(row);
                }
                HasUnsavedChanges = false;
                RaiseSummaryPropertiesChanged();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"分析失败：{ex.Message}", "分析失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsLoading = false;
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
                MessageBox.Show("分析结果已保存。", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存失败：{ex.Message}", "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
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
                IsExecuting = true;
                CommandManager.InvalidateRequerySuggested();
                var selected = BuildAnalysisItemsFromRows();
                var executionResult = await Task.Run(async () =>
                {
                    var report = await _service.ExecuteSelectedAsync(_task, selected);
                    var reportPath = SyncReportFileWriter.Write(_task.Id, _task.TaskName, report);
                    _service.SaveAnalysis(_task, selected);
                    return (report, reportPath);
                });

                _onSaved?.Invoke();
                HasUnsavedChanges = false;
                MessageBox.Show(
                    $"已执行 {selected.Count(x => x.ShouldSync)} 项。{Environment.NewLine}{Environment.NewLine}生成的日志/报告文件：{Environment.NewLine}- {Path.GetFileName(executionResult.reportPath)}{Environment.NewLine}{Environment.NewLine}请到程序目录下的 log 文件夹中自行打开。",
                    "执行完成",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                await LoadAnalysisAsync(useSavedIfAvailable: false);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"执行失败：{ex.Message}", "执行失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsExecuting = false;
                CommandManager.InvalidateRequerySuggested();
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
                ShouldSync = i.ShouldSync
            }).ToList();
        }

        private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TaskAnalysisRowViewModel.ShouldSync))
            {
                HasUnsavedChanges = true;
                RaiseSummaryPropertiesChanged();
            }
        }

        private void RaiseSummaryPropertiesChanged()
        {
            OnPropertyChanged(nameof(SelectedSyncFileCount));
            OnPropertyChanged(nameof(TotalSyncSizeText));
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
