# Codex Helper v3.3.0 发布说明

本页为 `v3.3.0` 的中文发布说明，与当前正式版 `3.3.0` 一致。

## 主要功能

- **Reasonix CLI 多来源自动发现**：Helper 从已保存的用户选择、Reasonix Windows 卸载注册表（HKCU/HKLM、32/64 位视图）、正在运行的 `reasonix-desktop.exe`/`Reasonix.exe`、常见安装位置（`%LOCALAPPDATA%\Programs\Reasonix`、`%LOCALAPPDATA%\reasonix`、`%ProgramFiles%\Reasonix` 等）、PATH 与 npm `reasonix.cmd` 兜底中自动发现 Reasonix CLI；不进行全盘递归扫描。支持自定义盘安装根 `D:\reasonix` 与 `versions\vX.Y.Z` 版本目录。
- **能力探测与择优**：不再“找到第一个即使用”。对每个候选快速探测版本与 `doctor --json`，去重后按兼容性择优：用户固定且兼容的路径优先，兼容新诊断结构的 Desktop/正式 CLI 优先于 npm 旧版，版本较新者优先；单个损坏候选不会阻断其他候选。
- **保存路径失效自动恢复**：已保存路径被删除或不再兼容时自动重新发现并切换到可用候选，并在诊断中说明，不会静默回退到更差候选。
- **doctor 容错诊断**：`doctor` 退出码非零时仍先尝试解析 stdout 中的有效 JSON，`config/providers` 可用则模型列表照常读取并保留警告；JSON 容忍 BOM、ANSI 转义与前后噪声。错误信息始终包含原因、实际 CLI 路径、版本（可得时）与退出码，凭据类敏感字段自动脱敏。
- **状态拆分**：诊断区分为 CLI 发现、版本/协议兼容、模型配置、凭据/API 健康；API 或 Provider 失败不再伪装成“未安装”，读取本地模型列表不发起额外模型生成请求。
- **协作开发页执行环境行**：主状态区域新增紧凑的执行环境行，显示当前实际 CLI 路径、版本、来源与协议兼容性；提供“重新扫描”与“选择 CLI 文件”两个入口。手动选择先验证、成功后才持久化；取消不改状态；无效文件给出可恢复错误。启用状态下切换 CLI 后托管脚本自动刷新到新路径。
- **任务进度更直观**：最近任务直接显示任务 ID、预计/已运行时间，并把 `workerChecks` 按步骤列出；已完成为绿色、当前步骤为蓝色、待执行为灰色、失败步骤为红色。无任务时复制 ID 与打开目录按钮自动禁用。
- **默认流程更轻量**：新配置默认采用 Standard 执行强度，Fast/Standard 不自动启动复查智能体；任务使用软步骤预算但不设置默认硬上限，避免重复审查和无意义打包，同时保留长任务自然完成的能力。

## 升级说明

- 从任意旧版本升级到 `v3.3.0` 时，本机保险库（官方账号、API 档案、Sub2API）、项目选择、快照仓库与迁移包保持兼容，无需重新配置。
- 已保存的 Reasonix CLI 路径若仍有效，升级后继续优先使用；若路径已失效，Helper 会自动重新发现可用候选。

## 安装包区别

| 安装包 | 说明 |
| --- | --- |
| `codex-helper-v3.3.0-setup.exe` | 精简一键安装包：提供安装向导、桌面快捷方式、开始菜单入口与卸载项；需要电脑已安装 **.NET 8 Desktop Runtime**，缺少时一键打开微软官方下载页。 |
| `codex-helper-v3.3.0-setup-full.exe` | 完整离线安装包：内置运行时，适合未安装 .NET 8 Runtime 的电脑。 |
| `codex-helper-v3.3.0-windows-x64-portable.zip` | 便携包：免安装、内含运行时，解压即可使用。 |

卸载不会删除 Codex 数据、账号保险库或备份目录。

## 已知限制

- 独立“协作开发”页面中的 Reasonix App 会话可能延迟同步；Helper 显示实时任务状态，需等任务结束产生会话文件后 Reasonix App 才会同步。
- 由 Helper 启动的任务重试无法自动唤醒既有 GPT 轮次，完成后请回到原 Codex 任务继续验收。
- 旧版 npm Reasonix（如 0.53.x）的 `doctor --json` 不含 `providers`，会被明确标记为协议不兼容并提示改用 Reasonix Desktop；在没有任何兼容候选时仍可作兜底使用。
- 连接切换与协作开发开启后需重新打开 Codex 才生效。
