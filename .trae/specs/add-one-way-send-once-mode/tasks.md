# Tasks
- [x] Task 1: 新增 `单向一次性同步` 模式定义与任务配置字段接线。
  - [x] SubTask 1.1: 在 `SyncMode` 中新增模式枚举，并补充 UI 可选项与说明文字。
  - [x] SubTask 1.2: 保证旧任务与旧模式行为保持兼容，不影响 `单向增量 / 更新 / 镜像 / 双向`。

- [x] Task 2: 设计并实现一次性同步状态存储。
  - [x] SubTask 2.1: 新增任务级 SQLite 状态存储（建议 `OneWayDeliveryStateStore.cs`）。
  - [x] SubTask 2.2: 定义状态表结构，至少包含 `task_id`, `relative_path`, `source_size`, `source_last_write_utc`, `source_hash`, `delivered_utc`。
  - [x] SubTask 2.3: 提供按任务读取、写入、重置状态的 API。

- [x] Task 3: 改造同步执行器，实现“一次性投递后不补发”。
  - [x] SubTask 3.1: 在一次性模式下，执行前加载该任务的已投递状态。
  - [x] SubTask 3.2: 仅对未成功投递过的文件生成复制动作。
  - [x] SubTask 3.3: 复制成功后写入状态；复制失败不记账。
  - [x] SubTask 3.4: 同路径内容变化时默认跳过并产生日志/报告 warning。

- [x] Task 4: 改造分析与报告展示。
  - [x] SubTask 4.1: 在分析结果中展示“已同步过，按一次性规则跳过”。
  - [x] SubTask 4.2: 在目标端缺失但已投递过时，仍显示跳过原因而不是待创建。
  - [x] SubTask 4.3: 在运行报告中增加“已同步过跳过”统计项。

- [x] Task 5: 增加任务级维护操作。
  - [x] SubTask 5.1: 在任务列表或任务详情中增加“一次性同步状态重置”入口。
  - [x] SubTask 5.2: 增加确认提示，防止误清空状态。
  - [x] SubTask 5.3: 重置后允许历史文件重新投递。

- [x] Task 6: 验证与回归。
  - [x] SubTask 6.1: 验证首次同步成功后会写入状态。
  - [x] SubTask 6.2: 验证 B 端删除后不会补发。
  - [x] SubTask 6.3: 验证复制失败不会记账且下次会重试。
  - [x] SubTask 6.4: 验证重置状态后允许重新同步。
  - [x] SubTask 6.5: 验证旧同步模式行为不受影响。

# Task Dependencies
- Task 2 depends on Task 1
- Task 3 depends on Task 1 and Task 2
- Task 4 depends on Task 3
- Task 5 depends on Task 2 and Task 4
- Task 6 depends on Task 1, Task 2, Task 3, Task 4, Task 5
