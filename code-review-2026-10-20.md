# Folder Sync 当前项目代码审查

审查日期：2026-10-20  
基线：HEAD `7c44473`，包含当前工作区未提交的 `FolderSync.csproj` 修改。  
配套计划：`llm-execution-plan-2026-10-20.md`。

## 1. 结论与验证边界

当前项目存在构建阻断，以及多个可导致目标文件丢失、删除越过用户选择范围、失败操作不再重试的问题。建议先修复 P0，再处理并发与审计，不宜先做视觉或大规模架构重写。

本次直接审查 Core/VFS、Diff、Filters、Sync、Config、Scheduler、Reporting，以及 App、任务编辑/任务列表/分析窗口/导航/统计等主要代码。重点是实际执行链路，而非仅重述旧报告。本轮未修改程序代码，未提交，未递增版本或发布 exe。

验证结果：

- PowerShell 默认 `dotnet` 指向 x86 runtime，无法执行 SDK 命令。
- `C:\Program Files\dotnet\dotnet.exe` 存在 SDK 8.0.419。
- 使用上述 x64 路径执行 `build FolderSync.csproj -c Release --no-restore`，实际失败：0 警告、61 错误，主要为测试源文件被主工程编译导致的 `Xunit` / `Fact` 类型缺失。
- 未运行测试、真实 FTP/SMB 联调或 WPF 交互复现。除构建问题外，以下结论为静态代码证据与可推导触发场景，不能称为运行时已复现。
- 未读取用户运行中的 `data/` 数据、FTP 密码或 `local_test/` 内容。

## 2. 问题清单

### R01 / P0：当前工作区无法构建

位置：`FolderSync.csproj:9`；`FolderSync.Core.Tests/FolderSync.Core.Tests.csproj:20`。

未提交修改将 `FolderSync.Core.Tests\**` 从 `DefaultItemExcludes` 移除。SDK 默认递归纳入测试 `.cs`，但主工程没有 xUnit 引用，Release 构建实际报 61 个错误。HEAD 排除项存在，故应区分“当前工作区回归”与“已提交代码缺陷”。

同时工作区版本为 0.0.54，HEAD 为 0.0.55。不能直接回滚整个 csproj，必须先保留和确认用户版本意图。

### R02 / P0：复制失败可能删除原有正常目标，甚至从未开始写目标

位置：`Core/Sync/OneWayDeliveryStateStore.cs:273-342`；`Core/VFS/LocalFileSystem.cs:138-148`。

复制帮助函数把打开源流、打开目标流、传输和关闭流全部包在同一个 try 内，任何异常均调用 `DeleteFileAsync(path)`。若源文件缺失或被排他占用，源流打开失败，此时目标原本正常且完全未被写入，却仍会被删除。已经开始写入时，`FileMode.Create` 会先截断原有目标，取消/失败也无法恢复旧内容。

需要临时文件传输和成功后的替换提交，失败只清理本次创建的临时对象。仅加“写入已开始”标记不能解决覆盖旧版本导致的数据丢失。

### R03 / P0：递归目录删除越过过滤规则和用户勾选范围

位置：`Core/Sync/SyncExecutor.cs:602-605`；`Core/Sync/TaskAnalysisService.cs:238-256`；`Core/VFS/LocalFileSystem.cs:171-176`；`Core/VFS/FtpFileSystem.cs:139-148`。

分析/执行集合经过过滤，并会把命中文件的父目录加入集合；但删除父目录时直接递归删除整棵子树。举例：目标独有 `old/a.txt` 与被排除的 `old/keep.jpg`，仅 `.txt` 命中过滤并带入父目录，镜像动作删除父目录时也会删掉 `keep.jpg`。

手动执行同样如此：只勾选父目录删除、取消勾选某个子文件，仍会递归删掉子文件。子文件删除失败后继续删父目录，还可能让“失败”文件最终被删除。

必须把“目录结构参与分析”和“授权删除整棵子树”分开；默认仅删除已授权文件，目录仅在实际为空时删除。

### R04 / P0：FTP 部分扫描失败被当作文件不存在，镜像/双向可误删

位置：`Core/VFS/FtpFileSystem.cs:260-309`；`Core/Sync/SyncExecutor.cs:92-123, 140-157, 325-335`。

FTP 子目录重试失败后只写 warning 并跳过，接口仍返回普通完整列表，没有“不完整扫描”状态。源端某子树扫描失败时，镜像把目标该子树中的文件视为源端不存在；双向有历史基线时，也可能把缺失扫描解释为真实删除并投射到另一端。

