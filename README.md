# Codex Helper

Codex Helper 是面向 Windows 10/11 的 Codex 专属工作台，统一管理官方账号、第三方 Responses API、重要项目、个人 Skills、Codex 配置、加密增量备份与批量迁移。

当前开发版本：`3.3.1`

![Codex Helper Logo](assets/CodexHelper-256.png)

## 连接中心与协作开发

连接中心统一管理官方账号、原生 Responses API 与 Sub2API。Base URL 支持填写服务根地址、`/v1` 或完整的 `/responses` 地址；Helper 会去重后缀，并让 Codex 统一调用 `/responses`。第三方 Responses API 是普通主模型连接，不冒充 Codex 原生子智能体。

旧版本的“Responses 子智能体”档案会显示为“旧版子智能体档案”；选中后点“修复旧 Responses 档案”可清理导致任务正文丢失的旧 provider/worker/强制委派规则，并把它转为普通 Responses API 档案，档案和 DPAPI 加密的 API Key 都会保留。

### 协作开发：GPT + Reasonix

独立的“协作开发”页面（不在“连接中心”）管理 GPT + Reasonix 协作：GPT 负责规划与验收，Reasonix 独立执行项目文件修改，Codex 原生子智能体保持关闭。页面可管理 Reasonix 默认模型、权限、执行强度、最近任务（停止/重试/返回原任务）以及 DeepSeek 缓存统计。Reasonix App 会话可能延迟同步，Helper 显示实时任务状态。

`3.3.1` 修复 Reasonix 1.19.x 在“完全权限”下因旧权限参数而于首轮模型调用前立即退出的问题；完全权限现在使用经实际写文件验证的兼容模式。

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

完整发布构建输出到 `artifacts/v3.3.1/`，文件名均包含版本号。若需要制作精简安装包，运行 `powershell -ExecutionPolicy Bypass -File scripts\build-runtime-required-installer.ps1`，输出到 `artifacts/v3.3.1-runtime-required/`。

`codex-helper-v3.3.1-setup.exe` 是精简一键安装包：提供安装向导、可选桌面快捷方式、开始菜单入口和卸载项；它需要电脑已安装 **.NET 8 Desktop Runtime**，缺少时会一键打开微软官方下载页。`codex-helper-v3.3.1-setup-full.exe` 是内置运行时的完整离线安装包，适合未安装 .NET 8 Runtime 的电脑。`codex-helper-v3.3.1-windows-x64-portable.zip` 是免安装、内含运行时的完整包，解压即可使用。卸载不会删除 Codex 数据、账号保险库或备份目录。
