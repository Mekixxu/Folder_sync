using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Core.Diff;
using FolderSync.Core.Filters;
using FolderSync.Core.VFS;

namespace FolderSync.Core.Sync
{
    /// <summary>
    /// 同步执行器：连接 VFS、Filter 和 Diff Engine。
    /// - 单向模式：沿用 Diff 策略输出动作
    /// - 双向模式：使用 SQLite 基线进行可靠判定（新增/修改/删除/冲突）
    /// </summary>
    public class SyncExecutor : IDisposable
    {
        private readonly IFileSystem _sourceFs;
        private readonly IFileSystem _destFs;
        private readonly IDiffStrategy _diffStrategy;
        private readonly FilterEngine _filterEngine;
        private readonly SyncMode _syncMode;
        private readonly TwoWayStateStore _twoWayStateStore;
        private readonly OneWayDeliveryStateStore _oneWayDeliveryStateStore;
        private readonly string _taskId;

        public event EventHandler<SyncProgressEventArgs>? ProgressChanged;
        public event EventHandler<SyncErrorEventArgs>? ErrorOccurred;

        public SyncExecutor(
            IFileSystem sourceFs,
            IFileSystem destFs,
            IDiffStrategy diffStrategy,
            FilterEngine filterEngine,
            SyncMode syncMode,
            string? taskId = null)
        {
            _sourceFs = sourceFs ?? throw new ArgumentNullException(nameof(sourceFs));
            _destFs = destFs ?? throw new ArgumentNullException(nameof(destFs));
            _diffStrategy = diffStrategy ?? throw new ArgumentNullException(nameof(diffStrategy));
            _filterEngine = filterEngine ?? throw new ArgumentNullException(nameof(filterEngine));
            _syncMode = syncMode;
            _twoWayStateStore = new TwoWayStateStore();
            _oneWayDeliveryStateStore = new OneWayDeliveryStateStore();
            _taskId = taskId ?? $"{_syncMode}::{_sourceFs.RootIdentifier}->{_destFs.RootIdentifier}";
        }

        public void Dispose()
        {
            _sourceFs.Dispose();
            _destFs.Dispose();
        }

        public async Task<SyncReport> ExecuteAsync(CancellationToken cancellationToken = default)
        {
            var report = new SyncReport
            {
                StartTime = DateTime.UtcNow,
                SyncMode = _syncMode,
                SourceRoot = _sourceFs.RootIdentifier,
                DestRoot = _destFs.RootIdentifier
            };

            try
            {
                // 定时与手动入口共享运行锁，避免同一任务同时写目标与状态库。
                using var executionLease = await TaskExecutionCoordinator.AcquireAsync(_taskId, cancellationToken);

                await _sourceFs.ConnectAsync(cancellationToken);
                await _destFs.ConnectAsync(cancellationToken);

                if (_syncMode == SyncMode.TwoWay)
                {
                    await ExecuteReliableTwoWayAsync(report, cancellationToken);
                }
                else
                {
                    await ExecuteOneWayAsync(report, cancellationToken);
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
                report.AddErrorDetail(string.Empty, null, false, "Global execution failed", ex);
                ErrorOccurred?.Invoke(this, new SyncErrorEventArgs(ex, "Global execution failed"));
            }
            finally
            {
                report.EndTime = DateTime.UtcNow;
            }

            return report;
        }

        private void MarkScanIncomplete(SyncReport report, IFileSystem fs, string side)
        {
            report.ScanIncomplete = true;
            foreach (var failedPath in fs.LastScanFailures)
            {
                report.WarningDetails.Add(new SyncWarningDetail
                {
                    ItemPath = failedPath,
                    Context = $"Incomplete {side} scan",
                    Message = $"{side} 端目录扫描失败，该子树未参与本次判定；已禁止基于不完整结果执行删除或刷新可信基线。"
                });
            }
        }

        private async Task ExecuteOneWayAsync(SyncReport report, CancellationToken cancellationToken)
        {
            var rawSourceItems = (await _sourceFs.ListFilesAsync(cancellationToken: cancellationToken)).ToList();
            var rawDestItems = (await _destFs.ListFilesAsync(cancellationToken: cancellationToken)).ToList();
            var sourceScanIncomplete = !_sourceFs.LastScanComplete;
            var destScanIncomplete = !_destFs.LastScanComplete;
            if (sourceScanIncomplete)
            {
                MarkScanIncomplete(report, _sourceFs, "源");
            }
            if (destScanIncomplete)
            {
                MarkScanIncomplete(report, _destFs, "目标");
            }

            var sourceItems = StructureAwarePathHelper.ExpandWithAncestorDirectories(rawSourceItems, _filterEngine.Filter(rawSourceItems));
            var destItems = StructureAwarePathHelper.ExpandWithAncestorDirectories(rawDestItems, _filterEngine.Filter(rawDestItems));
            Dictionary<string, OneWayDeliveryRecord>? deliveredRecords = null;

            if (_syncMode == SyncMode.OneWaySendOnce)
            {
                await _oneWayDeliveryStateStore.InitializeAsync(cancellationToken);
                deliveredRecords = await _oneWayDeliveryStateStore.LoadAsync(_taskId, cancellationToken);

                foreach (var sourceItem in sourceItems)
                {
                    if (!deliveredRecords.TryGetValue(sourceItem.Path, out var deliveredRecord))
                    {
                        continue;
                    }

                    report.SkippedAlreadyDelivered++;
                    if (await OneWayDeliverySupport.HasSourceChangedAsync(deliveredRecord, sourceItem, _sourceFs, cancellationToken))
                    {
                        report.WarningDetails.Add(new SyncWarningDetail
                        {
                            ItemPath = sourceItem.Path,
                            Context = "Source changed after first successful delivery",
                            Message = "该路径已完成一次性同步，检测到源文件内容变化，已按一次性规则跳过。"
                        });
                    }
                }
            }

            var isMirror = _syncMode == SyncMode.OneWayMirror;
            var actions = await _diffStrategy.CompareAsync(
                sourceItems,
                destItems,
                _sourceFs,
                _destFs,
                isMirror,
                cancellationToken);
            var conflictSuppressedActions = StructureAwarePathHelper.SuppressActionsUnderTypeConflicts(
                ApplySyncModeFilter(actions, deliveredRecords));
            var finalActions = StructureAwarePathHelper.OrderOneWayActions(
                SuppressUnsafeActionsForIncompleteScan(conflictSuppressedActions, report, sourceScanIncomplete, destScanIncomplete));
            report.TotalActions = finalActions.Count;
            await ExecuteActionsAsync(finalActions, report, cancellationToken, deliveredRecords);
        }

        private async Task ExecuteReliableTwoWayAsync(SyncReport report, CancellationToken cancellationToken)
        {
            await _twoWayStateStore.InitializeAsync(cancellationToken);

            var rawSourceItems = (await _sourceFs.ListFilesAsync(cancellationToken: cancellationToken)).ToList();
            var rawDestItems = (await _destFs.ListFilesAsync(cancellationToken: cancellationToken)).ToList();
            var sourceScanComplete = _sourceFs.LastScanComplete;
            var destScanComplete = _destFs.LastScanComplete;
            if (!sourceScanComplete)
            {
                MarkScanIncomplete(report, _sourceFs, "源");
            }
            if (!destScanComplete)
            {
                MarkScanIncomplete(report, _destFs, "目标");
            }

            var sourceItems = StructureAwarePathHelper.ExpandWithAncestorDirectories(rawSourceItems, _filterEngine.Filter(rawSourceItems));
            var destItems = StructureAwarePathHelper.ExpandWithAncestorDirectories(rawDestItems, _filterEngine.Filter(rawDestItems));
            var sourceMap = sourceItems.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
            var destMap = destItems.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);

            var sourceSnapshots = await BuildSnapshotsAsync(_sourceFs, sourceMap, cancellationToken);
            var destSnapshots = await BuildSnapshotsAsync(_destFs, destMap, cancellationToken);
            var baseline = await _twoWayStateStore.LoadAsync(_taskId, cancellationToken);

            var permissions = TwoWayScanPermissions.From(sourceScanComplete, destScanComplete);
            var operations = BuildTwoWayOperations(sourceSnapshots, destSnapshots, baseline, permissions);
            report.TotalActions = operations.Count;
            var succeededPaths = await ExecuteTwoWayOperationsAsync(
                operations, sourceSnapshots, destSnapshots, report, cancellationToken);

            // 只提交本次成功动作对应的基线；失败、未执行、扫描不完整路径保留旧基线。
            await CommitTwoWayBaselineAsync(
                sourceSnapshots,
                destSnapshots,
                baseline,
                operations,
                succeededPaths,
                report,
                cancellationToken);
        }

