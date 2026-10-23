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
        private static readonly TimeSpan DefaultMatchTimeout = TimeSpan.FromSeconds(2);
        private readonly Regex? _regexPattern;
        private readonly bool _isExcludePattern;
        private readonly string _pattern;

        /// <summary>
        /// 构造正则表达式过滤器
        /// </summary>
        /// <param name="pattern">正则表达式字符串</param>
        /// <param name="isExcludePattern">如果是 true，匹配正则的文件将被拦截；如果是 false，仅放行匹配正则的文件</param>
        public RegexFilter(string pattern, bool isExcludePattern = true, TimeSpan? matchTimeout = null)
        {
            _pattern = pattern ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(pattern))
            {
                try
                {
                    _regexPattern = new Regex(
                        pattern,
                        RegexOptions.IgnoreCase | RegexOptions.Compiled,
                        matchTimeout ?? DefaultMatchTimeout);
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
            catch (RegexMatchTimeoutException ex)
            {
                // 超时表示规则无法可靠评估，不能把“未知”转换为命中或放行。
                // 让本次分析/执行失败，避免基于不完整过滤结果生成删除或覆盖动作。
                throw new FilterEvaluationException(
                    $"正则规则匹配超时，无法可靠判定过滤结果。Pattern='{_pattern}', Path='{item.Path}'。",
                    ex);
            }

            return _isExcludePattern ? !isMatch : isMatch;
        }
    }
}
