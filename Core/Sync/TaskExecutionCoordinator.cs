using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace FolderSync.Core.Sync
{
    /// <summary>
    /// 进程内任务执行协调器。
    /// Quartz 的 DisallowConcurrentExecution 只约束同一个 JobKey，不能覆盖手动分析/执行，
    /// 因此定时与手动入口必须共享同一把按 taskId 的运行锁。
    /// </summary>
    public static class TaskExecutionCoordinator
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
            new(StringComparer.OrdinalIgnoreCase);

        public static async Task<IDisposable> AcquireAsync(string taskId, CancellationToken cancellationToken = default)
        {
            var key = string.IsNullOrWhiteSpace(taskId) ? "(unknown)" : taskId.Trim();
            var gate = Gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken);
            return new Lease(gate);
        }

        private sealed class Lease : IDisposable
        {
            private SemaphoreSlim? _gate;

            public Lease(SemaphoreSlim gate)
            {
                _gate = gate;
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _gate, null)?.Release();
            }
        }
    }
}
