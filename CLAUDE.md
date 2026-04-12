# Folder Sync 程序架构与技术栈分析

## 1. 需求分析总结

根据当前项目目标，程序运行在 Windows 平台，核心能力包含：

- **存储协议支持**：本地路径 (Local)、FTP、SMB (UNC 路径)。
- **同步模式**：
  - 单向增量（A -> B，仅新增）
  - 单向更新（A -> B，新增+修改）
  - 单向镜像（A -> B，新增+修改+删除）
  - 双向同步（已接入 SQLite 状态基线，支持可靠判定与冲突处理）
- **差异比对机制**：
  - 快速比对（文件大小+时间）
  - 哈希比对（当前固定采用 **xxHash64**，性能优先）
- **过滤规则**：白名单（包括）+黑名单（排除）双名单并行，支持扩展名、大小、时间、正则；时间粒度为 **hour**。
- **调度系统**：支持按秒、分、时、天、周、月。
- **日志与报告**：
  - 运行日志：按启动实例生成文件
  - 任务报告：每次任务执行独立落盘，且文件名避免并发重名
- **UI**：WPF + MaterialDesign，MVVM 架构（已完成主框架与 Dashboard/Tasks/Logs/TaskEditor 的现代化布局重构）。
- **国际化与显示**：支持中文/英文切换，支持字体与界面缩放配置并持久化。

---

## 2. 最终技术栈（已落地）

- **框架**：.NET 8 + WPF
- **UI**：MaterialDesignThemes
- **协议层**：System.IO（Local/SMB）+ FluentFTP（FTP）
- **调度**：Quartz.NET
- **日志**：Serilog
- **哈希**：System.IO.Hashing（xxHash64）

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
├─ .trae/specs/
└─ publish/
```

### 3.2 模块到目录映射

- **UI 层（MVVM）**
  - 职责：导航、页面交互、任务编辑、日志展示、设置。
  - 目录：`UI/Views`, `UI/ViewModels`, `UI/Converters`, `UI/Localization`
  - 关键文件：
    - `MainWindow.xaml` / `MainViewModel.cs`
    - `DashboardView*`, `TasksView*`, `TaskEditorView*`, `LogsView*`, `SettingsView*`

- **VFS 抽象层**
  - 职责：统一 Local/SMB/FTP 的读写接口。
  - 目录：`Core/VFS`
  - 关键文件：`IFileSystem.cs`, `LocalFileSystem.cs`, `FtpFileSystem.cs`, `FileItem.cs`

- **过滤引擎**
  - 职责：白/黑名单并行评估，支持扩展名/大小/时间(hour)/正则，含冲突检测。
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

- **同步执行器**
  - 职责：执行实际复制/删除，收集进度与错误；双向模式使用 SQLite 基线可靠判定。
  - 目录：`Core/Sync`
  - 关键文件：`SyncExecutor.cs`, `SyncReport.cs`, `SyncMode.cs`, `TwoWayStateStore.cs`, `SyncTaskFactory.cs`

- **任务与设置持久化层**
  - 职责：任务定义与应用设置落盘（JSON），供 UI 与调度层使用。
  - 目录：`Core/Config`
  - 关键文件：`SyncTaskDefinition.cs`, `TaskRepository.cs`, `AppSettings.cs`, `SettingsRepository.cs`

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
│  └─ SyncTaskFactory.cs
├─ Config/
│  ├─ SyncTaskDefinition.cs
│  ├─ TaskRepository.cs
│  ├─ AppSettings.cs
│  └─ SettingsRepository.cs
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
├─ ViewModels/
│  ├─ ViewModelBase.cs
│  ├─ RelayCommand.cs
│  ├─ MainViewModel.cs
│  ├─ DashboardViewModel.cs
│  ├─ TasksViewModel.cs
│  ├─ TaskEditorViewModel.cs
│  ├─ LogsViewModel.cs
│  └─ SettingsViewModel.cs
└─ Views/
   ├─ DashboardView.xaml(.cs)
   ├─ TasksView.xaml(.cs)
   ├─ TaskEditorView.xaml(.cs)
   ├─ LogsView.xaml(.cs)
   └─ SettingsView.xaml(.cs)
```

---

## 4. 当前实现状态（简版里程碑）

1. 已完成：项目初始化、UI 骨架、VFS、过滤、Diff、Sync、Scheduler、Logs/Reports、任务/设置本地持久化。
2. 已完成：主框架与 Dashboard/Tasks/Logs/TaskEditor 视觉现代化（统一样式令牌、批量操作区层级优化、分析窗口可读性提升）。
3. 已完成：过滤规则升级为白/黑名单并行，时间粒度 hour，保存冲突提示。
4. 已完成：双向同步接入 SQLite 基线，支持可靠变更判定与默认冲突策略（保数据优先+时间优先）。
5. 待增强：双向冲突策略可扩展（例如保留双版本、副本命名策略、交互式冲突处理）。

---

## 5. Git 约束

1. 每次对代码修改后，必须 `git commit` 并附本次 message。

---

## 6. 交付约束

1. 每次对话结束后，如果涉及代码修改，必须生成 portable exe 文件，并按版本号递增 `0.0.1`。
