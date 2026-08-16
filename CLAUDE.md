# Folder Sync 程序架构与技术栈分析

## 1. 需求分析总结

根据当前项目目标，程序运行在 Windows 平台，核心能力包含：

- **存储协议支持**：本地路径 (Local)、FTP、SMB (UNC 路径)；FTP 支持匿名登录与账号密码登录。
- **同步模式**：
  - 单向增量（A -> B，仅新增）
  - 单向更新（A -> B，新增+修改）
  - 单向一次性同步（A -> B，每个文件仅首次成功同步；目标端后续删除也不补发）
  - 单向镜像（A -> B，新增+修改+删除）
  - 双向同步（定时执行链路已接入 SQLite 状态基线，支持可靠判定与冲突处理；手动分析/执行链路尚未接入，属于已知技术债，见第 7 节）
- **差异比对机制**：
  - 快速比对（文件大小+时间）
  - 哈希比对（当前固定采用 **xxHash64**，性能优先）
- **过滤规则**：白名单（包括）+黑名单（排除）双名单并行，支持扩展名、大小、时间、正则；时间粒度为 **hour**。
- **调度系统**：周期触发支持秒/分/时/天；Cron 触发支持 Quartz 6 段表达式（秒/分/时/日/月/周）。
- **日志与报告**：
  - 运行日志：按启动实例生成 `.log` 文本文件，统一写入程序目录下的 `log` 子文件夹
  - 任务报告：每次任务执行独立落盘为 `.txt` 文本文件，统一写入 `log` 子文件夹，且文件名避免并发重名
  - UI 不再内置日志内容查看窗口，仅展示文件名并提示用户自行用外部编辑器打开
- **UI**：WPF + MaterialDesign，MVVM 架构（已完成主框架与 Dashboard/Tasks/Logs/TaskEditor 的现代化布局重构，FTP 任务编辑支持测试连接、10 秒超时反馈，并修复测试结果弹窗关闭后主窗口卡住的问题；分析结果会提示“源文件被过滤规则全部排除”的场景；任务页分析与执行链路已移到后台线程，避免主窗口在 FTP 分析期间假死）。
- **国际化与显示**：支持中文/英文切换，支持字体与界面缩放配置并持久化。
- **桌面集成**：支持 Windows 托盘常驻、当前用户级开机启动；关闭窗口时可隐藏到托盘后台运行，开机自启动场景默认静默驻留托盘。

---

## 2. 最终技术栈（已落地）

- **框架**：.NET 8 + WPF
- **UI**：MaterialDesignThemes
- **协议层**：System.IO（Local/SMB）+ FluentFTP（FTP）
- **调度**：Quartz.NET
- **日志**：Serilog
- **哈希**：System.IO.Hashing（xxHash64）
- **桌面壳集成**：System.Windows.Forms（NotifyIcon 托盘图标）
- **系统集成**：Microsoft.Win32（注册表 Run 项开机启动）+ 启动参数（区分手动启动与静默托盘启动）
- **凭据保护**：Windows DPAPI（当前用户作用域）用于 FTP 密码加密保存

---

## 3. 架构与目录映射（核心模块 + 文件结构）

> 目标：让新同事能从“模块职责”直接定位到“目录与关键文件”。
> **强制要求：后续重构代码时，必须同步更新本节内容。**

### 3.1 总体目录

```text
Folder_sync/
├─ App.xaml
├─ App.xaml.cs
├─ MainWindow.xaml
├─ MainWindow.xaml.cs
├─ FolderSync.csproj
├─ CLAUDE.md
├─ .gitignore
├─ Core/
├─ UI/
│  └─ Services/
├─ .trae/specs/
├─ .trae/documents/
│  ├─ code-review-2026-08-16.md
│  └─ improvement-execution-plan-2026-08-16.md
└─ publish/
```

### 3.2 模块到目录映射

- **UI 层（MVVM）**
  - 职责：导航、页面交互、任务编辑、日志展示、设置、托盘文案与开机启动配置联动。
  - 目录：`UI/Views`, `UI/ViewModels`, `UI/Converters`, `UI/Localization`, `UI/Services`
  - 关键文件：
    - `MainWindow.xaml` / `MainViewModel.cs`
    - `DashboardView*`, `TasksView*`, `TaskEditorView*`, `TaskAnalysisWindow`, `LogsView*`, `SettingsView*`
    - `TaskAnalysisViewModel.cs`, `TrayIconService.cs`, `StartupRegistrationService.cs`

