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
        public string RootIdentifier => $"local:{_basePath.ToLowerInvariant()}";

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
            var fullPath = GetFullPath(path);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"File not found: {fullPath}");
            }
            
            return Task.FromResult<Stream>(new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read));
        }

        public Task<Stream> OpenWriteAsync(string path, CancellationToken cancellationToken = default)
        {
            var fullPath = GetFullPath(path);
            var dir = Path.GetDirectoryName(fullPath);
            
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            
            return Task.FromResult<Stream>(new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None));
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

        public Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken = default)
        {
            var fullPath = GetFullPath(path);
            if (Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, true); // true = recursive delete
            }
            return Task.CompletedTask;
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
            var combinedPath = Path.GetFullPath(Path.Combine(_basePath, relativePath.TrimStart('/', '\\')));
            
            if (!combinedPath.StartsWith(_basePath, StringComparison.OrdinalIgnoreCase))
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
