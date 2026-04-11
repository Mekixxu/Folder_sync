# Folder Sync 程序架构与技术栈分析

## 1. 需求分析总结

根据您的要求，我们需要开发一个运行在 Windows 平台上的高级文件夹同步工具。核心需求可拆解为以下几个维度：

*   **存储协议支持**：本地路径 (Local)、FTP、SMB (Server Message Block)。
*   **同步模式**：
    *   单向同步（A -> B，仅新增）。
    *   单向同步（A -> B，包含修改）。
    *   双向同步（A <-> B，保持完全一致，包括新增、修改、删除）。
*   **差异比对 (Diff) 机制**：
    *   基于文件大小 (Size-based) - 速度快。
    *   基于校验和 (Checksum-based, 如 MD5/SHA-256) - 准确度高。
*   **过滤规则 (黑/白名单)**：
    *   文件扩展名、文件大小、文件路径、文件名（正则）。
    *   时间属性：文件夹新于 X 天，文件新于 X 天。
*   **调度系统**：支持按秒、分、小时、天、周、月等周期执行的计划任务。
*   **日志与报告**：每次同步生成详细的执行报告和日志。
*   **UI 用户界面**：必须提供现代化的图形用户界面（GUI），以便用户可以直观地管理同步任务、查看进度和配置规则。

---

## 2. 技术栈备选方案分析

考虑到该程序需要在 Windows 平台上运行，且涉及大量文件 I/O、并发处理、网络协议通信以及定时任务，以下是三种主流技术栈的对比：

### 方案 A：C# / .NET 8 (强烈推荐)
作为 Windows 平台的“一等公民”，C# 在开发 Windows 桌面应用和后台服务方面具有天然优势。
*   **优势**：
    *   **完美契合 Windows**：对 SMB (通过 UNC 路径 `\\server\share` 或 `SMBLibrary`) 支持极佳。
    *   **丰富的生态**：拥有极其成熟的调度库 (`Quartz.NET`)、FTP 库 (`FluentFTP`) 和日志库 (`Serilog`)。
    *   **UI 开发便捷**：使用 WPF 构建现代化的 Windows 客户端，完美契合必须带有 UI 的需求。
    *   **性能优异**：.NET 8 的 I/O 性能和并发 (async/await) 处理能力非常强。
*   **劣势**：跨平台 UI 稍弱（但本需求明确针对 Windows，因此无影响）。

### 方案 B：Golang (备选 - 适合作为高性能 CLI/后台服务)
Go 语言以其超强的并发性能和极低的资源占用著称。
*   **优势**：极致的并发性能、单文件部署、强大的标准库。
*   **劣势**：开发原生 Windows GUI 比较困难（通常需要配合 Web 前端如 Wails/Tauri，增加系统复杂性）。

### 方案 C：Python (适合快速原型验证)
*   **优势**：开发速度极快，代码量少。
*   **劣势**：性能瓶颈明显；打包为 `.exe` 后体积较大且启动相对较慢；GUI 开发（如 PyQt/Tkinter）不如 WPF 现代化。

---

## 3. 最终推荐技术栈：C# / .NET 8 (WPF)

综合考虑 **Windows 平台兼容性**、**生态完善度** 以及 **强 UI 需求**，强烈推荐使用 **C# / .NET 8 + WPF**。

### 核心依赖库选型：
1.  **核心框架**：`.NET 8.0` (WPF Application)。
2.  **UI 框架**：`MaterialDesignThemes` (提供现代化的 UI 控件和 Fluent 风格设计)。
3.  **文件系统抽象**：
    *   **Local & SMB**：使用原生 `System.IO`（Windows 原生支持 `\\IP\Share` 格式的 SMB 路径）。
    *   **FTP**：`FluentFTP`（目前 .NET 生态中最强大、最稳定的 FTP/FTPS 客户端）。
4.  **任务调度**：`Quartz.NET`（支持 Cron 表达式，完美满足按秒、分、时、天、周、月调度的需求）。
5.  **日志与报告**：`Serilog`（结构化日志，可轻松输出到文件、控制台，甚至生成 JSON/HTML 报告）。
6.  **差异比对与哈希**：原生 `System.Security.Cryptography` (支持高效的 MD5, SHA256 流式计算)。

---

## 4. 核心模块架构设计

### 4.1 UI 交互层 (Presentation Layer)
采用 MVVM (Model-View-ViewModel) 架构，通过 WPF 渲染界面，分离界面逻辑和业务逻辑。
*   **主控制台 (Dashboard)**：展示所有同步任务、状态、进度条和快捷操作。
*   **任务配置向导 (Task Editor)**：四步 Tab 引导（基础路径、调度配置、过滤规则、高级日志）。
*   **报告中心 (Reports)**：展示历史日志和详细的 Diff 差异报告。

### 4.2 虚拟文件系统抽象层 (VFS)
定义统一的 `IFileSystem` 接口，屏蔽 Local、FTP、SMB 的底层差异。
*   `IEnumerable<FileItem> ListFiles(string path)`
*   `Stream OpenRead(string path)`
*   `Stream OpenWrite(string path)`
*   `void Delete(string path)`

### 4.3 过滤引擎 (Filter Engine)
实现责任链模式或规则引擎，在获取文件列表后，通过配置的黑白名单规则过滤：
*   **正则匹配器**：根据文件名正则表达式放行/拦截。
*   **属性匹配器**：判断文件大小、扩展名。
*   **时间匹配器**：判断 `LastWriteTime` 或 `CreationTime`，筛选新于 X 天的文件/文件夹。

### 4.4 差异比对引擎 (Diff Engine)
负责对比源目录 (A) 和目标目录 (B) 的状态，生成同步执行计划（Action Plan）：
*   **快速比对**：仅比对文件路径和文件大小 (Size-based)。
*   **深度比对**：比对文件大小 + 计算文件 Hash (Checksum-based)。

### 4.5 同步执行器 (Sync Executor)
根据 Diff Engine 生成的执行计划，结合同步模式执行实际的 I/O 操作：
*   **单向增量** / **单向同步 (含修改)** / **双向同步** (引入 SQLite 记录状态)。

### 4.6 调度与触发器 (Scheduler)
基于 `Quartz.NET`，解析用户的调度配置，定时触发同步任务。

### 4.7 日志与报告器 (Reporter)
监听 Sync Executor 的事件，在每次同步任务结束后，汇总数据并写入日志文件。

---

## 5. 开发计划与里程碑

1.  **第一阶段：项目初始化与 UI 骨架**
    *   初始化 WPF / .NET 8 解决方案。
    *   引入 MaterialDesignThemes，搭建 Dashboard、任务编辑器、日志查看器的基本界面结构。
2.  **第二阶段：核心基础类库搭建**
    *   实现 `IFileSystem` 接口及 Local、FTP 的适配器。
    *   实现 Filter Engine (黑白名单机制)。
3.  **第三阶段：比对与同步逻辑**
    *   实现基于大小和校验和的 Diff Engine。
    *   实现单向同步（新增、修改）。
4.  **第四阶段：高级特性与双向同步**
    *   引入 SQLite 记录文件状态基线，实现安全的双向同步引擎。
5.  **第五阶段：调度与服务化**
    *   集成 Quartz.NET，实现任务调度，并与 UI 进度条绑定。
    *   集成 Serilog，完善日志和报告生成。