        /// <summary>
        /// 双向模式：基于 SQLite 基线生成与手动执行链路一致的分析项。
        /// 用于任务页手动“分析”时展示即将执行的文件列表。
        /// </summary>
        public async Task<List<TaskAnalysisItem>> AnalyzeTwoWayAsync(CancellationToken cancellationToken = default)
        {
            await _sourceFs.ConnectAsync(cancellationToken);
            await _destFs.ConnectAsync(cancellationToken);
            await _twoWayStateStore.InitializeAsync(cancellationToken);

            var rawSourceItems = (await _sourceFs.ListFilesAsync(cancellationToken: cancellationToken)).ToList();
            var rawDestItems = (await _destFs.ListFilesAsync(cancellationToken: cancellationToken)).ToList();
            var sourceScanComplete = _sourceFs.LastScanComplete;
            var destScanComplete = _destFs.LastScanComplete;
            var sourceMap = StructureAwarePathHelper.ExpandWithAncestorDirectories(rawSourceItems, _filterEngine.Filter(rawSourceItems))
                .ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
            var destMap = StructureAwarePathHelper.ExpandWithAncestorDirectories(rawDestItems, _filterEngine.Filter(rawDestItems))
                .ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);

            var sourceSnapshots = await BuildSnapshotsAsync(_sourceFs, sourceMap, cancellationToken);
            var destSnapshots = await BuildSnapshotsAsync(_destFs, destMap, cancellationToken);
            var baseline = await _twoWayStateStore.LoadAsync(_taskId, cancellationToken);

            var permissions = TwoWayScanPermissions.From(sourceScanComplete, destScanComplete);
            var operations = BuildTwoWayOperations(sourceSnapshots, destSnapshots, baseline, permissions);

            var result = new List<TaskAnalysisItem>();
            foreach (var op in operations)
            {
                sourceMap.TryGetValue(op.Path, out var s);
                destMap.TryGetValue(op.Path, out var d);
                var isTypeConflict = op.Kind == TwoWayOpKind.TypeConflict;
                result.Add(new TaskAnalysisItem
                {
                    RelativePath = op.Path,
                    IsDirectory = op.IsDirectory,
                    SourceSize = s?.Size,
                    DestSize = d?.Size,
                    SourceLastWrite = s?.LastWriteTime,
                    DestLastWrite = d?.LastWriteTime,
                    ActionType = MapToSyncActionType(op.Kind),
                    Direction = ResolveTwoWayAnalysisDirection(op.Kind),
                    Reason = BuildTwoWayReason(op.Kind),
                    ShouldSync = !isTypeConflict,
                    HasWarning = isTypeConflict
                });
            }

            var operationPathSet = new HashSet<string>(operations.Select(o => o.Path), StringComparer.OrdinalIgnoreCase);
            var allPaths = new HashSet<string>(sourceMap.Keys, StringComparer.OrdinalIgnoreCase);
            allPaths.UnionWith(destMap.Keys);
            foreach (var path in allPaths)
            {
                if (operationPathSet.Contains(path))
                {
                    continue;
                }

                sourceMap.TryGetValue(path, out var s);
                destMap.TryGetValue(path, out var d);
                var same = SnapshotEquals(
                    sourceSnapshots.TryGetValue(path, out var sourceSnapshot) ? sourceSnapshot : null,
                    destSnapshots.TryGetValue(path, out var destSnapshot) ? destSnapshot : null);
                result.Add(new TaskAnalysisItem
                {
                    RelativePath = path,
                    IsDirectory = s?.IsDirectory ?? d?.IsDirectory ?? false,
                    SourceSize = s?.Size,
                    DestSize = d?.Size,
                    SourceLastWrite = s?.LastWriteTime,
                    DestLastWrite = d?.LastWriteTime,
                    Direction = AnalysisDirection.None,
                    Reason = same ? "已一致，无需同步" : "冲突或忽略，未生成操作",
                    ShouldSync = false
                });
            }

