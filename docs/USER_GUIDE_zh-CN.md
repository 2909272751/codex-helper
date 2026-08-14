# Codex Helper 使用教程

这是一份首次使用指南。最容易混淆的两个目录先记住一句话：

> **“选择要保护的项目”选择从哪里找项目；“备份与恢复”选择把备份放到哪里。两者不能选成同一个目录。**

## 第一次使用：建议顺序

1. 打开“设置”，确认 **Codex 根目录** 是你的 `CODEX_HOME`。通常是 `C:\Users\你的用户名\.codex`，一般保持软件自动识别的值即可。
2. 打开“选择要保护的项目”，选择 **从这里查找项目**，扫描并勾选需要长期保护的项目。
3. 打开“备份与恢复”，选择一个独立的 **备份保存位置** 文件夹。
4. 点击“创建快照”。首次是基线备份；之后只保存变化内容。
5. 要换电脑或重装系统时，在“迁移中心”导出 `.chbundle`，并保存好迁移口令。

首次启动会显示七步引导；点击“下一步”时主窗口会自动跳到相应页面，便于边看边对照。之后可在“设置”点击“重新观看新手引导”。

## 这两个目录怎么选

| 页面 | 要选什么 | 示例 | 不要选什么 |
| --- | --- | --- | --- |
| 选择要保护的项目 → 从这里查找项目 | 放着多个开发项目的上级目录，用来**扫描和发现项目** | `C:\实用软件开发`、`D:\Projects` | 备份目录；不相关的大型磁盘根目录 |
| 备份与恢复 → 备份保存位置 | 专门存放加密快照的独立目录，用来**保存备份** | `D:\CodexHelperBackup`、移动硬盘中的 `CodexBackup` | 项目目录、项目子目录、`CODEX_HOME`、`CODEX_HOME` 子目录 |

### 1. “选择要保护的项目”的查找位置

请选择一个“项目集合”的上级文件夹。比如你的文件结构是：

```text
C:\实用软件开发\
├─ codex-helper\
├─ codex-web-remote\
└─ my-python-tool\
```

就选择 `C:\实用软件开发`。

软件会检查所选目录本身和它的第一层子目录，识别带有 `.git`、`.sln`、`.csproj`、`package.json`、`pyproject.toml` 等标记的项目。扫描完成后：

1. 在下方列表选中需要备份的项目；
2. 点击“保护所选项目”；
3. 这些项目会进入后续“创建快照”和“批量导出”的范围。

如果项目实际位于更深层，例如 `D:\工作\客户A\项目1`，请选择 `D:\工作\客户A`，或直接选择 `D:\工作\客户A\项目1` 后扫描。

### 2. “备份与恢复”的保存位置

请选择一个**专门用于备份、最好不在项目所在磁盘内部**的文件夹。它是 Codex Helper 保存加密内容、快照清单和恢复信息的位置。

推荐：

```text
D:\CodexHelperBackup
E:\Backup\CodexHelper
```

不推荐：

```text
C:\实用软件开发\codex-helper\backup       ← 在被保护项目里面
C:\Users\你的用户名\.codex\backup          ← 在 Codex 数据目录里面
C:\实用软件开发\codex-helper\.git\backup  ← 更不能选
```

原因是：把备份放进被备份的源目录会让备份把自己再次备份，造成空间膨胀和恢复混乱。软件会阻止这类危险选择。

选好后点击“创建快照”。恢复时请使用“恢复到新目录”，例如 `D:\Recovered\2026-07-22`，先确认文件无误，再手动替换原项目。

## 连接中心：账号与 API

- **保存当前登录**：先在 Codex 中正常登录官方账号，再填写一个便于识别的名称，例如“个人账号”或“公司账号”，点击保存。凭据仅使用当前 Windows 用户的 DPAPI 加密保存。
- **准备登录新账号**：先安全保存现有账号，再让 Codex 回到未登录状态；重新打开 Codex 后登录另一个账号，再回来“保存当前登录”。
- **保存 API 档案**：填写名称、Base URL、模型和 API Key。Key 不会写进 `config.toml`。
- **切换连接**：先关闭 Codex。软件会检查进程、备份并同步需要的配置/会话元数据，然后再切换。
- **修复旧 Responses 档案**：如果连接类型显示“旧版子智能体档案”，先选中它，再点击“修复旧 Responses 档案”。修复会清理旧 provider/worker/强制委派规则并把它改为普通 Responses API 档案，档案和加密 Key 都会保留；之后可用“切换到所选连接”把它作为主模型使用。

