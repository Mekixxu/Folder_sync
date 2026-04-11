using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using FolderSync.Core.Sync;

namespace FolderSync.UI.ViewModels
{
    /// <summary>
    /// 任务编辑器/向导的 ViewModel
    /// </summary>
    public class TaskEditorViewModel : ViewModelBase
    {
        private string _taskName = string.Empty;
        public string TaskName
        {
            get => _taskName;
            set => SetProperty(ref _taskName, value);
        }

        // 基础配置
        public ObservableCollection<string> Protocols { get; } = new(new[] { "Local/SMB", "FTP" });
        private string _sourceProtocol = "Local/SMB";
        public string SourceProtocol
        {
            get => _sourceProtocol;
            set => SetProperty(ref _sourceProtocol, value);
        }

        private string _destProtocol = "Local/SMB";
        public string DestProtocol
        {
            get => _destProtocol;
            set => SetProperty(ref _destProtocol, value);
        }

        private string _sourcePath = string.Empty;
        public string SourcePath
        {
            get => _sourcePath;
            set => SetProperty(ref _sourcePath, value);
        }

        private string _destPath = string.Empty;
        public string DestPath
        {
            get => _destPath;
            set => SetProperty(ref _destPath, value);
        }

        // 同步模式与策略
        public ObservableCollection<string> SyncModes { get; } = new(new[] { "单向增量 (仅新增)", "单向更新 (新增与修改)", "单向镜像 (让B等于A)", "双向同步 (实验性)" });
        private string _selectedSyncMode = "单向更新 (新增与修改)";
        public string SelectedSyncMode
        {
            get => _selectedSyncMode;
            set
            {
                if (SetProperty(ref _selectedSyncMode, value))
                {
                    UpdateModeDescription();
                }
            }
        }

        public ObservableCollection<string> DiffStrategies { get; } = new(new[] { "快速 (大小与修改时间)", "严格 (SHA256 哈希比对)" });
        private string _selectedDiffStrategy = "快速 (大小与修改时间)";
        public string SelectedDiffStrategy
        {
            get => _selectedDiffStrategy;
            set => SetProperty(ref _selectedDiffStrategy, value);
        }

        private string _modeDescription = "A目录中新增和修改的文件会被同步到B目录。B目录中独立存在的文件将保持原样，不会被删除。";
        public string ModeDescription
        {
            get => _modeDescription;
            set => SetProperty(ref _modeDescription, value);
        }

        // 调度配置
        private bool _isManualTrigger = true;
        public bool IsManualTrigger
        {
            get => _isManualTrigger;
            set => SetProperty(ref _isManualTrigger, value);
        }

        private bool _isPeriodicTrigger;
        public bool IsPeriodicTrigger
        {
            get => _isPeriodicTrigger;
            set => SetProperty(ref _isPeriodicTrigger, value);
        }

        private bool _isCronTrigger;
        public bool IsCronTrigger
        {
            get => _isCronTrigger;
            set => SetProperty(ref _isCronTrigger, value);
        }

        public string IntervalValue { get; set; } = "10";
        public string IntervalUnit { get; set; } = "分钟";
        public string CronExpression { get; set; } = "0 0 12 * * ?";

        // 过滤配置
        private bool _filterTypeNone = true;
        public bool FilterTypeNone
        {
            get => _filterTypeNone;
            set => SetProperty(ref _filterTypeNone, value);
        }

        private bool _filterTypeWhitelist;
        public bool FilterTypeWhitelist
        {
            get => _filterTypeWhitelist;
            set => SetProperty(ref _filterTypeWhitelist, value);
        }

        private bool _filterTypeBlacklist;
        public bool FilterTypeBlacklist
        {
            get => _filterTypeBlacklist;
            set => SetProperty(ref _filterTypeBlacklist, value);
        }

        public string ExtensionFilterText { get; set; } = string.Empty;
        public string MinSizeMB { get; set; } = string.Empty;
        public string MaxSizeMB { get; set; } = string.Empty;
        public string NewerThanDays { get; set; } = string.Empty;
        public string RegexPattern { get; set; } = string.Empty;

        // 命令
        public ICommand BrowseSourceCommand { get; }
        public ICommand BrowseDestCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand SaveTaskCommand { get; }

        public TaskEditorViewModel(Action goBackAction)
        {
            BrowseSourceCommand = new RelayCommand(_ => { /* TODO: Open Folder Dialog */ });
            BrowseDestCommand = new RelayCommand(_ => { /* TODO: Open Folder Dialog */ });
            CancelCommand = new RelayCommand(_ => goBackAction?.Invoke());
            SaveTaskCommand = new RelayCommand(SaveTask, CanSaveTask);
        }

        private void UpdateModeDescription()
        {
            ModeDescription = SelectedSyncMode switch
            {
                "单向增量 (仅新增)" => "仅将A目录中新出现的文件复制到B目录，忽略修改。",
                "单向更新 (新增与修改)" => "A目录中新增和修改的文件会被同步到B目录。B目录独有的文件将保留。",
                "单向镜像 (让B等于A)" => "完全将B目录变成A目录的镜像！B目录中所有多余的文件将会被强制删除。",
                "双向同步 (实验性)" => "A和B目录互相作为源和目标进行增删改双向同步。",
                _ => ""
            };
        }

        private bool CanSaveTask(object? parameter)
        {
            // 简单验证：名称、路径必填
            return !string.IsNullOrWhiteSpace(TaskName) && 
                   !string.IsNullOrWhiteSpace(SourcePath) && 
                   !string.IsNullOrWhiteSpace(DestPath);
        }

        private void SaveTask(object? parameter)
        {
            // TODO: 解析配置并生成后台 SyncJob，然后返回列表页
            CancelCommand.Execute(null);
        }
    }
}
