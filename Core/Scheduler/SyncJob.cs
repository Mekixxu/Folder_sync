using System;
using System.Linq;
using System.Threading.Tasks;
using FolderSync.Core.Config;
using FolderSync.Core.Reporting;
using FolderSync.Core.Sync;
using Quartz;
using Serilog;

namespace FolderSync.Core.Scheduler
{
    /// <summary>
    /// Quartz.NET 的 IJob 实现，作为定时触发的执行载体
    /// </summary>
    [DisallowConcurrentExecution] // 防止同一个任务的下一次触发与上一次执行重叠
    public class SyncJob : IJob
    {
        public const string JobDataKey_TaskId = "TaskId";
        public const string JobDataKey_TaskName = "TaskName";

        public async Task Execute(IJobExecutionContext context)
        {
            var dataMap = context.MergedJobDataMap;
            
            var taskId = dataMap.GetString(JobDataKey_TaskId) ?? "Unknown";
            var taskName = dataMap.GetString(JobDataKey_TaskName) ?? "Unnamed Task";

            Log.Information("Starting scheduled sync job: {TaskName} ({TaskId})", taskName, taskId);

            try
            {
                // 每次执行创建并独占执行器，避免任务编辑/删除时 Dispose 正在传输的 FTP 连接。
                var task = new TaskRepository().LoadAll()
                    .FirstOrDefault(t => string.Equals(t.Id, taskId, StringComparison.OrdinalIgnoreCase));
                if (task == null)
                {
                    Log.Warning("Scheduled sync job skipped because task no longer exists: {TaskName} ({TaskId})", taskName, taskId);
                    return;
                }

                using var executor = SyncTaskFactory.CreateExecutor(task);
                var report = await executor.ExecuteAsync(context.CancellationToken);
                var reportPath = SyncReportFileWriter.Write(taskId, taskName, report);
                
                if (report.IsSuccess)
                {
                    Log.Information("Scheduled sync job {TaskName} finished successfully. ReportFile: {ReportPath} Report: {@Report}", taskName, reportPath, report);
                }
                else
                {
                    Log.Warning("Scheduled sync job {TaskName} finished with errors. ReportFile: {ReportPath} Report: {@Report}", taskName, reportPath, report);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Unhandled exception occurred during scheduled sync job: {TaskName}", taskName);
            }
        }
    }
}
