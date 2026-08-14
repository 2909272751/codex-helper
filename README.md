# Codex Helper

Codex Helper 是面向 Windows 10/11 的 Codex 专属工作台，统一管理官方账号、第三方 Responses API、重要项目、个人 Skills、Codex 配置、加密增量备份与批量迁移。

当前开发版本：`4.0.0`

![Codex Helper Logo](assets/CodexHelper-256.png)

## 下载安装

**Codex Helper v4.0.0** 精简一键安装包（GitHub Release）：

- 精简安装包：`codex-helper-v4.0.0-setup.exe`（依赖 Windows x64 的 **.NET 8 Desktop Runtime**，安装 .NET 8 SDK 也可满足）
- [打开 v4.0.0 Release 页面](https://github.com/2909272751/codex-helper/releases/tag/v4.0.0)
- [直接下载精简安装包](https://github.com/2909272751/codex-helper/releases/download/v4.0.0/codex-helper-v4.0.0-setup.exe)
- [微软官方 .NET 8 下载页](https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0)

> 若安装器提示缺少运行库，请先安装 **.NET 8 Desktop Runtime（Windows x64）**，再重新打开并运行本安装包。自 `v3.3.3` 起项目只发布精简安装包与对应的 SHA-256 校验文件，不再提供完整离线安装包或便携 ZIP。

## 连接中心与协作开发

连接中心统一管理官方账号、原生 Responses API 与 Sub2API。Base URL 支持填写服务根地址、`/v1` 或完整的 `/responses` 地址；Helper 会去重后缀，并让 Codex 统一调用 `/responses`。第三方 Responses API 是普通主模型连接，不冒充 Codex 原生子智能体。

旧版本的“Responses 子智能体”档案会显示为“旧版子智能体档案”；选中后点“修复旧 Responses 档案”可清理导致任务正文丢失的旧 provider/worker/强制委派规则，并把它转为普通 Responses API 档案，档案和 DPAPI 加密的 API Key 都会保留。

### 协作开发：执行器选择（Reasonix / DeepSeek Harness）

独立的“协作开发”页面（不在“连接中心”）顶部提供**实现执行器**选择：`关闭协作` / `Reasonix` / `DeepSeek Harness（预览）`，选择会持久化并按执行器同步 Helper 管理的全局指导与 executor skill，Reasonix 与 Harness 设置分区显示。旧版已启用 Reasonix 的设备升级后仍保持 Reasonix；未启用则保持关闭（Off），不会因旧设置意外开启 Harness。

选择 Reasonix 时：实现类任务按规模三档路由——微任务（≤2 文件 / 约 80 行、低风险、无跨模块接口）由 GPT 直接实现；其余交给单个 Reasonix 合同；仅中大型且含两个独立模块、接口冻结、写集合不重叠、可机械合并时才 Reasonix 有限并行。页面可管理 Reasonix 默认模型、权限、执行强度、最近任务（停止/重试/返回原任务）以及 DeepSeek 缓存统计。

选择 DeepSeek Harness 时：GPT 仍负责规划与验收，Harness 负责实现；任务优先进入同一个持久 Web Host 会话，用户可在官方 Harness Web UI 实时查看事件并干预。Harness 设置分区只检测并启动你已有环境中的 Node/Harness，**不安装** Node、npm 包或 Harness；**可用性由入口与能力探测决定**——Harness 是否可用不再用版本硬白名单，而是由实际入口、CLI、Web profile 与中继能力探测决定，版本只用于风险提示和诊断（以已知基线 `@deepseek-ai/dsh@0.1.0-rc.5` 为参照分档：已知基线 / 同系列新版本 / 跨次版本未验证 / 跨主版本未验证 / 非法损坏，禁止静默使用 `latest`）。入口完整且必要能力通过时，未知新版本（如 rc.7、0.1.0、0.2.0）也可直接使用，界面显示“新版本已验证”；跨主版本显示更醒目的警告。Node 需 `22.19+`（LTS）或 `24+`；Web Host 仅监听 `127.0.0.1`。探测使用绝对 `node.exe` + dsh `lib/bin.js` 做无副作用 `--version`/`--help` 检查（不依赖 PATH、不安装或更新包），结果按 node/dsh 入口路径、文件时间/大小与实际版本缓存，升级或文件变化自动失效，损坏/无权限/旧结构安全重建，且绝不写入凭据、环境变量值或任务正文；“重新检测”会强制绕过缓存。当前机器没有 Node 是正常降级场景：诊断会解释缺少什么并提供官方下载入口，开启按钮不会误报成功。Web Host 状态与自动中继状态分开：CLI/Web profile 通过只代表能启动 Web；只有任务提交、事件流、取消全部由运行时能力探测确认才宣称“实时协作可用”，否则界面诚实显示“Web 可用但自动中继不可用”，不伪造会话或回退成不可见 headless 后仍声称实时可见。

`3.3.1` 修复 Reasonix 1.19.x 在“完全权限”下因旧权限参数而于首轮模型调用前立即退出的问题；完全权限现在使用经实际写文件验证的兼容模式。

`3.3.2` 收敛视觉验收职责：Reasonix 不截图、不看图、不作视觉结论（禁止 PrintWindow、BitBlt、离屏渲染与像素分析等），所有视觉验收归 GPT 独立完成；Reasonix 最多做一次“窗口能启动/响应/退出/无残留”的事实型 GUI 烟测，失败即记录并收敛，不再为截图反复诊断环境。

`3.3.3` 收紧启动扫描：Helper 启动扫描不得执行 Reasonix Desktop/launcher/update-helper，只能探测真实 CLI；任何来源命中 Desktop 启动器或辅助可执行文件即被排除，形成单一安全边界。

`3.3.4` 阻止 Reasonix 最终门禁循环：托管 Reasonix 永远使用 `balanced` profile（Strict 映射为 `balanced + high`，Fast 为 `balanced + low`），任何执行强度都不再生成 `delivery`，也绝不自动启动 review/security-review/explore 子代理（GPT 是唯一评审者）。执行器在任务目录派生 `WORKER_ACCEPTANCE.md`，只含 manifest 的 workerChecks 并经职责过滤（视觉/GUI/发布类移交 GPT，普通 build/test/source inspection 保留），Reasonix 提示只读取该派生合同而不读完整 `ACCEPTANCE.md`。退出码 1 + final_readiness 事件 + 实际活动证据会被谨慎分类为“等待 GPT 复核（Reasonix 最终门禁未通过）”，既不伪装 worker 成功，也不当作普通代码开发失败。

`3.3.5` 优化 P0/P1 流程：① 收紧 workerCheck 职责过滤——只有明确要求截图/看图/像素分析/真实 GUI 操作或烟测/DPI 视觉判断/颜色遮挡视觉判断/屏幕捕获/发布安装包等才移交 GPT，普通 XAML/XML/DOM 布局数学、图片资源存在性、GUI 项目构建等结构测试保留给 worker，并识别“不截图/不看图/不启动 GUI/不进行视觉判断”等否定约束说明（不当作待执行检查，也不误伤相邻结构测试）。② 消除合同矛盾：Reasonix 只读 `SPEC.md`/`HANDOFF.md`/`manifest.json`/`WORKER_ACCEPTANCE.md`，从不读 `ACCEPTANCE.md`，只写 `EXECUTION_REPORT.md`（`REVIEW_PACKET.md` 由 Helper 自动生成）；HANDOFF 明确“允许读取/允许修改/直接依赖”，目标明确时禁止递归扫描。③ DeepSeek Flash/Pro 托管默认 effort：Fast/Standard/Auto 默认 low，仅 Strict 或 major 为 high。④ 动态软预算默认值调整为聚焦修复 16 / 普通功能 35 / 完整 major 56 步（manifest `budgetSteps` 始终优先）。⑤ 生成/指导精简：集中读取与集中修改（同一文件无变化不重复读取、先形成一次修改集再批量编辑）、workerCheck 去重、GUI 烟测最多一次、GPT 采用增量验收（重跑受影响聚焦检查，仅高风险/release/合同要求时才跑完整回归）。

`3.3.6` 提升 Reasonix 可靠性与进度体验：① 统一标准 JSON——Reasonix 任务状态统一走标准 `System.Text.Json` 序列化与 UTF-8 无 BOM 原子写入，Windows 路径反斜杠正确转义、中文路径不乱码，新写文件同时兼容 PowerShell `ConvertFrom-Json` 读取，并保留宽容读取旧损坏状态的能力。② 预计剩余百分比单调保护——运行中状态持久化 `RemainingPercent`（5%–100%），同一 attempt 内只降不升，进度源从步骤切换到 workerChecks、状态刷新或重启恢复时均不回升；完成时 UI 不显示预计剩余，新任务/新 attempt 重新初始化，历史状态缺字段保持兼容。③ 标准 `PROGRESS.json` 协议——定义并验证 `stage`/`summary`/`updatedUtc`/`completedChecks`/`totalChecks`/`currentCheck`/`checks`（名称+状态 pending/running/passed/failed）字段；Reasonix 提示明确要求在 workerCheck 前后原子更新任务目录内 `PROGRESS.json`，Helper 从事件安全推导基础进度作回退，绝不伪造视觉/GPT 检查为 worker 完成；损坏、越界、陈旧内容安全忽略。④ 合同启动前体检与安全归一化——启动前检查 HANDOFF 是否要求 Reasonix 读 `ACCEPTANCE`/写 `REVIEW_PACKET`、workerChecks 重复或混入视觉/发布、DeepSeek 错误使用 delivery profile、普通 small/medium 显式 high/max、HANDOFF 缺少允许读取/修改/直接依赖；可安全归一化的（去重、职责过滤、强制 balanced、移除矛盾执行要求）自动应用于派生运行合同，原始合同文件不被覆盖，无法安全修正的合同阻止启动并给出中文诊断，状态/UI 显示归一化摘要。⑤ 影响范围增量验收映射——Helper 按实际 changed files 与合同风险生成 GPT 验收建议（focused/full/release/security/visual）写入 `REVIEW_PACKET.md`：文案/文档、单策略/纯函数 → focused；UI/XAML → focused+visual；凭据/加密/备份/迁移 → security+full；installer/版本/发布脚本 → release+full；多核心模块或无法识别 → full；GPT 按建议做增量验收，高风险、release、security 或合同明确要求时完整回归。

`3.4.0` 新增 Reasonix 多任务并行调度领域层（合同 A）：独立、可测试、不改 UI 的纯函数调度服务 `ReasonixParallelScheduler`，支持按任务合同摘要（taskId/displayName/taskDirectory/projectRoot/allowedWriteFiles/dependencyTaskIds/state）决策。默认最大并发 2（合法范围 1..3）；已运行任务占槽，依赖未成功则等待，写文件集合重叠则冲突并转串行等待，不同项目/不同文件且有空槽则可启动。路径比较在 Windows 下大小写不敏感、统一绝对/相对分隔符，目录所有权覆盖其子文件，通配符或无法规范化的范围保守视为冲突；每项任务返回 ready/running/waiting_dependency/waiting_conflict/queued/completed/failed 决策与可读原因、冲突任务 ID，并提供 running/queued/blocked/completed/failed/maxConcurrency 快照统计。不创建 worktree 或进程，不修改现有单任务执行行为。

`3.4.0` 并行功能整合（合同 D）：① “模型与执行策略”新增并行设置——智能拆分、独立任务并行、最大并发（1..3，默认 2）、自动 worktree、超预算收敛，保存到 `AppSettings`（旧配置缺失字段自动保留默认值，保存时最大并发收敛到 1..3）。② 新增 `ReasonixWorktreePreparationService`——为每个任务生成安全唯一、限定在配置 worktree 根内的隔离目录（优先 git worktree），运行前验证 Git 仓库/HEAD/目标目录/任务 ID/允许写文件范围，宽泛通配符或路径越界阻断，触及脏文件或依赖未跟踪文件转串行；只读清理计划，本版本不真实创建/删除 git worktree，不改凭据/连接/备份。③ 并行任务中心——最近 Reasonix 任务改为列表（运行中优先、按更新时间倒序），选择任务后操作作用于所选任务，用 `ReasonixParallelScheduler` 生成统计与等待/冲突原因，状态文件无合同摘要时仍显示基础状态不崩溃。④ 更新全局指导文字——实现类任务先智能拆分，可独立且写集合不重叠时最多按设置并行，公共文件/依赖/脏文件风险串行，Reasonix 执行、GPT 合并验收，不强制每个请求并行。

`3.4.1` 新增任务规模三档路由：新增可单元测试的纯函数服务 `ReasonixTaskRouter`，按合同规模返回 `gpt_direct` / `reasonix_single` / `reasonix_parallel_candidate` 及中文原因——微任务（≤2 文件 / 约 80 行、低风险、无跨模块接口、一次聚焦测试可验收）由 GPT 直接实现；命中任一较大/高风险/跨模块/用户指定条件则走 Reasonix 单合同；仅中大型且含两个独立模块、接口冻结、写集合不重叠、无需接线、可机械合并时才 Reasonix 有限并行。边界：GPT 微修中途越过阈值即转 Reasonix 合同；Reasonix 主体完成后仅剩 ≤2 文件/80 行低风险的验收微修由 GPT 直接修，不再启动新 Reasonix；需接线退回单合同。更新全局协作指导文字与协作开发页说明，新增边界测试覆盖 2 文件 80 行 GPT、3 文件/81 行 Reasonix、高风险单文件 Reasonix、用户指定 Reasonix、验收微修 GPT、可机械合并并行候选、需接线退回单合同。不修改执行器协议、任务进度算法、连接/备份功能。

`3.4.1` 调度可靠性收敛：① **漏报告自动恢复**——当 Reasonix 退出码为 0、存在实际模型/工具活动，且能证明存在本次新增 diff 或 workerChecks 已通过，但缺少 `EXECUTION_REPORT.md` 时，Helper 自动生成最小、明确标注“自动恢复”的执行报告与 Review Packet，把任务置为等待 GPT 验收的完成态；绝不伪造测试通过，无活动、无改动、模型失败或非零退出均不恢复。② **连续失败熔断**——同一任务 `missing-report` 或 `model-run-failed` 累计 2 次（含 `attempts/` 归档）后禁止一键重试，UI 给出明确原因要求先检查合同、模型与日志；用户主动停止不计入；保留同 taskId 归档与已通过检查，禁止为“仅补报告”创建全新实现合同。③ **状态归一化**——完成/失败/取消/等待 GPT 验收的剩余百分比归零（运行中才允许 5%–100%），阶段归一（完成必为 `done`，其余终态保留失败发生阶段，绝不残留进行中百分比语义），状态文件继续统一走标准 JSON 原子写入。④ **历史预算校准**——按同项目同复杂度最近成功任务的实际 steps 推导软预算（样本不足回退默认、异常值截尾、结果有合理上下限），不使用 token 数或运行时长作为硬上限。⑤ **避免重复验收**——自动恢复报告、Retry Context、Review Packet 明确记录已通过 workerChecks（排除视觉/GPT 项），后续只运行未完成检查，不重复构建或重复视觉检查。⑥ 协作开发页说明正在运行的旧 Codex 任务不会实时重载全局指导，建议主要阶段完成后新建任务。

### Reasonix CLI 自动发现与手动选择

Helper 会从多个来源自动发现 Reasonix CLI（不进行全盘递归扫描）：已保存的用户选择、Reasonix Windows 卸载注册表（HKCU/HKLM、32/64 位视图，支持安装根 `D:\Apps\Reasonix` 与 `versions\vX.Y.Z` 版本目录）、正在运行的 `reasonix-desktop.exe`/`Reasonix.exe`、常见安装位置、PATH，以及 npm `reasonix.cmd` 兜底。对每个候选做版本与 `doctor --json` 快速能力探测后择优：兼容新诊断结构的正式 Desktop/CLI 优先于 npm 旧版、版本较新者优先、单个损坏候选不影响其他候选。已保存路径被删除或不再兼容时自动重新发现并迁移，并在诊断中说明。

协作开发页主状态区域显示当前实际 CLI 路径、版本、来源与协议兼容性；“重新扫描”可立即重新探测；“选择 CLI 文件”可手动指定（先验证、成功后才持久化，取消或无效文件不改变现有状态，启用状态下切换后托管脚本自动刷新到新路径）。`doctor` 即使退出码非零，只要 stdout 含有效 JSON 仍可读取模型列表并保留警告；错误信息始终包含原因、路径、版本与退出码，凭据类敏感字段会被脱敏。

### DeepSeek 作为普通主模型

同一个官方 DeepSeek 档案也可以作为普通 Codex 主模型使用：当且仅当连接为官方 `api.deepseek.com` 且模型为 `deepseek-v4-flash` 时，Helper 会根据本机完整 Codex 模型模板生成临时合并目录，让 GPT 模型仍保留在模型列表中，并加入 text-only 的 DeepSeek 条目及自动上下文压缩参数。切回官方账号或其他 API 时会恢复接管前的目录引用；用户自有模型目录不会被修改。找不到完整模板时会在写配置前停止切换。

当前适配边界以 DeepSeek 官方 Responses 实现为准：Helper 仅为 `deepseek-v4-flash` 启用此目录适配；`previous_response_id`、`conversation` 和 `truncation` 尚不支持，图片/文件输入不支持，reasoning 的 `encrypted_content` 也不支持，缓存由服务端自动处理。因此 DeepSeek 只能作为普通 Codex 主模型使用，不能伪装成接收加密任务正文的 Codex 原生 worker。

## 设计原则

- 本地优先，秘密默认加密。
- 切换前快照，写入使用事务，失败可以回滚。
- 项目备份包含 Git 未提交和未跟踪数据，但排除可重建缓存。
- 批量导入先预览，默认不激活账号、不启用陌生 Skill、不覆盖项目。
- 主程序损坏时仍可使用独立救援程序恢复。

## 当前功能

- 官方账号、Responses API 与 Sub2API 统一档案；支持安全删除和缺失保险库档案自动清理。
- 官方账号可直接检查登录状态与官方返回的额度摘要；账号 JSON 支持官方 Codex、CPA Codex 与 Sub2API 格式的批量导入导出。
- 官方账号健康详情提供套餐、双额度窗口、重置时间和本机检测历史；可串行刷新全部官方账号。
- API 切换同时同步 `config.toml`、状态数据库与会话 JSONL，任何一步失败都回滚。
- 自动发现个人 Skills、Codex 关键数据与 Git 项目，一键创建认证加密增量快照。
- `.chbundle` 批量导出/导入；账号令牌和 API Key 在已加密迁移包内再次独立加密。
- 直接迁移旧版 `codex-account-switcher` 与 `codex-api-switcher` 数据，导入后不自动激活。
- 独立 Rescue 程序可在主程序无法启动时将快照恢复到新目录。

## 快速使用

1. 在“设置”确认 `CODEX_HOME`，在“项目与数据”选择需要保护的项目。
2. 在“快照中心”选择仓库并创建首个快照。仓库不要放在被保护目录内部。
3. 在“连接中心”保存账号/API 档案；切换时先彻底退出 Codex。
4. 在“迁移中心”设置至少 10 位口令，批量导出普通数据与连接档案。
5. 新机器先预览迁移包，再分别导入普通文件和连接档案；连接不会自动启用。

本地快照仓库密钥由当前 Windows 用户 DPAPI 保护，适合本机恢复；跨机器迁移请使用带口令的 `.chbundle`。详细安全模型见 [docs/SECURITY.md](docs/SECURITY.md)。

第一次使用请阅读 [中文使用教程](docs/USER_GUIDE_zh-CN.md)，其中解释了“工作区根目录”和“备份仓库”的区别。

## 项目结构

- `src/CodexHelper.App`：WPF 主程序。
- `src/CodexHelper.Core`：发现、连接、备份、迁移和恢复核心。
- `src/CodexHelper.Rescue`：独立救援程序。
- `src/CodexHelper.CredentialHelper`：稳定、无明文落盘的 API Key 凭据助手。
- `tests/CodexHelper.Core.Tests`：无需第三方测试框架的行为测试入口。
- `docs/ACCEPTANCE.md`：需求与验收账本。

## 构建

需要 .NET 8 SDK。仓库脚本优先读取 `DOTNET_EXE`，否则使用 PATH 中的 `dotnet`。

```powershell
powershell -ExecutionPolicy Bypass -File scripts\test.ps1
powershell -ExecutionPolicy Bypass -File scripts\build.ps1
powershell -ExecutionPolicy Bypass -File scripts\build-release.ps1
```

精简发布构建输出到 `artifacts/v4.0.0/`，只生成 `codex-helper-v4.0.0-setup.exe` 与 `codex-helper-v4.0.0-sha256.txt`：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-release.ps1
```

`codex-helper-v3.4.0-setup.exe` 是精简一键安装包：提供安装向导、可选桌面快捷方式、开始菜单入口和卸载项；它需要电脑已安装 **.NET 8 Desktop Runtime**，缺少时会可靠检测并一键打开微软官方下载页。自 `v3.3.3` 起不再发布完整离线安装包或便携 ZIP。卸载不会删除 Codex 数据、账号保险库或备份目录。
