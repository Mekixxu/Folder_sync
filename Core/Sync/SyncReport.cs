using System;
using FolderSync.Core.Diff;

namespace FolderSync.Core.Sync
{
    /// <summary>
    /// 包含同步任务执行完毕后的统计结果
    /// </summary>
    public class SyncReport
    {
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration => EndTime - StartTime;

        public SyncMode SyncMode { get; set; }
        
        public int TotalActions { get; set; }
        public int CreatedFiles { get; set; }
        public int UpdatedFiles { get; set; }
        public int DeletedFiles { get; set; }
        public int FailedFiles { get; set; }

        public string ErrorMessage { get; set; } = string.Empty;
        public bool IsSuccess => string.IsNullOrEmpty(ErrorMessage) && FailedFiles == 0;

        public override string ToString()
        {
            return $"Sync [{SyncMode}] finished in {Duration.TotalSeconds:F2}s. " +
                   $"Created: {CreatedFiles}, Updated: {UpdatedFiles}, Deleted: {DeletedFiles}, Failed: {FailedFiles}. " +
                   (IsSuccess ? "Success" : $"Error: {ErrorMessage}");
        }
    }

    /// <summary>
    /// 同步进度事件参数，用于 UI 更新进度条
    /// </summary>
    public class SyncProgressEventArgs : EventArgs
    {
        public int CompletedActions { get; }
        public int TotalActions { get; }
        public string CurrentItemPath { get; }
        public SyncActionType CurrentAction { get; }
        public double Percentage => TotalActions > 0 ? (double)CompletedActions / TotalActions * 100 : 100;

        public SyncProgressEventArgs(int completed, int total, string path, SyncActionType action)
        {
            CompletedActions = completed;
            TotalActions = total;
            CurrentItemPath = path;
            CurrentAction = action;
        }
    }

    /// <summary>
    /// 同步错误事件参数，用于记录单个文件的失败信息
    /// </summary>
    public class SyncErrorEventArgs : EventArgs
    {
        public Exception Exception { get; }
        public string ContextMessage { get; }

        public SyncErrorEventArgs(Exception ex, string contextMessage)
        {
            Exception = ex;
            ContextMessage = contextMessage;
        }
    }
}
