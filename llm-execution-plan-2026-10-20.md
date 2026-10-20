# Folder Sync 下一位 LLM 执行计划

配套审查：`code-review-2026-10-20.md`  
审查基线：HEAD `7c44473` + 当时未提交的 `FolderSync.csproj`。  
状态：仅制定计划，全部实施任务待执行。本文不能作为“已经修复”的证明。

## 1. 接手须知

1. 先读当前 `CLAUDE.md`、本计划及配套审查，再检查 `git status --short`、最新日志及实际源码。行号可能随修复移动。
2. 审查时工作区存在用户修改 `FolderSync.csproj` 和未跟踪 `local_test/`。不删除、不覆盖、不自动提交用户内容。
3. `FolderSync.csproj` 工作区是 0.0.54，HEAD 是 0.0.55；用户移除了测试排除和 EnableWindowsTargeting。实施 T01 前先就受影响配置取得明确决定，不能用 git restore 或整文件回退。
4. 不照搬 2026-08-16 旧任务完成状态，参照新审查第 3 节区分已经实现与仍不正确。
5. 禁止在真实用户目录、FTP 生产目录执行破坏性测试。用系统临时目录、独立 SQLite 文件、可故障注入 VFS 与专用 FTP 测试目录；测试文件不放仓库交付目录。
6. 当前默认 dotnet 是 x86 runtime。全部 SDK 命令用 `C:\Program Files\dotnet\dotnet.exe`，SDK 8.0.419 已确认存在。
7. 本计划不是授权无条件提交：执行时遵守当前有效的上层指令；需要提交时取得明确授权，不跳过 hooks，不 amend，不 force push。只暂存本任务文件，先审查所有已暂存改动，防止混入用户 csproj 修改。
8. 产品策略有非显然副作用时，先给有限选项让用户决定；其余使用保数据默认策略，不因确认等待阻断无关的安全修复。

## 2. 目标与顺序

必须保持：

- 单向增量/更新/一次性/镜像/双向各自语义，不偷偷把单向变成反向写源。
- 成功/失败报告逐项含文件名、大小、路径；报告文件名只保留时间戳与数据量，不恢复随机串。
- 执行结束不自动再进入全量分析遮罩；重新分析由显式操作或“前置条件变化”提示触发。
- 不完整扫描不能授权删除，取消不能抹掉已成功投递的事实，失败/未选路径不能变为已同步基线。
- 失败传输不能损坏之前的完整目标。

执行顺序：

`T01 -> T02 -> T03 -> T04 -> T05 -> T06 -> T07 -> T08 -> T09 -> T10 -> T11 -> T12 -> T13 -> T14 -> T15`

T02-T05 为数据安全优先项。T08 依赖 T04/T07 的可靠扫描与协调机制；T12 的报告结构应早在 T02/T05 增加字段时预留，但完整 UI 收尾随后执行。每个任务做最小可验证修改，不同时重写整个同步架构。

## 3. 任务清单

### T01 / P0：恢复构建与可测试基线

- 对应 R01；修改 `FolderSync.csproj`。
- 在用户明确认可后，定点恢复主工程测试目录排除（可用显式 Compile Remove；同时排除测试 obj/bin 与 WPF 资源项），不把 xUnit 加入发布应用来掩盖错误。
- 保留用户版本决定；无需为 Windows 构建擅自恢复交叉平台 targeting。
- 运行 Release build、独立测试工程 restore/test，记录当前已存在测试数量与结果。
- 验收：主项目与测试项目均构建成功，原有测试全部通过；主应用不纳入测试源码/测试生成程序集属性。
- 建议提交主题：`fix(build): exclude core tests from application compilation`。

### T02 / P0：事务式复制与保留原目标