- **应用壳层 / 生命周期**
  - 职责：应用启动与退出、日志初始化、调度器生命周期、启动参数解析、主窗口关闭拦截与托盘常驻行为。
  - 目录：项目根目录 + `UI/Services`
  - 关键文件：
    - `App.xaml` / `App.xaml.cs`
    - `MainWindow.xaml` / `MainWindow.xaml.cs`
    - `UI/Services/TrayIconService.cs`, `UI/Services/StartupRegistrationService.cs`

- **VFS 抽象层**
  - 职责：统一 Local/SMB/FTP 的读写接口；FTP 递归列举采用应用层逐层遍历，并在异常子目录场景下记录 warning 后跳过，避免整次分析因单个慢目录超时失败；单文件信息查询在服务端返回异常路径元数据时会回退到调用方请求路径，避免分析链路把已存在目标误判为缺失；目录列举在服务端未返回 `FullName` 时会基于当前目录补全绝对路径，避免嵌套目录内已存在文件被误判为仅源端存在。
  - 目录：`Core/VFS`
  - 关键文件：`IFileSystem.cs`, `LocalFileSystem.cs`, `FtpFileSystem.cs`, `FileItem.cs`

- **过滤引擎**
  - 职责：白/黑名单并行评估，支持扩展名/大小/时间(hour)/正则，含冲突检测，并对空配置/空文件项做防御性处理。
  - 目录：`Core/Filters`
  - 关键文件：
    - `FilterEngine.cs`
    - `DualListFilterConfiguration.cs`
    - `FilterConflictDetector.cs`
    - `ExtensionFilter.cs`, `SizeFilter.cs`, `TimeFilter.cs`, `RegexFilter.cs`

- **Diff 引擎**
  - 职责：计算同步动作（Create/Update/Delete）。
  - 目录：`Core/Diff`
  - 关键文件：
    - `SizeAndTimeDiffStrategy.cs`
    - `ChecksumDiffStrategy.cs`（固定 xxHash64）
    - `IDiffStrategy.cs`, `SyncActionType.cs`, `SyncAction.cs`

- **同步执行器 / 分析服务**
  - 职责：执行实际复制/删除，收集进度与错误；双向模式使用 SQLite 基线可靠判定；单向一次性同步使用 SQLite 投递状态避免重复补发；分析与执行共享"结构感知"路径展开（父目录自动并入集合），保证目录结构完整。
  - 目录：`Core/Sync`
  - 关键文件：`SyncExecutor.cs`, `SyncReport.cs`, `SyncMode.cs`, `TwoWayStateStore.cs`, `OneWayDeliveryStateStore.cs`, `SyncTaskFactory.cs`, `TaskAnalysisService.cs`, `TaskAnalysisModels.cs`, `StructureAwarePathHelper.cs`

- **任务与设置持久化层**
  - 职责：任务定义与应用设置落盘（JSON），供 UI 与调度层使用；FTP 密码使用 Windows DPAPI 加密后保存。
  - 目录：`Core/Config`
  - 关键文件：`SyncTaskDefinition.cs`, `TaskRepository.cs`, `AppSettings.cs`, `SettingsRepository.cs`, `FtpCredentialProtector.cs`

- **调度层**
  - 职责：按 Cron 触发同步任务。
  - 目录：`Core/Scheduler`
  - 关键文件：`SchedulerManager.cs`, `SyncJob.cs`

- **报告层**
  - 职责：每次任务执行生成独立报告文件。
  - 目录：`Core/Reporting`
  - 关键文件：`SyncReportFileWriter.cs`

### 3.3 当前关键子树（详细）

