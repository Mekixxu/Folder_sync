﻿﻿﻿# Folder Sync 程序架构与技术栈分析

## 1. 需求分析总结

根据当前项目目标，程序运行在 Windows 平台，核心能力包含：

- **存储协议支持**：本地路径 (Local)、FTP、SMB (UNC 路径)；FTP 支持匿名登录与账号密码登录。
- **同步模式**：
  - 单向增量（A -> B，仅新增）
  - 单向更新（A -> B，新增+修改）
  - 单向一次性同步（A -> B，每个文件仅首次成功同步；目标端后续删除也不补发）
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
- **UI**：WPF + MaterialDesign，MVVM 架构（已完成主框架与 Dashboard/Tasks/Logs/TaskEditor 的现代化布局重构，FTP 任务编辑支持测试连接、10 秒超时反馈，并修复测试结果弹窗关闭后主窗口卡住的问题）。
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
└─ publish/
```

### 3.2 模块到目录映射

- **UI 层（MVVM）**
  - 职责：导航、页面交互、任务编辑、日志展示、设置、托盘文案与开机启动配置联动。
  - 目录：`UI/Views`, `UI/ViewModels`, `UI/Converters`, `UI/Localization`, `UI/Services`
  - 关键文件：
    - `MainWindow.xaml` / `MainViewModel.cs`
    - `DashboardView*`, `TasksView*`, `TaskEditorView*`, `LogsView*`, `SettingsView*`
    - `TrayIconService.cs`, `StartupRegistrationService.cs`

- **应用壳层 / 生命周期**
  - 职责：应用启动与退出、日志初始化、调度器生命周期、启动参数解析、主窗口关闭拦截与托盘常驻行为。
  - 目录：项目根目录 + `UI/Services`
  - 关键文件：
    - `App.xaml` / `App.xaml.cs`
    - `MainWindow.xaml` / `MainWindow.xaml.cs`
    - `UI/Services/TrayIconService.cs`, `UI/Services/StartupRegistrationService.cs`

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
  - 职责：执行实际复制/删除，收集进度与错误；双向模式使用 SQLite 基线可靠判定；单向一次性同步使用 SQLite 投递状态避免重复补发。
  - 目录：`Core/Sync`
  - 关键文件：`SyncExecutor.cs`, `SyncReport.cs`, `SyncMode.cs`, `TwoWayStateStore.cs`, `OneWayDeliveryStateStore.cs`, `SyncTaskFactory.cs`

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
│  └─ SyncTaskFactory.cs
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
5. 已完成：Windows 托盘常驻，支持关闭窗口后隐藏到托盘继续运行定时任务，并可从托盘恢复或退出。
6. 已完成：当前用户级开机启动，设置页可写入或移除注册表 Run 项。
7. 已完成：开机自启动场景使用启动参数静默驻留托盘，手动启动仍正常显示主窗口。
8. 已完成：新增单向一次性同步模式，使用 SQLite 投递状态记录“已成功同步过”的文件，目标端后续删除不再补发，并支持任务级状态重置。
9. 已完成：FTP 支持匿名登录与账号密码登录；密码使用 Windows DPAPI 加密保存，执行、分析与调度链路可自动恢复凭据。
10. 已完成：任务编辑器支持 FTP 测试连接，可在保存前验证认证信息与基础路径是否有效，并具备 10 秒超时反馈；测试流程已移出 UI 线程，修复结果弹窗关闭后主窗口仍不可点击的问题。
11. 待增强：双向冲突策略可扩展（例如保留双版本、副本命名策略、交互式冲突处理）。

---

## 5. Git 约束

1. 每次对代码修改后，必须 `git commit` 并附本次 message。

---

## 6. 交付约束

1. 每次对话结束后，如果涉及代码修改，必须生成 portable exe 文件，并按版本号递增 `0.0.1`。
