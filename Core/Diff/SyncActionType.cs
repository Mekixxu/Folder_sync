namespace FolderSync.Core.Diff
{
    /// <summary>
    /// 同步操作类型
    /// </summary>
    public enum SyncActionType
    {
        /// <summary>
        /// 源目录有，目标目录没有（需要在目标目录创建/复制）
        /// </summary>
        Create,
        
        /// <summary>
        /// 源和目标都有，但源更新或哈希不一致（需要在目标目录覆盖更新）
        /// </summary>
        Update,
        
        /// <summary>
        /// 源目录没有，目标目录有（在双向同步或镜像同步模式下，需要删除目标目录的文件）
        /// </summary>
        Delete
    }
}
