using System;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Core.Sync;
using FolderSync.Core.VFS;

namespace FolderSync.Core.Diff
{
    /// <summary>
    /// 基于文件内容的深度比对策略
    /// 固定采用 xxHash64（性能优先，允许极低概率碰撞）
    /// </summary>
    public class ChecksumDiffStrategy : IDiffStrategy
    {
        public async Task<IEnumerable<SyncAction>> CompareAsync(
            IEnumerable<FileItem> sourceItems,
            IEnumerable<FileItem> destinationItems,
            IFileSystem sourceFs,
            IFileSystem destFs,
            bool isTwoWayOrMirror = false,
            IProgress<TaskAnalysisProgressInfo>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var actions = new List<SyncAction>();
            long processedBytes = 0;
            
            var sourceList = sourceItems.ToList();
            var destinationList = destinationItems.ToList();
            var totalBytes = sourceList.Where(i => !i.IsDirectory).Sum(i => i.Size)
                + destinationList.Where(i => !i.IsDirectory).Sum(i => i.Size);
            var sourceDict = sourceList.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
            var destDict = destinationList.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);

            foreach (var src in sourceList)
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
                            processedBytes += src.Size + dest.Size;
                            ReportProgress(progress, src.Path, processedBytes, totalBytes);
                        }
                        else
                        {
                            // 大小一样，我们需要深度比较文件流的哈希值
                            var (srcHash, srcBytesRead) = await ComputeHashAsync(
                                sourceFs,
                                src.Path,
                                cancellationToken);
                            processedBytes += srcBytesRead;
                            ReportProgress(progress, src.Path, processedBytes, totalBytes);

                            var (destHash, destBytesRead) = await ComputeHashAsync(
                                destFs,
                                dest.Path,
                                cancellationToken);
                            processedBytes += destBytesRead;
                            ReportProgress(progress, dest.Path, processedBytes, totalBytes);

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
                    processedBytes += src.IsDirectory ? 0L : src.Size;
                    ReportProgress(progress, src.Path, processedBytes, totalBytes);
                }
            }

            if (isTwoWayOrMirror)
            {
                foreach (var dest in destinationList)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!sourceDict.ContainsKey(dest.Path))
                    {
                        actions.Add(new SyncAction(SyncActionType.Delete, null, dest));
                        processedBytes += dest.IsDirectory ? 0L : dest.Size;
                        ReportProgress(progress, dest.Path, processedBytes, totalBytes);
                    }
                }
            }

            ReportProgress(progress, string.Empty, totalBytes, totalBytes);

            return actions;
        }

        /// <summary>
        /// 从指定的文件系统和路径中计算文件散列值
        /// </summary>
        private async Task<(string Hash, long BytesRead)> ComputeHashAsync(
            IFileSystem fs,
            string path,
            CancellationToken cancellationToken)
        {
            using var stream = await fs.OpenReadAsync(path, cancellationToken);
            long bytesReadTotal = 0;
            byte[] hashBytes = await ComputeXxHash64Async(
                stream,
                bytesRead => bytesReadTotal += bytesRead,
                cancellationToken);
            return (Convert.ToHexString(hashBytes), bytesReadTotal);
        }

        private static async Task<byte[]> ComputeXxHash64Async(
            System.IO.Stream stream,
            Action<int>? onBytesRead,
            CancellationToken cancellationToken)
        {
            var hasher = new XxHash64();
            var buffer = new byte[81920];
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                hasher.Append(buffer.AsSpan(0, bytesRead));
                onBytesRead?.Invoke(bytesRead);
            }
            return hasher.GetCurrentHash();
        }

        private static void ReportProgress(
            IProgress<TaskAnalysisProgressInfo>? progress,
            string currentPath,
            long processedBytes,
            long totalBytes)
        {
            progress?.Report(new TaskAnalysisProgressInfo
            {
                Phase = TaskAnalysisPhase.Comparing,
                CurrentPath = currentPath,
                ProcessedBytes = Math.Min(processedBytes, totalBytes),
                TotalBytes = totalBytes
            });
        }
    }
}
