# Task 4 验证证据

## 验证范围
- 空规则：白/黑名单均为空时默认全部允许。
- 双名单同时配置：白名单先筛选，黑名单再排除。
- 小时过滤：UI 文案与解析/评估均使用 hour。
- 冲突拦截：保存时检测冲突并阻止保存，给出可读提示。

## 关键证据

### 1) 空规则语义（全部允许）
- `DualListFilterConfiguration.IsAllowAll` 在白/黑都无规则时返回 true。
- `FilterEngine.IsAllowed` 对白名单使用 `!_whitelistFilters.Any() || ...`，白名单为空直接放行第一阶段。
- `FilterEngine.IsAllowed` 对黑名单使用 `Any && All`，黑名单为空不会命中拦截。

### 2) 双名单同时生效
- `FilterEngine.Configure` 同时构建 `Whitelist` 与 `Blacklist` 过滤器集合。
- `FilterEngine.IsAllowed` 顺序为：先白名单判定，再黑名单拦截，最后历史过滤器叠加，满足“白+黑组合评估”。
- `TaskEditorView` 的“过滤规则 (黑/白名单)”页在同一界面并列提供白名单与黑名单配置卡片，可同时填写。

### 3) 小时粒度一致性（UI + 逻辑）
- `TaskEditorView` 白名单时间文案为“仅包括新于 X 小时”，黑名单时间文案为“排除新于 X 小时”。
- `TaskEditorViewModel` 字段为 `WhitelistNewerThanHours` / `BlacklistNewerThanHours`，保存时映射到 `FilterRuleSet.NewerThanHours`。
- `FilterRuleSet.ParseNewerThanHours` 负责解析小时阈值。
- `FilterEngine.BuildFilters` 使用 `ParseNewerThanHours` 构建 `TimeFilter`。
- `TimeFilter` 按 `TotalHours` 进行判断，超过阈值则不匹配，语义为“仅匹配新于 N 小时”。

### 4) 冲突检测与保存拦截
- `FilterConflictDetector.Detect` 覆盖扩展名、正则、大小区间、时间阈值冲突检测。
- `TaskEditorViewModel.SaveTask` 保存前调用 `FilterConflictDetector.Detect`。
- 冲突存在时展示 MessageBox：
  - 标题：`过滤规则冲突`
  - 文案：`检测到白名单与黑名单存在冲突，已阻止保存...`
- 冲突时 `return` 提前退出，保存被阻止。

## 手动交互检查结论
- 过滤规则页已移除“全部允许”独立选项。
- 白/黑名单两区块均具备扩展名、大小、小时、正则四类输入项。
- 冲突提示文案清晰，包含冲突类型与明细列表。

## 环境说明
- 当前执行环境缺少 .NET SDK，无法在此会话运行 `dotnet test/build/publish`。
- 本次 Task 4 结论基于静态回归检查与代码路径核验。