子目录 catch 还包含 `OperationCanceledException`，取消可能被误记为跳过目录，尤其最后一个子目录结束时缺少后续取消检查。

应区分不存在、访问失败、扫描未完成；不完整区域禁止生成删除动作或写入可信基线，取消异常应直接传播。

### R05 / P0：双向全量刷新基线吞掉失败、未勾选和外部修改

位置：`Core/Sync/SyncExecutor.cs:150-157, 241-256, 321-335, 479-484`；`Core/Sync/TaskAnalysisService.cs:323-327`。

调度链逐项捕获失败后，仍将两端当前全量状态写为新基线。若原来 A/B 一致，A 修改后复制失败且 B 保持旧版，新基线记录这一不一致；下一次两端都未变化，`changedA/changedB` 均 false，失败操作消失，不再重试。

手动链虽然要求 `FailedFiles == 0`，但允许只选部分动作，再全量刷新基线。被取消勾选的修改也会被记录为已处理。刷新期间发生的外部改动也可能被无条件吸收入基线。

基线应按实际确认成功的路径及删除结果增量提交；失败、未选、扫描不完整、前置条件变化的路径保留旧基线。不能只靠“全部失败数为 0”判断。

### R06 / P1：一次性同步批量状态只在正常循环结束落盘，取消丢失已成功投递记录

位置：`Core/Sync/SyncExecutor.cs:539-640`；`Core/Sync/TaskAnalysisService.cs:202-327`。

成功记录先保存在内存，直到循环完成才 `UpsertRangeAsync`。第 N 个文件取消会直接抛出，前 N-1 个已完成文件没有写入 SQLite；即使进入落盘，使用已经取消的 token 也可能阻止提交。

此后目标删除这些文件，程序又会补发，违反“每路径仅首次成功同步”。进程异常退出也扩大丢记录窗口。

批量性能优化应改为短批检查点或复用连接逐项事务，取消时以独立、有限超时保存已确认成功记录；写库失败必须报告，不能声称已可靠完成一次性投递。

### R07 / P1：定时和手动入口缺少共同互斥，修改/删除/退出会释放正在使用的执行器

位置：`Core/Scheduler/SyncJob.cs:13, 44`；`Core/Scheduler/SchedulerManager.cs:53-65, 90-103, 136-150`；`UI/ViewModels/TasksViewModel.cs:478-485, 591-638`。

Quartz 的 `DisallowConcurrentExecution` 只约束 Quartz JobKey，不覆盖手动 `TaskAnalysisService`。UI 自己的 `_isBusy` 也不约束定时任务或其他窗口，两条链可同时写同一个目标和状态库。

更新任务先 Dispose 旧 executor 再 DeleteJob；DeleteJob 不等于中止当前执行。删除任务和 `Shutdown(waitForJobsToComplete:false)` 后立即 Dispose 也有同样风险。`_gate` 只串行化调度管理，不保护正在运行的同步。

建议任务执行协调器覆盖全部入口；Job 每次执行创建并独占 executor。修改/删除/重置状态必须协调活动运行，退出先取消并有限等待，再释放资源。

### R08 / P1：仓库锁仅实例级，跨实例并发写会丢更新或共用临时文件冲突

位置：`Core/Config/TaskRepository.cs:13, 40-68, 92-97`；`Core/Config/SettingsRepository.cs:11, 53-58`；`UI/ViewModels/TaskAnalysisViewModel.cs:225-231`。

各 ViewModel/Service 创建独立 repository，锁不是共享的。两个实例 Upsert 不同任务会各自读取旧列表，最后写入覆盖先写结果；固定 `tasks.json.tmp` 还可能造成写入/移动竞争。

加载时 IOException 被当作 JSON 损坏返回空列表；后续 Upsert 可能把未成功读取的旧数据覆盖成单任务列表。旧 ViewModel 的完整任务对象 Upsert 也可能覆盖较新的编辑，或把已经删除的任务重新创建。

需按规范化文件路径共享锁，读-改-写事务覆盖完整流程；区分缺失、损坏和暂时 IO 错误；使用唯一临时文件及备份。分析保存宜只更新分析字段并校验任务修订版本。

### R09 / P1：任意勾选无动作/过滤项可隐式反向复制，旧分析可执行破坏性动作

位置：`UI/Views/TaskAnalysisWindow.xaml` 的 `DataGridCheckBoxColumn`；`Core/Sync/TaskAnalysisService.cs:214-299`；`UI/ViewModels/TasksViewModel.cs:332-343`。

