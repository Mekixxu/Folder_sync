using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using FolderSync.Core.Config;
using FolderSync.Core.Filters;
using FolderSync.Core.Scheduler;
using FolderSync.Core.Sync;

namespace FolderSync.UI.ViewModels
{
    /// <summary>
    /// 任务编辑器/向导的 ViewModel
    /// </summary>
    public class TaskEditorViewModel : ViewModelBase
    {
        private readonly Action _goBackAction;
        private readonly TaskRepository _taskRepository = new();
        private readonly SyncTaskDefinition? _editingTask;

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

        public ObservableCollection<string> DiffStrategies { get; } = new(new[] { "快速 (大小与修改时间)", "快速哈希 (xxHash64)" });
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

        // 过滤配置：白名单
        public string WhitelistExtensionFilterText { get; set; } = string.Empty;
        public string WhitelistMinSizeMB { get; set; } = string.Empty;
        public string WhitelistMaxSizeMB { get; set; } = string.Empty;
        public string WhitelistNewerThanHours { get; set; } = string.Empty;
        public string WhitelistRegexPattern { get; set; } = string.Empty;

        // 过滤配置：黑名单
        public string BlacklistExtensionFilterText { get; set; } = string.Empty;
        public string BlacklistMinSizeMB { get; set; } = string.Empty;
        public string BlacklistMaxSizeMB { get; set; } = string.Empty;
        public string BlacklistNewerThanHours { get; set; } = string.Empty;
        public string BlacklistRegexPattern { get; set; } = string.Empty;

        // 命令
        public ICommand BrowseSourceCommand { get; }
        public ICommand BrowseDestCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand SaveTaskCommand { get; }

