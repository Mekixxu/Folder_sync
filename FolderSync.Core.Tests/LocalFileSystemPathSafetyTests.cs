using System;
using System.IO;
using System.Threading.Tasks;
using FolderSync.Core.VFS;
using Xunit;

namespace FolderSync.Core.Tests
{
    /// <summary>
    /// LocalFileSystem 路径边界测试：验证 `..` 不会逃逸基础目录。
    /// </summary>
    public class LocalFileSystemPathSafetyTests : IDisposable
    {
        private readonly string _basePath;
        private readonly LocalFileSystem _fs;

        public LocalFileSystemPathSafetyTests()
        {
            var root = Path.Combine(Path.GetTempPath(), "fs-safety-" + Guid.NewGuid().ToString("N"));
            _basePath = Path.Combine(root, "data");
            Directory.CreateDirectory(_basePath);
            Directory.CreateDirectory(Path.Combine(root, "data2"));
            _fs = new LocalFileSystem(_basePath);
        }

        public void Dispose()
        {
            var root = Path.GetDirectoryName(_basePath);
            if (root != null && Directory.Exists(root))
            {
                try
                {
                    Directory.Delete(root, true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        [Fact]
        public void ListFiles_DoubleDotOutsideBase_ThrowsUnauthorizedAccess()
        {
            Assert.Throws<UnauthorizedAccessException>(() =>
                _fs.ListFilesAsync("../..", recursive: true).GetAwaiter().GetResult());
        }

        [Fact]
        public void GetFileInfo_DoubleDotOutsideBase_ThrowsUnauthorizedAccess()
        {
            Assert.Throws<UnauthorizedAccessException>(() =>
                _fs.GetFileInfoAsync("../../etc/passwd").GetAwaiter().GetResult());
        }

        [Fact]
        public void ListFiles_SiblingDirectoryOutsideBase_ThrowsUnauthorizedAccess()
        {
            // 与本目录同级的 "data2" 不应被视为本目录的子路径。
            Assert.Throws<UnauthorizedAccessException>(() =>
                _fs.ListFilesAsync("../data2", recursive: true).GetAwaiter().GetResult());
        }

        [Fact]
        public async Task ListFiles_EmptyPath_ReturnsBaseContent()
        {
            File.WriteAllText(Path.Combine(_basePath, "a.txt"), "hello");
            var items = await _fs.ListFilesAsync("", recursive: true);
            Assert.Contains(items, i => i.Name == "a.txt");
        }

        [Fact]
        public async Task ListFiles_SubdirectoryUnderBase_IsAllowed()
        {
            Directory.CreateDirectory(Path.Combine(_basePath, "sub"));
            File.WriteAllText(Path.Combine(_basePath, "sub", "b.txt"), "world");
            var items = await _fs.ListFilesAsync("sub", recursive: true);
            Assert.Contains(items, i => i.Name == "b.txt");
        }
    }
}