- 对应 R02；修改 `IFileSystem.cs`、`LocalFileSystem.cs`、`FtpFileSystem.cs`、`OneWayDeliveryStateStore.cs` 的复制帮助函数，必要时新增专用传输服务。
- 增加 VFS 临时写入和提交/替换能力，明确唯一临时路径、提交点、备份恢复和清理责任。
- 先打开源，再写同目录临时文件，复制/校验/关闭上传流成功后提交最终路径。失败只清理本次临时文件；日志记录清理失败，保留原异常。
- Local/SMB 使用同卷原子替换或重命名；FTP 依据服务器 rename/overwrite 能力选择备份后提交与回滚，不声称 FTP 具备跨服务器事务。
- 保存实际传输字节数；在 VFS 能力支持时保留最后修改时间；源元数据变化须报告，不以分析时大小假装准确。
- 故障注入测试：源打开失败、目标临时创建失败、读/写/flush/close/commit 失败、取消、原目标存在和不存在、备份恢复失败。
- 验收：上述所有失败场景原目标内容/hash 不变；新增失败不留正式半截文件；上传流关闭/最终响应失败不记成功。
- 建议提交主题：`fix(sync): preserve existing targets with staged file commits`。

### T03 / P0：删除授权与非递归目录清理

- 对应 R03；修改 Diff/执行操作模型、`SyncExecutor`、`TaskAnalysisService` 与 VFS 目录删除接口。
- 默认目录删除只允许空目录，绝不从“父目录命中结构展开”推导整棵子树删除权。
- 按文件在前、目录由深到浅排序；子项未选、被过滤、扫描未知或删除失败时，不递归删除其父目录。
- 如产品需要递归删除，必须独立显式操作、展示全部受影响项并确认；安全修复阶段先禁用隐式递归。
- 测试：目标独有目录内包含命中与排除文件；只选目录；子文件取消勾选；子文件删除失败；多层空目录。
- 验收：排除/未选/失败文件均保留，空目录正常清理；报告记录阻止/跳过原因。
- 建议提交主题：`fix(sync): restrict directory deletion to authorized empty trees`。

### T04 / P0：扫描完整性与取消传播

- 对应 R04；修改 `IFileSystem` 列举结果契约、`FtpFileSystem`、分析服务和执行器。
- 列举结果携带已扫描区域、失败子树和完整性标志；或短期对任何影响破坏性操作的扫描错误直接失败。
- FTP 子目录允许非破坏性模式继续显示已扫描项，但 UI 与报告明确“不完整”，未知区域禁止删除及基线确认。
- `OperationCanceledException` 不进入通用跳过 catch，扫描返回前再检查 token。
- 测试：源/目标根失败、源子树失败、目标子树失败、最后子目录取消、短重试恢复。
- 验收：镜像与双向遇源子树失败均不删除另一侧对应数据；不完整不是“仅源端/目标端存在”的肯定判定。
- 建议提交主题：`fix(vfs): propagate scan completeness into destructive sync planning`。

### T05 / P1：一次性投递可靠检查点

- 对应 R06；修改 `OneWayDeliveryStateStore`、两条执行链；依赖 T02 的明确成功提交点。
- 只对最终提交成功文件记录投递；复用连接、按项或短批提交，避免整次执行末尾唯一提交点。
- 用户取消后的已成功记录用独立有限超时完成持久化；不能沿用已取消 token。明确崩溃窗口与写库失败的报告语义。
- 单次写库故障不应让 UI声称已可靠完成“一次性”；后续恢复策略需要记录 pending/committed，不宣称 filesystem 与 SQLite 天然原子。
- 测试：前两文件成功、第三取消；随后删除目标前两文件再运行；状态提交失败；短批边界取消。
- 验收：正常停止后已成功文件不再补发，未成功文件可重试，现有批量 store 测试继续通过。
- 建议提交主题：`fix(sync): checkpoint successful send-once deliveries on cancellation`。

### T06 / P1：仓库跨实例事务与配置修订

