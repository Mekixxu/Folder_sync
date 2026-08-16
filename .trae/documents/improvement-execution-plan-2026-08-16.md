# FolderSync Pro 改进执行计划（供后续局部执行 LLM 使用）

> 生成日期：2026-08-16
> 适用基线：`2f0e779`（v0.0.54，.NET 8 + WPF）
> 目标：在不推翻现有架构的前提下，按 P0 → P1 → P2 顺序修复代码审查发现的高/中/低危问题。
> 审查报告：`.trae/documents/code-review-2026-08-16.md`（含第二轮复查增补）

---

## 0. 执行前必读（对执行 LLM 的硬约束）

1. **先读再改**：每个任务先打开“文件/位置”列出的文件，确认行号和上下文，再开始编辑。
2. **一次只做一个任务**：禁止把多个任务混在一起改；每个任务完成后单独 `git commit`。
3. **每步验证**：每个任务完成后至少执行 `dotnet build -c Release`；如果环境没有 `dotnet`，先安装 .NET 8 SDK，禁止以“没装 SDK”为由跳过构建验证。
4. **禁止顺手重构**：不要重写与本任务无关的方法，不要升级 NuGet 包，不要改变公开 API 名称（除非任务明确要求）。
5. **保留行为**：除任务明确要求外，现有同步语义、UI 文案和文件格式必须保持兼容。
6. **异常必须可观测**：新增逻辑的异常必须写 Serilog（`Log.Error/Warning`），不要空 `catch`。
7. **版本号**：全部任务完成后，把 `FolderSync.csproj` 的 `<Version>` 从 `0.0.54` 升到 `0.0.55`（AssemblyVersion/FileVersion 同步），并按 `CLAUDE.md` 的交付约束发布 portable exe。
8. **文档同步**：如果某个任务新增/删除/移动了文件，或改变了模块职责，必须同步更新 `CLAUDE.md` 第 3 节目录树。

---

## P0：紧急修复（调度失效 / 数据安全 / UI 阻塞 / 崩溃）

### T01 [P0] 启动时恢复定时任务（否则重启后所有定时任务静默失效）

- **文件/位置**：`App.xaml.cs` 的 `OnStartup`（当前第 43 行只调用 `SchedulerManager.Instance.StartAsync()`）。
- **问题**：Quartz 默认 RAMJobStore 不持久化；重启后内存任务清空，但没有任何代码从 `tasks.json` 重新注册任务。
- **改法**：
  1. 在 `App.xaml.cs` 的 `OnStartup` 中，`await SchedulerManager.Instance.StartAsync();` 之后新增一行 `await RestoreScheduledTasksAsync();`。
  2. 在 `App` 类中新增方法（需要 `FolderSync.Core.Config` 已 using）：

```csharp
private static async Task RestoreScheduledTasksAsync()
{
    List<SyncTaskDefinition> tasks;
    try
    {
        tasks = new TaskRepository().LoadAll();
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Failed to load task definitions during scheduler restore. Scheduled jobs were not restored.");
        return;
    }

    foreach (var task in tasks.Where(t => !t.IsManualTrigger))
    {
        try
        {
            var cron = SyncTaskFactory.ResolveCronExpression(task);
            var executor = SyncTaskFactory.CreateExecutor(task);
            await SchedulerManager.Instance.AddOrUpdateJobAsync(task.Id, task.TaskName, cron, executor);
            Log.Information("Restored scheduled job {TaskName} ({TaskId}) with cron {Cron}", task.TaskName, task.Id, cron);
        }
        catch (Exception ex)
        {
            // 单个任务损坏不能阻断整个应用启动
            Log.Error(ex, "Failed to restore scheduled job {TaskName} ({TaskId})", task.TaskName, task.Id);
        }
    }
}
```

- **验证**：构建通过；用 `grep -n "RestoreScheduledTasksAsync" App.xaml.cs` 确认调用存在。
- **提交 message**：`fix(scheduler): re-register persisted jobs on startup`

---

### T02 [P0] 移除 UI 线程上的 `.GetAwaiter().GetResult()` 同步阻塞

- **文件/位置**：
  - `UI/ViewModels/TasksViewModel.cs` 第 168-170 行（DeleteTask）、第 537-538 行（ResetSendOnceState）。
  - `UI/ViewModels/TaskEditorViewModel.cs` 第 303、309 行（SaveTask）。
