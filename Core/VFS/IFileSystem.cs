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
        /// 最近一次递归列举中失败并被跳过的子目录（相对路径）。
        /// 非空表示扫描结果不完整，调用方不得据此生成删除动作或刷新可信基线。
        /// </summary>
        IReadOnlyList<string> LastScanFailures { get; }

        /// <summary>
        /// 最近一次列举是否完整。
        /// </summary>
        bool LastScanComplete => LastScanFailures.Count == 0;

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
        /// 以复制场景专用方式打开源文件读取。
        /// 本地/SMB 实现应尽可能加排它锁，防止复制期间被修改；
        /// 不支持文件锁的协议（如 FTP）可降级为普通读取。
        /// </summary>
        Task<Stream> OpenReadForCopyAsync(string path, CancellationToken cancellationToken = default);

        /// <summary>
        /// 打开文件用于写入（如果目录不存在会自动创建）。
        /// 注意：该接口会直接截断已有目标，仅用于非覆盖风险的场景；
        /// 同步复制必须使用 <see cref="OpenWriteToTempAsync"/> + <see cref="CommitTempFileAsync"/>。
        /// </summary>
        Task<Stream> OpenWriteAsync(string path, CancellationToken cancellationToken = default);

        /// <summary>
        /// 打开同目录临时文件用于写入。失败时只允许清理该临时文件，绝不能删除最终目标。
        /// </summary>
        Task<Stream> OpenWriteToTempAsync(string tempPath, CancellationToken cancellationToken = default);

        /// <summary>
        /// 将已完整写入并关闭的临时文件提交为最终目标。
        /// 已有目标必须保留到提交点；提交失败时不得丢失旧目标。
        /// </summary>
        Task CommitTempFileAsync(string tempPath, string targetPath, CancellationToken cancellationToken = default);

        /// <summary>
        /// 清理本次创建但未提交的临时文件。实现不得删除最终目标。
        /// </summary>
        Task CleanupTempFileAsync(string tempPath, CancellationToken cancellationToken = default);

        /// <summary>
        /// 设置文件最后修改时间（协议不支持时可抛出/由调用方降级）。
        /// </summary>
        Task SetLastWriteTimeAsync(string path, DateTime lastWriteTimeUtc, CancellationToken cancellationToken = default);

        /// <summary>
        /// 删除指定文件
        /// </summary>
        Task DeleteFileAsync(string path, CancellationToken cancellationToken = default);

        /// <summary>
        /// 创建指定目录
        /// </summary>
        Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);

        /// <summary>
        /// 仅当目录为空时删除该目录。返回 false 表示目录非空或不存在，调用方应保留该目录。
        /// 禁止通过该接口递归删除目录内容。
        /// </summary>
        Task<bool> DeleteDirectoryIfEmptyAsync(string path, CancellationToken cancellationToken = default);

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
