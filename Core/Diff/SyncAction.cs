using FolderSync.Core.VFS;

namespace FolderSync.Core.Diff
{
    /// <summary>
    /// 单个同步动作，记录了需要执行的操作类型以及涉及的文件项
    /// </summary>
    public class SyncAction
    {
        public SyncActionType ActionType { get; set; }
        
        /// <summary>
        /// 源文件夹中的文件项（如果是 Delete 操作，可能为 null）
        /// </summary>
        public FileItem? SourceItem { get; set; }
        
        /// <summary>
        /// 目标文件夹中的文件项（如果是 Create 操作，可能为 null）
        /// </summary>
        public FileItem? DestinationItem { get; set; }

        public SyncAction(SyncActionType actionType, FileItem? sourceItem, FileItem? destinationItem)
        {
            ActionType = actionType;
            SourceItem = sourceItem;
            DestinationItem = destinationItem;
        }
    }
}
