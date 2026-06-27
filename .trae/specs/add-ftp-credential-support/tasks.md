# Tasks
- [x] Task 1: 扩展任务配置模型，支持源端与目标端分别保存 FTP 认证配置。
  - [x] SubTask 1.1: 为源端和目标端增加 FTP 认证方式字段。
  - [x] SubTask 1.2: 为源端和目标端增加 FTP 用户名字段。
  - [x] SubTask 1.3: 为源端和目标端增加加密密码字段。
  - [x] SubTask 1.4: 保证旧任务加载时默认迁移为匿名 FTP。

- [x] Task 2: 新增 Windows DPAPI 凭据保护服务。
  - [x] SubTask 2.1: 实现密码加密与解密封装。
  - [x] SubTask 2.2: 明确使用 `CurrentUser` 作用域保存密码。
  - [x] SubTask 2.3: 为解密失败场景提供可理解的异常提示。

- [x] Task 3: 改造任务编辑器 FTP UI 与保存校验。
  - [x] SubTask 3.1: 在 FTP 协议下显示认证方式、用户名、密码输入控件。
  - [x] SubTask 3.2: 校验账号密码登录模式下的必填项。
  - [x] SubTask 3.3: 拦截 URL 中内嵌凭据的 FTP 路径。
  - [x] SubTask 3.4: 保存任务时对密码进行加密后落盘。

- [x] Task 4: 改造 FTP 文件系统创建逻辑。
  - [x] SubTask 4.1: `SyncTaskFactory` 读取任务中的 FTP 认证配置。
  - [x] SubTask 4.2: 根据匿名/账号密码模式创建 `FtpFileSystem`。
  - [x] SubTask 4.3: 移除固定 `anonymous / anonymous@` 的硬编码行为。

- [x] Task 5: 验证执行、分析和调度链路。
  - [x] SubTask 5.1: 验证任务手动分析时能使用保存的 FTP 凭据连接。
  - [x] SubTask 5.2: 验证手动执行时能使用保存的 FTP 凭据连接。
  - [x] SubTask 5.3: 验证 Quartz 定时触发时能自动恢复凭据并连接。

- [x] Task 6: 回归与兼容验证。
  - [x] SubTask 6.1: 验证匿名 FTP 任务仍可正常运行。
  - [x] SubTask 6.2: 验证历史仅路径 FTP 任务自动兼容为匿名登录。
  - [x] SubTask 6.3: 验证 `tasks.json` 中不出现明文密码。
  - [x] SubTask 6.4: 验证错误提示文案与 UI 显示逻辑。

# Task Dependencies
- Task 2 depends on Task 1
- Task 3 depends on Task 1 and Task 2
- Task 4 depends on Task 1 and Task 2
- Task 5 depends on Task 3 and Task 4
- Task 6 depends on Task 3, Task 4, Task 5
