# Codex Helper 开发约定

- 主版本源是根目录 `Directory.Build.props` 中的 `Version`。
- 所有可分发 EXE、ZIP 和安装包名称必须包含 `vX.Y.Z`。
- 不在源码、测试夹具、日志、README 或发布产物中写入真实 token、API Key、账号文件或私人服务地址。
- 涉及 `auth.json`、`config.toml`、SQLite、Skills 或项目原位恢复的修改，必须覆盖安全快照、原子提交、失败回滚和路径越界测试。
- UI 长任务必须异步、可取消，并区分成功、部分成功、失败和取消。
- Harness 合同必须按事件 `seq` 去重；重复文本或重复工具调用达到保护阈值时立即取消当前 Session 并交给 GPT，禁止自动重提合同。接回旧 Session 前必须核验其仍为 `running=true`，同一任务须具备跨进程单飞保护。
- 构建：`powershell -ExecutionPolicy Bypass -File scripts\build.ps1`
- 测试：`powershell -ExecutionPolicy Bypass -File scripts\test.ps1`
- 发布构建：`powershell -ExecutionPolicy Bypass -File scripts\build-release.ps1`
