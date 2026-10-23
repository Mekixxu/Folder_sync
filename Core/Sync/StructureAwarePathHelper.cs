using System;
using System.Collections.Generic;
using System.Linq;
using FolderSync.Core.Diff;
using FolderSync.Core.VFS;

namespace FolderSync.Core.Sync
{
    internal static class StructureAwarePathHelper
    {
        public static List<FileItem> ExpandWithAncestorDirectories(
            IEnumerable<FileItem> rawItems,
            IEnumerable<FileItem> includedItems)
        {
            var rawList = rawItems?.Where(i => i != null).ToList() ?? new List<FileItem>();
            var includedList = includedItems?.Where(i => i != null).ToList() ?? new List<FileItem>();
            var rawMap = new Dictionary<string, FileItem>(StringComparer.OrdinalIgnoreCase);
            var resultMap = new Dictionary<string, FileItem>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in rawList)
            {
                rawMap[NormalizePath(item.Path)] = item;
            }

            foreach (var item in includedList)
            {
                var normalizedPath = NormalizePath(item.Path);
                resultMap[normalizedPath] = item;

                var parentPath = GetParentPath(normalizedPath);
                while (!string.IsNullOrWhiteSpace(parentPath))
                {
                    if (rawMap.TryGetValue(parentPath, out var parentDirectory) && parentDirectory.IsDirectory)
                    {
                        resultMap[parentPath] = parentDirectory;
                    }

                    parentPath = GetParentPath(parentPath);
                }
            }

            return resultMap.Values
                .OrderBy(item => item.IsDirectory ? 0 : 1)
                .ThenBy(item => GetDepth(item.Path))
                .ThenBy(item => NormalizePath(item.Path), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static HashSet<string> BuildPathSet(IEnumerable<FileItem> items)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (items == null)
            {
                return result;
            }

            foreach (var item in items)
            {
                if (item == null)
                {
                    continue;
                }

                result.Add(NormalizePath(item.Path));
            }

            return result;
        }

        /// <summary>
        /// 文件/目录类型冲突路径下的任何删除或覆盖动作都必须被阻断，
        /// 否则会先删除冲突目录内的内容，再留下一个空目录或直接覆盖。
        /// </summary>
        public static List<SyncAction> SuppressActionsUnderTypeConflicts(IEnumerable<SyncAction> actions)
        {
            var actionList = actions?.ToList() ?? new List<SyncAction>();
            var conflictPaths = actionList
                .Where(action => action.ActionType == SyncActionType.Conflict)
                .Select(action => VfsPath.NormalizeRelativePath(action.SourceItem?.Path ?? action.DestinationItem?.Path))
                .Where(path => !string.IsNullOrEmpty(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (conflictPaths.Count == 0)
            {
                return actionList;
            }

            return actionList
                .Where(action =>
                {
                    if (action.ActionType == SyncActionType.Conflict)
                    {
                        return true;
                    }

                    var path = VfsPath.NormalizeRelativePath(action.SourceItem?.Path ?? action.DestinationItem?.Path);
                    return !conflictPaths.Any(conflictPath => VfsPath.IsSameOrUnderPath(path, conflictPath));
                })
                .ToList();
        }

        public static List<SyncAction> OrderOneWayActions(IEnumerable<SyncAction> actions)
        {
            return actions
                .OrderBy(GetActionStage)
                .ThenBy(GetActionDepthSortKey)
                .ThenBy(action => NormalizePath(action.SourceItem?.Path ?? action.DestinationItem?.Path ?? string.Empty), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path == "." || path == "/" || path == "\\")
            {
                return string.Empty;
            }

            return path.Replace('\\', '/').Trim().Trim('/');
        }

        private static int GetDepth(string path)
        {
            var normalized = NormalizePath(path);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return 0;
            }

            return normalized.Count(c => c == '/') + 1;
        }

        private static string GetParentPath(string path)
        {
            var normalized = NormalizePath(path);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }

            var lastSlash = normalized.LastIndexOf('/');
            return lastSlash <= 0 ? string.Empty : normalized[..lastSlash];
        }

        private static int GetActionStage(SyncAction action)
        {
            var item = action.SourceItem ?? action.DestinationItem;
            var isDirectory = item?.IsDirectory ?? false;

            return action.ActionType switch
            {
                SyncActionType.Create or SyncActionType.Update when isDirectory => 0,
                SyncActionType.Create or SyncActionType.Update => 1,
                SyncActionType.Delete when !isDirectory => 2,
                SyncActionType.Delete => 3,
                _ => 4
            };
        }

        private static int GetActionDepthSortKey(SyncAction action)
        {
            var path = action.SourceItem?.Path ?? action.DestinationItem?.Path ?? string.Empty;
            var depth = GetDepth(path);
            return action.ActionType == SyncActionType.Delete ? -depth : depth;
        }
    }
}