- **问题**：在 UI 线程同步等待 async 方法，Quartz/SQLite 慢时会导致窗口假死。
- **改法**：
  1. `TasksViewModel` 构造函数中：
     - `DeleteTaskCommand = new RelayCommand(async _ => await DeleteTaskAsync(_));`
     - `ResetSendOnceStateCommand = new RelayCommand(async _ => await ResetSendOnceStateAsync(_));`
  2. 把 `DeleteTask(object? parameter)` 改为 `private async Task DeleteTaskAsync(object? parameter)`，内部：
     - `await SchedulerManager.Instance.RemoveJobAsync(task.Id);`
     - `await _deliveryStateStore.InitializeAsync();`
     - `await _deliveryStateStore.ResetTaskAsync(task.Id);`
  3. 把 `ResetSendOnceState(object? parameter)` 改为 `private async Task ResetSendOnceStateAsync(object? parameter)`，内部同样用 `await`。
  4. `TaskEditorViewModel` 构造函数中：
     - `SaveTaskCommand = new RelayCommand(async _ => await SaveTaskAsync(_), CanSaveTask);`
  5. 把 `SaveTask(object? parameter)` 改为 `private async Task SaveTaskAsync(object? parameter)`，`await` 两个 `SchedulerManager` 调用。
  6. 保留每个方法内已有的 `try/catch`；`MessageBox` 提示行为不变。
- **注意**：现有 `RelayCommand` 只接受 `Action`，lambda `async _ => await ...` 会变成 `async void`。这是本任务的最小改法；由于方法内部已经 `try/catch` 全部异常，不会产生未观察异常。后续 T17 再决定是否引入 `AsyncRelayCommand`。
- **验证**：
  - `grep -R "GetAwaiter().GetResult()" UI/` 应只剩 `TaskEditorViewModel.ExecuteFtpConnectionTest` 内部（该方法运行在 `Task.Run` 线程池中，不属于 UI 阻塞；若顺手改成 `await` 更好，但不强制）。
  - 构建通过。
- **提交 message**：`fix(ui): remove blocking sync-over-async from task commands`

---

### T03 [P0] 修复路径包含检查，阻止 `..` 逃逸基础目录

- **文件/位置**：
  - `Core/VFS/LocalFileSystem.cs` `GetFullPath`（第 204-212 行）。
  - `Core/VFS/FtpFileSystem.cs` `GetFullPath`（第 176-185 行）。
- **问题**：
  - Local 用 `combinedPath.StartsWith(_basePath)`，`C:\data2` 会被误判为 `C:\data` 的子路径。
  - FTP 直接拼接 basePath，`../../etc` 可逃逸 FTP 基础目录。
- **Local 改法**（替换 `GetFullPath` 中的安全判断）：

```csharp
private string GetFullPath(string relativePath)
{
    if (string.IsNullOrWhiteSpace(relativePath) || relativePath == "/" || relativePath == "\\")
    {
        return _basePath;
    }

    var baseRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_basePath));
    var combinedPath = Path.GetFullPath(Path.Combine(baseRoot, relativePath.TrimStart('/', '\\')));

    var relative = Path.GetRelativePath(baseRoot, combinedPath);
    var isRoot = relative == ".";
    var isChild = !Path.IsPathRooted(relative)
                  && relative != ".."
                  && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    if (!isRoot && !isChild)
    {
        throw new UnauthorizedAccessException($"Access to path '{relativePath}' is denied as it's outside the base directory.");
    }

    return combinedPath;
}
```

- **FTP 改法**：先按 `/` 拆分，拒绝任何 `..` 段（FTP 服务器文件名通常不允许 `.`/`..`；本项目同步路径更不应出现）：

```csharp
private string GetFullPath(string relativePath)
{
    if (string.IsNullOrWhiteSpace(relativePath) || relativePath == "/" || relativePath == "\\")
    {
        return _basePath.TrimEnd('/');
    }

    var relNormalized = relativePath.Replace('\\', '/').Trim().TrimStart('/');
    var baseSegments = _basePath.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
    var segments = relNormalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
    var combined = new List<string>(baseSegments);

    foreach (var segment in segments)
    {
        if (segment is "." or "")
        {
            continue;
        }

        if (segment == "..")
        {
            throw new UnauthorizedAccessException($"FTP path '{relativePath}' contains '..' and is not allowed.");
        }

        combined.Add(segment);
    }

    return "/" + string.Join('/', combined);
}
```

