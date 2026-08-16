using System;
using System.Text.RegularExpressions;
using FolderSync.Core.VFS;

namespace FolderSync.Core.Filters
{
    /// <summary>
    /// 正则表达式过滤器，支持对文件名或路径进行高级匹配
    /// </summary>
    public class RegexFilter : IFilter
    {
        private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);
        private readonly Regex? _regexPattern;
        private readonly bool _isExcludePattern;

        /// <summary>
        /// 构造正则表达式过滤器
        /// </summary>
        /// <param name="pattern">正则表达式字符串</param>
        /// <param name="isExcludePattern">如果是 true，匹配正则的文件将被拦截；如果是 false，仅放行匹配正则的文件</param>
        public RegexFilter(string pattern, bool isExcludePattern = true)
        {
            if (!string.IsNullOrWhiteSpace(pattern))
            {
                try
                {
                    _regexPattern = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, MatchTimeout);
                }
                catch (ArgumentException ex)
                {
                    throw new InvalidOperationException($"正则表达式无效：{pattern}", ex);
                }
            }
            _isExcludePattern = isExcludePattern;
        }

        public bool IsMatch(FileItem item)
        {
            if (item == null)
            {
                return false;
            }

            if (_regexPattern == null)
            {
                return true;
            }

            bool isMatch;
            try
            {
                // 对完整相对路径进行匹配，而不仅仅是文件名
                isMatch = _regexPattern.IsMatch(item.Path ?? item.Name ?? string.Empty);
            }
            catch (RegexMatchTimeoutException)
            {
                // 匹配超时视为命中：黑名单发拦截，白名单则不放行，避免灾难性回溯卡死同步。
                isMatch = !_isExcludePattern;
            }

            return _isExcludePattern ? !isMatch : isMatch;
        }
    }
}
