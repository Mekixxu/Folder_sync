using System;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Core.Sync;
using Quartz;
using Quartz.Impl;
using Serilog;

namespace FolderSync.Core.Scheduler
{
    /// <summary>
    /// 提供计划任务的管理接口（单例模式管理调度引擎）
    /// </summary>
    public class SchedulerManager
    {
        private static readonly Lazy<SchedulerManager> _instance = new(() => new SchedulerManager());
        public static SchedulerManager Instance => _instance.Value;

        private IScheduler? _scheduler;

        private SchedulerManager() { }

        /// <summary>
        /// 初始化调度引擎
        /// </summary>
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (_scheduler == null || !_scheduler.IsStarted)
            {
                var factory = new StdSchedulerFactory();
                _scheduler = await factory.GetScheduler(cancellationToken);
                await _scheduler.Start(cancellationToken);
                Log.Information("Quartz Scheduler started successfully.");
            }
        }

        /// <summary>
        /// 停止调度引擎
        /// </summary>
        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            if (_scheduler != null && _scheduler.IsStarted)
            {
                await _scheduler.Shutdown(waitForJobsToComplete: true, cancellationToken);
                Log.Information("Quartz Scheduler stopped successfully.");
            }
        }

        /// <summary>
        /// 添加或更新定时同步任务
        /// 支持基于 Cron 表达式配置复杂的时间周期
        /// </summary>
        /// <param name="taskId">唯一任务ID</param>
        /// <param name="taskName">用户友好的任务名</param>
        /// <param name="cronExpression">Quartz 支持的 Cron 表达式</param>
        /// <param name="executor">同步执行器实例</param>
        public async Task AddOrUpdateJobAsync(string taskId, string taskName, string cronExpression, SyncExecutor executor)
        {
            if (_scheduler == null) throw new InvalidOperationException("Scheduler is not started.");

            var jobKey = new JobKey(taskId, "SyncJobs");
            var triggerKey = new TriggerKey(taskId, "SyncTriggers");

            // 如果已经存在同名任务，先删除旧任务以进行更新
            if (await _scheduler.CheckExists(jobKey))
            {
                await _scheduler.DeleteJob(jobKey);
            }

            var job = JobBuilder.Create<SyncJob>()
                .WithIdentity(jobKey)
                .WithDescription($"Sync task: {taskName}")
                .UsingJobData(SyncJob.JobDataKey_TaskId, taskId)
                .UsingJobData(SyncJob.JobDataKey_TaskName, taskName)
                .Build();

            // 因为 SyncExecutor 是一个对象引用，无法通过原生的简单类型 JobDataMap 保存，我们需要将其放入到对象级 JobDataMap 中
            job.JobDataMap.Put(SyncJob.JobDataKey_SyncExecutor, executor);

            var trigger = TriggerBuilder.Create()
                .WithIdentity(triggerKey)
                .WithCronSchedule(cronExpression, x => x.WithMisfireHandlingInstructionDoNothing())
                .Build();

            await _scheduler.ScheduleJob(job, trigger);
            Log.Information("Job {TaskName} ({TaskId}) scheduled with cron: {CronExpression}", taskName, taskId, cronExpression);
        }

        /// <summary>
        /// 取消/删除一个定时任务
        /// </summary>
        public async Task RemoveJobAsync(string taskId)
        {
            if (_scheduler == null) return;
            var jobKey = new JobKey(taskId, "SyncJobs");
            if (await _scheduler.CheckExists(jobKey))
            {
                await _scheduler.DeleteJob(jobKey);
                Log.Information("Job ({TaskId}) removed successfully.", taskId);
            }
        }

        /// <summary>
        /// 获取下一个任务执行的时间（如果有）
        /// </summary>
        public async Task<DateTimeOffset?> GetNextFireTimeAsync(string taskId)
        {
            if (_scheduler == null) return null;
            var triggerKey = new TriggerKey(taskId, "SyncTriggers");
            var trigger = await _scheduler.GetTrigger(triggerKey);
            return trigger?.GetNextFireTimeUtc();
        }

        /// <summary>
        /// 立即执行一次指定的任务，不影响其 Cron 调度周期
        /// </summary>
        public async Task TriggerJobImmediatelyAsync(string taskId)
        {
            if (_scheduler == null) return;
            var jobKey = new JobKey(taskId, "SyncJobs");
            if (await _scheduler.CheckExists(jobKey))
            {
                await _scheduler.TriggerJob(jobKey);
                Log.Information("Job ({TaskId}) triggered manually.", taskId);
            }
        }
    }
}
