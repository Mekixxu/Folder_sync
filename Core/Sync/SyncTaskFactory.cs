using System;
using FolderSync.Core.Config;
using FolderSync.Core.Diff;
using FolderSync.Core.Filters;
using FolderSync.Core.VFS;

namespace FolderSync.Core.Sync
{
    public static class SyncTaskFactory
    {
        public static (IFileSystem SourceFs, IFileSystem DestFs) CreateFileSystems(SyncTaskDefinition task)
        {
            return (CreateFileSystem(task.SourceProtocol, task.SourcePath), CreateFileSystem(task.DestProtocol, task.DestPath));
        }

        public static FilterEngine CreateFilterEngine(DualListFilterConfiguration configuration)
        {
            var filterEngine = new FilterEngine();
            filterEngine.Configure(configuration);
            return filterEngine;
        }

        public static SyncExecutor CreateExecutor(SyncTaskDefinition task)
        {
            var (sourceFs, destFs) = CreateFileSystems(task);
            var diff = CreateDiffStrategy(task.DiffStrategy);
            var filterEngine = CreateFilterEngine(task.FilterConfiguration ?? new DualListFilterConfiguration());

            return new SyncExecutor(sourceFs, destFs, diff, filterEngine, task.SyncMode, task.Id);
        }

        public static string ResolveCronExpression(SyncTaskDefinition task)
        {
            if (task.IsCronTrigger && !string.IsNullOrWhiteSpace(task.CronExpression))
            {
                return task.CronExpression.Trim();
            }

            if (task.IsPeriodicTrigger)
            {
                if (!int.TryParse(task.IntervalValue, out var n) || n <= 0)
                {
                    n = 10;
                }

                return task.IntervalUnit switch
                {
                    "秒" => $"0/{n} * * * * ?",
                    "分钟" => $"0 0/{n} * * * ?",
                    "小时" => $"0 0 0/{n} * * ?",
                    "天" => $"0 0 0 1/{n} * ?",
                    _ => $"0 0/{n} * * * ?"
                };
            }

            // 手动模式默认每天凌晨触发占位（实际不会用于调度）
            return "0 0 0 * * ?";
        }

        public static IDiffStrategy CreateDiffStrategy(string diffStrategy)
        {
            return string.Equals(diffStrategy, "XxHash64", StringComparison.OrdinalIgnoreCase)
                ? new ChecksumDiffStrategy()
                : new SizeAndTimeDiffStrategy();
        }

        public static IFileSystem CreateFileSystem(string protocol, string path)
        {
            if (string.Equals(protocol, "FTP", StringComparison.OrdinalIgnoreCase))
            {
                return CreateFtpFileSystem(path);
            }

            return new LocalFileSystem(path);
        }

        private static IFileSystem CreateFtpFileSystem(string path)
        {
            if (!Uri.TryCreate(path, UriKind.Absolute, out var uri) || !string.Equals(uri.Scheme, "ftp", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("FTP 路径格式无效，请使用 ftp://host[:port]/basePath");
            }

            var host = uri.Host;
            var port = uri.IsDefaultPort ? 21 : uri.Port;
            var basePath = string.IsNullOrWhiteSpace(uri.AbsolutePath) ? "/" : uri.AbsolutePath;

            return new FtpFileSystem(host, "anonymous", "anonymous@", port, basePath);
        }
    }
}