## 协作开发：执行器选择（Reasonix / DeepSeek Harness）

“协作开发”页面顶部提供**实现执行器**下拉框：`关闭协作` / `Reasonix` / `DeepSeek Harness（预览）`。选择会保存，重启后保持；旧版已启用 Reasonix 的设备升级后仍保持 Reasonix，未启用则保持关闭。Reasonix 与 Harness 的设置分区显示。

选择 Reasonix 时，在此页开启“协作编码”后，GPT 负责规划与验收，Reasonix 作为实现执行端，在任务期间由临时进程运行，任务结束后自动退出。页面可管理 Reasonix 默认模型、权限（安全开发/完全权限）、执行强度、最近任务（刷新进度、停止、重试、返回原 Codex 任务）以及 DeepSeek 缓存统计。

选择 DeepSeek Harness（预览）时，GPT 仍负责规划与验收，Harness 负责实现；任务优先进入同一个持久 Web Host 会话，你可以在官方 Harness Web UI 实时看到事件并干预。Harness 设置分区只检测并启动你**已有环境**中的 Node/Harness，不会安装 Node、npm 包或 Harness；**可用性由入口与能力探测决定**，不再用版本硬白名单——版本只用于风险提示与诊断（以已知基线 `@deepseek-ai/dsh@0.1.0-rc.5` 分档：已知基线 / 同系列新版本 / 跨次版本未验证 / 跨主版本未验证 / 非法损坏，禁止静默使用 latest）：

- 入口完整且 CLI、Web profile、中继能力通过时，未知新版本（如 rc.7、0.1.0、0.2.0）也可直接使用，界面显示“新版本已验证”；跨主版本会显示更醒目警告；
- Node 需 `22.19+`（LTS 分支）或 `24+`（当前分支）；
- Web Host 仅监听 `127.0.0.1`，关闭浏览器不会停止任务；
- 当前机器没有 Node 是正常降级场景：诊断会解释缺少什么并提供官方下载入口，开启按钮不会误报成功；
- Web Host 状态与自动中继状态分开：CLI/Web profile 通过只代表能启动 Web；只有任务提交、事件流、取消全部由运行时能力探测确认才宣称“实时协作可用”，否则界面显示“可打开 Web 但自动中继未确认”，不会伪造会话或回退成不可见 headless 后仍声称实时可见。
- “打开 Harness Web”打开本机 Harness UI；“重新检测”重新探测；“停止 Helper 启动的 Host”停止由 Helper 启动的 Web Host；“选择 Node 路径”手动指定 `node.exe`。

从 `3.3.1` 起，“完全权限”使用 Reasonix 1.19.x 已验证可执行工具和写入文件的兼容权限模式，避免任务在 0 token、0 工具调用时瞬间结束。

从 `3.3.2` 起，Reasonix 不再承担任何截图、看图和视觉结论，所有屏幕、DPI、布局、颜色与视觉验收都归 GPT 独立完成；Reasonix 最多只做一次“窗口能启动/响应/退出/无残留”的事实型 GUI 烟测，失败时记录并收敛，不再为截图反复更换环境方案。

从 `3.3.3` 起，Helper 启动扫描不得执行 Reasonix Desktop/launcher/update-helper，只能探测真实 CLI；任何来源命中 Desktop 启动器或辅助可执行文件即被排除，形成单一安全边界，运行中的 Desktop 路径仅作为定位线索派生 `reasonix-cli.exe`。

