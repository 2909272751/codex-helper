# Codex Helper 开发约定

- 当前恢复基线为 `v4.4.7`。用户已撤回 `v4.4.8` 的后台独立 GPT 验收/自动修复，以及未完成的原任务唤醒改造；后续开发不得从旧合同、历史提交或安装包自动恢复这些功能，重新引入须有用户新的明确要求。

- 主版本源是根目录 `Directory.Build.props` 中的 `Version`。
- v4.4.13 起，同一规范化开发目录续用 Helper 登记的已停止会话，不要求相同 rootCauseKey。不同合同必须排队后发送自己的提示，不得接管或冒用前序运行中的合同；取消排队不取消前序。TaskId、指纹、组键和报告仍独立，跨目录隔离、关闭续用和已取消任务的禁止自动重提继续生效。
- 新版 DSH Gateway 从 session/follow 首快照读取可信 cursor、最小终态元数据与 modelSelection.next；禁止以全局 catalog.default 冒充会话模型，禁止对新版调用旧 session.history。报告门禁未通过的前轮不得在连续上下文中宣称成功。
- 所有可分发 EXE、ZIP 和安装包名称必须包含 `vX.Y.Z`。
- 不在源码、测试夹具、日志、README 或发布产物中写入真实 token、API Key、账号文件或私人服务地址。
- 涉及 `auth.json`、`config.toml`、SQLite、Skills 或项目原位恢复的修改，必须覆盖安全快照、原子提交、失败回滚和路径越界测试。
- UI 长任务必须异步、可取消，并区分成功、部分成功、失败和取消。
- Harness 合同必须按事件 `seq` 去重；重复文本或重复工具调用达到保护阈值时立即取消当前 Session 并交给 GPT，禁止自动重提合同。接回旧 Session 前必须核验其仍为 `running=true`，同一任务须具备跨进程单飞保护。
- 启动 `CodexHelper.HarnessRunner` 后，命令工具返回“运行中”、PTY/session id、提交成功或短暂无输出都只是中间态，绝不是执行完成或可标记受阻的信号。GPT 必须持续等待同一个 Runner；观察通道断开时以任务目录状态和 Harness `session.list` 接回同一会话，绝不重提合同。
- 只有同时满足 **Runner 已退出**、`HARNESS_STATUS.json` 是非 `running`/`starting` 的真实终态、以及成功路径的 `EXECUTION_REPORT.md` 已通过门禁，才能结束实施回合。`awaiting-gpt` 只表示等待 GPT 验收；事件流静默、Node relay 退出、WebSocket 断流、Host 暂不可达、长时间推理或状态文件暂未更新，均不得单独标记“受阻”或失败。重复调用触发的 Harness 防循环取消是唯一例外，必须如实报告终态且禁止自动重提。
- **额度纪律**：Runner 运行期间只保持受管进程等待；不得用 Codex 定时 heartbeat、重复短轮询或重复读取 DSH `session.history` 来确认存活。存活证据仅取固定大小的 `HARNESS_STATUS.json` / `PROGRESS.json`、本地 Runner PID 与 `session.list` 的 `running` 布尔值；除终态诊断外，禁止把 DSH 消息正文、推理流、工具参数或全量历史带回 Codex 上下文。
- GPT 只执行一次计划提交和一次终态验收。状态探针输出必须限制为状态、更新时间、PID 存活、会话 `running`、报告门禁结果；`rg`/日志/差异读取必须限定文件与行数，禁止把递归目录清单、全量日志或无关源码回灌到当前线程。续接工作流用 `rootCauseKey` 和任务目录的压缩上下文，不复制旧合同/报告全文。监控或打包等轻量操作使用最低可用推理强度；仅复杂设计、风险修复与最终验收使用较高强度。
- 构建：`powershell -ExecutionPolicy Bypass -File scripts\build.ps1`
- 测试：`powershell -ExecutionPolicy Bypass -File scripts\test.ps1`
- 发布构建：`powershell -ExecutionPolicy Bypass -File scripts\build-release.ps1`
