# Codex Helper v4.0.0

本次大版本新增可选择的协作执行器架构，并引入官方 DeepSeek Harness 开发者预览支持。

## 主要变化

- 协作开发可选择关闭、Reasonix 或 DeepSeek Harness，旧版已启用 Reasonix 的设备升级后保持原选择。
- GPT 继续负责规划和最终验收；执行器负责实现，Reasonix 原有三档任务路由完整保留。
- Harness 自动发现 Node 与全局 npm 安装，不依赖 Codex 启动时继承的旧 PATH。
- Harness 使用绝对 `node.exe + @deepseek-ai/dsh/lib/bin.js`，支持安装后无需重启即可重新检测。
- 取消 Harness 版本硬白名单：版本只作风险提示，实际可用性由 CLI、Web profile 和运行时能力探测决定。
- 新版本升级或入口文件变化会自动使能力缓存失效；手动“重新检测”可强制绕过缓存。
- Web Host 与自动任务中继分开判断，未确认任务提交、事件流和取消能力时不会误报实时协作成功。
- Harness Web Host 仅绑定本机 `127.0.0.1`；凭据和任务正文不会进入命令行或能力缓存。

## 安装要求

- Windows 10/11 x64。
- .NET 8 Desktop Runtime x64；安装器缺失时会显示官方安装指引。
- Reasonix 与 DeepSeek Harness 均为可选外部执行器，不随安装包捆绑。

## 发布资产

- `codex-helper-v4.0.0-setup.exe`
- `codex-helper-v4.0.0-sha256.txt`

本版本继续只发布依赖系统 .NET 8 Desktop Runtime 的精简安装包，不捆绑 .NET、Node、Harness、浏览器内核或其他可选运行时。
