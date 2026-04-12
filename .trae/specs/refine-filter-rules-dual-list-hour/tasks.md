# Tasks
- [x] Task 1: 重构过滤规则数据模型与保存结构，支持白名单与黑名单并行且可同时配置。
  - [x] SubTask 1.1: 设计并落地白/黑名单统一的二级规则结构（扩展名、大小、时间、正则）
  - [x] SubTask 1.2: 增加历史配置兼容与迁移（“全部允许”迁移为空白/空黑）

- [x] Task 2: 调整任务编辑器 UI，移除“全部允许”，改为双名单配置入口与小时粒度输入。
  - [x] SubTask 2.1: 更新过滤规则界面第一级为白名单/黑名单双区块
  - [x] SubTask 2.2: 在两区块内提供相同二级过滤项编辑能力
  - [x] SubTask 2.3: 将时间过滤输入单位改为 hour

- [x] Task 3: 实现过滤评估逻辑更新与冲突检测。
  - [x] SubTask 3.1: 实现双名单组合评估逻辑（空白+空黑=全部允许）
  - [x] SubTask 3.2: 实现白/黑名单冲突检测器并输出可读冲突信息
  - [x] SubTask 3.3: 保存流程接入冲突检测（有冲突则提示并阻止保存）

- [x] Task 4: 验证与回归。
  - [x] SubTask 4.1: 覆盖关键场景测试（空规则、双名单同时配置、小时过滤、冲突拦截）
  - [x] SubTask 4.2: 手动验证任务编辑器交互与提示文案

# Task Dependencies
- Task 2 depends on Task 1
- Task 3 depends on Task 1
- Task 4 depends on Task 2 and Task 3
