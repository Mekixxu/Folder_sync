using System;
using System.IO;
using FolderSync.Core.Config;
using FolderSync.Core.Sync;
using Xunit;

namespace FolderSync.Core.Tests
{
    /// <summary>
    /// PathSafetyValidator 重叠/非重叠用例。
    /// </summary>
    public class PathSafetyValidatorTests
    {
        private static SyncTaskDefinition CreateLocalTask(string source, string dest)
        {
            return new SyncTaskDefinition
            {
                SourceProtocol = "Local/SMB",
                DestProtocol = "Local/SMB",
                SourcePath = source,
                DestPath = dest
            };
        }

        [Fact]
        public void EnsureSourceDestDoNotOverlap_SamePath_Throws()
        {
            var path = Path.Combine(Path.GetTempPath(), "same");
            var task = CreateLocalTask(path, path);
            Assert.Throws<InvalidOperationException>(() => PathSafetyValidator.EnsureSourceDestDoNotOverlap(task));
        }

        [Fact]
        public void EnsureSourceDestDoNotOverlap_ParentChild_Throws()
        {
            var root = Path.Combine(Path.GetTempPath(), "parent");
            var childLeaf = Path.Combine(root, "child");
            var task = CreateLocalTask(root, childLeaf);
            Assert.Throws<InvalidOperationException>(() => PathSafetyValidator.EnsureSourceDestDoNotOverlap(task));
        }

        [Fact]
        public void EnsureSourceDestDoNotOverlap_ChildParent_Throws()
        {
            var root = Path.Combine(Path.GetTempPath(), "parent2");
            var childLeaf = Path.Combine(root, "child");
            var task = CreateLocalTask(childLeaf, root);
            Assert.Throws<InvalidOperationException>(() => PathSafetyValidator.EnsureSourceDestDoNotOverlap(task));
        }

        [Fact]
        public void EnsureSourceDestDoNotOverlap_SiblingPaths_DoesNotThrow()
        {
            var root = Path.Combine(Path.GetTempPath(), "siblings");
            var source = Path.Combine(root, "a");
            var dest = Path.Combine(root, "b");
            var task = CreateLocalTask(source, dest);
            PathSafetyValidator.EnsureSourceDestDoNotOverlap(task);
        }

        [Fact]
        public void EnsureSourceDestDoNotOverlap_PrefixLookalike_DoesNotThrow()
        {
            // C:\data 与 C:\data2 是“前缀相似”但不同路径，不应误判为重叠。
            var root = Path.GetTempPath();
            var source = Path.Combine(root, "data");
            var dest = Path.Combine(root, "data2");
            var task = CreateLocalTask(source, dest);
            PathSafetyValidator.EnsureSourceDestDoNotOverlap(task);
        }

        [Fact]
        public void EnsureSourceDestDoNotOverlap_FtpSameHostSamePath_Throws()
        {
            var task = new SyncTaskDefinition
            {
                SourceProtocol = "FTP",
                DestProtocol = "FTP",
                SourcePath = "ftp://host/share/a",
                DestPath = "ftp://host/share/a"
            };
            Assert.Throws<InvalidOperationException>(() => PathSafetyValidator.EnsureSourceDestDoNotOverlap(task));
        }

        [Fact]
        public void EnsureSourceDestDoNotOverlap_FtpSameHostParentChild_Throws()
        {
            var task = new SyncTaskDefinition
            {
                SourceProtocol = "FTP",
                DestProtocol = "FTP",
                SourcePath = "ftp://host/share",
                DestPath = "ftp://host/share/sub"
            };
            Assert.Throws<InvalidOperationException>(() => PathSafetyValidator.EnsureSourceDestDoNotOverlap(task));
        }

        [Fact]
        public void EnsureSourceDestDoNotOverlap_FtpDifferentHost_DoesNotThrow()
        {
            var task = new SyncTaskDefinition
            {
                SourceProtocol = "FTP",
                DestProtocol = "FTP",
                SourcePath = "ftp://host-a/share",
                DestPath = "ftp://host-b/share"
            };
            PathSafetyValidator.EnsureSourceDestDoNotOverlap(task);
        }
    }
}