用户可以把“被过滤规则排除”或“无需同步”的行勾为 true。这些行 ActionType 为 null、Direction 为 None，执行器不拒绝，而是按旧时间选择方向，甚至在单向任务中 B -> A 覆盖源文件；最终计为 Update。

保存分析可跨运行复用，没有任务配置修订或源/目标元数据前置条件。分析后出现的新目标文件可能被旧 Delete 动作删除；源文件变化也导致实际执行与用户确认内容不同。

要明确“手动强制同步”产品语义，生成显式、符合模式的动作；无合法动作、提示行不能执行。执行前校验配置及路径前置条件，变化项提示重新分析，不自动静默全量重分析。

### R10 / P1：任务复用 ID 而端点变化，旧基线/投递状态污染新目录

位置：`UI/ViewModels/TaskEditorViewModel.cs:492-518`；`Core/Sync/SyncExecutor.cs:43-44`；`Core/Sync/OneWayDeliveryStateStore.cs:43-47`；`Core/Sync/TwoWayStateStore.cs:53-57`。

编辑保留任务 Id，但 SQLite 仅按任务 Id 与相对路径分区。一次性任务换到新目标目录后，同名源文件仍被认为已投递，导致新目标永远收不到；双向端点或过滤范围改变，也会把旧基线用于新扫描范围，产生错误的新增/删除判断。

需要端点与配置修订标识，或在语义相关编辑时提示并创建新状态世代；不能静默清空用户一次性状态。新世代不删除旧数据，提供显式重置/迁移策略。

### R11 / P1：大小写敏感 FTP 路径被全链路合并

位置：`Core/VFS/FtpFileSystem.cs:267`；`Core/Diff/SizeAndTimeDiffStrategy.cs:74-81`；`Core/Diff/ChecksumDiffStrategy.cs` 的 BuildPathMap；`Core/Sync/StructureAwarePathHelper.cs:16-17`；两个 SQLite store 的 LoadAsync。

`A.txt` 与 `a.txt` 在常见 Unix FTP 上是两个不同文件，但当前路径字典/访问集合使用 OrdinalIgnoreCase，会覆盖条目、跳过同名不同大小写目录或匹配到错误对象。可能导致漏同步、错误覆盖或删除。大小写冲突写到 Windows 目标时也无法可靠表示。

应增加 VFS 路径比较能力；对于目标无法表达的冲突，显式阻断并报告，禁止静默取最后一个。必须同时修改分析、状态和结构辅助，不能只改 Diff 字典。

### R12 / P1：文件/目录同路径类型冲突被当成一致

位置：`Core/Diff/SizeAndTimeDiffStrategy.cs:35-49`；`Core/Diff/ChecksumDiffStrategy.cs:35-64`。

路径匹配后仅当双方都不是目录才比较，源文件/目标目录或源目录/目标文件没有动作、没有冲突提示。分析无动作分支会显示“已一致，无需同步”。双向也缺少安全的类型替换计划，复制或创建将直接失败。

应显式建模类型冲突，默认保留现存数据并提示；需确认用户允许替换后才备份、删除和创建，不能简单把冲突当 Update。

### R13 / P1：正则超时的白名单结果反向，超时文件被放行

位置：`Core/Filters/RegexFilter.cs:55-61`；`Core/Filters/FilterEngine.cs:161-164`。

超时分支 `isMatch = !_isExcludePattern`，白名单 `isExcludePattern:false` 返回 true，与“白名单不放行”的注释相反。双名单引擎黑名单也以 false 构造，导致超时被当作匹配。规则无法可靠评估时，不能继续给破坏性操作提供授权。

推荐抛出可识别规则超时异常，让本次计划失败或标记不完整；不要把“未知”转换成真实命中/未命中。必要时使用可支持的 NonBacktracking，并补实际超时测试。

### R14 / P1：手动取消/全局异常不产出部分报告，UI 完成提示不看失败数

位置：`Core/Sync/TaskAnalysisService.cs:303-334`；`UI/ViewModels/TaskAnalysisViewModel.cs:225-243`；`UI/ViewModels/TasksViewModel.cs:342-357, 637-645`；`Core/Reporting/SyncReportFileWriter.cs:89, 98-115`。

手动服务发生取消、连接/状态保存异常时直接抛出，只在 finally 设 EndTime，UI 因未取得 report 而不落盘。此前已成功/失败文件明细丢失。正常返回的 report 即使 FailedFiles > 0，UI 仍显示完成项数为选择数，没有失败摘要。

文件命名 `File.Exists` 后 `WriteAllText` 存在并发检查-写入竞争；发生同名时可以覆盖另一报告。应使用 CreateNew 原子抢占文件名，冲突只递增时间戳，不恢复随机后缀。

