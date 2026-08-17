using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FolderSync.Core.Sync;
using FolderSync.Core.VFS;
using Xunit;

namespace FolderSync.Core.Tests
{
    /// <summary>
    /// 单向一次性投递状态存储：写入/读取/重置，以及批量写入。
    /// </summary>
    public class OneWayDeliveryStateStoreTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly OneWayDeliveryStateStore _store;

        public OneWayDeliveryStateStoreTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"owd-{Guid.NewGuid():N}.db");
            _store = new OneWayDeliveryStateStore(_dbPath);
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

        private static OneWayDeliveryRecord CreateRecord(string path, long size = 1234, string? hash = "ABC")
        {
            return new OneWayDeliveryRecord
            {
                RelativePath = path,
                IsDirectory = false,
                SourceSize = size,
                SourceLastWriteUtc = DateTime.UtcNow.AddMinutes(-5),
                SourceHash = hash,
                DeliveredUtc = DateTime.UtcNow
            };
        }

        [Fact]
        public async Task UpsertAsync_ThenLoadAsync_ReturnsRecord()
        {
            await _store.InitializeAsync();
            var record = CreateRecord("dir/file.txt");
            await _store.UpsertAsync("task-1", record);

            var loaded = await _store.LoadAsync("task-1");
            Assert.True(loaded.TryGetValue("dir/file.txt", out var persisted));
            Assert.Equal(record.SourceSize, persisted.SourceSize);
            Assert.Equal(record.SourceHash, persisted.SourceHash);
        }

        [Fact]
        public async Task UpsertAsync_SamePathOverwrites()
        {
            await _store.InitializeAsync();
            await _store.UpsertAsync("task-1", CreateRecord("dir/file.txt", size: 100));
            await _store.UpsertAsync("task-1", CreateRecord("dir/file.txt", size: 200));

            var loaded = await _store.LoadAsync("task-1");
            Assert.Equal(200, loaded["dir/file.txt"].SourceSize);
            Assert.Single(loaded);
        }

        [Fact]
        public async Task UpsertRangeAsync_ManyRecords_AllPersisted()
        {
            await _store.InitializeAsync();
            var records = Enumerable.Range(0, 10)
                .Select(i => CreateRecord($"dir/file-{i}.txt", size: i))
                .ToList();

            await _store.UpsertRangeAsync("task-1", records);
            var loaded = await _store.LoadAsync("task-1");
            Assert.Equal(10, loaded.Count);
            Assert.Equal(5, loaded["dir/file-5.txt"].SourceSize);
        }

        [Fact]
        public async Task LoadAsync_UnknownTask_ReturnsEmpty()
        {
            await _store.InitializeAsync();
            var loaded = await _store.LoadAsync("does-not-exist");
            Assert.Empty(loaded);
        }

        [Fact]
        public async Task ResetTaskAsync_ClearsTaskRecords()
        {
            await _store.InitializeAsync();
            await _store.UpsertAsync("task-1", CreateRecord("a.txt"));
            await _store.UpsertAsync("task-2", CreateRecord("b.txt"));

            await _store.ResetTaskAsync("task-1");

            Assert.Empty(await _store.LoadAsync("task-1"));
            Assert.Single(await _store.LoadAsync("task-2"));
        }
    }
}