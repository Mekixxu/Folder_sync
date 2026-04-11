using System;

namespace FolderSync.Core.VFS
{
    public class FileItem
    {
        /// <summary>
        /// 相对于系统根目录的相对路径
        /// </summary>
        public string Path { get; set; } = string.Empty;
        
        /// <summary>
        /// 文件或目录名
        /// </summary>
        public string Name { get; set; } = string.Empty;
        
        /// <summary>
        /// 文件大小（字节）
        /// </summary>
        public long Size { get; set; }
        
        /// <summary>
        /// 最后修改时间
        /// </summary>
        public DateTime LastWriteTime { get; set; }
        
        /// <summary>
        /// 是否为目录
        /// </summary>
        public bool IsDirectory { get; set; }
    }
}