        public TaskEditorViewModel(Action goBackAction, SyncTaskDefinition? editingTask = null)
        {
            _goBackAction = goBackAction;
            _editingTask = editingTask;
            BrowseSourceCommand = new RelayCommand(_ => BrowseFolder(isSource: true));
            BrowseDestCommand = new RelayCommand(_ => BrowseFolder(isSource: false));
            CancelCommand = new RelayCommand(_ => goBackAction?.Invoke());
            SaveTaskCommand = new RelayCommand(SaveTask, CanSaveTask);

            if (editingTask != null)
            {
                LoadFromTask(editingTask);
            }
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
            var configuration = BuildFilterConfiguration();
            var conflicts = FilterConflictDetector.Detect(configuration);
            if (conflicts.Count > 0)
            {
                var conflictMessage = string.Join(
                    Environment.NewLine,
                    conflicts.Select(c => $"• {c.Type}: {c.Message}"));
                MessageBox.Show(
                    "检测到白名单与黑名单存在冲突，已阻止保存：\n\n" + conflictMessage,
                    "过滤规则冲突",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
            try
            {
                var task = BuildTaskDefinition(configuration);
                _taskRepository.Upsert(task);

                if (task.IsManualTrigger)
                {
                    SchedulerManager.Instance.RemoveJobAsync(task.Id).GetAwaiter().GetResult();
                }
                else
                {
                    var cron = SyncTaskFactory.ResolveCronExpression(task);
                    var executor = SyncTaskFactory.CreateExecutor(task);
                    SchedulerManager.Instance.AddOrUpdateJobAsync(task.Id, task.TaskName, cron, executor).GetAwaiter().GetResult();
                }

                MessageBox.Show("任务已保存。", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
                _goBackAction?.Invoke();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存任务失败：{ex.Message}", "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 兼容历史配置的迁移入口：
        /// 历史“全部允许”会迁移为白/黑名单都为空。
        /// </summary>
        public void LoadLegacyFilterConfig(LegacyFilterConfiguration legacyConfiguration)
        {
            var configuration = DualListFilterConfiguration.FromLegacy(legacyConfiguration);
            ApplyRuleSetToUi(configuration.Whitelist, isWhitelist: true);
            ApplyRuleSetToUi(configuration.Blacklist, isWhitelist: false);
        }

        private DualListFilterConfiguration BuildFilterConfiguration()
        {
            return new DualListFilterConfiguration
            {
                Whitelist = new FilterRuleSet
                {
                    ExtensionFilterText = WhitelistExtensionFilterText,
                    MinSizeMB = WhitelistMinSizeMB,
                    MaxSizeMB = WhitelistMaxSizeMB,
                    NewerThanHours = WhitelistNewerThanHours,
                    RegexPattern = WhitelistRegexPattern
                },
                Blacklist = new FilterRuleSet
                {
                    ExtensionFilterText = BlacklistExtensionFilterText,
                    MinSizeMB = BlacklistMinSizeMB,
                    MaxSizeMB = BlacklistMaxSizeMB,
                    NewerThanHours = BlacklistNewerThanHours,
                    RegexPattern = BlacklistRegexPattern
                }
            };
        }

        private void ApplyRuleSetToUi(FilterRuleSet ruleSet, bool isWhitelist)
        {
            if (isWhitelist)
            {
                WhitelistExtensionFilterText = ruleSet.ExtensionFilterText ?? string.Empty;
                WhitelistMinSizeMB = ruleSet.MinSizeMB ?? string.Empty;
                WhitelistMaxSizeMB = ruleSet.MaxSizeMB ?? string.Empty;
                WhitelistNewerThanHours = ruleSet.NewerThanHours ?? string.Empty;
                WhitelistRegexPattern = ruleSet.RegexPattern ?? string.Empty;
                return;
            }

            BlacklistExtensionFilterText = ruleSet.ExtensionFilterText ?? string.Empty;
            BlacklistMinSizeMB = ruleSet.MinSizeMB ?? string.Empty;
            BlacklistMaxSizeMB = ruleSet.MaxSizeMB ?? string.Empty;
            BlacklistNewerThanHours = ruleSet.NewerThanHours ?? string.Empty;
            BlacklistRegexPattern = ruleSet.RegexPattern ?? string.Empty;
        }

        private void BrowseFolder(bool isSource)
        {
            var protocol = isSource ? SourceProtocol : DestProtocol;
            if (string.Equals(protocol, "FTP", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    "FTP 路径请手动输入（例如 ftp://host/path）。",
                    "协议提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var currentPath = isSource ? SourcePath : DestPath;
            var dialog = new OpenFileDialog
            {
                Title = isSource ? "请选择源目录 (Folder A)" : "请选择目标目录 (Folder B)",
                CheckFileExists = false,
                CheckPathExists = true,
                ValidateNames = false,
                FileName = "选择此文件夹"
            };

            if (!string.IsNullOrWhiteSpace(currentPath) && Directory.Exists(currentPath))
            {
                dialog.InitialDirectory = currentPath;
            }

            if (dialog.ShowDialog() == true)
            {
                var selectedDirectory = Path.GetDirectoryName(dialog.FileName);
                if (string.IsNullOrWhiteSpace(selectedDirectory))
                {
                    return;
                }

                if (isSource)
                {
                    SourcePath = selectedDirectory;
                }
                else
                {
                    DestPath = selectedDirectory;
                }
            }
        }

        private SyncTaskDefinition BuildTaskDefinition(DualListFilterConfiguration configuration)
        {
            return new SyncTaskDefinition
            {
                Id = _editingTask?.Id ?? Guid.NewGuid().ToString("N"),
                TaskName = TaskName.Trim(),
                SourceProtocol = SourceProtocol,
                DestProtocol = DestProtocol,
                SourcePath = SourcePath.Trim(),
                DestPath = DestPath.Trim(),
                SyncMode = MapSyncMode(SelectedSyncMode),
                DiffStrategy = MapDiffStrategy(SelectedDiffStrategy),
                IsManualTrigger = IsManualTrigger,
                IsPeriodicTrigger = IsPeriodicTrigger,
                IsCronTrigger = IsCronTrigger,
                IntervalValue = IntervalValue,
                IntervalUnit = IntervalUnit,
                CronExpression = CronExpression,
                FilterConfiguration = configuration
            };
        }

        private void LoadFromTask(SyncTaskDefinition task)
        {
            TaskName = task.TaskName;
            SourceProtocol = task.SourceProtocol;
            DestProtocol = task.DestProtocol;
            SourcePath = task.SourcePath;
            DestPath = task.DestPath;
            SelectedSyncMode = task.SyncMode switch
            {
                SyncMode.OneWayIncremental => "单向增量 (仅新增)",
                SyncMode.OneWayMirror => "单向镜像 (让B等于A)",
                SyncMode.TwoWay => "双向同步 (实验性)",
                _ => "单向更新 (新增与修改)"
            };
            SelectedDiffStrategy = task.DiffStrategy == "XxHash64"
                ? "快速哈希 (xxHash64)"
                : "快速 (大小与修改时间)";

            IsManualTrigger = task.IsManualTrigger;
            IsPeriodicTrigger = task.IsPeriodicTrigger;
            IsCronTrigger = task.IsCronTrigger;
            IntervalValue = task.IntervalValue;
            IntervalUnit = task.IntervalUnit;
            CronExpression = task.CronExpression;

            var config = task.FilterConfiguration ?? new DualListFilterConfiguration();
            ApplyRuleSetToUi(config.Whitelist, true);
            ApplyRuleSetToUi(config.Blacklist, false);
        }

        private static SyncMode MapSyncMode(string selected)
        {
            return selected switch
            {
                "单向增量 (仅新增)" => SyncMode.OneWayIncremental,
                "单向镜像 (让B等于A)" => SyncMode.OneWayMirror,
                "双向同步 (实验性)" => SyncMode.TwoWay,
                _ => SyncMode.OneWayUpdate
            };
        }

        private static string MapDiffStrategy(string selected)
        {
            return selected.Contains("xxHash64", StringComparison.OrdinalIgnoreCase) ? "XxHash64" : "SizeAndTime";
        }
    }
}
