namespace FolderSync.Core.Sync
{
    /// <summary>
    /// 同步模式枚举
    /// </summary>
    public enum SyncMode
    {
        /// <summary>
        /// 单向增量同步：A -> B（仅把 A 中新增的复制到 B，B 中原有文件或 A 中修改过的文件不处理）
        /// </summary>
        OneWayIncremental,
        
        /// <summary>
        /// 单向完全同步：A -> B（A 中新增和修改的都会覆盖到 B，但 B 中独有的文件不会被删除）
        /// </summary>
        OneWayUpdate,
        
        /// <summary>
        /// 单向镜像同步：A -> B（完全让 B 变成 A 的镜像。A 中新增/修改会同步到 B，B 中多余的文件会被删除）
        /// </summary>
        OneWayMirror,
        
        /// <summary>
        /// 双向同步：A <-> B（A 和 B 互为源和目标，增删改双向同步，如果产生冲突则依据策略解决）
        /// </summary>
        TwoWay
    }
}
