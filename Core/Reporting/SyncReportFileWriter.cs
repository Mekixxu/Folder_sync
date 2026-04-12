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
            var logsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Directory.CreateDirectory(logsDir);

            var safeTaskId = Sanitize(taskId);
            var safeTaskName = Sanitize(taskName);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var nonce = Guid.NewGuid().ToString("N")[..8];

            // 命名规则：无 foldersync 前缀，含毫秒 + taskId + 随机后缀，彻底规避并发重名
            var fileName = $"{timestamp}_{safeTaskId}_{nonce}.txt";
            var path = Path.Combine(logsDir, fileName);

            var sb = new StringBuilder();
            sb.AppendLine($"TaskName: {taskName}");
            sb.AppendLine($"TaskId: {taskId}");
            sb.AppendLine($"TaskNameSafe: {safeTaskName}");
            sb.AppendLine($"Start(Local): {report.StartTime.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine($"End(Local): {report.EndTime.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine($"Duration: {report.Duration.TotalSeconds:F3}s");
            sb.AppendLine($"SyncMode: {report.SyncMode}");
            sb.AppendLine($"Success: {report.IsSuccess}");
            sb.AppendLine($"Actions: total={report.TotalActions}, create={report.CreatedFiles}, update={report.UpdatedFiles}, delete={report.DeletedFiles}, failed={report.FailedFiles}");
            if (!string.IsNullOrWhiteSpace(report.ErrorMessage))
            {
                sb.AppendLine($"ErrorMessage: {report.ErrorMessage}");
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
                    sb.AppendLine($"  - [{err.OccurredAtUtc:yyyy-MM-dd HH:mm:ss.fff}Z] Item={err.ItemPath}");
                    sb.AppendLine($"    Context={err.Context}");
                    sb.AppendLine($"    Type={err.ErrorType}");
                    sb.AppendLine($"    Message={err.Message}");
                }
            }

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            return path;
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "unknown";
            }

            var chars = value
                .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)
                .ToArray();
            return new string(chars);
        }
    }
}