从 `3.3.4` 起，协作开发杜绝 Reasonix 最终门禁循环：托管 Reasonix 永远使用 `balanced` profile（Strict 只映射为 `balanced + high`，Fast 为 `balanced + low`），任何执行强度都不再生成 `delivery`，也绝不自动启动 review/security-review/explore 子代理，GPT 是唯一评审者。执行器会在每个任务目录派生 `WORKER_ACCEPTANCE.md`——只包含 manifest 的 workerChecks 并做职责过滤，Reasonix 的提示只读取该派生合同而不读完整 `ACCEPTANCE.md`。退出码 1 且事件中出现 final_readiness 且有实际活动证据时，任务会谨慎分类为“等待 GPT 复核（Reasonix 最终门禁未通过）”：既不宣称 workerChecks 一定成功，也不当作普通代码开发失败，最终由 GPT 独立验收。

从 `3.3.5` 起，进一步优化 P0/P1 流程：① 收紧 workerCheck 职责过滤——只有明确要求截图/看图/像素分析/真实 GUI 操作或烟测/DPI 视觉判断/颜色遮挡视觉判断/屏幕捕获/发布安装包等才移交 GPT；普通 XAML/XML/DOM 布局数学、图片资源存在性或资源引用、GUI 项目构建等结构测试保留给 worker；识别“不截图/不看图/不启动 GUI/不进行视觉判断”等否定约束说明，不当作待执行检查，也不误伤相邻结构测试。② 消除合同矛盾：Reasonix 只读 `SPEC.md`/`HANDOFF.md`/`manifest.json`/`WORKER_ACCEPTANCE.md`，从不读 `ACCEPTANCE.md`，只写 `EXECUTION_REPORT.md`（`REVIEW_PACKET.md` 由 Helper 自动生成）；HANDOFF 明确“允许读取文件/允许修改文件/直接依赖”，目标明确时禁止递归扫描。③ DeepSeek Flash/Pro 托管默认 effort：Fast/Standard/Auto 默认 `low`，仅 Strict 或 major 才 `high`（manifest 显式 high/max 仍可用于真正高风险任务）。④ 动态软预算默认值调整为聚焦修复 16 / 普通功能 35 / 完整 major 56 步，软预算不终止任务，manifest 显式 `budgetSteps` 始终优先。⑤ 生成/指导精简：集中读取与集中修改（相关文件可并行读取、同一文件无变化不重复读取、先形成一次修改集再批量编辑）、workerCheck 去重（已通过且文件未改的检查不重跑）、GUI 烟测最多一次、GPT 采用增量验收（检查报告与 diff 后重跑受影响聚焦检查，仅高风险/release/合同明确要求时才跑完整回归）。

从 `3.3.6` 起，提升 Reasonix 可靠性与进度体验：① 统一标准 JSON——Reasonix 任务状态统一走标准 `System.Text.Json` 序列化与 UTF-8 无 BOM 原子写入，Windows 路径反斜杠正确转义、中文路径不乱码，新写文件同时兼容 PowerShell `ConvertFrom-Json` 读取，并保留宽容读取旧损坏状态的能力。② 预计剩余百分比单调保护——运行中状态持久化 `RemainingPercent`（5%–100%），同一 attempt 内只降不升，进度源从步骤切换到 workerChecks、状态刷新或重启恢复时均不回升；完成时 UI 不显示预计剩余（等价 0%），新任务/新 attempt 重新初始化，历史状态缺字段保持兼容。③ 标准 `PROGRESS.json` 协议——定义并验证 `stage`/`summary`/`updatedUtc`/`completedChecks`/`totalChecks`/`currentCheck`/`checks`（名称 + 状态 pending/running/passed/failed）字段；Reasonix 提示明确要求在 workerCheck 前后原子更新任务目录内 `PROGRESS.json`，Helper 从事件安全推导基础进度作回退，绝不把视觉/GPT 检查计为 worker 完成；损坏、越界、陈旧内容安全忽略。④ 合同启动前体检与安全归一化——启动前检查 HANDOFF 是否要求 Reasonix 读 `ACCEPTANCE`/写 `REVIEW_PACKET`、workerChecks 重复或混入视觉/发布、DeepSeek 错误使用 delivery profile、普通 small/medium 显式 high/max、HANDOFF 缺少允许读取/修改/直接依赖；可安全归一化的（去重、职责过滤、强制 balanced、移除矛盾执行要求）自动应用于派生运行合同，原始合同文件不被覆盖，无法安全修正的合同阻止启动并给出中文诊断，状态/UI 显示归一化摘要。⑤ 影响范围增量验收映射——Helper 按实际 changed files 与合同风险生成 GPT 验收建议（focused/full/release/security/visual）写入 `REVIEW_PACKET.md`：文案/文档、单策略/纯函数 → focused；UI/XAML → focused+visual；凭据/加密/备份/迁移 → security+full；installer/版本/发布脚本 → release+full；多核心模块或无法识别 → full；GPT 按建议做增量验收，高风险、release、security 或合同明确要求时完整回归。

