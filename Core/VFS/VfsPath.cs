using System;

namespace FolderSync.Core.VFS
{
    /// <summary>
    /// VFS 内部使用的路径辅助方法。
    /// 临时文件始终与最终目标位于同一目录，避免跨卷/跨挂载点提交。
    /// </summary>
    internal static class VfsPath
    {
        public static string CreateSiblingTemporaryPath(string path, string suffix)
        {
            var normalized = (path ?? string.Empty).Replace('\\', '/').TrimStart('/');
            var lastSlash = normalized.LastIndexOf('/');
            var directory = lastSlash >= 0 ? normalized[..lastSlash] : string.Empty;
            var name = lastSlash >= 0 ? normalized[(lastSlash + 1)..] : normalized;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = "root";
            }

            // 避免在接近文件名长度上限时因临时后缀导致创建失败。
            if (name.Length > 48)
            {
                name = name[..48];
            }

            var tempName = $".{name}.foldersync-{Guid.NewGuid():N}.{suffix}";
            return string.IsNullOrEmpty(directory) ? tempName : $"{directory}/{tempName}";
        }

        public static string NormalizeRelativePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || path == "." || path == "/" || path == "\\")
            {
                return string.Empty;
            }

            return path.Replace('\\', '/').Trim('/');
        }

        public static bool IsSameOrUnderPath(string path, string root)
        {
            var normalizedPath = NormalizeRelativePath(path);
            var normalizedRoot = NormalizeRelativePath(root);
            if (string.IsNullOrEmpty(normalizedRoot))
            {
                return true;
            }

            return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase)
                   || normalizedPath.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase);
        }
    }
}