            if (!sourceScanComplete || !destScanComplete)
            {
                result.Add(new TaskAnalysisItem
                {
                    RelativePath = "[分析提示]",
                    ShouldSync = false,
                    Direction = AnalysisDirection.None,
                    HasWarning = true,
                    Reason = "扫描结果不完整：已禁止生成删除动作，也不会刷新双向基线；请先处理无法访问的子目录后重新分析。"
                });
            }

            return result
                .OrderByDescending(i => i.ShouldSync)
                .ThenBy(i => i.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static AnalysisDirection ResolveTwoWayAnalysisDirection(TwoWayOpKind kind)
        {
            return kind switch
            {
                TwoWayOpKind.CopyAToB or TwoWayOpKind.CreateDirInB or TwoWayOpKind.DeleteFileInB or TwoWayOpKind.DeleteDirInB => AnalysisDirection.AToB,
                TwoWayOpKind.CopyBToA or TwoWayOpKind.CreateDirInA or TwoWayOpKind.DeleteFileInA or TwoWayOpKind.DeleteDirInA => AnalysisDirection.BToA,
                _ => AnalysisDirection.None
            };
        }

        private static string BuildTwoWayReason(TwoWayOpKind kind)
        {
            return kind switch
            {
                TwoWayOpKind.CreateDirInB => "A 端新增目录，需在 B 端创建",
                TwoWayOpKind.CopyAToB => "A 端变更，需同步到 B 端",
                TwoWayOpKind.CreateDirInA => "B 端新增目录，需在 A 端创建",
                TwoWayOpKind.CopyBToA => "B 端变更，需同步到 A 端",
                TwoWayOpKind.DeleteFileInB or TwoWayOpKind.DeleteDirInB => "A 端已删除，B 端多余项需删除",
                TwoWayOpKind.DeleteFileInA or TwoWayOpKind.DeleteDirInA => "B 端已删除，A 端多余项需删除",
                TwoWayOpKind.TypeConflict => "同路径文件/目录类型冲突，已保留两端数据并跳过",
                _ => "规则判定需同步"
            };
        }

        private static bool SnapshotEquals(StateSnapshot? a, StateSnapshot? b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            return a.Exists == b.Exists
                   && a.IsDirectory == b.IsDirectory
                   && a.Size == b.Size
                   && Nullable.Equals(a.LastWriteUtc, b.LastWriteUtc)
                   && string.Equals(a.Hash, b.Hash, StringComparison.Ordinal);
        }

        private static List<TwoWayOp> BuildTwoWayOperations(
            IReadOnlyDictionary<string, StateSnapshot> source,
            IReadOnlyDictionary<string, StateSnapshot> dest,
            IReadOnlyDictionary<string, (StateSnapshot? A, StateSnapshot? B)> baseline,
            TwoWayScanPermissions permissions)
        {
            var paths = new HashSet<string>(source.Keys, StringComparer.OrdinalIgnoreCase);
            paths.UnionWith(dest.Keys);
            paths.UnionWith(baseline.Keys);

            var ops = new List<TwoWayOp>();
            foreach (var path in paths)
            {
                source.TryGetValue(path, out var curA);
                dest.TryGetValue(path, out var curB);
                baseline.TryGetValue(path, out var prev);
                var prevA = prev.A;
                var prevB = prev.B;
                var hasPrev = prevA != null || prevB != null;

                if (curA != null && curB != null && curA.IsDirectory != curB.IsDirectory)
                {
                    // 类型冲突始终阻断，不能把 Copy 当成安全的 Update。
                    ops.Add(TwoWayOp.TypeConflict(path));
                    continue;
                }

                if (!hasPrev)
                {
                    if (curA != null && curB == null) AddIfAllowed(ops, curA.IsDirectory ? TwoWayOp.CreateDirInB(path) : TwoWayOp.CopyAToB(path, curA.Size), permissions);
                    else if (curA == null && curB != null) AddIfAllowed(ops, curB.IsDirectory ? TwoWayOp.CreateDirInA(path) : TwoWayOp.CopyBToA(path, curB.Size), permissions);
                    else if (curA != null && curB != null && !SnapshotEquals(curA, curB)) AddIfAllowed(ops, ResolveConflict(path, curA, curB), permissions);
                    continue;
                }

                var changedA = !SnapshotEquals(curA, prevA);
                var changedB = !SnapshotEquals(curB, prevB);
                if (!changedA && !changedB) continue;

                if (changedA && !changedB)
                {
                    AddIfAllowed(ops, ProjectAChangeToB(path, curA, curB), permissions);
                    continue;
                }

                if (!changedA && changedB)
                {
                    AddIfAllowed(ops, ProjectBChangeToA(path, curA, curB), permissions);
                    continue;
                }

                if (SnapshotEquals(curA, curB)) continue;

                if (curA == null && curB != null)
                {
                    // 删除 vs 修改冲突：保数据优先（避免误删）
                    AddIfAllowed(ops, curB.IsDirectory ? TwoWayOp.CreateDirInA(path) : TwoWayOp.CopyBToA(path, curB.Size), permissions);
                    continue;
                }
                if (curA != null && curB == null)
                {
                    AddIfAllowed(ops, curA.IsDirectory ? TwoWayOp.CreateDirInB(path) : TwoWayOp.CopyAToB(path, curA.Size), permissions);
                    continue;
                }
                if (curA != null && curB != null)
                {
                    AddIfAllowed(ops, ResolveConflict(path, curA, curB), permissions);
                }
            }

            var conflictPaths = ops
                .Where(o => o.Kind == TwoWayOpKind.TypeConflict)
                .Select(o => o.Path)
                .ToList();
            if (conflictPaths.Count > 0)
            {
                // 冲突目录下的子项动作同样阻断，避免先清空目录再保留空壳。
                ops = ops
                    .Where(o => o.Kind == TwoWayOpKind.TypeConflict
                                || !conflictPaths.Any(conflictPath => VfsPath.IsSameOrUnderPath(o.Path, conflictPath)))
                    .ToList();
            }

            return ops
                .OrderBy(o => o.Kind switch
                {
                    TwoWayOpKind.CreateDirInA or TwoWayOpKind.CreateDirInB => 0,
                    TwoWayOpKind.CopyAToB or TwoWayOpKind.CopyBToA => 1,
                    TwoWayOpKind.DeleteFileInA or TwoWayOpKind.DeleteFileInB => 2,
                    _ => 3
                })
                .ThenByDescending(o => o.Path.Count(c => c == '/'))
                .ToList();
        }

        private static void AddIfAllowed(List<TwoWayOp> ops, TwoWayOp? op, TwoWayScanPermissions permissions)
        {
            if (op == null)
            {
                return;
            }

            var allowed = op.Kind switch
            {
                TwoWayOpKind.CreateDirInA or TwoWayOpKind.CopyBToA => permissions.CanWriteToA,
                TwoWayOpKind.CreateDirInB or TwoWayOpKind.CopyAToB => permissions.CanWriteToB,
                TwoWayOpKind.DeleteFileInA or TwoWayOpKind.DeleteDirInA => permissions.CanDeleteInA,
                TwoWayOpKind.DeleteFileInB or TwoWayOpKind.DeleteDirInB => permissions.CanDeleteInB,
                _ => true
            };

            if (allowed)
            {
                ops.Add(op);
            }
        }

        private static TwoWayOp ResolveConflict(string path, StateSnapshot a, StateSnapshot b)
        {
            if (a.IsDirectory != b.IsDirectory)
            {
                return TwoWayOp.TypeConflict(path);
            }

            var aTime = a.LastWriteUtc ?? DateTime.MinValue;
            var bTime = b.LastWriteUtc ?? DateTime.MinValue;
            if (aTime >= bTime)
            {
                return a.IsDirectory ? TwoWayOp.CreateDirInB(path) : TwoWayOp.CopyAToB(path, a.Size);
            }
            return b.IsDirectory ? TwoWayOp.CreateDirInA(path) : TwoWayOp.CopyBToA(path, b.Size);
        }

        private static TwoWayOp? ProjectAChangeToB(string path, StateSnapshot? curA, StateSnapshot? curB)
        {
            if (curA == null)
            {
                if (curB == null) return null;
                return curB.IsDirectory ? TwoWayOp.DeleteDirInB(path) : TwoWayOp.DeleteFileInB(path, curB.Size);
            }
            return curA.IsDirectory ? TwoWayOp.CreateDirInB(path) : TwoWayOp.CopyAToB(path, curA.Size);
        }

        private static TwoWayOp? ProjectBChangeToA(string path, StateSnapshot? curA, StateSnapshot? curB)
        {
            if (curB == null)
            {
                if (curA == null) return null;
                return curA.IsDirectory ? TwoWayOp.DeleteDirInA(path) : TwoWayOp.DeleteFileInA(path, curA.Size);
            }
            return curB.IsDirectory ? TwoWayOp.CreateDirInA(path) : TwoWayOp.CopyBToA(path, curB.Size);
        }

        private async Task<Dictionary<string, StateSnapshot>> BuildSnapshotsAsync(
            IFileSystem fs,
            IReadOnlyDictionary<string, FileItem> items,
            CancellationToken cancellationToken)
        {
            var result = new Dictionary<string, StateSnapshot>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = kv.Value;
                result[kv.Key] = new StateSnapshot
                {
                    Exists = true,
                    IsDirectory = item.IsDirectory,
                    Size = item.Size,
                    LastWriteUtc = item.LastWriteTime,
                    Hash = item.IsDirectory ? null : await OneWayDeliverySupport.ComputeXxHash64Async(fs, item.Path, cancellationToken)
                };
            }
            return result;
        }

