using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FolderSync.Core.VFS
{
    /// <summary>
    /// 虚拟文件系统抽象接口，用于统一 Local、SMB、FTP 等底层差异
    /// </summary>
    public interface IFileSystem : IDisposable
    {
        /// <summary>
        /// 文件系统根标识（用于状态库中的任务键推导）
        /// </summary>
        string RootIdentifier { get; }

        /// <summary>
        /// 连接到文件系统（如需要认证）
        /// </summary>
        Task ConnectAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 列出指定路径下的所有文件和文件夹
        /// </summary>
        Task<IEnumerable<FileItem>> ListFilesAsync(string path = "", bool recursive = true, CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取指定文件或目录的信息
        /// </summary>
        Task<FileItem?> GetFileInfoAsync(string path, CancellationToken cancellationToken = default);

        /// <summary>
        /// 打开文件用于读取
        /// </summary>
        Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default);

        /// <summary>
        /// 打开文件用于写入（如果目录不存在会自动创建）
        /// </summary>
        Task<Stream> OpenWriteAsync(string path, CancellationToken cancellationToken = default);

        /// <summary>
        /// 删除指定文件
        /// </summary>
        Task DeleteFileAsync(string path, CancellationToken cancellationToken = default);

        /// <summary>
        /// 创建指定目录
        /// </summary>
        Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);

        /// <summary>
        /// 删除指定目录及其内部所有文件
        /// </summary>
        Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken = default);

        /// <summary>
        /// 检查目录是否存在
        /// </summary>
        Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken = default);

        /// <summary>
        /// 检查文件是否存在
        /// </summary>
        Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default);
    }
}