报告还缺少源/目标根路径、实际方向，当前 Path 为相对路径；大小来自分析快照而非实际传输字节。上述需与用户审计要求一起完善。

### R15 / P2：周期触发用 Cron 步进，不能表达真正固定间隔

位置：`Core/Sync/SyncTaskFactory.cs:61-74`。

7 秒会在每分钟 0,7,...,56 秒触发，再到下一分钟 0 秒，出现 4 秒间隔；7 小时每天 0,7,14,21 再到次日 0，相邻最后间隔 3 小时；按天步进每月重置。正数校验也未覆盖 Cron 字段上限。

如果“周期”表示固定间隔，应使用 SimpleTrigger，Cron 留给日历计划；如果产品本来定义日历步进，应改文案并严格限制数值。这是需要确认的产品语义，不应擅自改存量触发时间。

### R16 / P2：统计与选中数据量不准确

位置：`UI/ViewModels/DashboardViewModel.cs:77, 100-114`；`UI/ViewModels/TaskAnalysisViewModel.cs:393-397`；`Core/Sync/SyncExecutor.cs:553-598`。

Dashboard 有 Actions 时只按 failed 数判断失败任务，连接失败通常 ErrorMessage 非空、failed=0，会漏记；“已调度”只依据配置，不查询实际注册成功状态。目录计数在不同执行链也不一致。

分析汇总只累计 SourceSize，B -> A 文件应使用 DestSize，删除不应计入传输量；尚未修复复制时间保留，快速比对在等大小目标更新于源时只按“源更新”策略跳过，应在文档中明确快速比对的语义边界。

### R17 / P2：路径安全仅词法校验，没有处理重解析点与 FTP 解码一致性

位置：`Core/VFS/LocalFileSystem.cs:199-222`；`Core/Sync/PathSafetyValidator.cs:47-80`；`Core/Sync/SyncTaskFactory.cs:113-136`。

本地路径 GetFullPath/GetRelativePath 能挡 `..` 和兄弟目录，但不能识别根目录内 junction/symlink 指向外部目录；真实文件操作可能越出授权根。FTP 重叠验证比较 Uri.AbsolutePath，创建 VFS 则先 UnescapeDataString，编码斜杠形式可能在校验阶段不同但执行阶段重叠。

应统一端点规范化，定义链接策略（默认不遍历/拒绝写入跨根链接），补 Windows junction 及编码路径测试。实际外部链接环境未验证。

## 3. 旧报告对照

下列旧问题已有实现，不应简单重复执行旧计划：

- 启动恢复定时任务：App.RestoreScheduledTasksAsync 已存在。
- 单实例：已存在 Local Mutex；第二实例激活现有窗口仍未实现。
- UI 保存/删除 GetResult 阻塞：已改 await；FTP 测试中的 GetResult 在 Task.Run 内，不能误判为 UI 同步阻塞。
- Local 路径前缀绕过：已换边界感知相对路径检查，但重解析点仍有缺口。
- 普通异常清理半截目标：已实现，但新增了 R02 的更严重数据丢失。
- 双向手动分析已复用 SQLite；手动执行也刷新基线，但 R05 的全量确认缺陷仍存在。
- 日志保留、正则 timeout、消息服务、Dashboard、执行器 IDisposable、仓库临时文件、汇总 debounce、导航取消与基础测试均已有实现；“有实现”不代表对应并发/语义正确。

`CLAUDE.md` 第 7 节仍列出多个已修复项，第 3 节漏掉 PathSafetyValidator/MessageDialogService 等实际文件，版本说明也滞后，应在修复实施后更新，旧文档保留为历史记录。

## 4. 待确认策略与剩余验证

1. 周期触发是固定间隔还是日历步进。
2. FTP 是否支持并要求 FTPS，是否允许无 TLS 降级；DPAPI 只保护落盘，不等于传输加密。
3. 同路径文件/目录替换、大小写冲突、一致项强制同步是否允许，允许时如何确认与备份。
4. 端点/过滤修改时，是否创建新状态世代或要求手动重置。
5. Portable 是否必须完全自包含。此前使用 self-contained false，不能据此保证干净 Windows 无 .NET Runtime 可运行。

依赖漏洞未执行专项扫描，FTPS 行为未对 FluentFTP 实际连接握手验证，不作已确认漏洞结论。性能、实际 FTP 时间精度、窗口关闭与取消交互还需下一阶段回归。已有测试主要覆盖辅助类，未覆盖上述执行失败、删除授权、基线提交与跨入口并发。