- **验证**：构建通过；在 `LocalFileSystem.GetFullPath` 中确认不再使用裸 `StartsWith`。
- **提交 message**：`fix(vfs): harden base-path containment for local and ftp`

---

### T04 [P0] 复制失败时清理不完整目标文件（当前只在取消时清理）

- **文件/位置**：`Core/Sync/OneWayDeliveryStateStore.cs` 的 `CopyFileAndComputeHashAsync` 与 `CopyFileAsync`（第 220-268 行）。
- **问题**：网络/磁盘异常中断复制时，目标端会残留半截文件；只有 `OperationCanceledException` 才删除。
- **改法**：两个方法的 `catch` 统一改为：

```csharp
catch (Exception ex) when (ex is not OperationCanceledException)
{
    await TryDeleteIncompleteTargetAsync(toFs, path);
    throw;
}
catch (OperationCanceledException)
{
    await TryDeleteIncompleteTargetAsync(toFs, path);
    throw;
}
```

  或者更简洁地统一 `catch (Exception)` 后清理再 `throw;`（`TryDeleteIncompleteTargetAsync` 本身吞掉清理异常，所以不会改变原异常）。注意保持 `throw;` 以保留原始堆栈。
- **验证**：构建通过；检查两个方法均调用 `TryDeleteIncompleteTargetAsync`。
- **提交 message**：`fix(sync): remove partial destination file on any copy failure`

---

### T05 [P0] 增加源/目标路径重叠保护，防止镜像/递归同步导致数据丢失

- **文件/位置**：新增 `Core/Sync/PathSafetyValidator.cs`；在以下位置调用：
  - `Core/Sync/SyncExecutor.cs` `ExecuteAsync`（第 53-56 行附近）。
  - `Core/Sync/TaskAnalysisService.cs` `AnalyzeAsync` 与 `ExecuteSelectedAsync`（方法开头）。
  - `UI/ViewModels/TaskEditorViewModel.cs` `SaveTaskAsync`（保存前）。
- **问题**：源路径与目标路径相同或互为父子目录时，镜像/双向同步可能递归枚举并误删数据；目前无任何保护。
- **新增文件内容**：

```csharp
using System;
using System.IO;
using FolderSync.Core.Config;

namespace FolderSync.Core.Sync
{
    public static class PathSafetyValidator
    {
        public static void EnsureSourceDestDoNotOverlap(SyncTaskDefinition task)
        {
            var sourceLocal = !string.Equals(task.SourceProtocol, "FTP", StringComparison.OrdinalIgnoreCase);
            var destLocal = !string.Equals(task.DestProtocol, "FTP", StringComparison.OrdinalIgnoreCase);

            if (sourceLocal && destLocal)
            {
                EnsureLocalPathsDoNotOverlap(task.SourcePath, task.DestPath);
                return;
            }

            if (!sourceLocal && !destLocal)
            {
                EnsureFtpPathsDoNotOverlap(task.SourcePath, task.DestPath);
            }
        }

        private static void EnsureLocalPathsDoNotOverlap(string source, string dest)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(dest))
            {
                throw new InvalidOperationException("本地源路径和目标路径不能为空。");
            }

            var sourceFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
            var destFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dest));
            var sourceToDest = Path.GetRelativePath(sourceFull, destFull);
            var destToSource = Path.GetRelativePath(destFull, sourceFull);

            var overlap = sourceToDest == "."
                          || (!Path.IsPathRooted(sourceToDest) && sourceToDest != ".." && !sourceToDest.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                          || destToSource == "."
                          || (!Path.IsPathRooted(destToSource) && destToSource != ".." && !destToSource.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));

            if (overlap)
            {
                throw new InvalidOperationException("源目录和目标目录不能相同或互为父子目录，否则递归同步可能造成数据丢失。");
            }
        }

        private static void EnsureFtpPathsDoNotOverlap(string source, string dest)
        {
            if (!Uri.TryCreate(source, UriKind.Absolute, out var sourceUri) ||
                !Uri.TryCreate(dest, UriKind.Absolute, out var destUri) ||
                !sourceUri.Scheme.Equals("ftp", StringComparison.OrdinalIgnoreCase) ||
                !destUri.Scheme.Equals("ftp", StringComparison.OrdinalIgnoreCase))
            {
                return; // 无效 FTP 路径由现有校验负责报错
            }

            if (!string.Equals(sourceUri.Host, destUri.Host, StringComparison.OrdinalIgnoreCase) ||
                sourceUri.Port != destUri.Port)
            {
                return;
            }

            var a = NormalizeFtpPath(sourceUri.AbsolutePath);
            var b = NormalizeFtpPath(destUri.AbsolutePath);
            if (a == b || a.StartsWith(b + "/", StringComparison.OrdinalIgnoreCase) || b.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("FTP 源目录和目标目录不能相同或互为父子目录。");
            }
        }

        private static string NormalizeFtpPath(string path)
        {
            return (path ?? "/").Replace('\\', '/').Trim().TrimEnd('/').ToLowerInvariant();
        }
    }
}
```

