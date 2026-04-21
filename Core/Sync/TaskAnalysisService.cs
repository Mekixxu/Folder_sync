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
            var sourceFs = SyncTaskFactory.CreateFileSystem(task.SourceProtocol, task.SourcePath);
            var destFs = SyncTaskFactory.CreateFileSystem(task.DestProtocol, task.DestPath);
            var diff = SyncTaskFactory.CreateDiffStrategy(task.DiffStrategy);
            var filterEngine = SyncTaskFactory.CreateFilterEngine(task.FilterConfiguration ?? new DualListFilterConfiguration());

            await sourceFs.ConnectAsync(cancellationToken);
            await destFs.ConnectAsync(cancellationToken);

            var rawSource = (await sourceFs.ListFilesAsync(cancellationToken: cancellationToken)).ToList();
            var rawDest = (await destFs.ListFilesAsync(cancellationToken: cancellationToken)).ToList();

            var filteredSource = filterEngine.Filter(rawSource).ToList();
            var filteredDest = filterEngine.Filter(rawDest).ToList();
            var isMirror = task.SyncMode == SyncMode.OneWayMirror;
            var diffActions = (await diff.CompareAsync(filteredSource, filteredDest, sourceFs, destFs, isMirror, cancellationToken)).ToList();

            // 按任务模式过滤
            var effectiveActions = diffActions.Where(a => task.SyncMode switch
            {
                SyncMode.OneWayIncremental => a.ActionType == SyncActionType.Create,
                SyncMode.OneWayUpdate => a.ActionType == SyncActionType.Create || a.ActionType == SyncActionType.Update,
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

                if (!inWhite)
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
            var sourceFs = SyncTaskFactory.CreateFileSystem(task.SourceProtocol, task.SourcePath);
            var destFs = SyncTaskFactory.CreateFileSystem(task.DestProtocol, task.DestPath);
            await sourceFs.ConnectAsync(cancellationToken);
            await destFs.ConnectAsync(cancellationToken);

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

                    // Create / Update / 手动开启
                    if (item.Direction == AnalysisDirection.AToB)
                    {
                        await CopyPathAsync(sourceFs, destFs, item.RelativePath, item.IsDirectory, cancellationToken);
                    }
                    else if (item.Direction == AnalysisDirection.BToA)
                    {
                        await CopyPathAsync(destFs, sourceFs, item.RelativePath, item.IsDirectory, cancellationToken);
                    }
                    else
                    {
                        // 无方向但勾选同步时：按更新时间较新的一端覆盖另一端
                        var sourceNewer = (item.SourceLastWrite ?? DateTime.MinValue) >= (item.DestLastWrite ?? DateTime.MinValue);
                        if (sourceNewer)
                        {
                            await CopyPathAsync(sourceFs, destFs, item.RelativePath, item.IsDirectory, cancellationToken);
                        }
                        else
                        {
                            await CopyPathAsync(destFs, sourceFs, item.RelativePath, item.IsDirectory, cancellationToken);
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

        private static async Task CopyPathAsync(IFileSystem from, IFileSystem to, string path, bool isDirectory, CancellationToken cancellationToken)
        {
            if (isDirectory)
            {
                await to.CreateDirectoryAsync(path, cancellationToken);
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
            if (s == null && d != null && mode is SyncMode.OneWayIncremental or SyncMode.OneWayUpdate) return "仅目标端存在，当前模式不删除";
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
                ShouldSync = item.ShouldSync
            };
        }
    }
}
