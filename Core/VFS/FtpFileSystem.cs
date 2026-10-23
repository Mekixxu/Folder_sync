using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using FluentFTP;
using Serilog;

namespace FolderSync.Core.VFS
{
    /// <summary>
    /// FTP 文件系统实现，使用 FluentFTP 客户端库处理连接和传输
    /// </summary>
    public class FtpFileSystem : IFileSystem
    {
        private const int DirectoryListingRetryCount = 3;
        private const int DirectoryListingRetryDelayMilliseconds = 500;
        private readonly AsyncFtpClient _client;
        private readonly string _basePath;
        private readonly string _host;
        private readonly int _port;
        private readonly List<string> _lastScanFailures = new();

        public string RootIdentifier => $"ftp:{_host}:{_port}:{_basePath.ToLowerInvariant()}";
        public IReadOnlyList<string> LastScanFailures => _lastScanFailures;
        public bool LastScanComplete => _lastScanFailures.Count == 0;

        public FtpFileSystem(string host, string username, string password, int port = 21, string basePath = "/")
        {
            _host = host;
            _port = port;
            _client = new AsyncFtpClient(host, username, password, port);
            _client.Config.TimeConversion = FtpDate.UTC;
            _client.Config.ServerTimeZone = TimeZoneInfo.Utc;
            _client.Config.ClientTimeZone = TimeZoneInfo.Utc;
            
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

        public async Task<IEnumerable<FileItem>> ListFilesAsync(
            string path = "",
            bool recursive = true,
            CancellationToken cancellationToken = default)
        {
            _lastScanFailures.Clear();
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

            var relativePath = ResolveRelativePathForObjectInfo(item, path);
            return new FileItem
            {
                Name = item.Name,
                Path = relativePath,
                IsDirectory = item.Type == FtpObjectType.Directory,
                LastWriteTime = NormalizeTimestamp(item.Modified),
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

        public Task<Stream> OpenWriteToTempAsync(string tempPath, CancellationToken cancellationToken = default)
        {
            // 临时文件路径由调用方保证唯一；仍按普通上传路径创建，以便与目标同目录。
            return OpenWriteAsync(tempPath, cancellationToken);
        }

        public async Task CommitTempFileAsync(string tempPath, string targetPath, CancellationToken cancellationToken = default)
        {
            await ConnectAsync(cancellationToken);
            var tempFullPath = GetFullPath(tempPath);
            var targetFullPath = GetFullPath(targetPath);

            if (!await _client.FileExists(tempFullPath, cancellationToken))
            {
                throw new IOException($"FTP 临时文件不存在，无法提交：{DescribeDirectory(tempFullPath)}");
            }

            string? backupFullPath = null;
            var targetExisted = await _client.FileExists(targetFullPath, cancellationToken);
            if (targetExisted)
            {
                backupFullPath = GetFullPath(VfsPath.CreateSiblingTemporaryPath(targetPath, "bak"));
                // FTP 没有跨文件事务：先把旧目标改名保留，再尝试提交新文件。
                await _client.Rename(targetFullPath, backupFullPath, cancellationToken);
            }

            try
            {
                await _client.Rename(tempFullPath, targetFullPath, cancellationToken);
            }
            catch (Exception commitException)
            {
                if (backupFullPath != null)
                {
                    try
                    {
                        await _client.Rename(backupFullPath, targetFullPath, cancellationToken);
                    }
                    catch (Exception restoreException)
                    {
                        throw new IOException(
                            $"FTP 提交失败，且旧目标恢复失败。旧目标备份位于：{DescribeDirectory(backupFullPath)}",
                            new AggregateException(commitException, restoreException));
                    }
                }

                throw;
            }

            if (backupFullPath != null)
            {
                try
                {
                    await _client.DeleteFile(backupFullPath, cancellationToken);
                }
                catch (Exception ex)
                {
                    // 备份清理失败不否定已经成功的提交，但必须保留可追溯日志。
                    Log.Warning(ex, "FTP 提交成功，但旧目标备份清理失败：{BackupPath}", DescribeDirectory(backupFullPath));
                }
            }
        }

        public async Task CleanupTempFileAsync(string tempPath, CancellationToken cancellationToken = default)
        {
            await ConnectAsync(cancellationToken);
            var fullPath = GetFullPath(tempPath);
            if (await _client.FileExists(fullPath, cancellationToken))
            {
                await _client.DeleteFile(fullPath, cancellationToken);
            }
        }

        public async Task SetLastWriteTimeAsync(string path, DateTime lastWriteTimeUtc, CancellationToken cancellationToken = default)
        {
            await ConnectAsync(cancellationToken);
            var utc = lastWriteTimeUtc.Kind == DateTimeKind.Utc
                ? lastWriteTimeUtc
                : lastWriteTimeUtc.ToUniversalTime();
            await _client.SetModifiedTime(GetFullPath(path), utc, cancellationToken);
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

        public async Task<bool> DeleteDirectoryIfEmptyAsync(string path, CancellationToken cancellationToken = default)
        {
            await ConnectAsync(cancellationToken);
            var fullPath = GetFullPath(path);

            if (!await _client.DirectoryExists(fullPath, cancellationToken))
            {
                return true;
            }

            var listing = await _client.GetListing(fullPath, FtpListOption.Auto, cancellationToken);
            if (listing.Any(item => item.Name != "." && item.Name != ".."))
            {
                return false;
            }

            // FluentFTP 的 DeleteDirectory 会递归删除内容；这里必须使用底层 RMD，
            // 由服务器在目录非空时拒绝，避免隐式删除未授权项。
            await _client.Execute($"RMD {fullPath}", cancellationToken);
            return true;
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

            if (relativePath.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
            {
                throw new UnauthorizedAccessException($"FTP path '{relativePath}' contains control characters and is not allowed.");
            }

            var relNormalized = relativePath.Replace('\\', '/').Trim().TrimStart('/');
            var baseSegments = _basePath.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
            var segments = relNormalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var combined = new List<string>(baseSegments);

            foreach (var segment in segments)
            {
                if (segment is "." or "")
                {
                    continue;
                }

                if (segment == "..")
                {
                    throw new UnauthorizedAccessException($"FTP path '{relativePath}' contains '..' and is not allowed.");
                }

                combined.Add(segment);
            }

            return "/" + string.Join('/', combined);
        }

        private async Task<List<FileItem>> ListSingleDirectoryAsync(
            string fullPath,
            CancellationToken cancellationToken)
        {
            Exception? lastException = null;
            for (var attempt = 1; attempt <= DirectoryListingRetryCount; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await ConnectAsync(cancellationToken);
                    var ftpItems = await _client.GetListing(fullPath, FtpListOption.Auto, cancellationToken);
                    var result = new List<FileItem>();

                    foreach (var item in ftpItems)
                    {
                        if (!TryMapListItem(item, fullPath, out var mapped))
                        {
                            continue;
                        }

                        result.Add(mapped);
                    }

                    return result;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (attempt < DirectoryListingRetryCount)
                {
                    lastException = ex;
                    Log.Warning(
                        ex,
                        "FTP 列举目录失败，准备重试：{Directory}（{Attempt}/{MaxAttempts}）",
                        DescribeDirectory(fullPath),
                        attempt,
                        DirectoryListingRetryCount);

                    ResetConnection();
                    await Task.Delay(DirectoryListingRetryDelayMilliseconds, cancellationToken);
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    break;
                }
            }

            throw new IOException(
                $"FTP 列举目录失败：{DescribeDirectory(fullPath)}。{lastException?.Message}",
                lastException);
        }

        private async Task<List<FileItem>> ListDirectoriesBreadthFirstAsync(
            string rootPath,
            CancellationToken cancellationToken)
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
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (!string.Equals(NormalizePath(currentPath), NormalizePath(rootPath), StringComparison.OrdinalIgnoreCase))
                {
                    // 记录不完整扫描，调用方禁止据此生成删除动作或刷新基线。
                    var failedDirectory = DescribeDirectory(currentPath);
                    _lastScanFailures.Add(failedDirectory);
                    Log.Warning(ex, "FTP 递归列举跳过异常子目录：{Directory}", failedDirectory);
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

            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }

        private bool TryMapListItem(FtpListItem item, string currentDirectoryFullPath, out FileItem mappedItem)
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

            var fullName = ResolveFullName(item, currentDirectoryFullPath);
            mappedItem = new FileItem
            {
                Name = item.Name,
                Path = GetRelativePath(fullName),
                IsDirectory = item.Type == FtpObjectType.Directory,
                LastWriteTime = NormalizeTimestamp(item.Modified),
                Size = item.Size
            };

            return true;
        }

        private void ResetConnection()
        {
            try
            {
                if (_client.IsConnected)
                {
                    _client.Disconnect();
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "FTP 重置连接时发生异常，将继续尝试重新连接。");
            }
        }

        private string ResolveFullName(FtpListItem item, string currentDirectoryFullPath)
        {
            if (!string.IsNullOrWhiteSpace(item.FullName))
            {
                var rawFullName = item.FullName.Replace('\\', '/').Trim();
                if (rawFullName.StartsWith("/", StringComparison.Ordinal))
                {
                    return NormalizePath(rawFullName);
                }

                return NormalizePath($"{NormalizePath(currentDirectoryFullPath).TrimEnd('/')}/{rawFullName.TrimStart('/')}");
            }

            if (string.IsNullOrWhiteSpace(item.Name))
            {
                return NormalizePath(currentDirectoryFullPath);
            }

            return NormalizePath($"{NormalizePath(currentDirectoryFullPath).TrimEnd('/')}/{item.Name.TrimStart('/')}");
        }

        private string ResolveRelativePathForObjectInfo(FtpListItem item, string requestedRelativePath)
        {
            var mappedRelativePath = GetRelativePath(item.FullName);
            var normalizedRequestedPath = NormalizeRelativeItemPath(requestedRelativePath);

            if (string.IsNullOrWhiteSpace(normalizedRequestedPath))
            {
                return mappedRelativePath;
            }

            if (!string.Equals(
                    NormalizeRelativeItemPath(mappedRelativePath),
                    normalizedRequestedPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return normalizedRequestedPath;
            }

            return mappedRelativePath;
        }

        /// <summary>
        /// 将 FTP 绝对路径转换为相对于 BasePath 的相对路径，并统一使用正斜杠 (/)
        /// </summary>
        private string GetRelativePath(string fullPath)
        {
            var normalizedBasePath = NormalizePath(_basePath);
            var normalizedFullPath = NormalizePath(fullPath);

            if (string.Equals(normalizedFullPath, normalizedBasePath, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            var normalizedBasePrefix = normalizedBasePath == "/" ? "/" : normalizedBasePath + "/";
            if (normalizedFullPath.StartsWith(normalizedBasePrefix, StringComparison.OrdinalIgnoreCase))
            {
                var rel = normalizedFullPath.Substring(normalizedBasePrefix.Length);
                return rel.TrimStart('/');
            }

            return normalizedFullPath.TrimStart('/');
        }

        private static string NormalizeRelativeItemPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path == "." || path == "/" || path == "\\")
            {
                return string.Empty;
            }

            return path.Replace('\\', '/').Trim().Trim('/');
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

        private static DateTime NormalizeTimestamp(DateTime timestamp)
        {
            if (timestamp == DateTime.MinValue)
            {
                return timestamp;
            }

            return timestamp.Kind switch
            {
                DateTimeKind.Utc => timestamp,
                DateTimeKind.Local => timestamp.ToUniversalTime(),
                _ => DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)
            };
        }

        private string DescribeDirectory(string fullPath)
        {
            var relative = GetRelativePath(fullPath);
            return string.IsNullOrWhiteSpace(relative) ? "/" : relative;
        }
    }
}