- **调用方式**：在三个执行入口处添加一行：

```csharp
PathSafetyValidator.EnsureSourceDestDoNotOverlap(task);
```

- **验证**：构建通过；`grep -R "EnsureSourceDestDoNotOverlap" Core/ UI/` 应至少出现 4 处（1 处定义 + 3 处调用，若 TaskEditor 也接上则为 4 处调用）。
- **提交 message**：`feat(sync): reject overlapping source and destination roots`

---

### T06 [P0] 单实例互斥，防止双进程并发写坏 tasks.json/SQLite

- **文件/位置**：`App.xaml.cs`。
- **问题**：当前允许多实例，两个进程可能同时写 `data/tasks.json`、SQLite 与日志。
- **改法**：
  1. `App` 类新增字段 `private Mutex? _singleInstanceMutex;`（`System.Threading`）。
  2. 在 `OnStartup` 最开头（`base.OnStartup(e);` 之后、`InitializeLogging()` 之前）执行：

```csharp
_singleInstanceMutex = new Mutex(true, @"Local\FolderSyncPro", out var isFirstInstance);
if (!isFirstInstance)
{
    MessageBox.Show("FolderSync Pro 已在运行。", "FolderSync Pro", MessageBoxButton.OK, MessageBoxImage.Information);
    Shutdown(-1);
    return;
}
```

  3. 在 `OnExit` 末尾 `Log.CloseAndFlush();` 之后释放：`_singleInstanceMutex?.ReleaseMutex(); _singleInstanceMutex?.Dispose();`。注意只有创建成功者才 Release，可记录 `bool _ownsSingleInstanceMutex`。
- **验证**：构建通过；确认 `OnStartup` 在非首实例时立即 `return`。
- **提交 message**：`fix(app): enforce single instance with named mutex`

---

### T07 [P0] 修正全局异常处理与退出流程

- **文件/位置**：`App.xaml.cs` `OnDispatcherUnhandledException` / `OnTaskSchedulerUnobservedTaskException` / `OnExit`；`Core/Scheduler/SchedulerManager.cs` `StopAsync`。
- **问题**：
  1. `OnDispatcherUnhandledException` 对所有异常 `e.Handled = true`，应用可能带病继续运行。
  2. `OnTaskSchedulerUnobservedTaskException` 里 `Log.CloseAndFlush()` 会关闭全局日志，后续运行日志全部丢失。
  3. `OnExit` 等待 `waitForJobsToComplete: true`，长 FTP 任务会让退出卡死。
- **改法**：
  1. `OnDispatcherUnhandledException`：保留日志与 MessageBox，但**删除 `Log.CloseAndFlush();`**；`e.Handled = true` 保持不变（这是产品当前策略，先不改变关闭行为，仅修日志生命周期）。
  2. `OnTaskSchedulerUnobservedTaskException`：保留 `Log.Fatal` 与 `e.SetObserved()`，**删除 `Log.CloseAndFlush();`**。
  3. `SchedulerManager.StopAsync` 改为 `await _scheduler.Shutdown(waitForJobsToComplete: false, cancellationToken);`。
  4. `App.OnExit` 保持 `await SchedulerManager.Instance.StopAsync();`，但确认其后才 `Log.CloseAndFlush()`。
- **验证**：构建通过；`grep -n "CloseAndFlush" App.xaml.cs` 应只出现在 `OnCurrentDomainUnhandledException` 与 `OnExit`。
- **提交 message**：`fix(app): do not close logger on transient exceptions and non-blocking scheduler shutdown`

---

## P1：迭代重构（一致性 / 资源 / 性能）

### T08 [P1] JSON 仓库原子写入 + 损坏文件降级，防止配置丢失或启动崩溃

