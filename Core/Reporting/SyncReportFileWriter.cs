using System;
using System.IO;
using System.Linq;
using System.Text;
using FolderSync.Core.Sync;

namespace FolderSync.Core.Reporting
{
    /// <summary>
    /// 将每次同步结果写入独立报告文件（避免重名，便于审计）
    /// </summary>
    public static class SyncReportFileWriter
    {
        public static string Write(string taskId, string taskName, SyncReport report)
        {
            var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log");
            Directory.CreateDirectory(logDir);

            var path = BuildReportPath(logDir, report);

            var sb = new StringBuilder();
            sb.AppendLine($"TaskName: {taskName}");
            sb.AppendLine($"TaskId: {taskId}");
            sb.AppendLine($"Start(Local): {report.StartTime.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine($"End(Local): {report.EndTime.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine($"Duration: {report.Duration.TotalSeconds:F3}s");
            sb.AppendLine($"SyncMode: {report.SyncMode}");
            sb.AppendLine($"Success: {report.IsSuccess}");
            sb.AppendLine($"Actions: total={report.TotalActions}, create={report.CreatedFiles}, update={report.UpdatedFiles}, delete={report.DeletedFiles}, skippedDelivered={report.SkippedAlreadyDelivered}, failed={report.FailedFiles}");
            if (!string.IsNullOrWhiteSpace(report.ErrorMessage))
            {
                sb.AppendLine($"ErrorMessage: {report.ErrorMessage}");
            }

            sb.AppendLine();
            sb.AppendLine("SuccessDetails:");
            if (!report.SuccessDetails.Any())
            {
                sb.AppendLine("  (none)");
            }
            else
            {
                foreach (var success in report.SuccessDetails)
                {
                    sb.AppendLine($"  - [{success.OccurredAtUtc:yyyy-MM-dd HH:mm:ss.fff}Z] Action={success.ActionType?.ToString() ?? "Unknown"} Type={(success.IsDirectory ? "Directory" : "File")}");
                    sb.AppendLine($"    Name={success.ItemName}");
                    sb.AppendLine($"    SizeBytes={FormatSizeBytes(success.ItemSizeBytes)}");
                    sb.AppendLine($"    Path={success.ItemPath}");
                    sb.AppendLine($"    Context={success.Context}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("WarningDetails:");
            if (!report.WarningDetails.Any())
            {
                sb.AppendLine("  (none)");
            }
            else
            {
                foreach (var warning in report.WarningDetails)
                {
                    sb.AppendLine($"  - [{warning.OccurredAtUtc:yyyy-MM-dd HH:mm:ss.fff}Z] Item={warning.ItemPath}");
                    sb.AppendLine($"    Context={warning.Context}");
                    sb.AppendLine($"    Message={warning.Message}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("ErrorDetails:");
            if (!report.ErrorDetails.Any())
            {
                sb.AppendLine("  (none)");
            }
            else
            {
                foreach (var err in report.ErrorDetails)
                {
                    sb.AppendLine($"  - [{err.OccurredAtUtc:yyyy-MM-dd HH:mm:ss.fff}Z] ItemType={(err.IsDirectory ? "Directory" : "File")}");
                    sb.AppendLine($"    Name={err.ItemName}");
                    sb.AppendLine($"    SizeBytes={FormatSizeBytes(err.ItemSizeBytes)}");
                    sb.AppendLine($"    Path={err.ItemPath}");
                    sb.AppendLine($"    Context={err.Context}");
                    sb.AppendLine($"    ErrorType={err.ErrorType}");
                    sb.AppendLine($"    Message={err.Message}");
                }
            }

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            return path;
        }

        private static string FormatSizeBytes(long? sizeBytes)
        {
            return sizeBytes?.ToString() ?? "(unknown)";
        }

        private static string BuildReportPath(string logDir, SyncReport report)
        {
            var transferredBytes = report.SuccessDetails
                .Where(item => !item.IsDirectory && item.ActionType is FolderSync.Core.Diff.SyncActionType.Create or FolderSync.Core.Diff.SyncActionType.Update)
                .Sum(item => item.ItemSizeBytes ?? 0L);

            var timestamp = DateTime.Now;
            while (true)
            {
                var fileName = $"{timestamp:yyyyMMdd_HHmmss_fffffff}_{transferredBytes}B.txt";
                var path = Path.Combine(logDir, fileName);
                if (!File.Exists(path))
                {
                    return path;
                }

                timestamp = timestamp.AddTicks(1);
            }
        }
    }
}