从 `3.4.0` 起，协作开发支持并行功能整合：① 在“模型与执行策略”卡片新增“并行设置”——智能拆分、独立任务并行、最大并发（1..3，默认 2）、自动 worktree、超预算收敛，保存到 `AppSettings`；旧配置缺失这些字段时自动保留默认值（智能拆分/并行/自动 worktree/超预算收敛默认开，最大并发默认 2），保存时最大并发越界会收敛到 1..3。② 并行调度与 worktree 准备——Helper 用 `ReasonixParallelScheduler` 对当前任务快照生成运行/排队/受阻/已完成/失败统计与每行的等待/冲突/排队原因；`ReasonixWorktreePreparationService` 为每个任务生成安全唯一、限定在配置 worktree 根内的隔离目录（优先 git worktree），运行前验证 Git 仓库、HEAD、目标目录、任务 ID、允许写文件范围，宽泛通配符或路径越界阻断，触及脏文件或依赖未跟踪文件自动转串行；本版本不真实创建/删除 git worktree，不改变凭据/连接/备份功能。③ 并行任务中心——最近 Reasonix 任务改为列表展示，运行中优先、同一优先级内按更新时间倒序；选择一个任务后，停止/重试/返回原任务/复制 ID/打开目录等操作作用于所选任务；状态文件没有合同摘要时仍显示基础状态，不崩溃。④ 更新全局指导文字——实现类任务先智能拆分，可独立且写集合不重叠时最多按设置并行，公共文件/依赖/脏文件风险串行，Reasonix 执行、GPT 合并验收，不强制每个请求都并行。

从 `3.4.1` 起，实现类任务改为任务规模三档路由：新增纯函数服务 `ReasonixTaskRouter`，按合同规模返回 `gpt_direct` / `reasonix_single` / `reasonix_parallel_candidate` 及中文原因——① 微任务（预计不超过 2 个文件、约 80 行、不新增跨模块/公共接口、不涉及安全/凭据/数据迁移/备份恢复/并发协调/安装升级/公共 runner 或核心配置结构、一次聚焦测试即可可靠验收，典型如文案、样式、小范围 UI、测试断言、简单明确 Bug）由 GPT 直接实现；② 命中任一 Reasonix 单合同条件时（预计至少 3 个文件或超过约 80 行、新增完整功能/跨模块接口或需大量阅读代码、高风险领域、需多轮实现测试、或用户明确要求 Reasonix/DeepSeek）交给单个 Reasonix 合同；③ 仅中大型任务含至少两个独立模块、接口冻结、写集合不重叠、无需二次接线、可机械合并时才采用 Reasonix 有限并行，否则退回单合同。边界规则：GPT 微修中途越过阈值即停止扩大并转 Reasonix 合同；Reasonix 主体完成后仅剩不超过 2 文件/80 行且低风险的验收微修由 GPT 直接修，不再启动新 Reasonix；纯问题与只读审查仍由 GPT。这些是默认路由而非安全授权扩张。协作开发页说明与全局协作指导文字已同步展示三档路由。

