using FolderSync.Core.VFS;

namespace FolderSync.Core.Filters
{
    /// <summary>
    /// 文件过滤器接口
    /// 返回 true 表示该文件/文件夹通过过滤（允许同步），返回 false 表示被拦截（忽略同步）
    /// </summary>
    public interface IFilter
    {
        bool IsMatch(FileItem item);
    }
}
