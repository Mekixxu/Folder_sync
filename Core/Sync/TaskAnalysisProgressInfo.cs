namespace FolderSync.Core.Sync
{
    public enum TaskAnalysisPhase
    {
        ListingSource,
        ListingDestination,
        Comparing,
        Finalizing
    }

    public sealed class TaskAnalysisProgressInfo
    {
        public TaskAnalysisPhase Phase { get; init; }

        public string CurrentPath { get; init; } = string.Empty;
    }
}
