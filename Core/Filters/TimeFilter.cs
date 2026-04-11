using System;
using FolderSync.Core.VFS;

namespace FolderSync.Core.Filters
{
    /// <summary>
    /// 时间过滤器（仅同步新于多少天的文件/文件夹）
    /// </summary>
    public class TimeFilter : IFilter
    {
        private readonly int? _newerThanDaysForFile;
        private readonly int? _newerThanDaysForFolder;
        private readonly DateTime _referenceTimeUtc;

        public TimeFilter(int? newerThanDaysForFile = null, int? newerThanDaysForFolder = null)
        {
            _newerThanDaysForFile = newerThanDaysForFile;
            _newerThanDaysForFolder = newerThanDaysForFolder;
            _referenceTimeUtc = DateTime.UtcNow;
        }

        public bool IsMatch(FileItem item)
        {
            var itemAgeInDays = (_referenceTimeUtc - item.LastWriteTime).TotalDays;

            if (item.IsDirectory)
            {
                if (_newerThanDaysForFolder.HasValue && itemAgeInDays > _newerThanDaysForFolder.Value)
                {
                    return false;
                }
            }
            else
            {
                if (_newerThanDaysForFile.HasValue && itemAgeInDays > _newerThanDaysForFile.Value)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
