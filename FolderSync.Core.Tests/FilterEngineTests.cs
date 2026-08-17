using System.Collections.Generic;
using System.Linq;
using FolderSync.Core.Filters;
using FolderSync.Core.VFS;
using Xunit;

namespace FolderSync.Core.Tests
{
    /// <summary>
    /// 过滤引擎：白名单/黑名单组合评估。
    /// </summary>
    public class FilterEngineTests
    {
        private static FileItem File(string name)
        {
            return new FileItem
            {
                Name = name,
                Path = name,
                IsDirectory = false,
                Size = 1
            };
        }

        private static FileItem Directory(string name)
        {
            return new FileItem
            {
                Name = name,
                Path = name,
                IsDirectory = true,
                Size = 0
            };
        }

        [Fact]
        public void IsAllowed_NoRules_AllowsEverything()
        {
            var engine = new FilterEngine();
            engine.Configure(new DualListFilterConfiguration());
            Assert.True(engine.IsAllowed(File("anything.bin")));
        }

        [Fact]
        public void IsAllowed_Whitelist_OnlyMatchesListedExtensions()
        {
            var engine = new FilterEngine();
            engine.Configure(new DualListFilterConfiguration
            {
                Whitelist = new FilterRuleSet { ExtensionFilterText = "txt, md" }
            });

            Assert.True(engine.IsAllowed(File("readme.txt")));
            Assert.True(engine.IsAllowed(Directory("folder")));
            Assert.False(engine.IsAllowed(File("photo.jpg")));
        }

        [Fact]
        public void IsAllowed_Blacklist_ExcludesListedExtensions()
        {
            var engine = new FilterEngine();
            engine.Configure(new DualListFilterConfiguration
            {
                Blacklist = new FilterRuleSet { ExtensionFilterText = "tmp, log" }
            });

            Assert.False(engine.IsAllowed(File("temp.tmp")));
            Assert.True(engine.IsAllowed(File("keep.txt")));
        }

        [Fact]
        public void IsAllowed_WhitelistAndBlacklist_BlacklistWins()
        {
            var engine = new FilterEngine();
            engine.Configure(new DualListFilterConfiguration
            {
                Whitelist = new FilterRuleSet { ExtensionFilterText = "txt" },
                Blacklist = new FilterRuleSet { ExtensionFilterText = "secret.txt" }
            });

            Assert.True(engine.IsAllowed(File("readme.txt")));
            Assert.False(engine.IsAllowed(File("secret.txt")));
            Assert.False(engine.IsAllowed(File("photo.jpg")));
        }

        [Fact]
        public void Filter_IgnoresNullItems()
        {
            var engine = new FilterEngine();
            engine.Configure(new DualListFilterConfiguration());
            var result = engine.Filter(new List<FileItem> { null!, File("a.txt") }).ToList();
            Assert.Single(result);
            Assert.Equal("a.txt", result[0].Name);
        }

        [Fact]
        public void IsAllowed_NullItem_ReturnsFalse()
        {
            var engine = new FilterEngine();
            engine.Configure(new DualListFilterConfiguration());
            Assert.False(engine.IsAllowed(null!));
        }
    }
}