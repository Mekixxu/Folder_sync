using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FolderSync.Core.Sync;
using Xunit;

namespace FolderSync.Core.Tests
{
    /// <summary>
    /// 双向基线按路径增量提交：只更新已确认路径，其他路径保留旧记录。
    /// </summary>
    public class TwoWayStateStoreTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly TwoWayStateStore _store;

        public TwoWayStateStoreTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"twoway-{Guid.NewGuid():N}.db");
            _store = new TwoWayStateStore(_dbPath);
        }

        public void Dispose()
        {
            if (File.Exists(_dbPath))
            {
                try
                {
                    File.Delete(_dbPath);
                }
                catch (IOException)
                {
                }
            }
        }

        private static StateSnapshot Snapshot(string hash = "H1", long size = 10)
        {
            return new StateSnapshot
            {
                Exists = true,
                IsDirectory = false,
                Size = size,
                LastWriteUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                Hash = hash
            };
        }

        [Fact]
        public async Task ApplyPathChanges_UpdatesOnlyProvidedPaths()
        {
            await _store.InitializeAsync();
            await _store.ApplyPathChangesAsync("task", new[]
            {
                new TwoWayBaselineChange { Path = "keep.txt", A = Snapshot(), B = Snapshot() },
                new TwoWayBaselineChange { Path = "change.txt", A = Snapshot(), B = Snapshot() }
            });

            await _store.ApplyPathChangesAsync("task", new[]
            {
                new TwoWayBaselineChange { Path = "change.txt", A = Snapshot("H2", 20), B = null }
            });

            var baseline = await _store.LoadAsync("task");
            Assert.True(baseline.ContainsKey("keep.txt"));
            Assert.Equal("H1", baseline["keep.txt"].A!.Hash);
            Assert.Equal("H2", baseline["change.txt"].A!.Hash);
            Assert.Null(baseline["change.txt"].B);
        }

        [Fact]
        public async Task ApplyPathChanges_BothSidesNull_RemovesPath()
        {
            await _store.InitializeAsync();
            await _store.ApplyPathChangesAsync("task", new[]
            {
                new TwoWayBaselineChange { Path = "gone.txt", A = Snapshot(), B = Snapshot() }
            });

            await _store.ApplyPathChangesAsync("task", new[]
            {
                new TwoWayBaselineChange { Path = "gone.txt", A = null, B = null }
            });

            Assert.Empty(await _store.LoadAsync("task"));
        }
    }
}
