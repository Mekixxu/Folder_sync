using System;
using System.Collections.Generic;
using System.IO.Hashing;
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
    public class SyncExecutor
    {
        private readonly IFileSystem _sourceFs;
        private readonly IFileSystem _destFs;
        private readonly IDiffStrategy _diffStrategy;
        private readonly FilterEngine _filterEngine;
        private readonly SyncMode _syncMode;
        private readonly TwoWayStateStore _twoWayStateStore;
        private readonly string _twoWayTaskId;

        public event EventHandler<SyncProgressEventArgs>? ProgressChanged;
        public event EventHandler<SyncErrorEventArgs>? ErrorOccurred;

        public SyncExecutor(
            IFileSystem sourceFs,
            IFileSystem destFs,
            IDiffStrategy diffStrategy,
            FilterEngine filterEngine,
            SyncMode syncMode,
            string? twoWayTaskId = null)
        {
            _sourceFs = sourceFs ?? throw new ArgumentNullException(nameof(sourceFs));
            _destFs = destFs ?? throw new ArgumentNullException(nameof(destFs));
            _diffStrategy = diffStrategy ?? throw new ArgumentNullException(nameof(diffStrategy));
            _filterEngine = filterEngine ?? throw new ArgumentNullException(nameof(filterEngine));
            _syncMode = syncMode;
            _twoWayStateStore = new TwoWayStateStore();
            _twoWayTaskId = twoWayTaskId ?? $"tw::{_sourceFs.RootIdentifier}<->{_destFs.RootIdentifier}";
        }

        public async Task<SyncReport> ExecuteAsync(CancellationToken cancellationToken = default)
        {
            var report = new SyncReport { StartTime = DateTime.UtcNow, SyncMode = _syncMode };

            try
            {
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
            catch (Exception ex)
            {
                report.ErrorMessage = ex.Message;
                report.ErrorDetails.Add(new SyncErrorDetail
                {
                    ItemPath = string.Empty,
                    Context = "Global execution failed",
                    ErrorType = ex.GetType().FullName ?? "Exception",
                    Message = ex.Message
                });
                ErrorOccurred?.Invoke(this, new SyncErrorEventArgs(ex, "Global execution failed"));
            }
            finally
            {
                report.EndTime = DateTime.UtcNow;
            }

            return report;
        }

        private async Task ExecuteOneWayAsync(SyncReport report, CancellationToken cancellationToken)
        {
            var sourceItems = _filterEngine.Filter(await _sourceFs.ListFilesAsync(cancellationToken: cancellationToken)).ToList();
            var destItems = _filterEngine.Filter(await _destFs.ListFilesAsync(cancellationToken: cancellationToken)).ToList();

            var isMirror = _syncMode == SyncMode.OneWayMirror;
            var actions = await _diffStrategy.CompareAsync(sourceItems, destItems, _sourceFs, _destFs, isMirror, cancellationToken);
            var finalActions = ApplySyncModeFilter(actions);
            report.TotalActions = finalActions.Count;
            await ExecuteActionsAsync(finalActions, report, cancellationToken);
        }

        private async Task ExecuteReliableTwoWayAsync(SyncReport report, CancellationToken cancellationToken)
        {
            await _twoWayStateStore.InitializeAsync(cancellationToken);

            var sourceItems = _filterEngine.Filter(await _sourceFs.ListFilesAsync(cancellationToken: cancellationToken)).ToList();
            var destItems = _filterEngine.Filter(await _destFs.ListFilesAsync(cancellationToken: cancellationToken)).ToList();
            var sourceMap = sourceItems.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
            var destMap = destItems.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);

            var sourceSnapshots = await BuildSnapshotsAsync(_sourceFs, sourceMap, cancellationToken);
            var destSnapshots = await BuildSnapshotsAsync(_destFs, destMap, cancellationToken);
            var baseline = await _twoWayStateStore.LoadAsync(_twoWayTaskId, cancellationToken);

            var operations = BuildTwoWayOperations(sourceSnapshots, destSnapshots, baseline);
            report.TotalActions = operations.Count;
            await ExecuteTwoWayOperationsAsync(operations, report, cancellationToken);

            // 同步完成后写回新基线
            var latestSourceItems = _filterEngine.Filter(await _sourceFs.ListFilesAsync(cancellationToken: cancellationToken)).ToList();
            var latestDestItems = _filterEngine.Filter(await _destFs.ListFilesAsync(cancellationToken: cancellationToken)).ToList();
            var latestSourceMap = latestSourceItems.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
            var latestDestMap = latestDestItems.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
            var latestSourceSnapshots = await BuildSnapshotsAsync(_sourceFs, latestSourceMap, cancellationToken);
            var latestDestSnapshots = await BuildSnapshotsAsync(_destFs, latestDestMap, cancellationToken);
            await _twoWayStateStore.SaveAsync(_twoWayTaskId, latestSourceSnapshots, latestDestSnapshots, cancellationToken);
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

        private List<TwoWayOp> BuildTwoWayOperations(
            IReadOnlyDictionary<string, StateSnapshot> source,
            IReadOnlyDictionary<string, StateSnapshot> dest,
            IReadOnlyDictionary<string, (StateSnapshot? A, StateSnapshot? B)> baseline)
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

                if (!hasPrev)
                {
                    if (curA != null && curB == null) ops.Add(curA.IsDirectory ? TwoWayOp.CreateDirInB(path) : TwoWayOp.CopyAToB(path));
                    else if (curA == null && curB != null) ops.Add(curB.IsDirectory ? TwoWayOp.CreateDirInA(path) : TwoWayOp.CopyBToA(path));
                    else if (curA != null && curB != null && !SnapshotEquals(curA, curB)) ops.Add(ResolveConflict(path, curA, curB));
                    continue;
                }

                var changedA = !SnapshotEquals(curA, prevA);
                var changedB = !SnapshotEquals(curB, prevB);
                if (!changedA && !changedB) continue;

                if (changedA && !changedB)
                {
                    var op = ProjectAChangeToB(path, curA, curB);
                    if (op != null) ops.Add(op);
                    continue;
                }

                if (!changedA && changedB)
                {
                    var op = ProjectBChangeToA(path, curA, curB);
                    if (op != null) ops.Add(op);
                    continue;
                }

                if (SnapshotEquals(curA, curB)) continue;

                if (curA == null && curB != null)
                {
                    // 删除 vs 修改冲突：保数据优先（避免误删）
                    ops.Add(curB.IsDirectory ? TwoWayOp.CreateDirInA(path) : TwoWayOp.CopyBToA(path));
                    continue;
                }
                if (curA != null && curB == null)
                {
                    ops.Add(curA.IsDirectory ? TwoWayOp.CreateDirInB(path) : TwoWayOp.CopyAToB(path));
                    continue;
                }
                if (curA != null && curB != null)
                {
                    ops.Add(ResolveConflict(path, curA, curB));
                }
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

        private static TwoWayOp ResolveConflict(string path, StateSnapshot a, StateSnapshot b)
        {
            var aTime = a.LastWriteUtc ?? DateTime.MinValue;
            var bTime = b.LastWriteUtc ?? DateTime.MinValue;
            if (aTime >= bTime)
            {
                return a.IsDirectory ? TwoWayOp.CreateDirInB(path) : TwoWayOp.CopyAToB(path);
            }
            return b.IsDirectory ? TwoWayOp.CreateDirInA(path) : TwoWayOp.CopyBToA(path);
        }

        private static TwoWayOp? ProjectAChangeToB(string path, StateSnapshot? curA, StateSnapshot? curB)
        {
            if (curA == null)
            {
                if (curB == null) return null;
                return curB.IsDirectory ? TwoWayOp.DeleteDirInB(path) : TwoWayOp.DeleteFileInB(path);
            }
            return curA.IsDirectory ? TwoWayOp.CreateDirInB(path) : TwoWayOp.CopyAToB(path);
        }

        private static TwoWayOp? ProjectBChangeToA(string path, StateSnapshot? curA, StateSnapshot? curB)
        {
            if (curB == null)
            {
                if (curA == null) return null;
                return curA.IsDirectory ? TwoWayOp.DeleteDirInA(path) : TwoWayOp.DeleteFileInA(path);
            }
            return curB.IsDirectory ? TwoWayOp.CreateDirInA(path) : TwoWayOp.CopyBToA(path);
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
                    Hash = item.IsDirectory ? null : await ComputeXxHash64Async(fs, item.Path, cancellationToken)
                };
            }
            return result;
        }

        private static async Task<string> ComputeXxHash64Async(IFileSystem fs, string path, CancellationToken cancellationToken)
        {
            using var stream = await fs.OpenReadAsync(path, cancellationToken);
            var hasher = new XxHash64();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                hasher.Append(buffer.AsSpan(0, read));
            }
            return Convert.ToHexString(hasher.GetCurrentHash());
        }

        private async Task ExecuteTwoWayOperationsAsync(List<TwoWayOp> operations, SyncReport report, CancellationToken cancellationToken)
        {
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
                            break;
                        case TwoWayOpKind.CreateDirInB:
                            await _destFs.CreateDirectoryAsync(op.Path, cancellationToken);
                            report.CreatedFiles++;
                            break;
                        case TwoWayOpKind.CopyAToB:
                            await CopyFileAsync(_sourceFs, _destFs, op.Path, cancellationToken);
                            report.UpdatedFiles++;
                            break;
                        case TwoWayOpKind.CopyBToA:
                            await CopyFileAsync(_destFs, _sourceFs, op.Path, cancellationToken);
                            report.UpdatedFiles++;
                            break;
                        case TwoWayOpKind.DeleteFileInA:
                            await _sourceFs.DeleteFileAsync(op.Path, cancellationToken);
                            report.DeletedFiles++;
                            break;
                        case TwoWayOpKind.DeleteFileInB:
                            await _destFs.DeleteFileAsync(op.Path, cancellationToken);
                            report.DeletedFiles++;
                            break;
                        case TwoWayOpKind.DeleteDirInA:
                            await _sourceFs.DeleteDirectoryAsync(op.Path, cancellationToken);
                            report.DeletedFiles++;
                            break;
                        case TwoWayOpKind.DeleteDirInB:
                            await _destFs.DeleteDirectoryAsync(op.Path, cancellationToken);
                            report.DeletedFiles++;
                            break;
                    }
                }
                catch (Exception ex)
                {
                    report.FailedFiles++;
                    report.ErrorDetails.Add(new SyncErrorDetail
                    {
                        ItemPath = op.Path,
                        Context = $"Two-way operation failed: {op.Kind}",
                        ErrorType = ex.GetType().FullName ?? "Exception",
                        Message = ex.Message
                    });
                    ErrorOccurred?.Invoke(this, new SyncErrorEventArgs(ex, $"Two-way operation failed: {op.Kind} {op.Path}"));
                }

                completed++;
                ProgressChanged?.Invoke(this, new SyncProgressEventArgs(completed, operations.Count, op.Path, MapToSyncActionType(op.Kind)));
            }
        }

        private static SyncActionType MapToSyncActionType(TwoWayOpKind kind)
        {
            return kind switch
            {
                TwoWayOpKind.DeleteFileInA or TwoWayOpKind.DeleteFileInB or TwoWayOpKind.DeleteDirInA or TwoWayOpKind.DeleteDirInB => SyncActionType.Delete,
                TwoWayOpKind.CopyAToB or TwoWayOpKind.CopyBToA => SyncActionType.Update,
                _ => SyncActionType.Create
            };
        }

        private List<SyncAction> ApplySyncModeFilter(IEnumerable<SyncAction> actions)
        {
            var result = new List<SyncAction>();
            foreach (var action in actions)
            {
                switch (_syncMode)
                {
                    case SyncMode.OneWayIncremental:
                        if (action.ActionType == SyncActionType.Create) result.Add(action);
                        break;
                    case SyncMode.OneWayUpdate:
                        if (action.ActionType == SyncActionType.Create || action.ActionType == SyncActionType.Update) result.Add(action);
                        break;
                    case SyncMode.OneWayMirror:
                    case SyncMode.TwoWay:
                        result.Add(action);
                        break;
                }
            }
            return result;
        }

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
                                    await CopyFileAsync(_sourceFs, _destFs, action.SourceItem.Path, cancellationToken);
                                    if (action.ActionType == SyncActionType.Create) report.CreatedFiles++;
                                    else report.UpdatedFiles++;
                                }
                            }
                            break;
                        case SyncActionType.Delete:
                            if (action.DestinationItem != null)
                            {
                                if (action.DestinationItem.IsDirectory) await _destFs.DeleteDirectoryAsync(action.DestinationItem.Path, cancellationToken);
                                else await _destFs.DeleteFileAsync(action.DestinationItem.Path, cancellationToken);
                                report.DeletedFiles++;
                            }
                            break;
                    }
                }
                catch (Exception ex)
                {
                    report.FailedFiles++;
                    report.ErrorDetails.Add(new SyncErrorDetail
                    {
                        ItemPath = itemName,
                        Context = $"Failed to process {itemName}",
                        ErrorType = ex.GetType().FullName ?? "Exception",
                        Message = ex.Message
                    });
                    ErrorOccurred?.Invoke(this, new SyncErrorEventArgs(ex, $"Failed to process {itemName}"));
                }
                completed++;
                ProgressChanged?.Invoke(this, new SyncProgressEventArgs(completed, actions.Count, itemName, action.ActionType));
            }
        }

        private static async Task CopyFileAsync(IFileSystem fromFs, IFileSystem toFs, string path, CancellationToken cancellationToken)
        {
            using var readStream = await fromFs.OpenReadForCopyAsync(path, cancellationToken);
            using var writeStream = await toFs.OpenWriteAsync(path, cancellationToken);
            await readStream.CopyToAsync(writeStream, 81920, cancellationToken);
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
            DeleteDirInB
        }

        private sealed class TwoWayOp
        {
            public TwoWayOpKind Kind { get; }
            public string Path { get; }

            private TwoWayOp(TwoWayOpKind kind, string path)
            {
                Kind = kind;
                Path = path;
            }

            public static TwoWayOp CreateDirInA(string path) => new(TwoWayOpKind.CreateDirInA, path);
            public static TwoWayOp CreateDirInB(string path) => new(TwoWayOpKind.CreateDirInB, path);
            public static TwoWayOp CopyAToB(string path) => new(TwoWayOpKind.CopyAToB, path);
            public static TwoWayOp CopyBToA(string path) => new(TwoWayOpKind.CopyBToA, path);
            public static TwoWayOp DeleteFileInA(string path) => new(TwoWayOpKind.DeleteFileInA, path);
            public static TwoWayOp DeleteFileInB(string path) => new(TwoWayOpKind.DeleteFileInB, path);
            public static TwoWayOp DeleteDirInA(string path) => new(TwoWayOpKind.DeleteDirInA, path);
            public static TwoWayOp DeleteDirInB(string path) => new(TwoWayOpKind.DeleteDirInB, path);
        }
    }
}
