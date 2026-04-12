# 任务页批量分析交互重构 Spec

## Why
当前“每任务点击 Analyze 弹窗”的流程与目标工作流不一致，缺少批量分析、统一执行入口以及可见进度反馈，导致操作效率与可控性不足。

## What Changes
- 将任务级 Analyze/Run 改为顶部统一按钮：`分析`、`执行`、`同步`。
- 在任务列表为每个任务增加复选框，支持多选后统一操作。
- 点击统一`分析`后，不立即打开分析窗口，而是在任务列表区域展示分析进度条与完成提示。
- 为每个任务增加分析状态图标：未完成显示问号，完成显示 ✅；点击 ✅ 打开该任务分析窗口。
- 分析窗口新增`保存`按钮，允许保存用户手动调整后的“是否同步”结果。
- 将每个任务的编辑和删除收纳到“竖向三点菜单”中。

## Impact
- Affected specs: 任务管理页面交互、分析与执行工作流、分析结果持久化
- Affected code: `UI/Views/TasksView.xaml`, `UI/ViewModels/TasksViewModel.cs`, `UI/Views/TaskAnalysisWindow.xaml`, `UI/ViewModels/TaskAnalysisViewModel.cs`, `Core/Sync/TaskAnalysisService.cs`, `Core/Config/*`

## ADDED Requirements
### Requirement: 批量分析与统一执行
系统 SHALL 支持用户多选任务后，通过统一按钮完成批量分析与批量执行。

#### Scenario: 多选统一分析
- **WHEN** 用户在任务列表勾选多个任务并点击统一`分析`
- **THEN** 系统对勾选任务依次分析并显示进度条
- **AND** 分析完成后显示“分析完成”提示

#### Scenario: 多选统一执行
- **WHEN** 用户在任务列表勾选多个任务并点击统一`执行`
- **THEN** 系统基于已保存或最新分析结果执行被勾选任务

#### Scenario: 多选统一同步（分析+执行）
- **WHEN** 用户在任务列表勾选多个任务并点击统一`同步`
- **THEN** 系统先对勾选任务执行分析
- **AND** 分析完成后直接执行同步
- **AND** 中间不打开分析窗口，也不等待手动修改

### Requirement: 任务分析状态可视化
系统 SHALL 在任务列表中展示每个任务的分析状态图标，并支持通过状态图标进入分析详情。

#### Scenario: 状态图标变化
- **WHEN** 任务尚未分析
- **THEN** 显示问号图标
- **WHEN** 任务分析完成
- **THEN** 显示 ✅ 图标

#### Scenario: 点击完成图标查看分析
- **WHEN** 用户点击某任务的 ✅ 图标
- **THEN** 打开该任务分析窗口

### Requirement: 分析结果可保存
系统 SHALL 提供分析窗口保存能力，将用户手动修改后的“是否同步”结果持久化。

#### Scenario: 保存手动调整
- **WHEN** 用户在分析窗口修改若干文件的“是否同步”并点击`保存`
- **THEN** 系统持久化该分析结果
- **AND** 后续执行优先使用该已保存结果（若未重新分析）

## MODIFIED Requirements
### Requirement: 任务页操作入口
系统 SHALL 将任务级 Analyze/Run 按钮改为列表上方统一按钮，并保留任务级查看分析入口（通过状态图标）。

#### Scenario: 任务级按钮收敛
- **WHEN** 用户查看任务卡片
- **THEN** 不再显示独立 Analyze/Run 按钮
- **AND** 显示复选框、分析状态图标、三点菜单
- **AND** 顶部统一按钮包含 `分析`、`执行`、`同步`

### Requirement: 任务项管理操作布局
系统 SHALL 将编辑和删除操作收纳在竖向三点菜单中。

#### Scenario: 菜单触发编辑删除
- **WHEN** 用户点击任务项三点菜单
- **THEN** 可选择编辑或删除

## REMOVED Requirements
### Requirement: 任务级即时弹出分析窗口
**Reason**: 与“先批量分析再按需查看结果”的目标流程冲突。
**Migration**: 分析窗口改由点击任务分析完成图标（✅）打开。
