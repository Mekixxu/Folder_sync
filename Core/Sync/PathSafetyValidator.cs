using System;
using System.IO;
using FolderSync.Core.Config;

namespace FolderSync.Core.Sync
{
    public static class PathSafetyValidator
    {
        public static void EnsureSourceDestDoNotOverlap(SyncTaskDefinition task)
        {
            var sourceLocal = !string.Equals(task.SourceProtocol, "FTP", StringComparison.OrdinalIgnoreCase);
            var destLocal = !string.Equals(task.DestProtocol, "FTP", StringComparison.OrdinalIgnoreCase);

            if (sourceLocal && destLocal)
            {
                EnsureLocalPathsDoNotOverlap(task.SourcePath, task.DestPath);
                return;
            }

            if (!sourceLocal && !destLocal)
            {
                EnsureFtpPathsDoNotOverlap(task.SourcePath, task.DestPath);
            }
        }

        private static void EnsureLocalPathsDoNotOverlap(string source, string dest)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(dest))
            {
                throw new InvalidOperationException("本地源路径和目标路径不能为空。");
            }

            var sourceFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
            var destFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dest));
            var sourceToDest = Path.GetRelativePath(sourceFull, destFull);
            var destToSource = Path.GetRelativePath(destFull, sourceFull);

            var overlap = sourceToDest == "."
                          || (!Path.IsPathRooted(sourceToDest) && sourceToDest != ".." && !sourceToDest.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                          || destToSource == "."
                          || (!Path.IsPathRooted(destToSource) && destToSource != ".." && !destToSource.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));

            if (overlap)
            {
                throw new InvalidOperationException("源目录和目标目录不能相同或互为父子目录，否则递归同步可能造成数据丢失。");
            }
        }

        private static void EnsureFtpPathsDoNotOverlap(string source, string dest)
        {
            if (!Uri.TryCreate(source, UriKind.Absolute, out var sourceUri) ||
                !Uri.TryCreate(dest, UriKind.Absolute, out var destUri) ||
                !sourceUri.Scheme.Equals("ftp", StringComparison.OrdinalIgnoreCase) ||
                !destUri.Scheme.Equals("ftp", StringComparison.OrdinalIgnoreCase))
            {
                return; // 无效 FTP 路径由现有校验负责报错
            }

            if (!string.Equals(sourceUri.Host, destUri.Host, StringComparison.OrdinalIgnoreCase) ||
                sourceUri.Port != destUri.Port)
            {
                return;
            }

            var a = NormalizeFtpPath(sourceUri.AbsolutePath);
            var b = NormalizeFtpPath(destUri.AbsolutePath);
            if (a == b || a.StartsWith(b + "/", StringComparison.OrdinalIgnoreCase) || b.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("FTP 源目录和目标目录不能相同或互为父子目录。");
            }
        }

        private static string NormalizeFtpPath(string path)
        {
            return (path ?? "/").Replace('\\', '/').Trim().TrimEnd('/').ToLowerInvariant();
        }
    }
}