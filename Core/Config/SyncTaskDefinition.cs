using System;
using System.Collections.Generic;
using FolderSync.Core.Diff;
using FolderSync.Core.Filters;
using FolderSync.Core.Sync;

namespace FolderSync.Core.Config
{
    public class SyncTaskDefinition
    {
        public string Id { get; set; } = string.Empty;
        public string TaskName { get; set; } = string.Empty;

        public string SourceProtocol { get; set; } = "Local/SMB";
        public string DestProtocol { get; set; } = "Local/SMB";
        public string SourcePath { get; set; } = string.Empty;
        public string DestPath { get; set; } = string.Empty;

        public SyncMode SyncMode { get; set; } = SyncMode.OneWayUpdate;
        public string DiffStrategy { get; set; } = "SizeAndTime";

        public bool IsManualTrigger { get; set; } = true;
        public bool IsPeriodicTrigger { get; set; }
        public bool IsCronTrigger { get; set; }
        public string IntervalValue { get; set; } = "10";
        public string IntervalUnit { get; set; } = "分钟";
        public string CronExpression { get; set; } = "0 0 12 * * ?";

        public DualListFilterConfiguration FilterConfiguration { get; set; } = new();

        public DateTime? AnalysisSavedAtUtc { get; set; }
        public List<SavedTaskAnalysisItem> SavedAnalysisItems { get; set; } = new();
    }

    public class SavedTaskAnalysisItem
    {
        public string RelativePath { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
        public long? SourceSize { get; set; }
        public long? DestSize { get; set; }
        public DateTime? SourceLastWrite { get; set; }
        public DateTime? DestLastWrite { get; set; }
        public SyncActionType? ActionType { get; set; }
        public AnalysisDirection Direction { get; set; }
        public string Reason { get; set; } = string.Empty;
        public bool ShouldSync { get; set; }
    }
}
