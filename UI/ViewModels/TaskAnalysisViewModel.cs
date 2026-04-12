using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.ComponentModel;
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
        private bool _hasUnsavedChanges;
        public bool HasUnsavedChanges
        {
            get => _hasUnsavedChanges;
            set => SetProperty(ref _hasUnsavedChanges, value);
        }

        public string TaskTitle => $"分析结果 - {_task.TaskName}";

        public TaskAnalysisViewModel(SyncTaskDefinition task, TaskAnalysisService? service = null, Action? onSaved = null)
        {
            _task = task;
            _service = service ?? new TaskAnalysisService();
            _onSaved = onSaved;
            ExecuteSelectedCommand = new RelayCommand(_ => ExecuteSelected());
            RefreshAnalysisCommand = new RelayCommand(_ => RefreshAnalysis());
            SaveAnalysisCommand = new RelayCommand(_ => SaveAnalysis());
            LoadAnalysis(useSavedIfAvailable: true);
        }

        private void LoadAnalysis(bool useSavedIfAvailable)
        {
            try
            {
                Items.Clear();
                var results = useSavedIfAvailable && _service.HasSavedAnalysis(_task)
                    ? _service.GetSavedAnalysis(_task)
                    : _service.AnalyzeAsync(_task).GetAwaiter().GetResult();

                foreach (var i in results)
                {
                    var row = MapToRow(i);
                    row.PropertyChanged += OnRowPropertyChanged;
                    Items.Add(row);
                }
                HasUnsavedChanges = false;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"分析失败：{ex.Message}", "分析失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RefreshAnalysis()
        {
            LoadAnalysis(useSavedIfAvailable: false);
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

        private void ExecuteSelected()
        {
            try
            {
                var selected = BuildAnalysisItemsFromRows();

                var report = _service.ExecuteSelectedAsync(_task, selected).GetAwaiter().GetResult();
                SyncReportFileWriter.Write(_task.Id, _task.TaskName, report);
                _service.SaveAnalysis(_task, selected);
                _onSaved?.Invoke();
                HasUnsavedChanges = false;
                MessageBox.Show($"已执行 {selected.Count(x => x.ShouldSync)} 项。", "执行完成", MessageBoxButton.OK, MessageBoxImage.Information);
                LoadAnalysis(useSavedIfAvailable: false);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"执行失败：{ex.Message}", "执行失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
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
                ShouldSync = i.ShouldSync
            }).ToList();
        }

        private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TaskAnalysisRowViewModel.ShouldSync))
            {
                HasUnsavedChanges = true;
            }
        }
    }
}
