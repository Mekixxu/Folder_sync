using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Core.VFS;

namespace FolderSync.Core.Diff
{
    /// <summary>
    /// 基于文件大小和最后修改时间的快速比对策略
    /// 速度快，但如果有文件大小和修改时间相同，内容被修改的情况，可能无法检测到
    /// </summary>
    public class SizeAndTimeDiffStrategy : IDiffStrategy
    {
        public Task<IEnumerable<SyncAction>> CompareAsync(
            IEnumerable<FileItem> sourceItems,
            IEnumerable<FileItem> destinationItems,
            IFileSystem sourceFs,
            IFileSystem destFs,
            bool isTwoWayOrMirror = false,
            CancellationToken cancellationToken = default)
        {
            var actions = new List<SyncAction>();
            
            // 使用区分大小写的忽略路径进行比较（如果是 Linux SMB 或 FTP，路径可能是大小写敏感的，但为了跨平台安全，这里暂用忽略大小写）
            var sourceDict = sourceItems.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
            var destDict = destinationItems.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);

            // 1. 查找源文件在目标文件中的变化（Create 或 Update）
            foreach (var src in sourceItems)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (destDict.TryGetValue(src.Path, out var dest))
                {
                    // 都是文件，且大小不同，或者源文件更新于目标文件
                    if (!src.IsDirectory && !dest.IsDirectory)
                    {
                        // 考虑到不同文件系统的精度（如 FAT32 的时间精度只有 2 秒），给予 2 秒的容差
                        if (src.Size != dest.Size || src.LastWriteTime > dest.LastWriteTime.AddSeconds(2))
                        {
                            actions.Add(new SyncAction(SyncActionType.Update, src, dest));
                        }
                    }
                }
                else
                {
                    // 目标中不存在
                    actions.Add(new SyncAction(SyncActionType.Create, src, null));
                }
            }

            // 2. 如果开启了双向或镜像模式，目标中多余的文件需要删除
            if (isTwoWayOrMirror)
            {
                foreach (var dest in destinationItems)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!sourceDict.ContainsKey(dest.Path))
                    {
                        actions.Add(new SyncAction(SyncActionType.Delete, null, dest));
                    }
                }
            }

            return Task.FromResult<IEnumerable<SyncAction>>(actions);
        }
    }
}