- **文件/位置**：`Core/Config/TaskRepository.cs`、`Core/Config/SettingsRepository.cs`。
- **问题**：`File.WriteAllText` 非原子，进程中断可能写坏 JSON；损坏 JSON 会直接抛异常，任务页或启动崩溃。
- **改法**（两个类同模式）：
  1. `SaveAll`/`Save` 先写 `_filePath + ".tmp"`，再 `File.Move(tmp, _filePath, overwrite: true)`。
  2. `LoadAll`/`Load` 捕获 `JsonException` 与 `IOException`：将坏文件复制为 `_filePath + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}"`，返回空列表/新 `AppSettings()`，并写 `Serilog.Log.Error`。
  3. 增加 `private readonly object _gate = new();`，`LoadAll/SaveAll/Upsert/DeleteById` 和 `Load/Save` 使用 `lock (_gate)` 保护“读-改-写”序列。
- **验证**：构建通过；手工临时把 `tasks.json` 改为非法 JSON 后启动应用，不应崩溃。
- **提交 message**：`fix(config): atomic json writes and corrupt-file fallback`

---

### T09 [P1] 调度器加锁并释放旧 SyncExecutor，堵住 FTP 连接泄漏

- **文件/位置**：`Core/Scheduler/SchedulerManager.cs`、`Core/Sync/SyncExecutor.cs`。
- **问题**：`AddOrUpdateJobAsync`/`RemoveJobAsync` 无锁；`DeleteJob` 替换任务后旧 `SyncExecutor` 及其 `FtpFileSystem` 不释放。
- **改法**：
  1. `SyncExecutor` 实现 `IDisposable`：

```csharp
public void Dispose()
{
    _sourceFs.Dispose();
    _destFs.Dispose();
}
```

  2. `SchedulerManager` 增加 `private readonly SemaphoreSlim _gate = new(1, 1);` 与 `private readonly Dictionary<string, SyncExecutor> _executors = new(StringComparer.OrdinalIgnoreCase);`。
  3. `StartAsync`、`StopAsync`、`AddOrUpdateJobAsync`、`RemoveJobAsync`、`GetNextFireTimeAsync`、`TriggerJobImmediatelyAsync` 内用 `await _gate.WaitAsync(); try { ... } finally { _gate.Release(); }`。
  4. `AddOrUpdateJobAsync` 在 `DeleteJob` 成功后，若 `_executors` 含旧 taskId，取出并 `Dispose`；随后把新 executor 存入字典。
  5. `RemoveJobAsync` 删除任务后同样 `Dispose` 并移除旧 executor。
  6. `StopAsync` 在 scheduler shutdown 后遍历字典 `Dispose` 所有 executor 并清空。
- **注意**：不要在锁内 `await` 一个长时间运行的调度触发；当前 Quartz API 调用都是短操作。
- **验证**：构建通过；`grep -n "Dispose" Core/Scheduler/SchedulerManager.cs Core/Sync/SyncExecutor.cs` 确认释放路径存在。
- **提交 message**：`fix(scheduler): serialize scheduler mutations and dispose replaced executors`

---

### T10 [P1] 打通双向同步的手动分析与手动执行基线（实验性模式数据一致性）

- **文件/位置**：
  - `Core/Sync/SyncExecutor.cs`
  - `Core/Sync/TaskAnalysisService.cs`
