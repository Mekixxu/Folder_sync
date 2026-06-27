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

        public async Task<List<TaskAnalysisItem>> AnalyzeAsync(SyncTaskDefinition task, CancellationToken cancellationToken = default)
        {
            using var sourceFs = SyncTaskFactory.CreateSourceFileSystem(task);
            using var destFs = SyncTaskFactory.CreateDestFileSystem(task);
            var diff = SyncTaskFactory.CreateDiffStrategy(task.DiffStrategy);
            var filterEngine = SyncTaskFactory.CreateFilterEngine(task.FilterConfiguration ?? new DualListFilterConfiguration());
            Dictionary<string, OneWayDeliveryRecord>? deliveredRecords = null;

            await sourceFs.ConnectAsync(cancellationToken);
            await destFs.ConnectAsync(cancellationToken);

            var rawSource = (await sourceFs.ListFilesAsync(cancellationToken: cancellationToken)).ToList();
            var rawDest = (await destFs.ListFilesAsync(cancellationToken: cancellationToken)).ToList();

            var filteredSource = filterEngine.Filter(rawSource).ToList();
            var filteredDest = filterEngine.Filter(rawDest).ToList();
            var rawSourceFileCount = rawSource.Count(i => !i.IsDirectory);
            var filteredSourceFileCount = filteredSource.Count(i => !i.IsDirectory);
            var isMirror = task.SyncMode == SyncMode.OneWayMirror;
            var diffActions = (await diff.CompareAsync(filteredSource, filteredDest, sourceFs, destFs, isMirror, cancellationToken)).ToList();

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
                a => a.SourceItem?.Path ?? a.DestinationItem?.Path ?? string.Empty,
                a => a,
                StringComparer.OrdinalIgnoreCase);

            var srcMap = rawSource.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
            var dstMap = rawDest.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
            var allPaths = new HashSet<string>(srcMap.Keys, StringComparer.OrdinalIgnoreCase);
            allPaths.UnionWith(dstMap.Keys);

            var items = new List<TaskAnalysisItem>();
            foreach (var path in allPaths)
            {
                srcMap.TryGetValue(path, out var s);
                dstMap.TryGetValue(path, out var d);

                var inWhite = filterEngine.Filter(new[] { s ?? d! }).Any();
                effectiveActions.TryGetValue(path, out var act);

                var item = new TaskAnalysisItem
                {
                    RelativePath = path,
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
                else if (!inWhite)
                {
                    item.ShouldSync = false;
                    item.Reason = "被过滤规则排除";
                    item.Direction = AnalysisDirection.None;
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
            task.SavedAnalysisItems = items.Select(ToSavedItem).ToList();
            task.AnalysisSavedAtUtc = DateTime.UtcNow;
            _taskRepository.Upsert(task);
        }

        public async Task<SyncReport> ExecuteSelectedAsync(
            SyncTaskDefinition task,
            IEnumerable<TaskAnalysisItem> selectedItems,
            CancellationToken cancellationToken = default)
        {
            var sourceFs = SyncTaskFactory.CreateSourceFileSystem(task);
            var destFs = SyncTaskFactory.CreateDestFileSystem(task);
            await sourceFs.ConnectAsync(cancellationToken);
            await destFs.ConnectAsync(cancellationToken);
            var isSendOnce = task.SyncMode == SyncMode.OneWaySendOnce;
            var stateStore = isSendOnce ? new OneWayDeliveryStateStore() : null;
            Dictionary<string, OneWayDeliveryRecord>? deliveredRecords = null;
            if (stateStore != null)
            {
                await stateStore.InitializeAsync(cancellationToken);
                deliveredRecords = await stateStore.LoadAsync(task.Id, cancellationToken);
            }

            var report = new SyncReport
            {
                StartTime = DateTime.UtcNow,
                SyncMode = task.SyncMode
            };

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

                    if (item.ActionType == SyncActionType.Delete)
                    {
                        if (item.Direction == AnalysisDirection.AToB)
                        {
                            if (item.IsDirectory) await destFs.DeleteDirectoryAsync(item.RelativePath, cancellationToken);
                            else await destFs.DeleteFileAsync(item.RelativePath, cancellationToken);
                        }
                        else if (item.Direction == AnalysisDirection.BToA)
                        {
                            if (item.IsDirectory) await sourceFs.DeleteDirectoryAsync(item.RelativePath, cancellationToken);
                            else await sourceFs.DeleteFileAsync(item.RelativePath, cancellationToken);
                        }
                        report.DeletedFiles++;
                        continue;
                    }

                    if (isSendOnce && item.Direction != AnalysisDirection.AToB)
                    {
                        report.WarningDetails.Add(new SyncWarningDetail
                        {
                            ItemPath = item.RelativePath,
                            Context = "Invalid manual selection for send-once mode",
                            Message = "单向一次性同步仅支持 A -> B 的首次投递，该项已跳过。"
                        });
                        continue;
                    }

                    // Create / Update / 手动开启
                    if (item.Direction == AnalysisDirection.AToB)
                    {
                        await CopyPathAsync(sourceFs, destFs, item.RelativePath, item.IsDirectory, isSendOnce, task.Id, stateStore, deliveredRecords, cancellationToken);
                    }
                    else if (item.Direction == AnalysisDirection.BToA)
                    {
                        await CopyPathAsync(destFs, sourceFs, item.RelativePath, item.IsDirectory, isSendOnce, task.Id, stateStore, deliveredRecords, cancellationToken);
                    }
                    else
                    {
                        // 无方向但勾选同步时：按更新时间较新的一端覆盖另一端
                        var sourceNewer = (item.SourceLastWrite ?? DateTime.MinValue) >= (item.DestLastWrite ?? DateTime.MinValue);
                        if (sourceNewer)
                        {
                            await CopyPathAsync(sourceFs, destFs, item.RelativePath, item.IsDirectory, isSendOnce, task.Id, stateStore, deliveredRecords, cancellationToken);
                        }
                        else
                        {
                            await CopyPathAsync(destFs, sourceFs, item.RelativePath, item.IsDirectory, isSendOnce, task.Id, stateStore, deliveredRecords, cancellationToken);
                        }
                    }

                    if (item.ActionType == SyncActionType.Create) report.CreatedFiles++;
                    else report.UpdatedFiles++;
                }
                catch (Exception ex)
                {
                    report.FailedFiles++;
                    report.ErrorDetails.Add(new SyncErrorDetail
                    {
                        ItemPath = item.RelativePath,
                        Context = "Manual selected execution failed",
                        ErrorType = ex.GetType().FullName ?? "Exception",
                        Message = ex.Message
                    });
                }
            }

            report.EndTime = DateTime.UtcNow;
            return report;
        }

        private static async Task CopyPathAsync(
            IFileSystem from,
            IFileSystem to,
            string path,
            bool isDirectory,
            bool isSendOnce,
            string taskId,
            OneWayDeliveryStateStore? stateStore,
            Dictionary<string, OneWayDeliveryRecord>? deliveredRecords,
            CancellationToken cancellationToken)
        {
            FileItem? sourceItem = null;
            if (isSendOnce)
            {
                sourceItem = await from.GetFileInfoAsync(path, cancellationToken)
                    ?? throw new InvalidOperationException($"无法获取源项信息：{path}");
            }

            if (isDirectory)
            {
                await to.CreateDirectoryAsync(path, cancellationToken);
                if (isSendOnce && sourceItem != null && stateStore != null)
                {
                    var directoryRecord = OneWayDeliverySupport.CreateDeliveredRecordFromCopy(path, sourceItem, sourceHash: null);
                    await stateStore.UpsertAsync(taskId, directoryRecord, cancellationToken);
                    if (deliveredRecords != null)
                    {
                        deliveredRecords[path] = directoryRecord;
                    }
                }
                return;
            }

            if (isSendOnce && sourceItem != null && stateStore != null)
            {
                var copiedHash = await OneWayDeliverySupport.CopyFileAndComputeHashAsync(from, to, path, cancellationToken);
                var record = OneWayDeliverySupport.CreateDeliveredRecordFromCopy(path, sourceItem, copiedHash);
                await stateStore.UpsertAsync(taskId, record, cancellationToken);
                if (deliveredRecords != null)
                {
                    deliveredRecords[path] = record;
                }
                return;
            }

            using var r = await from.OpenReadForCopyAsync(path, cancellationToken);
            using var w = await to.OpenWriteAsync(path, cancellationToken);
            await r.CopyToAsync(w, 81920, cancellationToken);
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
    }
}
