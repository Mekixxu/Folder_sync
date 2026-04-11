using FolderSync.Core.VFS;

namespace FolderSync.Core.Filters
{
    /// <summary>
    /// 文件大小过滤器，允许配置最小/最大字节数限制
    /// </summary>
    public class SizeFilter : IFilter
    {
        private readonly long? _minSizeBytes;
        private readonly long? _maxSizeBytes;

        public SizeFilter(long? minSizeBytes = null, long? maxSizeBytes = null)
        {
            _minSizeBytes = minSizeBytes;
            _maxSizeBytes = maxSizeBytes;
        }

        public bool IsMatch(FileItem item)
        {
            // 目录没有大小概念，跳过过滤
            if (item.IsDirectory)
            {
                return true;
            }

            if (_minSizeBytes.HasValue && item.Size < _minSizeBytes.Value)
            {
                return false;
            }

            if (_maxSizeBytes.HasValue && item.Size > _maxSizeBytes.Value)
            {
                return false;
            }

            return true;
        }
    }
}