        private async Task<HashSet<string>> ExecuteTwoWayOperationsAsync(
            List<TwoWayOp> operations,
            IReadOnlyDictionary<string, StateSnapshot> sourceSnapshots,
            IReadOnlyDictionary<string, StateSnapshot> destSnapshots,
            SyncReport report,
            CancellationToken cancellationToken)
        {
            var succeededPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var completed = 0;
            foreach (var op in operations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    switch (op.Kind)
                    {
                        case TwoWayOpKind.CreateDirInA:
                            await _sourceFs.CreateDirectoryAsync(op.Path, cancellationToken);
                            report.CreatedFiles++;
                            report.AddSuccessDetail(op.Path, 0, true, $"Two-way operation succeeded: {op.Kind}", MapToSyncActionType(op.Kind));
                            succeededPaths.Add(op.Path);
                            break;
                        case TwoWayOpKind.CreateDirInB:
                            await _destFs.CreateDirectoryAsync(op.Path, cancellationToken);
                            report.CreatedFiles++;
                            report.AddSuccessDetail(op.Path, 0, true, $"Two-way operation succeeded: {op.Kind}", MapToSyncActionType(op.Kind));
                            succeededPaths.Add(op.Path);
                            break;
                        case TwoWayOpKind.CopyAToB:
                        {
                            var sourceSnapshot = sourceSnapshots[op.Path];
                            var copyResult = await CopyFileAsync(
                                _sourceFs,
                                _destFs,
                                op.Path,
                                BuildSourceItem(op.Path, sourceSnapshot),
                                cancellationToken);
                            report.UpdatedFiles++;
                            report.AddSuccessDetail(op.Path, copyResult.BytesCopied, false, $"Two-way operation succeeded: {op.Kind}", MapToSyncActionType(op.Kind));
                            succeededPaths.Add(op.Path);
                            break;
                        }
                        case TwoWayOpKind.CopyBToA:
                        {
                            var sourceSnapshot = destSnapshots[op.Path];
                            var copyResult = await CopyFileAsync(
                                _destFs,
                                _sourceFs,
                                op.Path,
                                BuildSourceItem(op.Path, sourceSnapshot),
                                cancellationToken);
                            report.UpdatedFiles++;
                            report.AddSuccessDetail(op.Path, copyResult.BytesCopied, false, $"Two-way operation succeeded: {op.Kind}", MapToSyncActionType(op.Kind));
                            succeededPaths.Add(op.Path);
                            break;
                        }
                        case TwoWayOpKind.DeleteFileInA:
                            await _sourceFs.DeleteFileAsync(op.Path, cancellationToken);
                            report.DeletedFiles++;
                            report.AddSuccessDetail(op.Path, op.Size, false, $"Two-way operation succeeded: {op.Kind}", MapToSyncActionType(op.Kind));
                            succeededPaths.Add(op.Path);
                            break;
                        case TwoWayOpKind.DeleteFileInB:
                            await _destFs.DeleteFileAsync(op.Path, cancellationToken);
                            report.DeletedFiles++;
                            report.AddSuccessDetail(op.Path, op.Size, false, $"Two-way operation succeeded: {op.Kind}", MapToSyncActionType(op.Kind));
                            succeededPaths.Add(op.Path);
                            break;
                        case TwoWayOpKind.DeleteDirInA:
                        {
                            var deleted = await _sourceFs.DeleteDirectoryIfEmptyAsync(op.Path, cancellationToken);
                            if (deleted)
                            {
                                report.DeletedFiles++;
                                report.AddSuccessDetail(op.Path, 0, true, $"Two-way operation succeeded: {op.Kind}", MapToSyncActionType(op.Kind));
                                succeededPaths.Add(op.Path);
                            }
                            else
                            {
                                AddNotEmptyDirectoryWarning(report, op.Path);
                            }
                            break;
                        }
                        case TwoWayOpKind.DeleteDirInB:
                        {
                            var deleted = await _destFs.DeleteDirectoryIfEmptyAsync(op.Path, cancellationToken);
                            if (deleted)
                            {
                                report.DeletedFiles++;
                                report.AddSuccessDetail(op.Path, 0, true, $"Two-way operation succeeded: {op.Kind}", MapToSyncActionType(op.Kind));
                                succeededPaths.Add(op.Path);
                            }
                            else
                            {
                                AddNotEmptyDirectoryWarning(report, op.Path);
                            }
                            break;
                        }
                        case TwoWayOpKind.TypeConflict:
                            report.WarningDetails.Add(new SyncWarningDetail
                            {
                                ItemPath = op.Path,
                                Context = "Path type conflict",
                                Message = "同路径一端为文件、另一端为目录；已保留两端现存数据并跳过。"
                            });
                            break;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    report.FailedFiles++;
                    report.AddErrorDetail(op.Path, op.Size, op.IsDirectory, $"Two-way operation failed: {op.Kind}", ex);
                    ErrorOccurred?.Invoke(this, new SyncErrorEventArgs(ex, $"Two-way operation failed: {op.Kind} {op.Path}"));
                }

                completed++;
                ProgressChanged?.Invoke(this, new SyncProgressEventArgs(completed, operations.Count, op.Path, MapToSyncActionType(op.Kind)));
            }

            return succeededPaths;
        }

