using System;
using FolderSync.Core.Filters;
using FolderSync.Core.VFS;
using Xunit;

namespace FolderSync.Core.Tests
{
    /// <summary>
    /// 正则超时不能被当成“命中/放行”，必须让规则评估失败。
    /// </summary>
    public class RegexFilterTimeoutTests
    {
        [Fact]
        public void IsMatch_CatastrophicBacktracking_ThrowsFilterEvaluationException()
        {
            var filter = new RegexFilter("^(a+)+$", isExcludePattern: false, TimeSpan.FromMilliseconds(1));
            var item = new FileItem
            {
                Name = "payload.txt",
                Path = new string('a', 20000) + "!",
                IsDirectory = false
            };

            Assert.Throws<FilterEvaluationException>(() => filter.IsMatch(item));
        }
    }
}