- **问题**：`TaskAnalysisService` 对 TwoWay 只调用普通 Diff（`isMirror` 只对 OneWayMirror 为 true），不读/不写 `TwoWayStateStore`；手动分析会漏 Delete/B→A 动作，手动执行后基线陈旧。
- **分两步改**：
  **Step A**：在 `SyncExecutor` 中新增公开方法，复用现有私有 helpers，生成分析项：
  - `public async Task<List<TaskAnalysisItem>> AnalyzeTwoWayAsync(CancellationToken cancellationToken = default)`
  - 实现顺序：连接两个 FS → `InitializeAsync` → 列举/过滤/`ExpandWithAncestorDirectories` → `BuildSnapshotsAsync` → `LoadAsync` → `BuildTwoWayOperations`。
  - 映射规则：
    - 每个路径生成 `TaskAnalysisItem`，Source/Dest 信息来自 `sourceSnapshots`/`destSnapshots`（StateSnapshot 只有 Exists/Size/LastWrite/Hash，需要从 `sourceItems`/`destItems` 保留 `FileItem` 以填 SourceSize/DestSize 等；可先建 `FileItem` map）。
    - `ShouldSync = operations 中存在该路径`；`ActionType = MapToSyncActionType(op.Kind)`；方向 `CopyAToB/CreateDirInB` 为 `AToB`，`CopyBToA/CreateDirInA` 为 `BToA`；`Reason` 按 op.Kind 写中文说明。
    - 无 op 且两端都存在且 SnapshotEquals 为 true 时 `ShouldSync=false`，原因“已一致，无需同步”；无 op 但不同时写“冲突或忽略，未生成操作”。
  **Step B**：`TaskAnalysisService` 接入：
  - `AnalyzeAsync` 开头：`if (task.SyncMode == SyncMode.TwoWay) { using var executor = SyncTaskFactory.CreateExecutor(task); return await executor.AnalyzeTwoWayAsync(cancellationToken); }`
  - `ExecuteSelectedAsync` 结束前（`report.EndTime = DateTime.UtcNow;` 之前）：
    `if (task.SyncMode == SyncMode.TwoWay && report.FailedFiles == 0) { using var executor = SyncTaskFactory.CreateExecutor(task); await executor.RefreshTwoWayBaselineAsync(cancellationToken); }`
  - 在 `SyncExecutor` 中新增 `public async Task RefreshTwoWayBaselineAsync(CancellationToken)`，把现有 `ExecuteReliableTwoWayAsync` 第 151-159 行的“重新列举+写回基线”逻辑抽取为该方法。
- **验证**：
  - 构建通过。
  - 静态检查：`TaskAnalysisService.AnalyzeAsync` 中出现 `SyncMode.TwoWay` 分支；`ExecuteSelectedAsync` 中出现 `RefreshTwoWayBaselineAsync`。
- **提交 message**：`fix(sync): keep two-way baseline consistent for manual analysis and execution`

---

### T11 [P1] 触发配置与 Cron 校验：非法配置显式失败，禁止保存“已落盘但未调度”的任务

- **文件/位置**：
  - `UI/ViewModels/TaskEditorViewModel.cs` `SaveTaskAsync`。
  - `Core/Sync/SyncTaskFactory.cs` `ResolveCronExpression`（第 66-72 行）。
- **问题**：`IntervalUnit` 未知时静默按分钟执行；三个 RadioButton 可能同时为 false/多个为 true；坏 Cron 会先写 tasks.json 再在 `AddOrUpdateJob` 抛错，造成配置与调度不一致。
- **改法**：
  1. `SaveTaskAsync` 在 `ValidateFtpConfiguration()` 后、`BuildTaskDefinition` 前新增 `if (!ValidateTriggerConfiguration()) return;`：
     - `IsManualTrigger || IsPeriodicTrigger || IsCronTrigger` 中恰好一个为 true。
     - `IsPeriodicTrigger` 时 `int.TryParse(IntervalValue, out var n) && n > 0`。
     - `IsCronTrigger` 时 `Quartz.CronExpression.IsValidExpression(CronExpression.Trim())`（Quartz 已引用）。
     - 不合法用 MessageBox 提示，不落盘。
  2. `ResolveCronExpression` 的 default 分支改为 `throw new InvalidOperationException($"不支持的调度周期单位：{task.IntervalUnit}")`。
- **验证**：构建通过；`grep -n "IsValidExpression" UI/ViewModels/TaskEditorViewModel.cs` 存在。
- **提交 message**：`fix(scheduler): validate trigger and cron before persisting task`

---

### T12 [P1] 落实 `LogRetentionDays` 日志保留天数

- **文件/位置**：`App.xaml.cs` `InitializeLogging`/`ApplyDisplaySettings`；`Core/Config/AppSettings.cs`。
- **问题**：设置项存在但从未使用；Serilog 只按文件数量保留 30 个，不按天数清理。
- **改法**：
  1. 让 `ApplyDisplaySettings` 返回加载的 `AppSettings`（或新增字段保存），`OnStartup` 拿到 settings。
  2. 新增 `private static void CleanupOldLogs(AppSettings settings)`：扫描 `AppDomain.CurrentDomain.BaseDirectory/log` 下 `*.log` 与 `*.txt`，删除 `LastWriteTimeUtc < DateTime.UtcNow.AddDays(-settings.LogRetentionDays)` 的文件；用 `try/catch` 包裹单个文件删除，失败写 `Log.Warning`。
  3. `InitializeLogging` 打开日志后调用该清理（清理日志本身日志文件创建时间不可能过期，安全）。