        private async Task CommitTwoWayBaselineAsync(
            IReadOnlyDictionary<string, StateSnapshot> sourceSnapshots,
            IReadOnlyDictionary<string, StateSnapshot> destSnapshots,
            IReadOnlyDictionary<string, (StateSnapshot? A, StateSnapshot? B)> baseline,
            IReadOnlyList<TwoWayOp> operations,
            HashSet<string> succeededPaths,
            SyncReport report,
            CancellationToken cancellationToken)
        {
            var changes = new Dictionary<string, TwoWayBaselineChange>(StringComparer.OrdinalIgnoreCase);

            foreach (var op in operations)
            {
                if (!succeededPaths.Contains(op.Path) || op.Kind == TwoWayOpKind.TypeConflict)
                {
                    continue;
                }

                var change = await BuildBaselineChangeForSucceededOperationAsync(
                    op, sourceSnapshots, destSnapshots, report, cancellationToken);
                if (change != null)
                {
                    changes[op.Path] = change;
                }
            }

            // 初次运行时，两端已确认一致的路径可以直接建立基线。
            // 已有基线的未执行路径保留旧记录，不把外部变化或未选项自动确认为已同步。
            var operationPaths = new HashSet<string>(operations.Select(o => o.Path), StringComparer.OrdinalIgnoreCase);
            var allPaths = new HashSet<string>(sourceSnapshots.Keys, StringComparer.OrdinalIgnoreCase);
            allPaths.UnionWith(destSnapshots.Keys);
            allPaths.UnionWith(baseline.Keys);
            foreach (var path in allPaths)
            {
                if (operationPaths.Contains(path) || changes.ContainsKey(path))
                {
                    continue;
                }

                sourceSnapshots.TryGetValue(path, out var curA);
                destSnapshots.TryGetValue(path, out var curB);
                var hasPrev = baseline.TryGetValue(path, out var prev) && (prev.A != null || prev.B != null);

                if (!hasPrev && curA != null && curB != null && SnapshotEquals(curA, curB))
                {
                    changes[path] = new TwoWayBaselineChange { Path = path, A = curA, B = curB };
                }
                else if (hasPrev && curA == null && curB == null)
                {
                    // 两端都已删除：移除陈旧基线记录，避免永久保留不存在的路径。
                    changes[path] = new TwoWayBaselineChange { Path = path, A = null, B = null };
                }
            }

            if (changes.Count > 0)
            {
                await _twoWayStateStore.ApplyPathChangesAsync(_taskId, changes.Values, cancellationToken);
            }
        }

