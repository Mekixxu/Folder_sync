using System.Collections.Generic;
using System.Linq;
using FolderSync.Core.VFS;

namespace FolderSync.Core.Filters
{
    /// <summary>
    /// 组合过滤器引擎，管理所有的过滤规则
    /// </summary>
    public class FilterEngine
    {
        private readonly List<IFilter> _filters = new();

        public void AddFilter(IFilter filter)
        {
            if (filter != null)
            {
                _filters.Add(filter);
            }
        }

        public void ClearFilters()
        {
            _filters.Clear();
        }

        /// <summary>
        /// 检查 FileItem 是否通过了所有的过滤规则
        /// 如果没有任何规则，默认通过。如果任何一个规则返回 false，则拦截。
        /// </summary>
        public bool IsAllowed(FileItem item)
        {
            if (!_filters.Any())
            {
                return true;
            }

            return _filters.All(filter => filter.IsMatch(item));
        }

        /// <summary>
        /// 对集合进行过滤，返回被允许的条目
        /// </summary>
        public IEnumerable<FileItem> Filter(IEnumerable<FileItem> items)
        {
            return items.Where(IsAllowed);
        }
    }
}
