# FTP 账号密码登录与安全保存 Spec

## Why
当前 FTP 连接固定使用匿名登录，任务编辑器也没有用户名、密码输入入口，因此无法连接需要认证的 FTP 目录，也无法支持需要长期自动执行的定时任务场景。

## What Changes
- 为 FTP 源目录和目标目录增加认证方式配置：`匿名登录` / `账号密码登录`。
- 为 FTP 连接增加用户名与密码输入能力。
- 密码不允许明文落盘，也不通过 `ftp://user:pass@host/path` 形式嵌入 URL。
- 当用户选择保存密码时，使用 Windows DPAPI 按当前用户加密后落盘。
- 任务执行、分析、调度运行时统一从任务配置中恢复 FTP 凭据。
- 保持匿名 FTP 场景向后兼容。

## Impact
- Affected specs: 任务编辑器 FTP 配置、任务持久化结构、FTP 文件系统创建逻辑、调度任务运行、错误提示
- Affected code: `UI/Views/TaskEditorView.xaml`, `UI/ViewModels/TaskEditorViewModel.cs`, `Core/Config/SyncTaskDefinition.cs`, `Core/Config/TaskRepository.cs`, `Core/Sync/SyncTaskFactory.cs`, `Core/VFS/FtpFileSystem.cs`

## ADDED Requirements
### Requirement: FTP 认证方式选择
系统 SHALL 在 FTP 协议下为每一端目录提供认证方式选择，并支持匿名登录和账号密码登录。

#### Scenario: 选择匿名登录
- **WHEN** 用户将某一端协议选择为 `FTP`
- **AND** 认证方式选择为 `匿名登录`
- **THEN** 系统不要求输入用户名和密码
- **AND** 运行时使用匿名凭据连接 FTP

#### Scenario: 选择账号密码登录
- **WHEN** 用户将某一端协议选择为 `FTP`
- **AND** 认证方式选择为 `账号密码登录`
- **THEN** 系统要求输入用户名和密码
- **AND** 未填写完整时不得保存任务

### Requirement: FTP 密码安全保存
系统 SHALL 在用户选择保存 FTP 密码时，使用 Windows DPAPI 对密码加密后再落盘。

#### Scenario: 保存密码时加密落盘
- **WHEN** 用户输入 FTP 密码并保存任务
- **THEN** 系统不得将明文密码写入 `tasks.json`
- **AND** 只允许保存加密后的密码内容

#### Scenario: 运行时解密使用
- **WHEN** 任务执行、分析或调度触发 FTP 连接
- **THEN** 系统从已保存的加密字段中解密出密码
- **AND** 将解密结果传递给 FTP 客户端

### Requirement: 禁止 URL 内嵌凭据
系统 SHALL 禁止将 FTP 用户名和密码嵌入路径字符串中。

#### Scenario: 检测到 URL 中包含凭据
- **WHEN** 用户输入 `ftp://user:password@host/path`
- **THEN** 系统阻止保存
- **AND** 提示“请在认证字段中填写账号密码，不要在 URL 中内嵌凭据”

### Requirement: 定时任务可用
系统 SHALL 使使用账号密码登录的 FTP 任务能够在无人值守情况下正常运行。

#### Scenario: 已保存密码的定时任务
- **GIVEN** FTP 任务已保存加密密码
- **WHEN** Quartz 调度器触发任务执行
- **THEN** 系统能够自动恢复凭据并完成连接

### Requirement: 兼容历史匿名 FTP 任务
系统 SHALL 保持现有仅填写 FTP 路径的历史任务可继续运行。

#### Scenario: 历史 FTP 任务迁移
- **GIVEN** 旧任务仅保存了 FTP 路径
- **WHEN** 系统读取该任务
- **THEN** 默认将其视为 `匿名登录`
- **AND** 不要求额外补充字段即可继续执行

## MODIFIED Requirements
### Requirement: FTP 连接创建逻辑
系统 SHALL 根据任务保存的 FTP 认证配置创建 `FtpFileSystem`，不再固定使用匿名账号。

#### Scenario: 使用保存的用户名密码
- **WHEN** 某一端 FTP 配置为账号密码登录
- **THEN** `SyncTaskFactory` 使用该端保存的用户名和解密后的密码创建 `FtpFileSystem`

### Requirement: 任务配置模型
系统 SHALL 扩展任务配置模型，以分别保存源端和目标端的 FTP 认证配置。

#### Scenario: 分别保存源端与目标端凭据
- **WHEN** 源端和目标端都使用 FTP
- **THEN** 系统分别保存各自的认证方式、用户名和加密密码
- **AND** 两端配置互不影响

## DESIGN DECISIONS
### Decision: 认证信息与路径分离
- FTP 路径继续只表示 `ftp://host[:port]/basePath`
- 用户名、密码使用独立字段保存
- 原因：更易做校验、显示脱敏和安全存储

### Decision: 密码保存策略
- 使用 Windows DPAPI（`DataProtectionScope.CurrentUser`）加密保存
- 原因：适合本项目仅运行在 Windows 的前提，且不需要额外密钥管理

### Decision: 是否支持“不保存密码”
- 本阶段不单独增加“仅本次会话使用，不保存密码”模式
- 原因：会显著增加交互复杂度，且与定时任务无人值守需求冲突
- 当前聚焦支持两种模式：`匿名登录` 和 `账号密码登录（加密保存）`