        private async Task<TwoWayBaselineChange?> BuildBaselineChangeForSucceededOperationAsync(
            TwoWayOp op,
            IReadOnlyDictionary<string, StateSnapshot> sourceSnapshots,
            IReadOnlyDictionary<string, StateSnapshot> destSnapshots,
            SyncReport report,
            CancellationToken cancellationToken)
        {
            sourceSnapshots.TryGetValue(op.Path, out var sourceSnapshot);
            destSnapshots.TryGetValue(op.Path, out var destSnapshot);

            switch (op.Kind)
            {
                case TwoWayOpKind.CreateDirInB:
                {
                    var post = await GetPostOperationSnapshotAsync(_destFs, op.Path, cancellationToken);
                    if (post == null || !post.IsDirectory)
                    {
                        AddBaselineSkippedWarning(report, op.Path);
                        return null;
                    }

                    post.Hash = null;
                    return new TwoWayBaselineChange { Path = op.Path, A = sourceSnapshot, B = post };
                }
                case TwoWayOpKind.CreateDirInA:
                {
                    var post = await GetPostOperationSnapshotAsync(_sourceFs, op.Path, cancellationToken);
                    if (post == null || !post.IsDirectory)
                    {
                        AddBaselineSkippedWarning(report, op.Path);
                        return null;
                    }

                    post.Hash = null;
                    return new TwoWayBaselineChange { Path = op.Path, A = post, B = destSnapshot };
                }
                case TwoWayOpKind.CopyAToB:
                {
                    if (sourceSnapshot == null)
                    {
                        AddBaselineSkippedWarning(report, op.Path);
                        return null;
                    }

                    var post = await GetPostOperationSnapshotAsync(_destFs, op.Path, cancellationToken);
                    if (post == null || post.IsDirectory || post.Size != sourceSnapshot.Size)
                    {
                        AddBaselineSkippedWarning(report, op.Path);
                        return null;
                    }

                    post.Hash = sourceSnapshot.Hash;
                    return new TwoWayBaselineChange { Path = op.Path, A = sourceSnapshot, B = post };
                }
                case TwoWayOpKind.CopyBToA:
                {
                    if (destSnapshot == null)
                    {
                        AddBaselineSkippedWarning(report, op.Path);
                        return null;
                    }

                    var post = await GetPostOperationSnapshotAsync(_sourceFs, op.Path, cancellationToken);
                    if (post == null || post.IsDirectory || post.Size != destSnapshot.Size)
                    {
                        AddBaselineSkippedWarning(report, op.Path);
                        return null;
                    }

                    post.Hash = destSnapshot.Hash;
                    return new TwoWayBaselineChange { Path = op.Path, A = post, B = destSnapshot };
                }
                case TwoWayOpKind.DeleteFileInA:
                case TwoWayOpKind.DeleteDirInA:
                {
                    var post = await GetPostOperationSnapshotAsync(_sourceFs, op.Path, cancellationToken);
                    if (post != null)
                    {
                        AddBaselineSkippedWarning(report, op.Path);
                        return null;
                    }

                    return new TwoWayBaselineChange { Path = op.Path, A = null, B = destSnapshot };
                }
                case TwoWayOpKind.DeleteFileInB:
                case TwoWayOpKind.DeleteDirInB:
                {
                    var post = await GetPostOperationSnapshotAsync(_destFs, op.Path, cancellationToken);
                    if (post != null)
                    {
                        AddBaselineSkippedWarning(report, op.Path);
                        return null;
                    }

                    return new TwoWayBaselineChange { Path = op.Path, A = sourceSnapshot, B = null };
                }
                default:
                    return null;
            }
        }

        private static async Task<StateSnapshot?> GetPostOperationSnapshotAsync(
            IFileSystem fs,
            string path,
            CancellationToken cancellationToken)
        {
            var info = await fs.GetFileInfoAsync(path, cancellationToken);
            if (info == null)
            {
                return null;
            }

            // 复制动作的哈希直接复用已计算的源快照，避免为了写基线再次完整读取目标。
            return new StateSnapshot
            {
                Exists = true,
                IsDirectory = info.IsDirectory,
                Size = info.Size,
                LastWriteUtc = info.LastWriteTime,
                Hash = null
            };
        }

        private static void AddBaselineSkippedWarning(SyncReport report, string path)
        {
            report.WarningDetails.Add(new SyncWarningDetail
            {
                ItemPath = path,
                Context = "Baseline not advanced",
                Message = "执行结果与预期不一致或目标元数据无法确认，已保留旧基线以便下次重试。"
            });
        }

        /// <summary>
        /// 手动选择执行后，仅按实际成功路径增量更新双向基线。
        /// 不重新全量扫描，避免把未选择或外部变化的路径误记为已同步。
        /// </summary>
        public static async Task UpdateTwoWayBaselineForPathsAsync(
            string taskId,
            IFileSystem sourceFs,
            IFileSystem destFs,
            IEnumerable<string> successfullyExecutedPaths,
            CancellationToken cancellationToken = default)
        {
            var paths = successfullyExecutedPaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (paths.Count == 0)
            {
                return;
            }

            var store = new TwoWayStateStore();
            await store.InitializeAsync(cancellationToken);
            var changes = new List<TwoWayBaselineChange>();
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                changes.Add(new TwoWayBaselineChange
                {
                    Path = path,
                    A = await BuildCurrentSnapshotAsync(sourceFs, path, cancellationToken),
                    B = await BuildCurrentSnapshotAsync(destFs, path, cancellationToken)
                });
            }

            await store.ApplyPathChangesAsync(taskId, changes, cancellationToken);
        }

        private static async Task<StateSnapshot?> BuildCurrentSnapshotAsync(
            IFileSystem fs,
            string path,
            CancellationToken cancellationToken)
        {
            var info = await fs.GetFileInfoAsync(path, cancellationToken);
            if (info == null)
            {
                return null;
            }

            return new StateSnapshot
            {
                Exists = true,
                IsDirectory = info.IsDirectory,
                Size = info.Size,
                LastWriteUtc = info.LastWriteTime,
                Hash = info.IsDirectory ? null : await OneWayDeliverySupport.ComputeXxHash64Async(fs, path, cancellationToken)
            };
        }

        private static FileItem BuildSourceItem(string path, StateSnapshot snapshot)
        {
            return new FileItem
            {
                Path = path,
                Name = Path.GetFileName(path.Replace('/', Path.DirectorySeparatorChar)),
                IsDirectory = snapshot.IsDirectory,
                Size = snapshot.Size,
                LastWriteTime = snapshot.LastWriteUtc ?? DateTime.MinValue
            };
        }

        private static void AddNotEmptyDirectoryWarning(SyncReport report, string path)
        {
            report.WarningDetails.Add(new SyncWarningDetail
            {
                ItemPath = path,
                Context = "Directory not empty",
                Message = "目录仍包含未授权、未选择或删除失败的条目，已保留该目录。"
            });
        }

        private static SyncActionType MapToSyncActionType(TwoWayOpKind kind)
        {
            return kind switch
            {
                TwoWayOpKind.DeleteFileInA or TwoWayOpKind.DeleteFileInB or TwoWayOpKind.DeleteDirInA or TwoWayOpKind.DeleteDirInB => SyncActionType.Delete,
                TwoWayOpKind.CopyAToB or TwoWayOpKind.CopyBToA => SyncActionType.Update,
                TwoWayOpKind.TypeConflict => SyncActionType.Conflict,
                _ => SyncActionType.Create
            };
        }

