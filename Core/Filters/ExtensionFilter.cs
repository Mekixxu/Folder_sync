using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FolderSync.Core.VFS;

namespace FolderSync.Core.Filters
{
    /// <summary>
    /// 文件扩展名过滤器（黑名单/白名单模式）
    /// 支持如 "*.docx" 或 ".pdf" 的格式
    /// </summary>
    public class ExtensionFilter : IFilter
    {
        private readonly HashSet<string> _extensions = new(StringComparer.OrdinalIgnoreCase);
        private readonly bool _isWhitelist;

        /// <summary>
        /// 构造扩展名过滤器
        /// </summary>
        /// <param name="extensions">扩展名集合，例如 new[] { ".txt", "*.md" }</param>
        /// <param name="isWhitelist">如果是 true，则只允许包含在集合中的文件；如果是 false，则排除集合中的文件</param>
        public ExtensionFilter(IEnumerable<string> extensions, bool isWhitelist = false)
        {
            _isWhitelist = isWhitelist;
            if (extensions != null)
            {
                foreach (var ext in extensions)
                {
                    if (string.IsNullOrWhiteSpace(ext)) continue;
                    
                    var normalized = ext.Trim();
                    if (normalized.StartsWith("*."))
                    {
                        normalized = normalized.Substring(1); // 变为 ".ext"
                    }
                    else if (!normalized.StartsWith("."))
                    {
                        normalized = "." + normalized;
                    }
                    _extensions.Add(normalized);
                }
            }
        }

        public bool IsMatch(FileItem item)
        {
            // 目录不应用扩展名过滤规则
            if (item.IsDirectory)
            {
                return true;
            }

            if (!_extensions.Any())
            {
                return true;
            }

            var extension = Path.GetExtension(item.Name);
            var contains = _extensions.Contains(extension);

            // 白名单：包含则允许；黑名单：不包含则允许
            return _isWhitelist ? contains : !contains;
        }
    }
}