- **验证**：构建通过；`grep -n "CleanupOldLogs" App.xaml.cs` 存在调用。
- **提交 message**：`feat(app): enforce log retention days setting`

---

### T13 [P1] 修复任务编辑器绑定反馈与危险操作确认

- **文件/位置**：
  - `UI/ViewModels/TaskEditorViewModel.cs` 的 `SourcePath`/`DestPath` 属性。
  - `UI/Views/TaskEditorView.xaml` 第 48、87 行。
  - `UI/ViewModels/TasksViewModel.cs` `DeleteTaskAsync`。
- **问题**：`SourcePath/DestPath` 是自动属性且不通知，`CanSaveTask` 可能不会在用户输入后重新查询，保存按钮会一直灰；删除任务无确认。
- **改法**：
  1. 把 `SourcePath`、`DestPath` 改为带 `_sourcePath`/`_destPath` 字段的完整属性，`set` 中使用 `SetProperty`，并在变化时 `CommandManager.InvalidateRequerySuggested()`。
  2. XAML 两个路径 TextBox 加 `UpdateSourceTrigger=PropertyChanged`。
  3. `DeleteTaskAsync` 删除前加 `MessageBox.Show` 确认（YesNo + Warning），用户选 No 则直接返回。
- **验证**：构建通过；新建任务页面输入路径时保存按钮状态能刷新（逻辑绑定检查）。
- **提交 message**：`fix(ui): fix save-command requery and add delete confirmation`

---

### T14 [P1] 本地文件系统尊重 CancellationToken，停止按钮不再对本地大目录无效

- **文件/位置**：`Core/VFS/LocalFileSystem.cs` `ListFilesAsync`（第 41-79 行）。
- **问题**：递归枚举本地大目录时完全忽略取消令牌，用户点“停止”也要等枚举完成。
- **改法**：
  1. 在两个 `foreach` 循环内每处理一个条目调用 `cancellationToken.ThrowIfCancellationRequested();`。
  2. `EnumerateDirectories`/`EnumerateFiles` 仍不支持传入 token，这是 .NET API 限制；循环内检查已足以在可接受延迟内取消。
  3. 对 `OpenWriteAsync`/`DeleteFileAsync` 等短操作无需改动。
- **验证**：构建通过；`grep -n "ThrowIfCancellationRequested" Core/VFS/LocalFileSystem.cs` 至少 2 处。
- **提交 message**：`fix(vfs): honor cancellation token while enumerating local files`

---

## P2：架构演进与体验完善（低优先级，可后续单独执行）

### T15 [P2] 引入 `MessageDialogService` 并本地化硬编码文案

- **文件/位置**：新增 `UI/Services/MessageDialogService.cs`；逐步替换 33 处 `MessageBox.Show`；`TaskEditorView.xaml`、`MainWindow.xaml` 硬编码中文文案迁移到 `Strings.*.xaml`。
- **改法**：
  1. 服务提供 `ShowInfo(string key, params object[] args)` / `ShowError(...)` / `Confirm(...)`，内部从 `Application.Current.TryFindResource` 取文案。
  2. 在 `Strings.zh-CN.xaml` / `Strings.en-US.xaml` 补齐 `Common.ConfirmDelete`、`Main.Welcome`、`Main.SelectModule`、`Editor.*` 等键。
  3. 优先替换 `TasksViewModel`、`TaskAnalysisViewModel`、`SettingsViewModel`；不要一次提交 33 处，按 ViewModel 分 3 个 commit。
- **验证**：构建通过；中英文资源键数量一致（可用脚本比较 x:Key 列表）。
- **提交 message**：`refactor(ui): introduce message dialog service and localize user-facing strings`

---

### T16 [P2] 性能优化：SQLite 批量写入、分析结果批量加载与汇总防抖