        private List<SyncAction> ApplySyncModeFilter(
            IEnumerable<SyncAction> actions,
            IReadOnlyDictionary<string, OneWayDeliveryRecord>? deliveredRecords = null)
        {
            var result = new List<SyncAction>();
            foreach (var action in actions)
            {
                switch (_syncMode)
                {
                    case SyncMode.OneWayIncremental:
                        if (action.ActionType is SyncActionType.Create or SyncActionType.Conflict) result.Add(action);
                        break;
                    case SyncMode.OneWayUpdate:
                        if (action.ActionType is SyncActionType.Create or SyncActionType.Update or SyncActionType.Conflict) result.Add(action);
                        break;
                    case SyncMode.OneWaySendOnce:
                        if (action.ActionType is SyncActionType.Create or SyncActionType.Update or SyncActionType.Conflict
                            && action.SourceItem != null
                            && !(deliveredRecords?.ContainsKey(action.SourceItem.Path) ?? false))
                        {
                            result.Add(action);
                        }
                        break;
                    case SyncMode.OneWayMirror:
                    case SyncMode.TwoWay:
                        result.Add(action);
                        break;
                }
            }
            return result;
        }

        private IEnumerable<SyncAction> SuppressUnsafeActionsForIncompleteScan(
            IEnumerable<SyncAction> actions,
            SyncReport report,
            bool sourceScanIncomplete,
            bool destScanIncomplete)
        {
            if (!sourceScanIncomplete && !destScanIncomplete)
            {
                return actions;
            }

            var result = new List<SyncAction>();
            var skipped = 0;
            foreach (var action in actions)
            {
                var path = VfsPath.NormalizeRelativePath(action.DestinationItem?.Path ?? action.SourceItem?.Path);
                var isDelete = action.ActionType == SyncActionType.Delete;
                var writesToDest = action.ActionType is SyncActionType.Create or SyncActionType.Update;

                if (isDelete && sourceScanIncomplete)
                {
                    // 源端未知区域不能作为“源端已删除”的证据。
                    skipped++;
                    continue;
                }

                if (writesToDest && destScanIncomplete
                    && _destFs.LastScanFailures.Any(failed => VfsPath.IsSameOrUnderPath(path, failed)))
                {
                    // 目标未知区域中可能存在同名文件，禁止盲目覆盖。
                    skipped++;
                    continue;
                }

                result.Add(action);
            }

            if (skipped > 0)
            {
                report.WarningDetails.Add(new SyncWarningDetail
                {
                    ItemPath = string.Empty,
                    Context = "Incomplete scan safety guard",
                    Message = $"扫描结果不完整，已跳过 {skipped} 个可能越过未知区域的破坏性动作。"
                });
            }

            return result;
        }

