using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Core.Config;
using FolderSync.Core.Diff;
using FolderSync.Core.Filters;
using FolderSync.Core.VFS;

namespace FolderSync.Core.Sync
{
    public class TaskAnalysisService
    {
        private readonly TaskRepository _taskRepository;

        public TaskAnalysisService(TaskRepository? taskRepository = null)
        {
            _taskRepository = taskRepository ?? new TaskRepository();
        }

        public async Task<List<TaskAnalysisItem>> AnalyzeAsync(
            SyncTaskDefinition task,
            CancellationToken cancellationToken = default)
        {
            using var executionLease = await TaskExecutionCoordinator.AcquireAsync(task.Id, cancellationToken);
            PathSafetyValidator.EnsureSourceDestDoNotOverlap(task);

            // 双向模式：走 SQLite 基线可靠判定，确保分析结果与定时执行链路一致。
            if (task.SyncMode == SyncMode.TwoWay)
            {
                using var executor = SyncTaskFactory.CreateExecutor(task);
                return await executor.AnalyzeTwoWayAsync(cancellationToken);
            }

            using var sourceFs = SyncTaskFactory.CreateSourceFileSystem(task);
            using var destFs = SyncTaskFactory.CreateDestFileSystem(task);
            var diff = SyncTaskFactory.CreateDiffStrategy(task.DiffStrategy);
            var filterEngine = SyncTaskFactory.CreateFilterEngine(task.FilterConfiguration ?? new DualListFilterConfiguration());
            Dictionary<string, OneWayDeliveryRecord>? deliveredRecords = null;

            await sourceFs.ConnectAsync(cancellationToken);
            await destFs.ConnectAsync(cancellationToken);

            var rawSource = (await sourceFs.ListFilesAsync(cancellationToken: cancellationToken)).ToList();
            var rawDest = (await destFs.ListFilesAsync(cancellationToken: cancellationToken)).ToList();
            var sourceScanComplete = sourceFs.LastScanComplete;
            var destScanComplete = destFs.LastScanComplete;

            var filteredSourceFiles = filterEngine.Filter(rawSource).ToList();
            var filteredDestFiles = filterEngine.Filter(rawDest).ToList();
            var filteredSource = StructureAwarePathHelper.ExpandWithAncestorDirectories(rawSource, filteredSourceFiles);
            var filteredDest = StructureAwarePathHelper.ExpandWithAncestorDirectories(rawDest, filteredDestFiles);
            var filteredSourcePathSet = StructureAwarePathHelper.BuildPathSet(filteredSource);
            var filteredDestPathSet = StructureAwarePathHelper.BuildPathSet(filteredDest);
            var rawSourceFileCount = rawSource.Count(i => !i.IsDirectory);
            var filteredSourceFileCount = filteredSourceFiles.Count(i => !i.IsDirectory);
            var isMirror = task.SyncMode == SyncMode.OneWayMirror;
            var diffActions = StructureAwarePathHelper.SuppressActionsUnderTypeConflicts(
                await diff.CompareAsync(
                    filteredSource,
                    filteredDest,
                    sourceFs,
                    destFs,
                    isMirror,
                    cancellationToken));

            if (!sourceScanComplete || !destScanComplete)
            {
                diffActions.RemoveAll(action =>
                    (action.ActionType == SyncActionType.Delete && !sourceScanComplete)
                    || (action.ActionType is SyncActionType.Create or SyncActionType.Update
                        && !destScanComplete
                        && destFs.LastScanFailures.Any(failed =>
                            VfsPath.IsSameOrUnderPath(action.SourceItem?.Path ?? string.Empty, failed))));
            }

            if (task.SyncMode == SyncMode.OneWaySendOnce)
            {
                var stateStore = new OneWayDeliveryStateStore();
                await stateStore.InitializeAsync(cancellationToken);
                deliveredRecords = await stateStore.LoadAsync(task.Id, cancellationToken);
            }

            // 按任务模式过滤
            var effectiveActions = diffActions.Where(a => task.SyncMode switch
            {
                SyncMode.OneWayIncremental => a.ActionType == SyncActionType.Create,
                SyncMode.OneWayUpdate => a.ActionType == SyncActionType.Create || a.ActionType == SyncActionType.Update,
                SyncMode.OneWaySendOnce => (a.ActionType == SyncActionType.Create || a.ActionType == SyncActionType.Update)
                    && a.SourceItem != null
                    && !(deliveredRecords?.ContainsKey(a.SourceItem.Path) ?? false),
                _ => true
            }).ToDictionary(
                a => NormalizeAnalysisPath(a.SourceItem?.Path ?? a.DestinationItem?.Path ?? string.Empty),
                a => a,
                StringComparer.OrdinalIgnoreCase);

            var srcMap = BuildPathMap(rawSource);
            var dstMap = BuildPathMap(rawDest);
            var allPaths = new HashSet<string>(srcMap.Keys, StringComparer.OrdinalIgnoreCase);
            allPaths.UnionWith(dstMap.Keys);

            var items = new List<TaskAnalysisItem>();
            foreach (var path in allPaths)
            {
                srcMap.TryGetValue(path, out var s);
                dstMap.TryGetValue(path, out var d);

                var isIncludedByStructure = (s != null && filteredSourcePathSet.Contains(StructureAwarePathHelper.NormalizePath(path)))
                    || (d != null && filteredDestPathSet.Contains(StructureAwarePathHelper.NormalizePath(path)));
                effectiveActions.TryGetValue(path, out var act);

                var item = new TaskAnalysisItem
                {
                    RelativePath = s?.Path ?? d?.Path ?? act?.SourceItem?.Path ?? act?.DestinationItem?.Path ?? path,
                    IsDirectory = s?.IsDirectory ?? d?.IsDirectory ?? false,
                    SourceSize = s?.Size,
                    DestSize = d?.Size,
                    SourceLastWrite = s?.LastWriteTime,
                    DestLastWrite = d?.LastWriteTime
                };

                OneWayDeliveryRecord? deliveredRecord = null;
                var isProtectedByDeliveredState = task.SyncMode == SyncMode.OneWaySendOnce
                    && s != null
                    && deliveredRecords != null
                    && deliveredRecords.TryGetValue(path, out deliveredRecord);

                if (isProtectedByDeliveredState && deliveredRecord != null)
                {
                    item.ShouldSync = false;
                    item.Direction = AnalysisDirection.None;
                    item.IsProtectedByDeliveredState = true;
                    item.HasWarning = await OneWayDeliverySupport.HasSourceChangedAsync(deliveredRecord, s!, sourceFs, cancellationToken);
                    item.Reason = item.HasWarning
                        ? "源路径已完成一次性同步，且检测到内容变化，已告警并跳过"
                        : "已同步过，按一次性规则跳过";
                }
                else if (!isIncludedByStructure)
                {
                    item.ShouldSync = false;
                    item.Reason = "被过滤规则排除";
                    item.Direction = AnalysisDirection.None;
                }
                else if (act != null && act.ActionType == SyncActionType.Conflict)
                {
                    item.ShouldSync = false;
                    item.ActionType = SyncActionType.Conflict;
                    item.Direction = AnalysisDirection.None;
                    item.HasWarning = true;
                    item.Reason = "同路径文件/目录类型冲突，已保留两端数据并跳过";
                }
                else if (act != null)
                {
                    item.ShouldSync = true;
                    item.ActionType = act.ActionType;
                    item.Direction = ResolveDirection(task.SyncMode, act, s, d);
                    item.Reason = BuildReason(act, s, d);
                }
                else
                {
                    item.ShouldSync = false;
                    item.Direction = AnalysisDirection.None;
                    item.Reason = BuildNoActionReason(task.SyncMode, s, d);
                }

                items.Add(item);
            }

            if (rawSourceFileCount > 0 && filteredSourceFileCount == 0)
            {
                items.Add(new TaskAnalysisItem
                {
                    RelativePath = "[分析提示]",
                    ShouldSync = false,
                    Direction = AnalysisDirection.None,
                    Reason = $"源端共列举到 {rawSourceFileCount} 个文件，但 0 个文件命中过滤规则。请检查白名单/黑名单，尤其是扩展名和最近小时条件。"
                });
            }

            if (!sourceScanComplete || !destScanComplete)
            {
                items.Add(new TaskAnalysisItem
                {
                    RelativePath = "[分析提示]",
                    ShouldSync = false,
                    Direction = AnalysisDirection.None,
                    HasWarning = true,
                    Reason = "扫描结果不完整：未知区域已禁止生成删除动作；请先处理无法访问的子目录后重新分析。"
                });
            }

            return items
                .OrderByDescending(i => i.ShouldSync)
                .ThenBy(i => i.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public List<TaskAnalysisItem> GetSavedAnalysis(SyncTaskDefinition task)
        {
            return task.SavedAnalysisItems
                .Select(ToAnalysisItem)
                .OrderByDescending(i => i.ShouldSync)
                .ThenBy(i => i.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public bool HasSavedAnalysis(SyncTaskDefinition task)
        {
            return task.SavedAnalysisItems.Count > 0;
        }

        public void SaveAnalysis(SyncTaskDefinition task, IEnumerable<TaskAnalysisItem> items)
        {
            var savedItems = items.Select(ToSavedItem).ToList();
            var savedAt = DateTime.UtcNow;
            if (!_taskRepository.UpdateAnalysisSnapshot(task.Id, savedItems, savedAt))
            {
                throw new InvalidOperationException("任务已被删除，分析结果未保存。");
            }

            task.SavedAnalysisItems = savedItems;
            task.AnalysisSavedAtUtc = savedAt;
        }

        public async Task<SyncReport> ExecuteSelectedAsync(
            SyncTaskDefinition task,
            IEnumerable<TaskAnalysisItem> selectedItems,
            CancellationToken cancellationToken = default)
        {
            using var sourceFs = SyncTaskFactory.CreateSourceFileSystem(task);
            using var destFs = SyncTaskFactory.CreateDestFileSystem(task);
            var report = new SyncReport
            {
                StartTime = DateTime.UtcNow,
                SyncMode = task.SyncMode,
                SourceRoot = sourceFs.RootIdentifier,
                DestRoot = destFs.RootIdentifier
            };
            OneWayDeliveryStateStore? stateStore = null;
            var pendingDeliveryRecords = new List<OneWayDeliveryRecord>();
            var successfulPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using var executionLease = await TaskExecutionCoordinator.AcquireAsync(task.Id, cancellationToken);
                PathSafetyValidator.EnsureSourceDestDoNotOverlap(task);

                await sourceFs.ConnectAsync(cancellationToken);
                await destFs.ConnectAsync(cancellationToken);
                var isSendOnce = task.SyncMode == SyncMode.OneWaySendOnce;
                stateStore = isSendOnce ? new OneWayDeliveryStateStore() : null;
                Dictionary<string, OneWayDeliveryRecord>? deliveredRecords = null;
                if (stateStore != null)
                {
                    await stateStore.InitializeAsync(cancellationToken);
                    deliveredRecords = await stateStore.LoadAsync(task.Id, cancellationToken);
                }

                var list = selectedItems.Where(i => i.ShouldSync).ToList();
                report.TotalActions = list.Count;

                foreach (var item in list)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (isSendOnce && deliveredRecords != null && deliveredRecords.TryGetValue(item.RelativePath, out var deliveredRecord))
                        {
                            report.SkippedAlreadyDelivered++;
                            var currentSource = await sourceFs.GetFileInfoAsync(item.RelativePath, cancellationToken);
                            if (currentSource != null && await OneWayDeliverySupport.HasSourceChangedAsync(deliveredRecord, currentSource, sourceFs, cancellationToken))
                            {
                                report.WarningDetails.Add(new SyncWarningDetail
                                {
                                    ItemPath = item.RelativePath,
                                    Context = "Source changed after first successful delivery",
                                    Message = "该路径已完成一次性同步，检测到源文件内容变化，已按一次性规则跳过。"
                                });
                            }
                            continue;
                        }

                        if (!await ValidateManualItemAsync(task, item, sourceFs, destFs, report, cancellationToken))
                        {
                            continue;
                        }

                        if (item.ActionType == SyncActionType.Delete)
                        {
                            var targetFs = item.Direction == AnalysisDirection.AToB ? destFs : sourceFs;
                            if (item.IsDirectory)
                            {
                                var deleted = await targetFs.DeleteDirectoryIfEmptyAsync(item.RelativePath, cancellationToken);
                                if (!deleted)
                                {
                                    report.WarningDetails.Add(new SyncWarningDetail
                                    {
                                        ItemPath = item.RelativePath,
                                        Context = "Directory not empty",
                                        Message = "目录仍包含未授权、未选择或删除失败的条目，已保留该目录。"
                                    });
                                    continue;
                                }
                            }
                            else
                            {
                                await targetFs.DeleteFileAsync(item.RelativePath, cancellationToken);
                            }

                            report.DeletedFiles++;
                            successfulPaths.Add(item.RelativePath);
                            report.AddSuccessDetail(
                                item.RelativePath,
                                ResolveExecutedItemSize(item),
                                item.IsDirectory,
                                "Manual selected execution succeeded",
                                SyncActionType.Delete);
                            continue;
                        }

                        if (item.ActionType is null or SyncActionType.Conflict)
                        {
                            continue;
                        }

                        var source = item.Direction == AnalysisDirection.AToB ? sourceFs : destFs;
                        var dest = item.Direction == AnalysisDirection.AToB ? destFs : sourceFs;
                        var copiedBytes = await CopyPathAsync(
                            source,
                            dest,
                            item.RelativePath,
                            item.IsDirectory,
                            isSendOnce,
                            pendingDeliveryRecords,
                            deliveredRecords,
                            cancellationToken);

                        if (item.ActionType == SyncActionType.Create) report.CreatedFiles++;
                        else report.UpdatedFiles++;
                        successfulPaths.Add(item.RelativePath);
                        report.AddSuccessDetail(
                            item.RelativePath,
                            copiedBytes,
                            item.IsDirectory,
                            "Manual selected execution succeeded",
                            item.ActionType);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        report.FailedFiles++;
                        report.AddErrorDetail(
                            item.RelativePath,
                            ResolveExecutedItemSize(item),
                            item.IsDirectory,
                            "Manual selected execution failed",
                            ex);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                report.WasCancelled = true;
                report.ErrorMessage = "同步已停止。";
            }
            catch (Exception ex)
            {
                report.ErrorMessage = ex.Message;
                report.AddErrorDetail(string.Empty, null, false, "Manual execution failed", ex);
            }
            finally
            {
                // 取消也必须保存已经确认成功的“一次性”投递记录。
                if (pendingDeliveryRecords.Count > 0 && stateStore != null)
                {
                    using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    try
                    {
                        await stateStore.UpsertRangeAsync(task.Id, pendingDeliveryRecords, cleanupCts.Token);
                        pendingDeliveryRecords.Clear();
                    }
                    catch (Exception ex)
                    {
                        report.ErrorMessage = string.IsNullOrWhiteSpace(report.ErrorMessage)
                            ? $"一次性投递状态保存失败：{ex.Message}"
                            : report.ErrorMessage;
                        report.AddErrorDetail(string.Empty, null, false, "Failed to persist send-once delivery state", ex);
                    }
                }

                // 双向模式只按实际成功路径增量更新基线；不再全量刷新，避免吞掉未选项/失败项。
                if (task.SyncMode == SyncMode.TwoWay
                    && !report.WasCancelled
                    && string.IsNullOrWhiteSpace(report.ErrorMessage)
                    && successfulPaths.Count > 0)
                {
                    using var baselineCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    try
                    {
                        await SyncExecutor.UpdateTwoWayBaselineForPathsAsync(
                            task.Id, sourceFs, destFs, successfulPaths, baselineCts.Token);
                    }
                    catch (Exception ex)
                    {
                        report.ErrorMessage = $"双向基线更新失败：{ex.Message}";
                        report.AddErrorDetail(string.Empty, null, false, "Failed to update two-way baseline", ex);
                    }
                }

                report.EndTime = DateTime.UtcNow;
            }

            return report;
        }

        private static async Task<bool> ValidateManualItemAsync(
            SyncTaskDefinition task,
            TaskAnalysisItem item,
            IFileSystem sourceFs,
            IFileSystem destFs,
            SyncReport report,
            CancellationToken cancellationToken)
        {
            if (item.ActionType is null or SyncActionType.Conflict)
            {
                AddSkippedWarning(report, item.RelativePath, "Invalid action", "该分析行没有合法的同步动作，已跳过。");
                return false;
            }

            if (item.Direction == AnalysisDirection.None)
            {
                AddSkippedWarning(report, item.RelativePath, "Missing direction", "该分析行没有明确同步方向，已跳过以避免反向覆盖。");
                return false;
            }

            if (task.SyncMode != SyncMode.TwoWay && item.Direction != AnalysisDirection.AToB)
            {
                AddSkippedWarning(report, item.RelativePath, "Invalid direction for one-way task", "单向任务只允许 A -> B，已跳过多余方向。");
                return false;
            }

            if (item.IsProtectedByDeliveredState)
            {
                AddSkippedWarning(report, item.RelativePath, "Send-once protected", "该项目已完成一次性投递，不能再次执行。");
                return false;
            }

            if (item.ActionType == SyncActionType.Delete)
            {
                var source = item.Direction == AnalysisDirection.AToB ? sourceFs : destFs;
                var target = item.Direction == AnalysisDirection.AToB ? destFs : sourceFs;
                var currentSource = await source.GetFileInfoAsync(item.RelativePath, cancellationToken);
                if (currentSource != null)
                {
                    AddSkippedWarning(report, item.RelativePath, "Stale delete action", "源端对应项仍存在，删除动作可能已过期，已跳过。");
                    return false;
                }

                var currentTarget = await target.GetFileInfoAsync(item.RelativePath, cancellationToken);
                if (currentTarget == null)
                {
                    AddSkippedWarning(report, item.RelativePath, "Delete target missing", "目标项已不存在，已跳过删除。");
                    return false;
                }

                if (currentTarget.IsDirectory != item.IsDirectory)
                {
                    AddSkippedWarning(report, item.RelativePath, "Delete target type changed", "目标项类型已变化，已跳过删除。");
                    return false;
                }

                var expectedTargetSize = item.Direction == AnalysisDirection.AToB ? item.DestSize : item.SourceSize;
                if (!item.IsDirectory && expectedTargetSize.HasValue && currentTarget.Size != expectedTargetSize)
                {
                    AddSkippedWarning(report, item.RelativePath, "Delete target changed", "目标文件大小已变化，请重新分析后再删除。");
                    return false;
                }

                return true;
            }

            var currentSourceItem = item.Direction == AnalysisDirection.AToB
                ? await sourceFs.GetFileInfoAsync(item.RelativePath, cancellationToken)
                : await destFs.GetFileInfoAsync(item.RelativePath, cancellationToken);
            if (currentSourceItem == null)
            {
                AddSkippedWarning(report, item.RelativePath, "Source missing at execution", "源项已不存在，请重新分析。");
                return false;
            }

            if (currentSourceItem.IsDirectory != item.IsDirectory)
            {
                AddSkippedWarning(report, item.RelativePath, "Source type changed", "源项类型已变化，请重新分析。");
                return false;
            }

            var expectedSourceSize = item.Direction == AnalysisDirection.AToB ? item.SourceSize : item.DestSize;
            if (!item.IsDirectory && expectedSourceSize.HasValue && currentSourceItem.Size != expectedSourceSize)
            {
                AddSkippedWarning(report, item.RelativePath, "Source changed at execution", "源文件大小已变化，请重新分析后再同步。");
                return false;
            }

            var currentTargetItem = item.Direction == AnalysisDirection.AToB
                ? await destFs.GetFileInfoAsync(item.RelativePath, cancellationToken)
                : await sourceFs.GetFileInfoAsync(item.RelativePath, cancellationToken);
            if (currentTargetItem != null && currentTargetItem.IsDirectory != item.IsDirectory)
            {
                AddSkippedWarning(report, item.RelativePath, "Target type conflict", "目标端同路径类型不一致，已保留现有数据并跳过。");
                return false;
            }

            if (item.ActionType == SyncActionType.Create && currentTargetItem != null)
            {
                AddSkippedWarning(report, item.RelativePath, "Stale create action", "目标项已出现，分析结果可能已过期，已跳过创建。");
                return false;
            }

            if (item.ActionType == SyncActionType.Update && currentTargetItem == null)
            {
                AddSkippedWarning(report, item.RelativePath, "Stale update action", "目标项已不存在，分析结果可能已过期，已跳过更新。");
                return false;
            }

            if (item.ActionType == SyncActionType.Update
                && !item.IsDirectory
                && currentTargetItem != null
                && item.DestSize.HasValue
                && currentTargetItem.Size != item.DestSize)
            {
                AddSkippedWarning(report, item.RelativePath, "Target changed at execution", "目标文件大小已变化，请重新分析后再同步。");
                return false;
            }

            return true;
        }

        private static void AddSkippedWarning(SyncReport report, string path, string context, string message)
        {
            report.WarningDetails.Add(new SyncWarningDetail
            {
                ItemPath = path,
                Context = context,
                Message = message
            });
        }

        private static async Task<long?> CopyPathAsync(
            IFileSystem from,
            IFileSystem to,
            string path,
            bool isDirectory,
            bool isSendOnce,
            List<OneWayDeliveryRecord> pendingDeliveryRecords,
            Dictionary<string, OneWayDeliveryRecord>? deliveredRecords,
            CancellationToken cancellationToken)
        {
            var sourceItem = await from.GetFileInfoAsync(path, cancellationToken)
                ?? throw new InvalidOperationException($"无法获取源项信息：{path}");

            if (isDirectory)
            {
                await to.CreateDirectoryAsync(path, cancellationToken);
                if (isSendOnce)
                {
                    var directoryRecord = OneWayDeliverySupport.CreateDeliveredRecordFromCopy(path, sourceItem, sourceHash: null);
                    pendingDeliveryRecords.Add(directoryRecord);
                    if (deliveredRecords != null)
                    {
                        deliveredRecords[path] = directoryRecord;
                    }
                }
                return 0;
            }

            if (isSendOnce)
            {
                var copyResult = await OneWayDeliverySupport.CopyFileAndComputeHashAsync(
                    from, to, path, sourceItem, cancellationToken);
                var record = OneWayDeliverySupport.CreateDeliveredRecordFromCopy(path, sourceItem, copyResult.Hash);
                pendingDeliveryRecords.Add(record);
                if (deliveredRecords != null)
                {
                    deliveredRecords[path] = record;
                }
                return copyResult.BytesCopied;
            }

            var result = await OneWayDeliverySupport.CopyFileAsync(from, to, path, sourceItem, cancellationToken);
            return result.BytesCopied;
        }

        private static AnalysisDirection ResolveDirection(SyncMode mode, SyncAction act, FileItem? s, FileItem? d)
        {
            if (mode == SyncMode.TwoWay)
            {
                if (act.SourceItem != null && act.DestinationItem == null) return AnalysisDirection.AToB;
                if (act.SourceItem == null && act.DestinationItem != null) return AnalysisDirection.BToA;
                if ((s?.LastWriteTime ?? DateTime.MinValue) >= (d?.LastWriteTime ?? DateTime.MinValue)) return AnalysisDirection.AToB;
                return AnalysisDirection.BToA;
            }

            return AnalysisDirection.AToB;
        }

        private static string BuildReason(SyncAction act, FileItem? s, FileItem? d)
        {
            return act.ActionType switch
            {
                SyncActionType.Create => s == null ? "仅目标端存在，需反向创建" : "仅源端存在，需创建",
                SyncActionType.Delete => "目标端存在多余项，需删除",
                SyncActionType.Update => BuildUpdateReason(s, d),
                SyncActionType.Conflict => "同路径文件/目录类型冲突，已保留两端数据并跳过",
                _ => "规则判定需同步"
            };
        }

        private static string BuildUpdateReason(FileItem? s, FileItem? d)
        {
            if (s == null || d == null) return "内容差异，需更新";
            if (s.Size != d.Size) return "文件大小改变";
            if (s.LastWriteTime != d.LastWriteTime) return "校验和改变（或修改时间差异）";
            return "内容发生变化";
        }

        private static string BuildNoActionReason(SyncMode mode, FileItem? s, FileItem? d)
        {
            if (s != null && d != null) return "已一致，无需同步";
            if (s == null && d != null && mode is SyncMode.OneWayIncremental or SyncMode.OneWayUpdate or SyncMode.OneWaySendOnce) return "仅目标端存在，当前模式不删除";
            return "无需同步";
        }

        private static long? ResolveExecutedItemSize(TaskAnalysisItem item)
        {
            if (item.IsDirectory)
            {
                return 0;
            }

            if (item.ActionType == SyncActionType.Delete)
            {
                return item.Direction == AnalysisDirection.BToA
                    ? item.SourceSize ?? item.DestSize
                    : item.DestSize ?? item.SourceSize;
            }

            return item.Direction == AnalysisDirection.BToA
                ? item.DestSize ?? item.SourceSize
                : item.SourceSize ?? item.DestSize;
        }

        private static TaskAnalysisItem ToAnalysisItem(SavedTaskAnalysisItem item)
        {
            return new TaskAnalysisItem
            {
                RelativePath = item.RelativePath,
                IsDirectory = item.IsDirectory,
                SourceSize = item.SourceSize,
                DestSize = item.DestSize,
                SourceLastWrite = item.SourceLastWrite,
                DestLastWrite = item.DestLastWrite,
                ActionType = item.ActionType,
                Direction = item.Direction,
                Reason = item.Reason,
                IsProtectedByDeliveredState = item.IsProtectedByDeliveredState,
                HasWarning = item.HasWarning,
                ShouldSync = item.ShouldSync
            };
        }

        private static SavedTaskAnalysisItem ToSavedItem(TaskAnalysisItem item)
        {
            return new SavedTaskAnalysisItem
            {
                RelativePath = item.RelativePath,
                IsDirectory = item.IsDirectory,
                SourceSize = item.SourceSize,
                DestSize = item.DestSize,
                SourceLastWrite = item.SourceLastWrite,
                DestLastWrite = item.DestLastWrite,
                ActionType = item.ActionType,
                Direction = item.Direction,
                Reason = item.Reason,
                IsProtectedByDeliveredState = item.IsProtectedByDeliveredState,
                HasWarning = item.HasWarning,
                ShouldSync = item.ShouldSync
            };
        }

        private static Dictionary<string, FileItem> BuildPathMap(IEnumerable<FileItem> items)
        {
            var result = new Dictionary<string, FileItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                result[NormalizeAnalysisPath(item.Path)] = item;
            }

            return result;
        }

        private static string NormalizeAnalysisPath(string path)
        {
            return StructureAwarePathHelper.NormalizePath(path);
        }
    }
}