- **文件/位置**：`Core/Sync/OneWayDeliveryStateStore.cs`、`Core/Sync/TwoWayStateStore.cs`、`UI/ViewModels/TaskAnalysisViewModel.cs`。
- **改法**：
  1. `OneWayDeliveryStateStore` 增加 `UpsertRangeAsync(taskId, records, ct)`，单连接 + 单事务批量 upsert；`SyncExecutor` 与 `TaskAnalysisService` 收集成功后统一写，而不是每文件开连接。
  2. `TaskAnalysisViewModel.LoadAnalysisAsync` 在后台完成 List 构建，UI 线程一次性 `Clear + foreach Add`（当前已经是添加，但必须把 Map 与排序放到后台）。
  3. `TaskAnalysisViewModel` 的 `SelectedSyncFileCount/TotalSyncSizeText` 增加 `_summaryDirty` 与 `DispatcherTimer` 300ms 防抖，避免勾选几千行时每次全量求和。
- **验证**：构建通过；无行为变化。
- **提交 message**：`perf(sync): batch delivery-state writes and debounce analysis summary`

---

### T17 [P2] ViewModel 生命周期与导航状态治理

- **文件/位置**：`UI/ViewModels/MainViewModel.cs`、`UI/ViewModels/TasksViewModel.cs`、`UI/Views/TaskAnalysisWindow.xaml.cs`。
- **改法**：
  1. `MainViewModel.Navigate` 切换前调用旧 VM 的 `IDisposable`/`CancelPendingOperations()`（TasksViewModel 取消 `_currentOperationCts` 并退出前不弹完成 MessageBox）。
  2. `TaskAnalysisWindow` 在 `Closing` 中：若 `HasUnsavedChanges`，弹出保存/放弃/取消三选项；并取消当前 CTS。
  3. 后续再评估缓存 TasksViewModel 而不是每次返回重建（避免选中状态丢失）。
- **验证**：构建通过；手工验证切换页面与关闭分析窗口行为。
- **提交 message**：`refactor(ui): manage viewmodel lifecycle on navigation and window close`

---

### T18 [P2] 补齐 Dashboard 真实统计与“立即执行”入口

- **文件/位置**：`UI/ViewModels/DashboardViewModel.cs`、`Core/Scheduler/SchedulerManager.cs`、`UI/Views/TasksView.xaml`。
- **改法**：
  1. Dashboard 从 `TaskRepository.LoadAll()` 统计启用任务数；从当天报告或运行日志统计成功/失败（先统计报告文件内容简单计数，避免大改）。
  2. `TriggerJobImmediatelyAsync` 在任务三点菜单增加“立即运行”按钮，并对无调度的手动任务提示不可用。
- **验证**：构建通过；手动任务立即运行入口可见。
- **提交 message**：`feat(ui): populate dashboard stats and expose run-now action`

---

### T19 [P2] 测试与发布工程化

- **文件/位置**：新增 `FolderSync.Core.Tests` 测试项目（xUnit）或最小 console test harness。
- **优先级最高的测试**：
  1. `LocalFileSystem.GetFullPath` 对 `..`、`C:\data2`、根路径边界的测试。
  2. `PathSafetyValidator` 重叠/非重叠用例。
  3. `OneWayDeliveryStateStore` 写入/读取/重置。
  4. `StructureAwarePathHelper.ExpandWithAncestorDirectories` 父目录补齐。
  5. `FilterEngine` 白名单/黑名单组合。
- **验证**：`dotnet test` 通过。
- **提交 message**：`test(core): add core regression tests for path safety and state stores`

---

## 最终验收清单

- [ ] `dotnet build -c Release` 成功，0 error。
- [ ] `grep -R "GetAwaiter().GetResult()" UI/` 无 UI 线程同步阻塞（TaskEditor 后台测试可保留）。
- [ ] `grep -R "MessageBox.Show" UI/ViewModels` 数量至少不再增加；P2 后趋近于 0（App 级除外）。
- [ ] 启动应用后，`tasks.json` 中的非手动任务在重启后仍被 Quartz 调度。
- [ ] 本地路径 `C:\data` 与 `C:\data2`、父子目录、`..` 路径均不会逃逸或重叠。
- [ ] 复制中断（取消或异常）后不残留半截目标文件。
- [ ] 双向模式手动分析与执行后，SQLite 基线已刷新。
- [ ] 全部 P0/P1 任务已提交，版本号递增到 `0.0.55` 并发布 portable exe。
- [ ] `CLAUDE.md` 第 3 节目录树已同步本次新增文件（`PathSafetyValidator.cs`、`MessageDialogService.cs`、测试项目等）。

## 版本记录

| 版本 | 说明 |
|------|------|
| 0.0.54 → 0.0.55 | 按本计划完成 P0/P1（P2 未完成时也必须在最终提交前递增版本号） |
