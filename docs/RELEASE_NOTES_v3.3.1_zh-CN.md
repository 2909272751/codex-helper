# Codex Helper v3.3.1 发布说明

`v3.3.1` 是 Reasonix 1.19.x 协作执行兼容修复版，替代存在任务瞬间失败问题的 `v3.3.0` 作为最新推荐版本。

## 修复

- 修复“完全权限”仍传入旧 `bypassPermissions` 参数，导致任务在首轮模型调用前以 0 token、0 工具调用瞬间结束的问题；改用实际写文件验证通过的 `auto` 权限模式。
- 修复权限参数被追加在任务正文之后的问题；所有 CLI 选项现在都位于最终任务正文之前，兼容 Reasonix 1.19.x 参数解析。
- 修复 Standard 执行强度传入 DeepSeek 不接受的 `medium` effort；调用 CLI 时兼容映射为 `high`，Fast 仍使用 `low`。
- 0-turn、0-token 失败现在会显示明确的 CLI/权限兼容诊断和恢复建议，不再只有空泛的“模型运行失败”。

## 验证

- Codex Helper 完整测试 48/48 通过，Release 构建 0 警告、0 错误。
- 使用 OpenCode `deepseek-v4-flash` 完成真实 WPF 桌面日历项目：Reasonix 成功调用工具、写入源码、运行 16 项日期逻辑测试并生成 Release EXE。
- 本机从 `v3.3.0` 覆盖安装到 `v3.3.1` 后，版本、启动和托管脚本均验证正常。

## 下载选择

- `codex-helper-v3.3.1-setup.exe`：精简安装包，需要 .NET 8 Desktop Runtime。
- `codex-helper-v3.3.1-setup-full.exe`：完整离线安装包，内含运行时。
- `codex-helper-v3.3.1-windows-x64-portable.zip`：便携完整包，解压即用。
