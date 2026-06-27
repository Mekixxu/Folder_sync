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
            return (CreateSourceFileSystem(task), CreateDestFileSystem(task));
        }

        public static IFileSystem CreateSourceFileSystem(SyncTaskDefinition task)
        {
            return CreateFileSystem(
                task.SourceProtocol,
                task.SourcePath,
                task.SourceFtpUseAuthentication,
                task.SourceFtpUsername,
                task.SourceFtpEncryptedPassword);
        }

        public static IFileSystem CreateDestFileSystem(SyncTaskDefinition task)
        {
            return CreateFileSystem(
                task.DestProtocol,
                task.DestPath,
                task.DestFtpUseAuthentication,
                task.DestFtpUsername,
                task.DestFtpEncryptedPassword);
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

        public static IFileSystem CreateFileSystem(
            string protocol,
            string path,
            bool ftpUseAuthentication = false,
            string? ftpUsername = null,
            string? ftpEncryptedPassword = null)
        {
            if (string.Equals(protocol, "FTP", StringComparison.OrdinalIgnoreCase))
            {
                return CreateFtpFileSystem(path, ftpUseAuthentication, ftpUsername, ftpEncryptedPassword);
            }

            return new LocalFileSystem(path);
        }

        private static IFileSystem CreateFtpFileSystem(
            string path,
            bool ftpUseAuthentication,
            string? ftpUsername,
            string? ftpEncryptedPassword)
        {
            if (!Uri.TryCreate(path, UriKind.Absolute, out var uri) || !string.Equals(uri.Scheme, "ftp", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("FTP 路径格式无效，请使用 ftp://host[:port]/basePath");
            }

            if (!string.IsNullOrWhiteSpace(uri.UserInfo))
            {
                throw new InvalidOperationException("FTP 路径中不允许内嵌用户名或密码，请在认证配置中单独填写。");
            }

            var host = uri.Host;
            var port = uri.IsDefaultPort ? 21 : uri.Port;
            var basePath = string.IsNullOrWhiteSpace(uri.AbsolutePath)
                ? "/"
                : Uri.UnescapeDataString(uri.AbsolutePath);
            var username = "anonymous";
            var password = "anonymous@";

            if (ftpUseAuthentication)
            {
                if (string.IsNullOrWhiteSpace(ftpUsername))
                {
                    throw new InvalidOperationException("FTP 用户名不能为空。");
                }

                username = ftpUsername.Trim();
                password = FtpCredentialProtector.Unprotect(ftpEncryptedPassword ?? string.Empty);
                if (string.IsNullOrEmpty(password))
                {
                    throw new InvalidOperationException("已启用 FTP 账号密码登录，但未找到可用密码。");
                }
            }

            return new FtpFileSystem(host, username, password, port, basePath);
        }
    }
}
