using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderSync.Core.VFS
{
    /// <summary>
    /// 本地和 SMB 文件系统实现。
    /// SMB 路径（如 \\192.168.1.10\share）可以直接使用 System.IO 原生处理。
    /// </summary>
    public class LocalFileSystem : IFileSystem
    {
        private readonly string _basePath;
        private static readonly IReadOnlyList<string> EmptyScanFailures = Array.Empty<string>();

        public string RootIdentifier => $"local:{_basePath.ToLowerInvariant()}";
        public IReadOnlyList<string> LastScanFailures => EmptyScanFailures;
        public bool LastScanComplete => true;

        public LocalFileSystem(string basePath)
        {
            if (string.IsNullOrWhiteSpace(basePath))
            {
                throw new ArgumentException("Base path cannot be empty", nameof(basePath));
            }
            
            // 规范化基础路径
            _basePath = Path.GetFullPath(basePath);
        }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            // 本地和已被系统挂载/认证的 SMB 路径不需要显式连接。
            // 但我们可以在这里验证根目录是否存在并具有权限。
            if (!Directory.Exists(_basePath))
            {
                throw new DirectoryNotFoundException($"The base path '{_basePath}' does not exist or is not accessible.");
            }
            return Task.CompletedTask;
        }

        public Task<IEnumerable<FileItem>> ListFilesAsync(string path = "", bool recursive = true, CancellationToken cancellationToken = default)
        {
            var fullPath = GetFullPath(path);
            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            
            var result = new List<FileItem>();
            if (!Directory.Exists(fullPath)) 
                return Task.FromResult<IEnumerable<FileItem>>(result);

            var directoryInfo = new DirectoryInfo(fullPath);
            
            // 添加所有文件夹
            foreach (var dir in directoryInfo.EnumerateDirectories("*", searchOption))
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(new FileItem
                {
                    Name = dir.Name,
                    Path = GetRelativePath(dir.FullName),
                    IsDirectory = true,
                    LastWriteTime = dir.LastWriteTimeUtc,
                    Size = 0
                });
            }

            // 添加所有文件
            foreach (var file in directoryInfo.EnumerateFiles("*", searchOption))
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(new FileItem
                {
                    Name = file.Name,
                    Path = GetRelativePath(file.FullName),
                    IsDirectory = false,
                    LastWriteTime = file.LastWriteTimeUtc,
                    Size = file.Length
                });
            }

            return Task.FromResult<IEnumerable<FileItem>>(result);
        }

        public Task<FileItem?> GetFileInfoAsync(string path, CancellationToken cancellationToken = default)
        {
            var fullPath = GetFullPath(path);
            
            if (File.Exists(fullPath))
            {
                var info = new FileInfo(fullPath);
                return Task.FromResult<FileItem?>(new FileItem
                {
                    Name = info.Name,
                    Path = GetRelativePath(info.FullName),
                    IsDirectory = false,
                    LastWriteTime = info.LastWriteTimeUtc,
                    Size = info.Length
                });
            }
            
            if (Directory.Exists(fullPath))
            {
                var info = new DirectoryInfo(fullPath);
                return Task.FromResult<FileItem?>(new FileItem
                {
                    Name = info.Name,
                    Path = GetRelativePath(info.FullName),
                    IsDirectory = true,
                    LastWriteTime = info.LastWriteTimeUtc,
                    Size = 0
                });
            }
            
            return Task.FromResult<FileItem?>(null);
        }

        public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
        {
            return OpenReadInternalAsync(path, FileShare.Read);
        }

        public Task<Stream> OpenReadForCopyAsync(string path, CancellationToken cancellationToken = default)
        {
            // 复制时使用排它读锁，阻止其它进程在复制过程中修改该文件。
            return OpenReadInternalAsync(path, FileShare.None);
        }

        private Task<Stream> OpenReadInternalAsync(string path, FileShare fileShare)
        {
            var fullPath = GetFullPath(path);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"File not found: {fullPath}");
            }

            return Task.FromResult<Stream>(new FileStream(fullPath, FileMode.Open, FileAccess.Read, fileShare, 81920, FileOptions.SequentialScan));
        }

        public Task<Stream> OpenWriteAsync(string path, CancellationToken cancellationToken = default)
        {
            return OpenWriteInternalAsync(path, FileMode.Create);
        }

        public Task<Stream> OpenWriteToTempAsync(string tempPath, CancellationToken cancellationToken = default)
        {
            // CreateNew 确保不会覆盖同目录中已存在的临时文件。
            return OpenWriteInternalAsync(tempPath, FileMode.CreateNew);
        }

        private Task<Stream> OpenWriteInternalAsync(string path, FileMode mode)
        {
            var fullPath = GetFullPath(path);
            var dir = Path.GetDirectoryName(fullPath);

            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            return Task.FromResult<Stream>(new FileStream(fullPath, mode, FileAccess.Write, FileShare.None));
        }

        public Task CommitTempFileAsync(string tempPath, string targetPath, CancellationToken cancellationToken = default)
        {
            var tempFullPath = GetFullPath(tempPath);
            var targetFullPath = GetFullPath(targetPath);
            var dir = Path.GetDirectoryName(targetFullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (!File.Exists(tempFullPath))
            {
                throw new FileNotFoundException($"Temporary file not found: {tempFullPath}", tempFullPath);
            }

            if (File.Exists(targetFullPath))
            {
                // 先把旧目标保留在 Replace 的原子替换点之前；失败时旧目标仍然存在。
                try
                {
                    File.Replace(tempFullPath, targetFullPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Move(tempFullPath, targetFullPath, overwrite: true);
                }
            }
            else
            {
                File.Move(tempFullPath, targetFullPath);
            }

            return Task.CompletedTask;
        }

        public Task CleanupTempFileAsync(string tempPath, CancellationToken cancellationToken = default)
        {
            var fullPath = GetFullPath(tempPath);
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }

            return Task.CompletedTask;
        }

        public Task SetLastWriteTimeAsync(string path, DateTime lastWriteTimeUtc, CancellationToken cancellationToken = default)
        {
            var fullPath = GetFullPath(path);
            File.SetLastWriteTimeUtc(fullPath, lastWriteTimeUtc.Kind == DateTimeKind.Utc
                ? lastWriteTimeUtc
                : lastWriteTimeUtc.ToUniversalTime());
            return Task.CompletedTask;
        }

        public Task DeleteFileAsync(string path, CancellationToken cancellationToken = default)
        {
            var fullPath = GetFullPath(path);
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
            return Task.CompletedTask;
        }

        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        {
            var fullPath = GetFullPath(path);
            if (!Directory.Exists(fullPath))
            {
                Directory.CreateDirectory(fullPath);
            }
            return Task.CompletedTask;
        }

        public Task<bool> DeleteDirectoryIfEmptyAsync(string path, CancellationToken cancellationToken = default)
        {
            var fullPath = GetFullPath(path);
            if (!Directory.Exists(fullPath))
            {
                return Task.FromResult(true);
            }

            if (Directory.EnumerateFileSystemEntries(fullPath).Any())
            {
                // 目录中仍有未授权/未选/失败项时，绝不递归删除。
                return Task.FromResult(false);
            }

            Directory.Delete(fullPath, recursive: false);
            return Task.FromResult(true);
        }

        public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Directory.Exists(GetFullPath(path)));
        }

        public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(File.Exists(GetFullPath(path)));
        }

        public void Dispose()
        {
            // Local file system doesn't need to dispose any persistent connection
        }

        /// <summary>
        /// 获取绝对路径（合并 BasePath 和传入的相对路径）
        /// </summary>
        private string GetFullPath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || relativePath == "/" || relativePath == "\\")
            {
                return _basePath;
            }

            // 安全合并，防止使用 .. 跳出 BasePath
            var baseRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_basePath));
            var combinedPath = Path.GetFullPath(Path.Combine(baseRoot, relativePath.TrimStart('/', '\\')));

            var relative = Path.GetRelativePath(baseRoot, combinedPath);
            var isRoot = relative == ".";
            var isChild = !Path.IsPathRooted(relative)
                          && relative != ".."
                          && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);

            if (!isRoot && !isChild)
            {
                throw new UnauthorizedAccessException($"Access to path '{relativePath}' is denied as it's outside the base directory.");
            }

            return combinedPath;
        }

        /// <summary>
        /// 将绝对路径转换为相对于 BasePath 的相对路径，并统一使用正斜杠 (/)
        /// </summary>
        private string GetRelativePath(string fullPath)
        {
            var relative = Path.GetRelativePath(_basePath, fullPath);
            return relative == "." ? string.Empty : relative.Replace('\\', '/');
        }
    }
}
