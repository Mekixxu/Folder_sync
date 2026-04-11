using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Core.VFS;

namespace FolderSync.Core.Diff
{
    /// <summary>
    /// 基于文件内容的哈希（如 SHA256）的深度比对策略
    /// 计算资源消耗高，网络传输慢，但是最准确，适用于高安全性的文件同步
    /// </summary>
    public class ChecksumDiffStrategy : IDiffStrategy
    {
        public async Task<IEnumerable<SyncAction>> CompareAsync(
            IEnumerable<FileItem> sourceItems,
            IEnumerable<FileItem> destinationItems,
            IFileSystem sourceFs,
            IFileSystem destFs,
            bool isTwoWayOrMirror = false,
            CancellationToken cancellationToken = default)
        {
            var actions = new List<SyncAction>();
            
            var sourceDict = sourceItems.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
            var destDict = destinationItems.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);

            foreach (var src in sourceItems)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (destDict.TryGetValue(src.Path, out var dest))
                {
                    if (!src.IsDirectory && !dest.IsDirectory)
                    {
                        // 快速过滤：如果文件大小都不一样，内容肯定不一样，无需计算哈希
                        if (src.Size != dest.Size)
                        {
                            actions.Add(new SyncAction(SyncActionType.Update, src, dest));
                        }
                        else
                        {
                            // 大小一样，我们需要深度比较文件流的哈希值
                            var srcHash = await ComputeHashAsync(sourceFs, src.Path, cancellationToken);
                            var destHash = await ComputeHashAsync(destFs, dest.Path, cancellationToken);

                            if (srcHash != destHash)
                            {
                                actions.Add(new SyncAction(SyncActionType.Update, src, dest));
                            }
                        }
                    }
                }
                else
                {
                    actions.Add(new SyncAction(SyncActionType.Create, src, null));
                }
            }

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

            return actions;
        }

        /// <summary>
        /// 从指定的文件系统和路径中计算文件的 SHA256 散列值
        /// </summary>
        private async Task<string> ComputeHashAsync(IFileSystem fs, string path, CancellationToken cancellationToken)
        {
            using var stream = await fs.OpenReadAsync(path, cancellationToken);
            using var sha256 = SHA256.Create();
            var hashBytes = await sha256.ComputeHashAsync(stream, cancellationToken);
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }
    }
}
