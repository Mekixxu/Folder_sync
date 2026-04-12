using System.Collections.Generic;
using System.Linq;
using FolderSync.Core.VFS;

namespace FolderSync.Core.Filters
{
    /// <summary>
    /// 双名单过滤引擎，支持白名单（包含）+ 黑名单（排除）的组合评估。
    /// </summary>
    public class FilterEngine
    {
        private readonly List<IFilter> _legacyFilters = new();
        private readonly List<IFilter> _whitelistFilters = new();
        private readonly List<IFilter> _blacklistFilters = new();

        /// <summary>
        /// 兼容旧调用：按历史语义加入“必须满足”的过滤器。
        /// </summary>
        public void AddFilter(IFilter filter)
        {
            if (filter != null)
            {
                _legacyFilters.Add(filter);
            }
        }

        public void ClearFilters()
        {
            _legacyFilters.Clear();
            _whitelistFilters.Clear();
            _blacklistFilters.Clear();
        }

        public void Configure(DualListFilterConfiguration configuration)
        {
            _whitelistFilters.Clear();
            _blacklistFilters.Clear();

            if (configuration == null)
            {
                return;
            }

            BuildFilters(configuration.Whitelist, _whitelistFilters);
            BuildFilters(configuration.Blacklist, _blacklistFilters);
        }

        /// <summary>
        /// 检查 FileItem 是否允许同步：
        /// 1) 白名单为空 => 默认允许进入；
        /// 2) 白名单非空 => 必须满足白名单全部规则；
        /// 3) 黑名单非空且目标满足黑名单全部规则 => 拦截；
        /// 4) 最后叠加历史过滤器。
        /// </summary>
        public bool IsAllowed(FileItem item)
        {
            var passWhitelist = !_whitelistFilters.Any() || _whitelistFilters.All(filter => filter.IsMatch(item));
            if (!passWhitelist)
            {
                return false;
            }

            var hitBlacklist = _blacklistFilters.Any() && _blacklistFilters.All(filter => filter.IsMatch(item));
            if (hitBlacklist)
            {
                return false;
            }

            return _legacyFilters.All(filter => filter.IsMatch(item));
        }

        /// <summary>
        /// 对集合进行过滤，返回被允许的条目
        /// </summary>
        public IEnumerable<FileItem> Filter(IEnumerable<FileItem> items)
        {
            return items.Where(IsAllowed);
        }

        private static void BuildFilters(FilterRuleSet rules, List<IFilter> target)
        {
            if (rules == null || !rules.HasAnyRule)
            {
                return;
            }

            var extensions = rules.ParseExtensions();
            if (extensions.Count > 0)
            {
                target.Add(new ExtensionFilter(extensions, isWhitelist: true));
            }

            var min = rules.ParseMinSizeBytes();
            var max = rules.ParseMaxSizeBytes();
            if (min.HasValue || max.HasValue)
            {
                target.Add(new SizeFilter(min, max));
            }

            var hours = rules.ParseNewerThanHours();
            if (hours.HasValue)
            {
                target.Add(new TimeFilter(hours, hours));
            }

            if (!string.IsNullOrWhiteSpace(rules.RegexPattern))
            {
                target.Add(new RegexFilter(rules.RegexPattern, isExcludePattern: false));
            }
        }
    }
}