从 `3.4.1` 起，Reasonix 调度可靠性进一步收敛：① **漏报告自动恢复**——如果 Reasonix 退出码为 0、有实际模型/工具活动，且能证明存在本次新增改动或已通过 workerChecks，只是漏写了执行报告，Helper 会自动生成一份明确标注“自动恢复”的执行报告与 Review Packet，并把任务置为等待 GPT 验收的完成态；不会伪造测试通过，没有活动、没有改动、模型失败或非零退出都不会被误判为成功。② **连续失败熔断**——同一任务连续 2 次因“缺少交付报告”或“模型运行失败”结束时（含 `attempts/` 归档）会阻止一键重试，界面会提示先检查任务合同、Reasonix 模型与失败日志；用户主动停止不计入。③ **状态与百分比归一**——完成/失败/停止/等待验收的任务剩余百分比统一为 0%（运行中才显示 5%–100%），阶段归一（完成必为 `done`，其余终态保留失败发生阶段，不再显示进行中百分比）。④ **软预算按历史校准**——Helper 根据同一项目最近成功的同类复杂度任务实际步数微调软预算（样本不足回退默认值、异常值截尾、结果有合理上下限），软预算仍是提示不是硬上限。⑤ **已通过检查不重复**——自动恢复报告、重试说明（RETRY_CONTEXT）与 Review Packet 都会列出已通过的 workerChecks（排除视觉/GPT 项），后续只运行未完成检查，不重复构建或重复视觉检查。另外，正在运行的旧 Codex 任务不会实时重载全局指导，Helper 无法强制其改变调度，建议主要阶段完成后新建任务。

### Reasonix CLI 自动发现与手动选择

Helper 会从多个来源自动发现 Reasonix CLI，不进行全盘递归扫描：

- 已保存且仍存在的用户选择；
- Reasonix Windows 卸载注册表的 `InstallLocation`/`DisplayIcon`/`UninstallString` 推导安装目录（HKCU/HKLM、32/64 位视图），支持任意自定义磁盘目录（如 `D:\Apps\Reasonix`）与 `versions\vX.Y.Z` 版本目录，注册表值格式异常时自动容错；
- 正在运行的 `reasonix-desktop.exe`/`Reasonix.exe` 所在安装根或版本目录（无需管理员权限）；
- 常见位置：`%LOCALAPPDATA%\Programs\Reasonix`、`%LOCALAPPDATA%\reasonix`、`%ProgramFiles%\Reasonix` 等；
- PATH 中的 `reasonix-cli.exe`/`reasonix.exe`；
- npm `reasonix.cmd` 最后兜底。

对每个候选做快速能力探测（版本与 `doctor --json`）后去重择优：兼容新诊断结构的 Desktop/正式 CLI 优先于 npm 旧版，版本较新者优先；单个损坏候选不会阻断其他候选。已保存路径被删除或不再兼容时自动重新发现并迁移，并在诊断中说明。

页面主状态区域显示当前实际 CLI 路径、版本、来源与协议兼容性；“重新扫描”立即重新探测；“选择 CLI 文件”可手动指定（先验证、成功后才持久化；取消不改状态；无效文件给出可恢复错误；启用状态下切换后托管脚本自动刷新到新路径）。

- **doctor 容错**：`doctor` 退出码非零时仍先尝试解析 stdout 中的有效 JSON，`config/providers` 可用则模型列表照常读取并保留警告；JSON 容忍 BOM、ANSI 转义与前后噪声。错误信息始终包含原因、实际 CLI 路径、版本（可得时）与退出码，凭据类敏感字段自动脱敏。
- **状态拆分**：诊断区分为 CLI 发现、版本/协议兼容、模型配置、凭据/API 健康；API 或某个 Provider 失败不会伪装成“未安装”，读取本地模型列表不会发起额外模型生成请求。

