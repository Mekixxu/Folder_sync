using System;

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

        public long ProcessedBytes { get; init; }

        public long TotalBytes { get; init; }

        public long PendingBytes => Math.Max(0L, TotalBytes - ProcessedBytes);

        public bool IsIndeterminate => TotalBytes <= 0;
    }
}