        private async Task ExecuteActionsAsync(
            List<SyncAction> actions,
            SyncReport report,
            CancellationToken cancellationToken,
            Dictionary<string, OneWayDeliveryRecord>? deliveredRecords = null)
        {
            const int deliveryCheckpointSize = 10;
            int completed = 0;
            var pendingDeliveries = new List<OneWayDeliveryRecord>();

            try
            {
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
                                        if (_syncMode == SyncMode.OneWaySendOnce)
                                        {
                                            AddPendingDelivery(
                                                pendingDeliveries,
                                                deliveredRecords,
                                                OneWayDeliverySupport.CreateDeliveredRecordFromCopy(
                                                    action.SourceItem.Path,
                                                    action.SourceItem,
                                                    sourceHash: null));
                                        }
                                        report.AddSuccessDetail(
                                            action.SourceItem.Path,
                                            0,
                                            true,
                                            "One-way operation succeeded",
                                            action.ActionType);
                                    }
                                    else
                                    {
                                        CopyFileResult copyResult;
                                        if (_syncMode == SyncMode.OneWaySendOnce)
                                        {
                                            copyResult = await OneWayDeliverySupport.CopyFileAndComputeHashAsync(
                                                _sourceFs, _destFs, action.SourceItem.Path, action.SourceItem, cancellationToken);
                                            AddPendingDelivery(
                                                pendingDeliveries,
                                                deliveredRecords,
                                                OneWayDeliverySupport.CreateDeliveredRecordFromCopy(
                                                    action.SourceItem.Path,
                                                    action.SourceItem,
                                                    copyResult.Hash));
                                        }
                                        else
                                        {
                                            copyResult = await OneWayDeliverySupport.CopyFileAsync(
                                                _sourceFs, _destFs, action.SourceItem.Path, action.SourceItem, cancellationToken);
                                        }

                                        if (action.ActionType == SyncActionType.Create) report.CreatedFiles++;
                                        else report.UpdatedFiles++;
                                        report.AddSuccessDetail(
                                            action.SourceItem.Path,
                                            copyResult.BytesCopied,
                                            false,
                                            "One-way operation succeeded",
                                            action.ActionType);
                                    }
                                }
                                else
                                {
                                    report.WarningDetails.Add(new SyncWarningDetail
                                    {
                                        ItemPath = itemName,
                                        Context = "Missing source item",
                                        Message = "动作缺少源端条目，已跳过以避免反向覆盖。"
                                    });
                                }
                                break;
                            case SyncActionType.Delete:
                                if (action.DestinationItem != null)
                                {
                                    if (action.DestinationItem.IsDirectory)
                                    {
                                        var deleted = await _destFs.DeleteDirectoryIfEmptyAsync(action.DestinationItem.Path, cancellationToken);
                                        if (!deleted)
                                        {
                                            report.WarningDetails.Add(new SyncWarningDetail
                                            {
                                                ItemPath = action.DestinationItem.Path,
                                                Context = "Directory not empty",
                                                Message = "目标目录仍包含未授权、未选择或删除失败的条目，已保留该目录。"
                                            });
                                        }
                                        else
                                        {
                                            report.DeletedFiles++;
                                            report.AddSuccessDetail(
                                                action.DestinationItem.Path,
                                                0,
                                                true,
                                                "One-way operation succeeded",
                                                action.ActionType);
                                        }
                                    }
                                    else
                                    {
                                        await _destFs.DeleteFileAsync(action.DestinationItem.Path, cancellationToken);
                                        report.DeletedFiles++;
                                        report.AddSuccessDetail(
                                            action.DestinationItem.Path,
                                            action.DestinationItem.Size,
                                            false,
                                            "One-way operation succeeded",
                                            action.ActionType);
                                    }
                                }
                                break;
                            case SyncActionType.Conflict:
                                report.WarningDetails.Add(new SyncWarningDetail
                                {
                                    ItemPath = itemName,
                                    Context = "Path type conflict",
                                    Message = "源端与目标端同名路径类型不一致（文件/目录），为避免覆盖已有数据，已跳过。"
                                });
                                break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        report.FailedFiles++;
                        var failedItem = action.SourceItem ?? action.DestinationItem;
                        report.AddErrorDetail(
                            itemName,
                            failedItem?.Size,
                            failedItem?.IsDirectory ?? false,
                            $"Failed to process {itemName}",
                            ex);
                        ErrorOccurred?.Invoke(this, new SyncErrorEventArgs(ex, $"Failed to process {itemName}"));
                    }
                    completed++;
                    ProgressChanged?.Invoke(this, new SyncProgressEventArgs(completed, actions.Count, itemName, action.ActionType));

                    if (pendingDeliveries.Count >= deliveryCheckpointSize)
                    {
                        await FlushPendingDeliveriesAsync(pendingDeliveries, report, cancellationToken);
                    }
                }
            }
            finally
            {
                // 取消或异常也必须把已经确认成功的投递记录落盘；使用独立且有限超时的 token。
                if (pendingDeliveries.Count > 0)
                {
                    using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    try
                    {
                        await _oneWayDeliveryStateStore.UpsertRangeAsync(_taskId, pendingDeliveries, cleanupCts.Token);
                        pendingDeliveries.Clear();
                    }
                    catch (Exception ex)
                    {
                        report.ErrorMessage = string.IsNullOrWhiteSpace(report.ErrorMessage)
                            ? $"一次性投递状态保存失败：{ex.Message}"
                            : report.ErrorMessage;
                        report.AddErrorDetail(string.Empty, null, false, "Failed to persist send-once delivery checkpoints", ex);
                    }
                }
            }
        }

        private static void AddPendingDelivery(
            List<OneWayDeliveryRecord> pendingDeliveries,
            Dictionary<string, OneWayDeliveryRecord>? deliveredRecords,
            OneWayDeliveryRecord record)
        {
            pendingDeliveries.Add(record);
            if (deliveredRecords != null)
            {
                deliveredRecords[record.RelativePath] = record;
            }
        }

        private async Task FlushPendingDeliveriesAsync(
            List<OneWayDeliveryRecord> pendingDeliveries,
            SyncReport report,
            CancellationToken cancellationToken)
        {
            if (pendingDeliveries.Count == 0)
            {
                return;
            }

            try
            {
                await _oneWayDeliveryStateStore.UpsertRangeAsync(_taskId, pendingDeliveries, cancellationToken);
                pendingDeliveries.Clear();
            }
            catch (Exception ex)
            {
                report.ErrorMessage = $"一次性投递状态保存失败：{ex.Message}";
                report.AddErrorDetail(string.Empty, null, false, "Failed to persist send-once delivery checkpoints", ex);
                throw;
            }
        }

        private static Task<CopyFileResult> CopyFileAsync(
            IFileSystem fromFs,
            IFileSystem toFs,
            string path,
            FileItem? sourceItem,
            CancellationToken cancellationToken)
        {
            return OneWayDeliverySupport.CopyFileAsync(fromFs, toFs, path, sourceItem, cancellationToken);
        }

        private readonly record struct TwoWayScanPermissions(
            bool CanWriteToA,
            bool CanWriteToB,
            bool CanDeleteInA,
            bool CanDeleteInB)
        {
            public static TwoWayScanPermissions From(bool sourceScanComplete, bool destScanComplete)
            {
                // 写目标之前必须确认目标侧扫描完整，否则可能覆盖未知区域中的同名文件。
                // 删除证据来自“另一侧已不存在”，因此必须确认该侧扫描完整。
                return new TwoWayScanPermissions(
                    CanWriteToA: sourceScanComplete,
                    CanWriteToB: destScanComplete,
                    CanDeleteInA: destScanComplete,
                    CanDeleteInB: sourceScanComplete);
            }
        }

        private enum TwoWayOpKind
        {
            CreateDirInA,
            CreateDirInB,
            CopyAToB,
            CopyBToA,
            DeleteFileInA,
            DeleteFileInB,
            DeleteDirInA,
            DeleteDirInB,
            TypeConflict
        }

        private sealed class TwoWayOp
        {
            public TwoWayOpKind Kind { get; }
            public string Path { get; }
            public bool IsDirectory { get; }
            public long Size { get; }

            private TwoWayOp(TwoWayOpKind kind, string path, bool isDirectory, long size)
            {
                Kind = kind;
                Path = path;
                IsDirectory = isDirectory;
                Size = size;
            }

            public static TwoWayOp CreateDirInA(string path) => new(TwoWayOpKind.CreateDirInA, path, true, 0);
            public static TwoWayOp CreateDirInB(string path) => new(TwoWayOpKind.CreateDirInB, path, true, 0);
            public static TwoWayOp CopyAToB(string path, long size) => new(TwoWayOpKind.CopyAToB, path, false, size);
            public static TwoWayOp CopyBToA(string path, long size) => new(TwoWayOpKind.CopyBToA, path, false, size);
            public static TwoWayOp DeleteFileInA(string path, long size) => new(TwoWayOpKind.DeleteFileInA, path, false, size);
            public static TwoWayOp DeleteFileInB(string path, long size) => new(TwoWayOpKind.DeleteFileInB, path, false, size);
            public static TwoWayOp DeleteDirInA(string path) => new(TwoWayOpKind.DeleteDirInA, path, true, 0);
            public static TwoWayOp DeleteDirInB(string path) => new(TwoWayOpKind.DeleteDirInB, path, true, 0);
            public static TwoWayOp TypeConflict(string path) => new(TwoWayOpKind.TypeConflict, path, false, 0);
        }
    }
}
