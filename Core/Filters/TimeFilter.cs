using System;
using FolderSync.Core.VFS;

namespace FolderSync.Core.Filters
{
    /// <summary>
    /// 时间过滤器（仅同步新于多少小时的文件/文件夹）
    /// </summary>
    public class TimeFilter : IFilter
    {
        private readonly int? _newerThanHoursForFile;
        private readonly int? _newerThanHoursForFolder;
        private readonly DateTime _referenceTimeUtc;

        public TimeFilter(int? newerThanHoursForFile = null, int? newerThanHoursForFolder = null)
        {
            _newerThanHoursForFile = newerThanHoursForFile;
            _newerThanHoursForFolder = newerThanHoursForFolder;
            _referenceTimeUtc = DateTime.UtcNow;
        }

        public bool IsMatch(FileItem item)
        {
            var itemAgeInHours = (_referenceTimeUtc - item.LastWriteTime).TotalHours;

            if (item.IsDirectory)
            {
                if (_newerThanHoursForFolder.HasValue && itemAgeInHours > _newerThanHoursForFolder.Value)
                {
                    return false;
                }
            }
            else
            {
                if (_newerThanHoursForFile.HasValue && itemAgeInHours > _newerThanHoursForFile.Value)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
