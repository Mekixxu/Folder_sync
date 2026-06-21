# 单向一次性同步 Spec

## Why
当前单向同步模式始终基于 A/B 当下状态做差异比较，无法表达“某文件只允许成功同步一次”的业务语义。对于“源端 A 持续新增、目标端 B 允许人工删除且不应补发”的场景，现有 `单向增量` 会在 B 端删除后再次复制，和目标流程冲突。

## What Changes
- 新增独立同步模式：`单向一次性同步`。
- 为该模式新增任务级“已投递状态库”，记录每个文件是否已成功同步过一次。
- 执行时以“是否已成功投递”而不是“B 当前是否存在”作为是否需要复制的主判据。
- 分析窗口与执行报告新增“已同步过，按一次性规则跳过”的可见原因。
- 新增任务级维护能力：可重置一次性同步状态，以便人工重发。

## Impact
- Affected specs: 同步模式定义、单向同步执行逻辑、分析结果解释、任务维护操作、运行报告
- Affected code: `Core/Sync/SyncMode.cs`, `Core/Sync/SyncExecutor.cs`, `Core/Sync/TaskAnalysisService.cs`, `Core/Config/SyncTaskDefinition.cs`, `Core/Sync/SyncTaskFactory.cs`, `UI/ViewModels/TaskEditorViewModel.cs`, `UI/Views/TaskEditorView.xaml`, `UI/ViewModels/TasksViewModel.cs`, `Core/Reporting/*`

## ADDED Requirements
### Requirement: 单向一次性同步模式
系统 SHALL 提供独立的 `单向一次性同步` 模式，用于保证源文件在某任务下最多只被成功同步一次。

#### Scenario: 首次发现新文件时同步
- **GIVEN** 任务模式为 `单向一次性同步`
- **AND** 源端 A 存在文件 `x`
- **AND** 该任务的状态库中不存在 `x` 的成功投递记录
- **WHEN** 任务执行
- **THEN** 系统将 `x` 从 A 同步到 B
- **AND** 复制成功后写入 `x` 的成功投递记录

#### Scenario: 目标端删除后不补发
- **GIVEN** 任务模式为 `单向一次性同步`
- **AND** 文件 `x` 已有成功投递记录
- **AND** B 端当前不存在 `x`
- **WHEN** 任务再次执行
- **THEN** 系统不得再次同步 `x`
- **AND** 系统将该项标记为“已同步过，按一次性规则跳过”

### Requirement: 成功后记账
系统 SHALL 仅在文件实际复制成功后写入成功投递记录。

#### Scenario: 复制失败不记账
- **GIVEN** 文件 `x` 尚无成功投递记录
- **WHEN** 同步 `x` 的过程中发生异常
- **THEN** 系统不得写入 `x` 的成功投递记录
- **AND** 后续任务仍允许重新尝试同步 `x`

### Requirement: 一次性同步状态存储
系统 SHALL 为 `单向一次性同步` 模式维护独立的任务级状态存储，用于记录已成功投递的文件。

#### Scenario: 状态存储主键
- **WHEN** 系统记录某文件的一次性同步状态
- **THEN** 记录至少使用 `task_id + relative_path` 作为唯一标识
- **AND** 同时保存源端快照信息以支持审计和异常诊断

#### Scenario: 建议保存字段
- **WHEN** 系统写入成功投递记录
- **THEN** 记录包含 `relative_path`
- **AND** 包含 `source_size`
- **AND** 包含 `source_last_write_utc`
- **AND** 包含 `source_hash`
- **AND** 包含 `delivered_utc`

### Requirement: 同路径内容变化的默认策略
系统 SHALL 将“同一相对路径已投递过，但源内容后来变化”视为异常场景，并默认保持“一次性投递”优先。

#### Scenario: 同路径内容变化时默认跳过
- **GIVEN** 文件 `x` 已有成功投递记录
- **AND** 当前 A 端同一路径 `x` 的快照与历史记录不同
- **WHEN** 任务执行
- **THEN** 系统默认仍跳过 `x`
- **AND** 在分析结果、日志或报告中标记 warning
- **AND** 不自动重新投递

### Requirement: 分析结果可解释
系统 SHALL 在分析窗口和执行前结果中明确展示“一次性同步”导致的跳过原因。

#### Scenario: 已投递记录导致跳过
- **WHEN** 某文件因已有成功投递记录而不参与同步
- **THEN** 分析结果中显示“已同步过，按一次性规则跳过”

#### Scenario: 目标端缺失但仍跳过
- **WHEN** B 端文件已被删除，但该文件已有成功投递记录
- **THEN** 分析结果中仍显示“已同步过，按一次性规则跳过”
- **AND** 不把该项解释为普通缺失待创建

### Requirement: 任务级状态重置
系统 SHALL 提供任务级“一次性同步状态重置”能力，允许人工清空某任务的成功投递记录。

#### Scenario: 重置后允许重新投递
- **GIVEN** 某任务已存在若干成功投递记录
- **WHEN** 用户执行该任务的“一次性同步状态重置”
- **THEN** 系统清空该任务的一次性同步状态
- **AND** 下次任务运行时可重新同步这些文件

## MODIFIED Requirements
### Requirement: 单向增量模式语义
系统 SHALL 保持现有 `单向增量` 语义不变，不将“一次性投递”行为隐式并入 `单向增量`。

#### Scenario: 保持旧模式兼容
- **WHEN** 用户选择 `单向增量`
- **THEN** 系统仍按“仅根据当前 A/B 差异判断新增文件”执行
- **AND** 不读取一次性同步状态库

### Requirement: 运行报告统计
系统 SHALL 在报告中区分“成功同步”和“因已投递记录而跳过”。

#### Scenario: 报告包含已投递跳过数
- **WHEN** 一次性同步任务执行完成
- **THEN** 运行报告中包含“已同步过跳过”的统计项

## DESIGN DECISIONS
### Decision: 模式名称
- 建议名称：`单向一次性同步`
- 原因：比“投递”更贴近当前产品语境，也比复用 `单向增量` 更不易歧义

### Decision: 记录主键
- 采用 `task_id + relative_path` 作为唯一键
- 说明：满足当前业务“源端只新增”的主路径识别需求，同时保留快照字段供审计

### Decision: 同路径变化默认行为
- 默认策略：跳过并告警，不自动重发
- 原因：优先满足“所有文件都只需要同步一次”的核心目标
