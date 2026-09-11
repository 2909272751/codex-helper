# 新电脑基础修复（v4.4.13）

本页只说明 **安装器与 Helper 自带** 的基础修复，供换新电脑后免从开发目录复制。
精简安装包 **不含 DSH 本体、Node.js、任何密钥或账号配置**，也不含 npm 依赖缓存；
Helper 只做本机就绪检查与安全的就地修补，不联网安装任何包。

## 1. 88FRP 公网入口识别（以活动进程实际配置为准，双根仅作回退）

- **优先**读取当前正在运行的 `88frpc.exe` / `frpc.exe` 实际使用的配置（其 `-c/--config` 指向的
  `runtime-frpc.toml`）：这是唯一"正在生效"的入口，避免用户版与系统服务版两份同名实例配置
  互相覆盖（旧用户目录里的静止文件曾把修好的信任改回旧 IP）。
  - 只读查询走固定有界的 CIM（最多 3 秒、无窗口、UTF-8）；超时立即清理探针子进程；
    只接受文件名正确且严格落在已知实例根内的路径，越界/reparse 一律拒绝。
  - 不读取、不输出完整进程命令行或配置正文到任何日志。
  - 只做数秒级短缓存（不持久化，避免旧 IP 被固化）；中途读不到活动配置时返回缺失，
    让同步保留既有信任，**绝不回退到静止旧目录的旧 IP**。
- **只有查不到活动进程时**才保守回退到下面两个静止根（跨根去重、单根失败互不影响）：
  - 用户版：`%LOCALAPPDATA%\88frp-node\data\instances`
  - 系统服务版（公司电脑常见）：`%PROGRAMDATA%\88frp-node\data\instances`
- 多个不同 authority 仍判为歧义，绝不猜测；仍然只认 `127.0.0.1:3080` 的 TCP 隧道，
  不读任何 frp 凭据、不硬编码 IP/用户名/盘符。
- 显式传入实例根（测试隔离/诊断）时只扫描该目录，不查询真实进程。

## 2. 安装器实际携带的基础插件

安装器只收集四个 publish 输出，因此基础插件实体 **嵌入 Helper 程序集** 一起发布，
新电脑不需要从开发目录复制，也不需要联网：

| 包名 | 作用 |
| --- | --- |
| `dsh-catalog-fastpath` | 会话目录优先就绪，减少侧边栏等待；实际耗时仍取决于运行环境 |
| `dsh-model-vision-bridge` | 按运行时已声明能力同步图片模态，不猜测模型能力 |
| `dsh-browser-compat` | 明文 HTTP 外网（非安全上下文）下补 `crypto.randomUUID` |

`dsh-browser-compat` 通过 `webServer.tapIndex` 在 `index.html` 的 `<head>` 最前插入一段
classic script：仅在 `crypto.randomUUID` 缺失时用 `crypto.getRandomValues` 生成 UUIDv4。
**不添加任何授权绕过、host 白名单或凭据**；`getRandomValues` 是浏览器在非安全上下文仍提供的熵源。

> `dsh-open-web-access` 不会被自动安装或启用；已有配置保持不动。

## 3. DSH 基础插件部署服务

- 遵循 DSH 官方 Home 解析：显式路径 > `DSH_HOME` > `~/.dsh`，只操作其中的 `profiles/web`。
- `profiles/web/package.json` 存在才合并；不存在则只写一个待办标记，DSH 初始化完成后下次启动补齐。
- **先拿 `.deploy.lock` 再读再写**：同 profile 拿不到锁就报告延迟部署（Deferred）且不写入任何内容，
  等下一次启动重试；`package.json` 一律在锁内读取，避免读到并发中间态。
- **只补真正缺失的插件**：bundle 条目、依赖声明、`node_modules` 同名实体三者任一已存在，
  一律保持原样、不修改、不自动启用（用户既有安装可能依赖父层依赖，不强制补声明）。
- **结构不符只诊断不改造**：`dsh` / `dsh.profile` / `bundles` / `dependencies` 类型不符，
  或 `bundles` 含非字符串条目时，输出可读诊断并逐字保留原文，绝不改成数组、绝不丢未知项。
- **写入前逐级检查整条路径链**：从 DSH Home 到 profile、`.helper-bundles`、`node_modules`、
  `package.json` 的每个已存在祖先及目标本身若是 reparse point（目录联接/符号链接）即拒绝；
  先在创建前检查，绝不"先创建再检查"。
- **一致性与回滚**：写入前复核 `package.json` 内容未被外部改写（冲突不覆盖）；首次写入前把用户
  原始文件另存为 `package.json.helper-original.bak` 供恢复（后续不覆盖）；用事务统一回滚，
  失败或取消只移除本次真正新增/替换的内容，绝不删除已恢复的旧包。
- 实体复制到 profile 内受管稳定目录 `.helper-bundles/`，经 DSH 原生 `node_modules` 解析加载
  （目录链接 + `file:` 依赖）；**不调用 npm、不需要联网**。
- 保留未知 package 字段、非 Helper 依赖与原始 `cordis.patch.yml`；**不改任何凭据或会话**。
- 自动化测试必须注入隔离的部署器（临时 DSH Home 或"不写盘"桩），绝不把测试部署到当前真实 `~/.dsh`。

## 4. 接到 Helper 启动 DSH 的真实入口

GUI（`EnsureWebHostReadyAsync`）与隐藏宿主（`HarnessHiddenHost.RunAsync`）以及底层
`LaunchWebHost` 都会在真正拉起 DSH 之前部署一次基础插件：

- 已有正在运行的 Host **不会被中断**；新补的插件在 DSH 重启后生效。
- 部署失败或未就绪只产生有界中文诊断，**绝不让部署缺陷导致所有 DSH 无法启动**。
- 接口仍只监听 `127.0.0.1`，不改动已授权的网络访问安全机制。

## 换新电脑后的操作

1. 安装精简安装包（需已装 .NET 8 Desktop Runtime）。
2. 若本机 88FRP 为系统服务版，无需额外配置，Helper 会自动识别其实例根。
3. 首次启动 Helper 会补齐上述基础插件；提示“需重启 DSH”时重启 DSH 即生效。

## 恢复方法

- 修改配置前先结束活动任务、停下 DSH，并另存当前配置。不要删除 DSH Home、会话目录或模型凭据。
- 基础部署首次修改前留下 `profiles/web/package.json.helper-original.bak`。需要撤销时先比较它与当前
  `package.json`；若期间没有其他安装变更，可恢复备份。若有新增插件，只移除本次 Helper 新增的 bundle
  与依赖条目，不能直接用旧备份覆盖后来安装的内容。插件实体可以保留，不必递归删除。
- 合同预设修复在 `.agent-presets/codex-contract` 内留下带时间戳的 `.bak-*`。需要恢复时将对应原文件
  备份还原，或在 Helper 选择 `standard` 模式；不改标准预设所在的 DSH 安装包。
- 88FRP 同步仅改受管入口条目，并保存 patch 备份。入口读不到或有歧义时保留原信任，不清空整个 patch。
- 启动入口只能选一个。若原来另有独立 DSH Windows 服务，先停止其自动启动，再由 Helper 的计划任务
  托管；恢复旧服务时先停用 Helper 的宿主计划任务，避免两个入口相互拉起。不要停用 Windows 任务计划服务。
- 覆盖安装保留用户设置。回退可安装先前发布的版本，并按需要还原本次受管配置备份；不要恢复已撤回的 4.4.8。