```text
Core/
├─ VFS/
│  ├─ IFileSystem.cs
│  ├─ FileItem.cs
│  ├─ LocalFileSystem.cs
│  └─ FtpFileSystem.cs
├─ Filters/
│  ├─ IFilter.cs
│  ├─ FilterEngine.cs
│  ├─ ExtensionFilter.cs
│  ├─ SizeFilter.cs
│  ├─ TimeFilter.cs
│  ├─ RegexFilter.cs
│  ├─ DualListFilterConfiguration.cs
│  └─ FilterConflictDetector.cs
├─ Diff/
│  ├─ IDiffStrategy.cs
│  ├─ SyncActionType.cs
│  ├─ SyncAction.cs
│  ├─ SizeAndTimeDiffStrategy.cs
│  └─ ChecksumDiffStrategy.cs
├─ Sync/
│  ├─ SyncMode.cs
│  ├─ SyncExecutor.cs
│  ├─ SyncReport.cs
│  ├─ TwoWayStateStore.cs
│  ├─ OneWayDeliveryStateStore.cs
│  ├─ SyncTaskFactory.cs
│  ├─ TaskAnalysisService.cs
│  ├─ TaskAnalysisModels.cs
│  └─ StructureAwarePathHelper.cs
├─ Config/
│  ├─ SyncTaskDefinition.cs
│  ├─ TaskRepository.cs
│  ├─ AppSettings.cs
│  ├─ SettingsRepository.cs
│  └─ FtpCredentialProtector.cs
├─ Scheduler/
│  ├─ SchedulerManager.cs
│  └─ SyncJob.cs
└─ Reporting/
   └─ SyncReportFileWriter.cs

UI/
├─ Converters/
│  └─ InvertBooleanConverter.cs
├─ Localization/
│  ├─ LocalizationService.cs
│  ├─ Strings.zh-CN.xaml
│  └─ Strings.en-US.xaml
├─ Services/
│  ├─ TrayIconService.cs
│  └─ StartupRegistrationService.cs
├─ ViewModels/
│  ├─ ViewModelBase.cs
│  ├─ RelayCommand.cs
│  ├─ MainViewModel.cs
│  ├─ DashboardViewModel.cs
│  ├─ TasksViewModel.cs
│  ├─ TaskEditorViewModel.cs
│  ├─ TaskAnalysisViewModel.cs
│  ├─ LogsViewModel.cs
│  └─ SettingsViewModel.cs
└─ Views/
   ├─ DashboardView.xaml(.cs)
   ├─ TasksView.xaml(.cs)
   ├─ TaskEditorView.xaml(.cs)
   ├─ TaskAnalysisWindow.xaml(.cs)
   ├─ LogsView.xaml(.cs)
   └─ SettingsView.xaml(.cs)
```

---

## 4. 当前实现状态（简版里程碑）