- 对应 R08；修改 `TaskRepository`、`SettingsRepository`、`SyncTaskDefinition`、分析保存调用。
- 按规范化路径共享锁/仓库实例，完整读改写事务串行化；唯一临时文件与原子替换、备份恢复。
- 区分 JSON 损坏、暂时 IO 异常与不存在；读取失败时禁止继续基于空列表 Upsert。
- 引入修订号或等效乐观并发，分析保存只更新分析字段；任务不存在/已编辑时不允许旧分析重新创建或覆盖整个定义。
- 测试：多 repository 同路径并发新增/编辑/删除；读取失败；坏 JSON；旧快照写回；不同路径互不阻塞。
- 验收：并发更新不丢任务，固定 tmp 竞争消除，旧后台任务不能复活已删任务。
- 建议提交主题：`fix(config): serialize repository updates and reject stale snapshots`。

### T07 / P1：全入口运行协调与退出生命周期

- 对应 R07；修改 SchedulerManager/SyncJob/App 与 UI 入口，新增共享执行协调器。
- 所有手动执行、调度执行、状态重置按 taskId/状态世代使用同一运行锁；重叠端点任务用资源锁或明确阻断策略，不把不同 taskId 当作永不冲突。
- Job 仅保存任务定义 ID/修订，不长期保存活的 FTP executor；每次运行创建并独占，结束时释放。
- 修改/删除/重置请求协调活动运行：先取消、有限等待，或明确拒绝当前操作；不可 Dispose 正在传输的对象。
- App 不在无法被 WPF 等待的 async void OnExit 中承担核心异步收尾；退出前执行统一 RequestExitAsync，停止接收新运行，取消并等待，最终 Shutdown。
- 同步 Dispose FTP 不在 UI 线程做慢 disconnect；新增 async disposal 时明确兜底与异常传播。
- 测试：定时+手动同任务、两个窗口、编辑/删除中传输、退出中传输、调度注册失败、重复退出。
- 验收：同资源无并发写，运行对象不被提前释放；收尾完成/明确超时后退出，日志可追溯。
- 建议提交主题：`fix(scheduler): coordinate executions and defer disposal until completion`。

### T08 / P0：双向按确认结果增量更新基线

- 对应 R05；修改 `SyncExecutor`、`TwoWayStateStore`、`TaskAnalysisService`；依赖 T03/T04/T07。
- 提取手动/调度共用操作计划与逐项结果；state store 提供按路径事务更新和显式删除基线记录。
- 仅成功提交且前置条件校验通过的路径更新；未选/失败/未知扫描路径保留旧基线，删除保留必要 tombstone 语义。
- 初次无动作一致路径可初始化，但必须双侧确认；不要通过全量重扫描把未授权变化自动确认。
- 处理成功父目录与失败子文件、执行期间外部修改、取消的增量提交。
- 测试：A 改动复制失败后下次重试；只选部分后下次仍提示未选项；部分删除失败；刷新期间外部修改；一侧扫描失败；初次一致建立基线。
- 验收：失败/未选动作不消失，成功项不重复，双向手动与定时同一规则；无需每次执行后全量哈希所有文件。
- 建议提交主题：`fix(sync): advance two-way baselines only for confirmed operations`。

### T09 / P1：端点状态世代隔离

- 对应 R10；修改任务定义/编辑器/两个状态库/工厂。
- 就“换端点、模式、过滤范围”对状态的影响提供明确策略；使用规范化端点与配置世代标识隔离旧记录。
- 编辑端点必须失效 saved analysis；保存只改名称/调度时不得无故丢一次性记录。
- 数据库迁移在事务内，保留旧记录，不自动删除历史世代；任务删除时有明确历史状态清理策略。
- 测试：一次性任务换目标后新目标可收文件；双向换根不读取旧删除基线；名称变化状态不变；过滤改变不误删。
- 验收：无新端点漏同步，无旧世代跨目录污染，策略与 UI 提示一致。
- 建议提交主题：`fix(state): scope sync baselines to endpoint generations`。

