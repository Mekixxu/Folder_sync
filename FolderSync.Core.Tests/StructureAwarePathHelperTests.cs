using System.Collections.Generic;
using System.Linq;
using FolderSync.Core.Sync;
using FolderSync.Core.VFS;
using Xunit;

namespace FolderSync.Core.Tests
{
    /// <summary>
    /// 结构感知路径展开：命中文件的所有父目录自动并入集合。
    /// </summary>
    public class StructureAwarePathHelperTests
    {
        private static FileItem Item(string path, bool isDirectory = false)
        {
            return new FileItem
            {
                Path = path,
                Name = path.Split('/').Last(),
                IsDirectory = isDirectory
            };
        }

        [Fact]
        public void ExpandWithAncestorDirectories_AddsAllAncestors()
        {
            var raw = new List<FileItem>
            {
                Item("folder", isDirectory: true),
                Item("folder/sub", isDirectory: true),
                Item("folder/sub/file.txt"),
                Item("other")
            };
            var included = new List<FileItem> { Item("folder/sub/file.txt") };

            var expanded = StructureAwarePathHelper.ExpandWithAncestorDirectories(raw, included);

            Assert.Contains(expanded, i => i.Path == "folder/sub/file.txt");
            Assert.Contains(expanded, i => i.Path == "folder/sub" && i.IsDirectory);
            Assert.Contains(expanded, i => i.Path == "folder" && i.IsDirectory);
            Assert.DoesNotContain(expanded, i => i.Path == "other");
        }

        [Fact]
        public void ExpandWithAncestorDirectories_DirectoriesBeforeFiles()
        {
            var raw = new List<FileItem>
            {
                Item("a/b/c.txt")
            };
            raw.Add(Item("a", isDirectory: true));
            raw.Add(Item("a/b", isDirectory: true));

            var included = new List<FileItem> { Item("a/b/c.txt") };
            var expanded = StructureAwarePathHelper.ExpandWithAncestorDirectories(raw, included);

            Assert.True(expanded.First(i => i.IsDirectory).IsDirectory);
            Assert.Equal(3, expanded.Count);
        }

        [Fact]
        public void BuildPathSet_NormalizesAndIgnoresNull()
        {
            var items = new List<FileItem> { Item("folder\\a.txt"), Item(null!), Item("folder/b.txt") };
            var set = StructureAwarePathHelper.BuildPathSet(items);

            Assert.Contains("folder/a.txt", set);
            Assert.Contains("folder/b.txt", set);
            Assert.Equal(2, set.Count);
        }

        [Fact]
        public void NormalizePath_HandlesSlashVariants()
        {
            Assert.Equal("a/b", StructureAwarePathHelper.NormalizePath("a\\b"));
            Assert.Equal(string.Empty, StructureAwarePathHelper.NormalizePath("/"));
            Assert.Equal(string.Empty, StructureAwarePathHelper.NormalizePath(""));
            Assert.Equal("a", StructureAwarePathHelper.NormalizePath("/a/"));
        }
    }
}