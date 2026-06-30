using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Core.VFS;

namespace FolderSync.Core.Diff
{
    /// <summary>
    /// 差异比对策略接口，用于对比两个文件夹的文件集合并生成操作计划
    /// </summary>
    public interface IDiffStrategy
    {
        /// <summary>
        /// 对比源和目标文件集合，返回同步操作列表
        /// </summary>
        /// <param name="sourceItems">经过滤后的源文件列表</param>
        /// <param name="destinationItems">经过滤后的目标文件列表</param>
        /// <param name="sourceFs">源文件系统实例（某些策略如 Checksum 需要读取文件流）</param>
        /// <param name="destFs">目标文件系统实例</param>
        /// <param name="isTwoWayOrMirror">是否开启双向同步或镜像同步（如果开启，目标多出的文件将生成 Delete 操作）</param>
        /// <param name="cancellationToken">取消令牌</param>
        Task<IEnumerable<SyncAction>> CompareAsync(
            IEnumerable<FileItem> sourceItems,
            IEnumerable<FileItem> destinationItems,
            IFileSystem sourceFs,
            IFileSystem destFs,
            bool isTwoWayOrMirror = false,
            CancellationToken cancellationToken = default);
    }
}
