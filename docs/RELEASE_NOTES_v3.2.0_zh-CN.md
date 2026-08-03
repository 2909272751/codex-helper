# Codex Helper v3.2.0 发布说明

本页为 `v3.2.0` 的中文发布说明，与当前正式版 `3.2.0` 一致。

## 主要功能

- **连接中心**：统一管理官方账号、原生 Responses API 与 Sub2API；支持登录状态与额度检查、测试 API、修复旧 Responses 档案，以及一键切换 Codex 连接。
- **协作开发（独立页面）**：集中管理 GPT + Reasonix 协作——GPT 负责规划与验收，Reasonix 独立执行项目文件修改，Codex 原生子智能体保持关闭。页面可管理 Reasonix 默认模型、权限、执行强度、最近任务（刷新进度、停止、重试、返回原 Codex 任务）以及 DeepSeek 缓存统计。
- **选择要保护的项目**：扫描并发现 Git / 常见项目文件，把项目加入日常备份范围。
- **备份与恢复**：加密增量快照本机 Codex 关键数据、个人 Skills 与已保护项目，可恢复到新目录。
- **迁移中心**：带迁移口令的 `.chbundle` 批量导出/导入，官方账号标准 JSON 互通，旧版切换工具数据直接迁移。
- **健康中心**：检查 Codex、凭据、配置与备份仓库，不显示秘密内容。
- **救援程序**：主程序无法启动时仍可恢复快照。

## 升级说明

- 从任意旧版本升级到 `v3.2.0` 时，本机保险库（官方账号、API 档案、Sub2API）、项目选择、快照仓库与迁移包保持兼容，无需重新配置。
- 旧“Responses 子智能体”档案显示为“旧版子智能体档案”；可在“连接中心”选中后点击“修复旧 Responses 档案”，将其转为普通 Responses API 档案，档案与加密 Key 都会保留。
- 旧的 DeepSeek 编码子智能体流程已废弃并移除，DeepSeek 现在作为普通 Codex 主模型使用；协作开发由独立页面中的 Reasonix 承担。

## 安装包区别

| 安装包 | 说明 |
| --- | --- |
| `codex-helper-v3.2.0-setup.exe` | 精简一键安装包：提供安装向导、桌面快捷方式、开始菜单入口与卸载项；需要电脑已安装 **.NET 8 Desktop Runtime**，缺少时一键打开微软官方下载页。 |
| `codex-helper-v3.2.0-setup-full.exe` | 完整离线安装包：内置运行时，适合未安装 .NET 8 Runtime 的电脑。 |
| `codex-helper-v3.2.0-windows-x64-portable.zip` | 便携包：免安装、内含运行时，解压即可使用。 |

卸载不会删除 Codex 数据、账号保险库或备份目录。

## 已知限制

- 独立“协作开发”页面中的 Reasonix App 会话可能延迟同步；Helper 显示实时任务状态，需等任务结束产生会话文件后 Reasonix App 才会同步。
- 由 Helper 启动的任务重试无法自动唤醒既有 GPT 轮次，完成后请回到原 Codex 任务继续验收。
- DeepSeek 仅适配官方 `deepseek-v4-flash` 的 Responses 接口：不支持 `previous_response_id`、`conversation`、`truncation` 以及图片/文件输入，也不支持 reasoning 的 `encrypted_content`，只能作为普通 Codex 主模型使用。
- 连接切换与协作开发开启后需重新打开 Codex 才生效。