- **阶段与进度**：Helper 在无外部 PROGRESS 时按安全事实推导基础阶段（分析中 → 实现中 → 整理交付 → 已完成/受阻）；标准 `PROGRESS.json` 可把阶段提升到测试/交付，界面标注“Helper 推断”或“Reasonix 报告”。标准协议字段为 `stage`/`summary`/`updatedUtc`/`completedChecks`/`totalChecks`/`currentCheck`/`checks`（每项含名称与 pending/running/passed/failed 状态）；当前检查会显示在阶段行，`passed` 计数会排除视觉/GUI 与发布打包类项（Helper 绝不把视觉/GPT 检查计为 worker 完成）；损坏、越界、超过 16KB、未知阶段、任务不匹配或陈旧内容安全忽略并给出诊断。
- **运行时间展示**：运行中显示“开始 HH:mm:ss · 已运行 … · 预计剩余 N%”。剩余百分比取“workerChecks 完成数/总数”与“当前步骤数/软预算”两者较大的完成比例（更可信、不会倒退），范围为 5%–100%，达到或超过软预算仍显示 5%，绝不显示负数或 0%；自 `3.3.6` 起 Helper 把单调值持久化到状态（同一 attempt 内只降不升，进度源切换/状态刷新/重启恢复均不回升），UI 优先显示该持久化值，历史状态缺字段时回退实时计算；没有任何有效进度或预算时显示“预计剩余：估算中”。任务结束后改为显示“开始 … · 完成总耗时 …”，失败/停止任务同样不伪装为完成百分比。
- **合同体检与归一化**：任务启动前 Helper 体检当前合同（HANDOFF/manifest）——HANDOFF 是否要求 Reasonix 读 `ACCEPTANCE`/写 `REVIEW_PACKET`、workerChecks 重复或混入视觉/发布、DeepSeek 错误使用 delivery profile、普通 small/medium 显式 high/max、HANDOFF 缺少允许读取/修改/直接依赖；可安全归一化的（去重、职责过滤、强制 balanced、移除矛盾执行要求）自动应用于派生运行合同，原始合同文件不被覆盖，状态与界面显示“合同已归一化：…”摘要；无法安全修正的合同（如要求 Reasonix 截图交付）阻止启动并给出中文原因。
- **增量验收建议**：任务结束后 Helper 按实际 changed files 与合同风险在 `REVIEW_PACKET.md` 写入验收范围建议（focused/full/release/security/visual）——文案/文档、单策略/纯函数 → focused；UI/XAML → focused+visual；凭据/加密/备份/迁移 → security+full；installer/版本/发布脚本 → release+full；多核心模块或无法识别 → full。GPT 按建议做增量验收，高风险、release、security 或合同明确要求时完整回归。
- **软预算不是上限**：manifest 的 `budgetSteps` 是软预算（估算步数），达到时仅提示“已接近/已超过软预算”，**不会终止任务**；只有显式 `maxSteps` 才会传给 CLI 形成硬上限。
- **失败诊断**：失败时界面给出准确失败类型（模型运行失败 / CLI 退出异常 / 缺少交付报告 / 宿主异常 / 用户停止 / 中断），无交付报告时自动生成脱敏的 `FAILURE_REPORT.md`（不含命令、正文与秘密）。
- **安全原地重试**：失败任务可点“重试未完成任务”。旧尝试证据会归档到 `attempts/`，合同保持不变、项目改动不回滚，尝试编号递增，同一时间只允许一个任务宿主；无合同、进程仍在运行、存在任务锁或路径越界时会阻止并给出原因。由 Helper 启动的重试**无法自动唤醒既有 GPT 轮次**，完成后请回到原 Codex 任务继续验收；有原任务 URI 时可点“返回原 Codex 任务”。
- **Reasonix App 延迟同步**：Reasonix CLI 在任务运行期间不实时落盘会话，Helper 只显示实时事件视图；Reasonix App 需等任务结束产生会话文件后才会同步。若结束仍无新会话文件，会明确提示“本次未产生可同步会话”，不会绑定旧会话，也不会声称已解决上游限制。

### DeepSeek v4 flash

保存官方 DeepSeek 连接时可填写：

```text
Base URL: https://api.deepseek.com
模型: deepseek-v4-flash
```

切换后，DeepSeek 是 Codex 的主模型，不是隐藏的 worker。Helper 会临时合并本机 Codex 模型目录，所以模型列表仍保留 GPT，同时增加 DeepSeek；切回官方账号或其他 API 后会自动恢复原目录。用户自己配置的模型目录只会被读取和备份，不会被改写。若本机还没有完整模型模板，请先用官方模型正常启动一次 Codex，再回来切换。

目前只适配官方 `deepseek-v4-flash` 的 Responses 接口。需要注意以下限制：

