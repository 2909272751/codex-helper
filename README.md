# Codex Helper

Codex Helper 是面向 Windows 10/11 的 Codex 专属工作台，统一管理官方账号、第三方 Responses API、重要项目、个人 Skills、Codex 配置、加密增量备份与批量迁移。

当前正式版本：`2.1.0`

![Codex Helper Logo](assets/CodexHelper-256.png)

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

完整发布构建输出到 `artifacts/v2.1.0/`，文件名均包含版本号。若需要制作精简安装包，运行 `powershell -ExecutionPolicy Bypass -File scripts\build-runtime-required-installer.ps1`，输出到 `artifacts/v2.1.0-runtime-required/`。

`codex-helper-v2.1.0-setup.exe` 是约 2.5MB 的精简一键安装包：提供安装向导、可选桌面快捷方式、开始菜单入口和卸载项；它需要电脑已安装 **.NET 8 Desktop Runtime**。`codex-helper-v2.1.0-windows-x64-portable.zip` 是免安装、内含运行时的完整包，适合未安装 .NET 8 Runtime 或希望直接解压使用的电脑。卸载不会删除 Codex 数据、账号保险库或备份目录。
