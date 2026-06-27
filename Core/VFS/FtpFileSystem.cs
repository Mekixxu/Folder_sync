using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentFTP;
using Serilog;

namespace FolderSync.Core.VFS
{
    /// <summary>
    /// FTP 文件系统实现，使用 FluentFTP 客户端库处理连接和传输
    /// </summary>
    public class FtpFileSystem : IFileSystem
    {
        private readonly AsyncFtpClient _client;
        private readonly string _basePath;
        private readonly string _host;
        private readonly int _port;
        public string RootIdentifier => $"ftp:{_host}:{_port}:{_basePath.ToLowerInvariant()}";

        public FtpFileSystem(string host, string username, string password, int port = 21, string basePath = "/")
        {
            _host = host;
            _port = port;
            _client = new AsyncFtpClient(host, username, password, port);
            
            // 确保基础路径始终以 "/" 结尾
            _basePath = string.IsNullOrWhiteSpace(basePath) ? "/" : basePath;
            if (!_basePath.EndsWith("/"))
            {
                _basePath += "/";
            }
        }

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (!_client.IsConnected)
            {
                await _client.AutoConnect(cancellationToken);
            }
        }

        public async Task<IEnumerable<FileItem>> ListFilesAsync(string path = "", bool recursive = true, CancellationToken cancellationToken = default)
        {
            await ConnectAsync(cancellationToken);
            var fullPath = GetFullPath(path);

            if (!recursive)
            {
                return await ListSingleDirectoryAsync(fullPath, cancellationToken);
            }

            return await ListDirectoriesBreadthFirstAsync(fullPath, cancellationToken);
        }

        public async Task<FileItem?> GetFileInfoAsync(string path, CancellationToken cancellationToken = default)
        {
            await ConnectAsync(cancellationToken);
            var fullPath = GetFullPath(path);
            
            var item = await _client.GetObjectInfo(fullPath, token: cancellationToken);
            if (item == null) return null;

            return new FileItem
            {
                Name = item.Name,
                Path = GetRelativePath(item.FullName),
                IsDirectory = item.Type == FtpObjectType.Directory,
                LastWriteTime = item.Modified,
                Size = item.Size
            };
        }

        public async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
        {
            await ConnectAsync(cancellationToken);
            var fullPath = GetFullPath(path);
            
            // 使用二进制模式打开进行读取流
            return await _client.OpenRead(fullPath, FtpDataType.Binary, 0, true, cancellationToken);
        }

        public Task<Stream> OpenReadForCopyAsync(string path, CancellationToken cancellationToken = default)
        {
            // FTP 协议通常不提供跨客户端文件锁，这里降级为普通读取。
            return OpenReadAsync(path, cancellationToken);
        }

        public async Task<Stream> OpenWriteAsync(string path, CancellationToken cancellationToken = default)
        {
            await ConnectAsync(cancellationToken);
            var fullPath = GetFullPath(path);
            var dir = GetDirectoryName(fullPath);
            
            // 确保写入的文件夹存在
            if (!string.IsNullOrEmpty(dir))
            {
                if (!await _client.DirectoryExists(dir, cancellationToken))
                {
                    await _client.CreateDirectory(dir, cancellationToken);
                }
            }
            
            return await _client.OpenWrite(fullPath, FtpDataType.Binary, true, cancellationToken);
        }

        public async Task DeleteFileAsync(string path, CancellationToken cancellationToken = default)
        {
            await ConnectAsync(cancellationToken);
            var fullPath = GetFullPath(path);
            
            if (await _client.FileExists(fullPath, cancellationToken))
            {
                await _client.DeleteFile(fullPath, cancellationToken);
            }
        }

        public async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        {
            await ConnectAsync(cancellationToken);
            var fullPath = GetFullPath(path);
            
            if (!await _client.DirectoryExists(fullPath, cancellationToken))
            {
                await _client.CreateDirectory(fullPath, cancellationToken);
            }
        }

