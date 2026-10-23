using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FolderSync.Core.Config;
using Xunit;

namespace FolderSync.Core.Tests
{
    /// <summary>
    /// 多个 repository 实例写同一路径时不能丢更新。
    /// </summary>
    public class TaskRepositoryConcurrencyTests : IDisposable
    {
        private readonly string _directory;
        private readonly string _filePath;

        public TaskRepositoryConcurrencyTests()
        {
            _directory = Path.Combine(Path.GetTempPath(), "fs-repo-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            _filePath = Path.Combine(_directory, "tasks.json");
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_directory))
                {
                    Directory.Delete(_directory, recursive: true);
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
        public void ConcurrentUpserts_FromIndependentRepositories_DoNotLoseTasks()
        {
            const int taskCount = 30;
            Parallel.For(0, taskCount, i =>
            {
                var repository = new TaskRepository(_filePath);
                repository.Upsert(new SyncTaskDefinition
                {
                    Id = $"task-{i}",
                    TaskName = $"Task {i}"
                });
            });

            var tasks = new TaskRepository(_filePath).LoadAll();
            Assert.Equal(taskCount, tasks.Count);
            Assert.Equal(taskCount, tasks.Select(t => t.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        [Fact]
        public void CorruptJson_IsBackedUpAndNotOverwritten()
        {
            File.WriteAllText(_filePath, "{ this is not json");
            var repository = new TaskRepository(_filePath);

            Assert.Throws<InvalidDataException>(() => repository.LoadAll());
            Assert.True(File.Exists(_filePath));
            Assert.Contains(Directory.EnumerateFiles(_directory), path => path.Contains(".corrupt-", StringComparison.Ordinal));
        }
    }
}