### T10 / P1：合法选择动作与执行前置条件

- 对应 R09/R12；修改分析模型/窗口/ViewModel/Diff/执行服务。
- 提示行、无动作行默认不可执行；“强制同步”如允许，应生成明确方向和动作，单向只能 A -> B。
- 明确文件/目录类型冲突及大小写不可表达冲突，默认阻断保留数据，不能显示“已一致”。
- 保存分析携带配置修订、源目标前置元数据/必要 hash；执行前重新查询受选路径，旧 Delete/Update 不盲执行。
- 分析行执行顺序与目录依赖共用规划器；只有成功结果才清除该行待执行状态，失败行保留可重试，保存新状态而非原勾选快照。
- 测试：勾过滤项/一致项/提示行；单向目标较新；旧删除后目标发生变化；类型冲突；成功后再次执行。
- 验收：无隐式反向写源，无未经确认删除变化对象；不恢复执行后全量自动重分析遮罩。
- 建议提交主题：`fix(analysis): validate explicit actions and stale execution preconditions`。

### T11 / P1：路径比较与安全规范化

- 对应 R11/R17；修改 VFS、Diff、结构辅助、state store 与 PathSafetyValidator。
- VFS 暴露路径比较/规范化能力，FTP 保持大小写；Windows 目标冲突必须检测并阻断。不得 Trim 掉合法文件名空格再悄悄合并。
- 重叠校验与实际 FTP 构造使用同一解码/规范化函数，根路径不输出不明确空串。
- Local 默认不遍历跨根重解析点，写入/删除路径检查中间目录链接；拒绝安全性不明确路径，说明 TOCTOU 残余边界。
- 测试：FTP `A.txt/a.txt` 和大小写目录；Windows 大小写碰撞；编码斜杠/点段；根 `/`；Windows junction 外部目标。
- 验收：不静默丢条目、不写错路径、词法检查与真实授权根一致。
- 建议提交主题：`fix(vfs): honor endpoint path semantics and reject unsafe aliases`。

### T12 / P1：超时规则与完整执行审计

- 对应 R13/R14；修改 RegexFilter/FilterEngine、SyncReport、报告 writer、UI 完成提示与 Dashboard 解析。
- 正则未知结果不当成通过，报告规则超时并禁止基于不完整过滤结果删除；补实际超时回归。
- 所有运行返回/传递含已完成明细的报告，包括取消/连接失败/状态提交失败；明确 Completed/Partial/Cancelled/Failed、跳过与未执行。
- 总是落盘部分报告；如 writer 失败保留原执行状态并记录错误，不能丢掉已有执行事实。
- 明细使用实际方向/根路径/相对路径/名称/传输字节与操作；区分目录、删除大小与传输量，凭据永不写报告。
- 使用 FileMode.CreateNew 抢占报告名，冲突递增时间戳；只保留 `时间戳_数据量B.txt`。可先写临时后提交，但最终命名仍须原子不覆盖。
- UI 先清 busy，再展示结果，真实成功/失败计数，不将选择数冒充成功数；关闭后抑制迟到弹窗并释放 timer/事件。
- 测试：取消后部分明细、零成功连接失败、写库失败、并发同名报告、反向大小、删除零传输、窗口关闭中执行。
- 验收：每次执行可审计，所有成功/失败文件可定位；无报告覆盖和残留分析/执行动画。
- 建议提交主题：`fix(reporting): retain partial outcomes and atomically create reports`。

### T13 / P2：触发器、统计与文案