- 不支持 `previous_response_id`、`conversation` 和 `truncation`。
- 不支持图片或文件输入，只能处理文字。
- 不支持 reasoning 的 `encrypted_content`，所以 DeepSeek 只作为普通 Codex 主模型使用，不能伪装成接收加密任务正文的 Codex 原生子智能体。
- 缓存由 DeepSeek 服务端自动处理，Helper 的“缓存与诊断”卡片同时读取本机 Codex 会话用量与 Helper 的 Reasonix 任务统计，不读取密钥、不拦截请求。

### 缓存命中统计会统计哪些来源

协作开发页面的“缓存与诊断”卡片用统计范围下拉框选择最近 **24 小时 / 7 天 / 14 天（默认）/ 30 天 / 全部**；选择会保存，重启后保持，非法旧值回退 14 天。刷新在后台执行且可取消，重复点击不会并发扫描；“全部”会扫描所有符合的数据文件。

统计读取所选范围两个来源的 DeepSeek 用量：

- **Codex 会话**：本机 `sessions` 下的 JSONL 会话日志中，能确认使用 DeepSeek 的会话用量（如 `deepseek-v4-flash`、`deepseek-v4-pro`）。DeepSeek 无需设为 Codex 主模型，只要实际使用过即可被统计。
- **Reasonix 任务**：Helper 自己记录的 Reasonix 任务统计，仅纳入能确认使用 DeepSeek 的任务（例如默认模型为 `opencode/deepseek-v4-flash`）；旧任务缺少模型信息时会安全跳过并在诊断中说明。

统计只在后台执行，单个损坏、半写或暂时锁定的文件会被自动忽略，不会影响整体结果；页面只显示数量级诊断，不展示文件路径、对话正文或任何秘密。

**修复历史统计**只采用严格证据补写旧 Reasonix 状态模型：关联 Reasonix 会话文件或 meta 中的模型、manifest 的 `executionModel`/`model`、Review Packet 中独立的 `Model:` 行明确为 DeepSeek 时才补写；报告正文、当前默认模型、项目名、任务名都不作为证据。明确为非 DeepSeek 或证据冲突时会安全跳过，已补写会幂等跳过；完成后自动刷新缓存统计。

## 官方账号 JSON：与官方客户端互通

在“迁移中心”可以单独处理官方账号的标准登录 JSON：

1. 点击“选择目录并批量导出 JSON”，已保存的官方账号会分别导出为 `账号名称.json`，不会使用固定的 `auth.json` 文件名；同名时会自动加序号。
2. 点击“选择多个 JSON 并导入”，可一次选择多个这类文件。软件会先验证每一个文件都是有效的 Codex 官方登录 JSON，再保存到本机保险库。
3. 导入不会替换 `CODEX_HOME\auth.json`，也不会自动切换当前账号；请到“连接中心”确认后手动切换。

这些 JSON 含有登录令牌，相当于账号凭据。只保存在可信本地目录或加密介质中，不要发送给他人；如需跨电脑长期保存，更推荐使用带迁移口令的 `.chbundle`。

## 快照与迁移有什么不同

| 功能 | 适合场景 | 能否跨电脑 |
| --- | --- | --- |
| 快照中心 | 日常保护、误删恢复、项目回退 | 本机恢复为主 |
| 迁移中心 `.chbundle` | 换电脑、重装系统、手动交接 | 可以；需要迁移口令 |

本机快照仓库使用当前 Windows 用户保护。若要跨电脑，请用“迁移中心”导出 `.chbundle`，并保存至少 10 位的迁移口令。

## 常见问题

**扫描不到项目？** 选择项目的上级目录，或者直接选择项目目录再扫描；确认项目顶层有 Git 或常见项目配置文件。

**备份仓库能放 OneDrive/网盘同步目录吗？** 可以，但不要让两台电脑同时写同一个仓库；更稳妥的方式是本机完成快照后，再由同步软件复制。

**快照会包含 `node_modules`、`bin` 吗？** 默认排除可重新生成的缓存/构建目录，但保留 Git 未提交与未跟踪的源文件。

**迁移包导入后为什么没有自动切换账号？** 这是安全设计。导入连接档案只写入本机保险库，确认无误后由你在“连接中心”手动切换。

**遇到“调用线程无法访问此对象”？** 请使用 `v0.1.1` 或更新版本；该问题已修复。
