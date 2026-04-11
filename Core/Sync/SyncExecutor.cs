using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Core.Diff;
using FolderSync.Core.Filters;
using FolderSync.Core.VFS;

namespace FolderSync.Core.Sync
{
    /// <summary>
    /// 同步执行器：连接 VFS、Filter 和 Diff Engine
    /// 负责解析同步模式，执行比对，并最终调用文件系统进行数据传输
    /// </summary>
    public class SyncExecutor
    {
        private readonly IFileSystem _sourceFs;
        private readonly IFileSystem _destFs;
        private readonly IDiffStrategy _diffStrategy;
        private readonly FilterEngine _filterEngine;
        private readonly SyncMode _syncMode;

        // 可以在执行过程中上报进度的事件
        public event EventHandler<SyncProgressEventArgs>? ProgressChanged;
        public event EventHandler<SyncErrorEventArgs>? ErrorOccurred;

        public SyncExecutor(
            IFileSystem sourceFs, 
            IFileSystem destFs, 
            IDiffStrategy diffStrategy, 
            FilterEngine filterEngine,
            SyncMode syncMode)
        {
            _sourceFs = sourceFs ?? throw new ArgumentNullException(nameof(sourceFs));
            _destFs = destFs ?? throw new ArgumentNullException(nameof(destFs));
            _diffStrategy = diffStrategy ?? throw new ArgumentNullException(nameof(diffStrategy));
            _filterEngine = filterEngine ?? throw new ArgumentNullException(nameof(filterEngine));
            _syncMode = syncMode;
        }

        /// <summary>
        /// 启动同步任务
        /// </summary>
        public async Task<SyncReport> ExecuteAsync(CancellationToken cancellationToken = default)
        {
            var report = new SyncReport { StartTime = DateTime.UtcNow, SyncMode = _syncMode };

            try
            {
                // 1. 确保连接
                await _sourceFs.ConnectAsync(cancellationToken);
                await _destFs.ConnectAsync(cancellationToken);

                // 2. 获取源和目标文件列表
                var rawSourceItems = await _sourceFs.ListFilesAsync(cancellationToken: cancellationToken);
                var rawDestItems = await _destFs.ListFilesAsync(cancellationToken: cancellationToken);

                // 3. 通过过滤引擎过滤不需要处理的文件
                var sourceItems = _filterEngine.Filter(rawSourceItems).ToList();
                var destItems = _filterEngine.Filter(rawDestItems).ToList();

                // 4. 调用差异比对引擎，生成执行计划
                bool isMirrorOrTwoWay = _syncMode == SyncMode.OneWayMirror || _syncMode == SyncMode.TwoWay;
                var actions = await _diffStrategy.CompareAsync(sourceItems, destItems, _sourceFs, _destFs, isMirrorOrTwoWay, cancellationToken);

                // 5. 过滤和调整基于同步模式的动作
                var finalActions = ApplySyncModeFilter(actions);
                report.TotalActions = finalActions.Count;

                // 6. 执行同步动作
                await ExecuteActionsAsync(finalActions, report, cancellationToken);
            }
            catch (Exception ex)
            {
                report.ErrorMessage = ex.Message;
                ErrorOccurred?.Invoke(this, new SyncErrorEventArgs(ex, "Global execution failed"));
            }
            finally
            {
                report.EndTime = DateTime.UtcNow;
            }

            return report;
        }

        /// <summary>
        /// 根据不同的同步模式过滤不必要的同步动作
        /// </summary>
        private List<SyncAction> ApplySyncModeFilter(IEnumerable<SyncAction> actions)
        {
            var result = new List<SyncAction>();

            foreach (var action in actions)
            {
                switch (_syncMode)
                {
                    case SyncMode.OneWayIncremental:
                        // 增量：只关心源目录新增的文件
                        if (action.ActionType == SyncActionType.Create)
                        {
                            result.Add(action);
                        }
                        break;
                    case SyncMode.OneWayUpdate:
                        // 更新：关心新增和修改，不删除目标文件
                        if (action.ActionType == SyncActionType.Create || action.ActionType == SyncActionType.Update)
                        {
                            result.Add(action);
                        }
                        break;
                    case SyncMode.OneWayMirror:
                        // 镜像：新增、修改、以及删除目标中多余的文件
                        result.Add(action);
                        break;
                    case SyncMode.TwoWay:
                        // 双向：暂时简化为等同于镜像逻辑，实际工业级双向同步需要引入 State 数据库（SQLite）记录上一次状态
                        // 否则无法区分“源文件被删除了”还是“目标文件是新创建的”。
                        // 这里我们为了保持简单，先全部加入，在后续开发中可以集成 SQLite State Tracker
                        result.Add(action);
                        break;
                }
            }

            return result;
        }

        /// <summary>
        /// 实际执行读写 I/O
        /// </summary>
        private async Task ExecuteActionsAsync(List<SyncAction> actions, SyncReport report, CancellationToken cancellationToken)
        {
            int completed = 0;

            foreach (var action in actions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var itemName = action.SourceItem?.Path ?? action.DestinationItem?.Path ?? "Unknown";

                try
                {
                    switch (action.ActionType)
                    {
                        case SyncActionType.Create:
                        case SyncActionType.Update:
                            if (action.SourceItem != null)
                            {
                                if (action.SourceItem.IsDirectory)
                                {
                                    await _destFs.CreateDirectoryAsync(action.SourceItem.Path, cancellationToken);
                                }
                                else
                                {
                                    await CopyFileAsync(action.SourceItem, cancellationToken);
                                    if (action.ActionType == SyncActionType.Create) report.CreatedFiles++;
                                    else report.UpdatedFiles++;
                                }
                            }
                            break;

                        case SyncActionType.Delete:
                            if (action.DestinationItem != null)
                            {
                                if (action.DestinationItem.IsDirectory)
                                {
                                    await _destFs.DeleteDirectoryAsync(action.DestinationItem.Path, cancellationToken);
                                }
                                else
                                {
                                    await _destFs.DeleteFileAsync(action.DestinationItem.Path, cancellationToken);
                                }
                                report.DeletedFiles++;
                            }
                            break;
                    }
                }
                catch (Exception ex)
                {
                    report.FailedFiles++;
                    ErrorOccurred?.Invoke(this, new SyncErrorEventArgs(ex, $"Failed to process {itemName}"));
                }

                completed++;
                ProgressChanged?.Invoke(this, new SyncProgressEventArgs(completed, actions.Count, itemName, action.ActionType));
            }
        }

        /// <summary>
        /// 复制单个文件（流式传输，支持不同文件系统间的拷贝，如 Local 到 FTP）
        /// </summary>
        private async Task CopyFileAsync(FileItem sourceItem, CancellationToken cancellationToken)
        {
            using var readStream = await _sourceFs.OpenReadAsync(sourceItem.Path, cancellationToken);
            using var writeStream = await _destFs.OpenWriteAsync(sourceItem.Path, cancellationToken);
            
            // 使用 81920 字节（80KB）作为默认缓冲区大小，对于文件拷贝性能较好
            await readStream.CopyToAsync(writeStream, 81920, cancellationToken);
        }
    }
}