- 对应 R15/R16；先确认周期语义。
- 若固定间隔：SimpleTrigger，保留 Cron 独立入口；设计存量周期任务迁移，验证启动/错过触发/夏令时。
- Dashboard 根据实际注册状态与 report outcome 统计，不按配置假定已调度；统一区分文件与目录计数。
- 汇总按实际方向和 Create/Update 计算传输量，Delete 不计；统一现存硬编码文案与本地化资源。
- 测试：7秒、7小时、跨月N天、无效间隔；连接失败 failed=0；反向复制大小；中文/英文切换。
- 验收：周期与约定一致，统计与报告逐项可对账。
- 建议提交主题：`fix(ui): align schedules and statistics with actual execution outcomes`。

### T14 / P2：文档与回归矩阵

- 更新 CLAUDE.md 第 3/4/7 节，实际新增文件与测试全部入树；把旧技术债改为已修/仍待验证，引用本轮材料。
- 新增测试不只覆盖辅助函数：用 Fake/FaultingFileSystem 支持完整性、取消、读写/提交失败、比较语义与执行屏障。
- 集成回归：Local->Local、Local->FTP、FTP->Local，五种模式，过滤、深目录、删除、取消、状态重启、部分执行与重试。
- 真实 FTP 测试必须用专用目录，记录服务器版本、大小写/rename/TLS 能力，日志不得包含凭据。
- 现有“FTP 已存在误判仅源端存在”与“执行/分析遮罩残留”列为固定回归项。
- 验收：每项问题有对应测试或明确手动步骤/测试边界，未验证不写“完成”。
- 建议提交主题：`test(sync): cover failure recovery and update architecture documentation`。

### T15：发布门禁与最终交接

- 新版本基于用户确认后的最新发布版本递增 0.0.1，不能照旧文档固定发布 0.0.55，也不能把 HEAD 0.0.55 覆盖成旧号。
- 确认 portable 是否 self-contained；若需干净 Windows 可运行，发布 self-contained true 并在无 Runtime 环境验收。SQLite 原生依赖须检查，不能只交一个无法加载依赖的 exe。
- 发布到新的 `publish/v<版本>/`，保留旧发布目录和用户 data/log；生成 SHA256、版本、运行前提、测试结果与剩余问题清单。
- 最终交接列每个 T 的状态/提交/测试证据，不把未做的 FTP 联调写成通过。
- 仅在用户授权后提交对应代码与文档；不提交 exe、data、log、凭据、local_test。

## 4. Windows 验证命令

以下命令单独运行；不要用清理命令删除用户工作区内容。

```powershell
& "C:\Program Files\dotnet\dotnet.exe" restore FolderSync.csproj
& "C:\Program Files\dotnet\dotnet.exe" build FolderSync.csproj -c Release
& "C:\Program Files\dotnet\dotnet.exe" test FolderSync.Core.Tests\FolderSync.Core.Tests.csproj -c Release
& "C:\Program Files\dotnet\dotnet.exe" list FolderSync.csproj package --vulnerable --include-transitive
```

最终发布示例（将版本替换为已确认的新版本）：

```powershell
& "C:\Program Files\dotnet\dotnet.exe" publish FolderSync.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "publish\v<版本>"
Get-FileHash "publish\v<版本>\FolderSync.exe" -Algorithm SHA256
```

发布前先检查目标父目录并核对工程实际发布配置。自提取原生库行为、WPF/WinForms runtime 及无 Runtime 机器启动都要验收。依赖扫描若无法联网需报告限制，不以失败扫描推导“无漏洞”。

## 5. 每任务交接模板

```text
任务：Txx
状态：待执行 / 进行中 / 已验证完成 / 阻塞
对应问题：Rxx
用户决策与假设：
修改文件：
测试与实际结果：
真实 FTP/SMB 是否验证：
剩余风险：
提交：未授权未提交 / <hash 与 message>
下一步及依赖：
```

最终完成条件：构建与全部自动测试通过；P0 删除/覆盖/扫描/基线场景有证据；手动与调度共享一致执行约束；本轮需求的日志明细、文件命名和遮罩交互不回归；版本发布与用户原始改动均正确保留。