1. 已完成：项目初始化、UI 骨架、VFS、过滤、Diff、Sync、Scheduler、Logs/Reports、任务/设置本地持久化。
2. 已完成：主框架与 Dashboard/Tasks/Logs/TaskEditor 视觉现代化（统一样式令牌、批量操作区层级优化、分析窗口可读性提升）。
3. 已完成：过滤规则升级为白/黑名单并行，时间粒度 hour，保存冲突提示。
4. 已完成（定时链路）：双向同步接入 SQLite 基线，支持可靠变更判定与默认冲突策略（保数据优先+时间优先）；手动分析/执行链路待打通，见第 7 节执行计划。
5. 已完成：Windows 托盘常驻，支持关闭窗口后隐藏到托盘继续运行定时任务，并可从托盘恢复或退出。
6. 已完成：当前用户级开机启动，设置页可写入或移除注册表 Run 项。
7. 已完成：开机自启动场景使用启动参数静默驻留托盘，手动启动仍正常显示主窗口。
8. 已完成：新增单向一次性同步模式，使用 SQLite 投递状态记录“已成功同步过”的文件，目标端后续删除不再补发，并支持任务级状态重置。
9. 已完成：FTP 支持匿名登录与账号密码登录；密码使用 Windows DPAPI 加密保存，执行、分析与调度链路可自动恢复凭据。
10. 已完成：任务编辑器支持 FTP 测试连接，可在保存前验证认证信息与基础路径是否有效，并具备 10 秒超时反馈；测试流程已移出 UI 线程，修复结果弹窗关闭后主窗口仍不可点击的问题。
11. 已完成：增强过滤引擎空值防护，避免任务在白/黑名单评估阶段因空引用导致全局执行失败；若后续仍有过滤异常，错误信息会包含触发问题的条目路径与名称。
12. 已完成：增强 FTP 分析链路稳定性，FTP 基础路径会先做 URL 解码；递归列举改为逐层遍历，若某个子目录超时，仅记录 warning 并跳过该子树，不再导致整次分析失败；目录列举会对瞬时失败做短重试后再决定是否跳过；单文件 `GetFileInfoAsync` 在服务端返回异常 `FullName` 时会回退到请求相对路径，修复真实 FTP `Local -> FTP` 场景下个别 `.heic` 文件被误判为“仅源端存在，需创建”的问题。
13. 已完成：当白名单/黑名单导致源端文件 0 命中过滤规则时，分析结果会追加明确提示，避免误判为同步链路故障。
14. 已完成：任务页批量分析/执行/同步会将重型分析与实际执行切到后台线程，避免 FTP 连接、递归列举或文件系统释放阻塞 UI 线程，导致主窗口按钮全部不可点击。
15. 已完成：单任务分析完成后会自动打开分析结果窗口，直接展示即将同步的文件列表；多任务批量分析完成后，会在状态文本中明确提示可通过任务左侧状态图标查看各自列表。
16. 已完成：分析结果窗口会汇总当前勾选为“需要同步”的文件数量与总同步数据量，且在用户手动勾选/取消后实时刷新，便于评估本次实际传输规模。
17. 已完成：每次运行日志会直接保存为 `log` 子文件夹中的文本文件（运行日志为 `.log`，任务报告为 `.txt`）；程序内不再提供日志内容查看窗口，而是在执行完成提示与日志页中展示文件名，供用户自行打开。
18. 已完成：日志页改为后台扫描 `log` 子文件夹，并按文件后缀直接区分运行日志与任务报告，避免在日志较多时点击“运行报告”阻塞 UI 主线程导致界面假死。
19. 已完成：应用启动和日志页入口都会主动确保 `log` 子文件夹存在，并增加 WPF Dispatcher / AppDomain / TaskScheduler 级别的全局未处理异常落日志能力，便于继续定位“点击运行报告后退出”的问题。
20. 已完成：修复日志页右侧 `TextBox.Text` 对只读属性默认使用 TwoWay 绑定导致的未处理异常；相关路径展示改为显式 `Mode=OneWay`，避免点击“运行报告”立即崩溃退出。
21. 已完成：分析结果窗口点击“执行已勾选项”时，实际执行、报告落盘和状态保存会切到后台线程，避免 `GetAwaiter().GetResult()` 阻塞 UI 主线程导致整个程序假死。
22. 已完成：分析结果窗口首次打开与点击“重新分析”都会走后台异步加载，并显示“正在分析”遮罩；加载期间禁用列表与操作按钮，避免再次误判为程序卡死。
23. 已完成：任务页批量分析/执行/同步与分析结果窗口中的分析/执行均支持“停止”按钮；停止后会通过 `CancellationToken` 中断当前操作，状态文案会切换为“正在停止...”。
24. 已完成：当用户停止同步时，如果当前文件正处于传输中，复制层会捕获取消并主动删除目标端未传输完整的文件，避免残留半截文件。
25. 已完成：分析与同步改为“结构感知”策略。若某个文件命中过滤规则，其所有父目录会自动并入分析/同步集合；目标端仅按完整相对路径匹配，不会把根目录下的同名文件误当成子目录内文件的匹配项；执行顺序也会保证先创建目录，再同步子文件。
26. 已完成：回滚分析阶段的当前文件实时提示与逐项进度回调，避免 WPF 频繁刷新对大目录/FTP 分析造成肉眼可见的性能损失；分析阶段恢复为仅显示总体状态文案与轻量进度条。
27. 已完成：修复 FTP 递归列举在部分服务端未返回条目 `FullName` 时的路径补全错误；嵌套目录中的目标文件会基于当前目录正确还原相对路径，避免 `Local -> FTP` 分析把 FTP 已存在文件误判为“仅源端存在，需创建”。
28. 待增强：双向冲突策略可扩展（例如保留双版本、副本命名策略、交互式冲突处理）。
29. 已完成：Dashboard 显示真实统计（活动任务数、今日同步文件数、今日失败任务数，均由后台线程扫描任务仓库与 `log` 报告汇总）；任务列表支持“立即运行”，点击后直接分析并执行该任务一次，不受调度计划与手动触发标记影响。

---

## 5. Git 约束

1. 每次对代码修改后，必须 `git commit` 并附本次 message。

---

## 6. 交付约束

1. 每次对话结束后，如果涉及代码修改，必须生成 portable exe 文件，并按版本号递增 `0.0.1`。

---

## 7. 已知技术债与改进执行计划

> 后续负责局部修改代码的 LLM 必须先阅读本节与执行计划，再开始编码。

当前代码审查发现的主要技术债包括：应用启动后未从 `tasks.json` 恢复定时任务、UI 线程存在 `.GetAwaiter().GetResult()` 阻塞、Local/FTP 路径包含检查不严谨、复制失败可能残留半截文件、缺少源/目标路径重叠保护、缺少单实例互斥、JSON 仓库写入非原子、旧 `SyncExecutor`/FTP 连接未释放、双向同步手动链路与 SQLite 基线脱节、日志保留天数未生效等。

详细问题清单与逐文件修复步骤：
- 审查报告：`.trae/documents/code-review-2026-08-16.md`
- 执行计划：`.trae/documents/improvement-execution-plan-2026-08-16.md`

执行顺序硬性要求：
1. 按 `P0 -> P1 -> P2` 顺序执行；每个任务完成必须 `dotnet build -c Release` 并单独 `git commit`。
2. 全部 P0/P1 完成后，版本号从 `0.0.54` 递增到 `0.0.55` 并发布 portable exe。
3. 新增/移动文件时，必须同步更新本文件第 3 节目录树。