        public async Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken = default)
        {
            await ConnectAsync(cancellationToken);
            var fullPath = GetFullPath(path);
            
            if (await _client.DirectoryExists(fullPath, cancellationToken))
            {
                // FTP 中通常删除目录需要先递归删除里面的文件
                // 但 FluentFTP 提供了帮助我们执行清理的功能：
                await _client.DeleteDirectory(fullPath, cancellationToken);
            }
        }

        public async Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken = default)
        {
            await ConnectAsync(cancellationToken);
            return await _client.DirectoryExists(GetFullPath(path), cancellationToken);
        }

        public async Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
        {
            await ConnectAsync(cancellationToken);
            return await _client.FileExists(GetFullPath(path), cancellationToken);
        }

        public void Dispose()
        {
            if (_client.IsConnected)
            {
                _client.Disconnect();
            }
            _client.Dispose();
        }

        /// <summary>
        /// 获取绝对路径（合并 FTP BasePath 和传入的相对路径）
        /// </summary>
        private string GetFullPath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || relativePath == "/" || relativePath == "\\")
            {
                return _basePath.TrimEnd('/');
            }
            
            var relPathNormalized = relativePath.TrimStart('/', '\\').Replace('\\', '/');
            return _basePath + relPathNormalized;
        }

        private async Task<List<FileItem>> ListSingleDirectoryAsync(string fullPath, CancellationToken cancellationToken)
        {
            try
            {
                var ftpItems = await _client.GetListing(fullPath, FtpListOption.Auto, cancellationToken);
                var result = new List<FileItem>();

                foreach (var item in ftpItems)
                {
                    if (!TryMapListItem(item, out var mapped))
                    {
                        continue;
                    }

                    result.Add(mapped);
                }

                return result;
            }
            catch (Exception ex)
            {
                throw new IOException(
                    $"FTP 列举目录失败：{DescribeDirectory(fullPath)}。{ex.Message}",
                    ex);
            }
        }

        private async Task<List<FileItem>> ListDirectoriesBreadthFirstAsync(string rootPath, CancellationToken cancellationToken)
        {
            var result = new List<FileItem>();
            var pending = new Queue<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            pending.Enqueue(rootPath);
            visited.Add(NormalizePath(rootPath));

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var currentPath = pending.Dequeue();
                List<FileItem> items;
                try
                {
                    items = await ListSingleDirectoryAsync(currentPath, cancellationToken);
                }
                catch (Exception ex) when (!string.Equals(NormalizePath(currentPath), NormalizePath(rootPath), StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warning(ex, "FTP 递归列举跳过异常子目录：{Directory}", DescribeDirectory(currentPath));
                    continue;
                }

                foreach (var item in items)
                {
                    result.Add(item);

                    if (!item.IsDirectory)
                    {
                        continue;
                    }

                    var childFullPath = GetFullPath(item.Path);
                    var normalizedChildPath = NormalizePath(childFullPath);
                    if (visited.Add(normalizedChildPath))
                    {
                        pending.Enqueue(childFullPath);
                    }
                }
            }

            return result;
        }

        private bool TryMapListItem(FtpListItem item, out FileItem mappedItem)
        {
            mappedItem = null!;

            // 跳过上层目录的指针（如 .. 和 .）
            if (item.Name == "." || item.Name == "..")
            {
                return false;
            }

            if (item.Type != FtpObjectType.Directory && item.Type != FtpObjectType.File)
            {
                return false;
            }

            var fullName = ResolveFullName(item);
            mappedItem = new FileItem
            {
                Name = item.Name,
                Path = GetRelativePath(fullName),
                IsDirectory = item.Type == FtpObjectType.Directory,
                LastWriteTime = item.Modified,
                Size = item.Size
            };

            return true;
        }

        private string ResolveFullName(FtpListItem item)
        {
            if (!string.IsNullOrWhiteSpace(item.FullName))
            {
                return NormalizePath(item.FullName);
            }

            if (string.IsNullOrWhiteSpace(item.Name))
            {
                return _basePath.TrimEnd('/');
            }

            return NormalizePath($"{_basePath.TrimEnd('/')}/{item.Name.TrimStart('/')}");
        }

        /// <summary>
        /// 将 FTP 绝对路径转换为相对于 BasePath 的相对路径，并统一使用正斜杠 (/)
        /// </summary>
        private string GetRelativePath(string fullPath)
        {
            var normalizedBasePath = NormalizePath(_basePath);
            var normalizedFullPath = NormalizePath(fullPath);

            if (normalizedFullPath.StartsWith(normalizedBasePath, StringComparison.OrdinalIgnoreCase))
            {
                var rel = normalizedFullPath.Substring(normalizedBasePath.Length);
                return rel.TrimStart('/');
            }

            return normalizedFullPath.TrimStart('/');
        }

        /// <summary>
        /// 从路径中提取目录名
        /// </summary>
        private string GetDirectoryName(string path)
        {
            var lastSlash = path.LastIndexOf('/');
            if (lastSlash >= 0)
            {
                return path.Substring(0, lastSlash);
            }
            return string.Empty;
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "/";
            }

            var normalized = path.Replace('\\', '/').Trim();
            if (!normalized.StartsWith("/"))
            {
                normalized = "/" + normalized;
            }

            while (normalized.Contains("//", StringComparison.Ordinal))
            {
                normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
            }

            return normalized.TrimEnd('/');
        }

        private string DescribeDirectory(string fullPath)
        {
            var relative = GetRelativePath(fullPath);
            return string.IsNullOrWhiteSpace(relative) ? "/" : relative;
        }
    }
}
