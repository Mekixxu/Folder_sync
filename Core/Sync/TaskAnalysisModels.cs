using System;
using FolderSync.Core.Diff;

namespace FolderSync.Core.Sync
{
    public enum AnalysisDirection
    {
        None,
        AToB,
        BToA
    }

    public class TaskAnalysisItem
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

        public string ActionLabel => ShouldSync ? "同步" : "不同步";
        public string DirectionLabel => Direction switch
        {
            AnalysisDirection.AToB => "A -> B",
            AnalysisDirection.BToA => "B -> A",
            _ => "-"
        };
    }
}
