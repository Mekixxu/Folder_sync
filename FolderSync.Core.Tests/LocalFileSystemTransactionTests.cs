using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FolderSync.Core.Sync;
using FolderSync.Core.VFS;
using Xunit;

namespace FolderSync.Core.Tests
{
    /// <summary>
    /// 复制事务与目录删除授权回归测试。
    /// </summary>
    public class LocalFileSystemTransactionTests : IDisposable
    {
        private readonly string _root;
        private readonly string _basePath;
        private readonly LocalFileSystem _fs;

        public LocalFileSystemTransactionTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "fs-transaction-" + Guid.NewGuid().ToString("N"));
            _basePath = Path.Combine(_root, "data");
            Directory.CreateDirectory(_basePath);
            _fs = new LocalFileSystem(_basePath);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        [Fact]
        public async Task CopyFile_WhenSourceOpenFails_KeepsExistingTarget()
        {
            var targetPath = Path.Combine(_basePath, "target.txt");
            await File.WriteAllTextAsync(targetPath, "original");

            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                OneWayDeliverySupport.CopyFileAsync(_fs, _fs, "missing.txt"));

            Assert.Equal("original", await File.ReadAllTextAsync(targetPath));
            Assert.Empty(Directory.GetFiles(_basePath, "*.tmp", SearchOption.AllDirectories));
        }

        [Fact]
        public async Task CopyFile_Success_ReplacesTargetAndLeavesNoTemporaryFile()
        {
            await File.WriteAllTextAsync(Path.Combine(_basePath, "source.txt"), "new-content");
            await File.WriteAllTextAsync(Path.Combine(_basePath, "target.txt"), "old-content");

            var result = await OneWayDeliverySupport.CopyFileAsync(_fs, _fs, "source.txt", new FileItem
            {
                Path = "source.txt",
                Name = "source.txt",
                Size = 11,
                IsDirectory = false
            });

            Assert.Equal(11, result.BytesCopied);
            Assert.Equal("new-content", await File.ReadAllTextAsync(Path.Combine(_basePath, "target.txt")));
            Assert.DoesNotContain(
                Directory.EnumerateFiles(_basePath, "*", SearchOption.AllDirectories),
                path => path.Contains(".foldersync-", StringComparison.Ordinal));
        }

        [Fact]
        public async Task DeleteDirectoryIfEmpty_NonEmptyDirectory_IsKept()
        {
            var directory = Path.Combine(_basePath, "old");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "excluded.jpg"), "keep");

            var deleted = await _fs.DeleteDirectoryIfEmptyAsync("old");

            Assert.False(deleted);
            Assert.True(File.Exists(Path.Combine(directory, "excluded.jpg")));
        }

        [Fact]
        public async Task DeleteDirectoryIfEmpty_EmptyDirectory_IsRemoved()
        {
            Directory.CreateDirectory(Path.Combine(_basePath, "empty"));

            var deleted = await _fs.DeleteDirectoryIfEmptyAsync("empty");

            Assert.True(deleted);
            Assert.False(Directory.Exists(Path.Combine(_basePath, "empty")));
        }
    }
}
