# DSH 离线附件包构建器（阶段一）

> 状态：阶段一已实现，仅供 Codex Helper 未来的导入功能（阶段二）消费；
> “只装 Helper 即可”的承诺在阶段二完成验收前不成立。

本构建器从当前机器**已验证**的 DSH runtime 与 Web profile 收集运行所需的实体
插件与 profile 模板，生成一个可随 Helper 发布的**离线**压缩附件包，供没有网络
的目标机器在导入功能帮助下恢复 DSH Web profile。

## 产物与约定

- 产物路径：`artifacts/v<HelperVersion>/codex-helper-dsh-offline-v<HelperVersion>.zip`
- 同级 SHA-256 文本：`codex-helper-dsh-offline-v<HelperVersion>-sha256.txt`
- 附件 zip 内顶层目录与 zip 同名，含：`manifest.json`、`README.md`、
  `runtime/`（DSH CLI runtime 实体与依赖闭包）、`profile/`（物化 package.json +
  脱敏 patch）、`entities/`（插件实体副本）。
- HelperVersion 取自仓库根 `Directory.Build.props` 的 `Version`（与
  `scripts/build-release.ps1` 同一来源）。

## 用法

构建（默认输出到仓库 `artifacts/v<版本>`）：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-dsh-offline-attachment.ps1
```

隔离输出（校验用，可指向任意临时目录）：

```powershell
$out = Join-Path $env:TEMP 'dsh-offline-check'
powershell -ExecutionPolicy Bypass -File scripts\build-dsh-offline-attachment.ps1 -OutputDirectory $out
```

验证（退出码 0 = 全部通过）：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\verify-dsh-offline-attachment.ps1 -ArchivePath $out\codex-helper-dsh-offline-v<版本>.zip -ShaFile $out\codex-helper-dsh-offline-v<版本>-sha256.txt
```

可选参数：`-ProfilePath`（profile 目录，缺省 `%USERPROFILE%\.dsh\profiles\web`）、
`-DshPackagePath`（DSH runtime 包目录，缺省自动探测 npm 全局
`@deepseek-ai/dsh`）。

## 附件内布局与恢复计划

| zip 内相对路径 | 来源 | 恢复目标（相对） | 说明 |
| --- | --- | --- | --- |
| `runtime/*` | `@deepseek-ai/dsh` 包目录（含嵌套依赖闭包） | DSH CLI 安装位置（如 `node_modules/@deepseek-ai/dsh`），相对路径原样映射 | 已剔除开发期 `@types`、测试、`.map`、日志等 |
| `profile/package.json` | profile 清单 | `.dsh/profiles/<profile>/package.json` | `link:`/绝对 `file:` 依赖已改写为指向 `../entities/` 的相对 `file:` |
| `profile/cordis.patch.yml` | profile patch | `.dsh/profiles/<profile>/cordis.patch.yml` | 已脱敏（见下） |
| `entities/*` | 当前依赖声明的 link:/file:/registry 实体 | 附件解压根内 `entities/`，由物化 `file:` 引用装配 | 目录实体与 tgz 实体 |
| `manifest.json` | 构建期生成 | 只读元数据 | 逐文件 SHA-256 + 类别 + restorePlan |
| `README.md` | 构建期生成 | 只读说明 | 中文离线说明 |

bundles 中的 `@deepseek-ai/dsh-base`、`@deepseek-ai/dsh-web-app` 是 DSH runtime
内建包，随 `runtime/node_modules/@deepseek-ai/` 一并携带，恢复时沿用 DSH 的模块
回退解析机制，与构建机行为一致，无需单独实体。

## 排除与脱敏规则（构建 + 验证两侧保持一致）

1. **一律不打包**：`auth.json`、`.netrc`、任何凭据/密钥文件；会话、聊天/附件、
   技能数据；缓存、日志、临时文件；`.git`/`.github`/`.hg` 等元数据目录；
   runtime 依赖闭包内的开发垃圾（`@types`、`test/tests/__tests__/spec`、
   `coverage`、`docs`、`examples`、`.map`、`.tsbuildinfo` 等）。
2. **junction / symlink 目录不进入附件**（防循环、防跟随本机链接）。
3. **profile patch 脱敏**：打包前用 DSH runtime 自带的 `js-yaml` 解析 patch，
   按条目 id 剔除机器/用户特定条目（DeepSeek/第三方 API 配置、私人信任主机、
   文件选择器/索引补丁等），重新序列化保证 YAML 合法；输出再做 fail-closed
   敏感标记扫描。剔除条目 id 记录在 `manifest.json.policy.patchSanitizedEntryIds`。
4. **内容过滤**：任何候选实体/文档文本文件（限文本扩展名、≤2MB）若含本机绝对
   路径标记（`C:\Users\29092`、`C:\实用软件开发` 等），剔除该文件并在构建日志
   登记。
5. **版本化路径**：附件内全部为相对布局；`profile/package.json` 无 `link:` 与
   绝对 `file:`，仅剩指向 `entities/` 的相对引用。

## 验证入口覆盖（workerCheck）

`verify-dsh-offline-attachment.ps1` 独立审计：

- 条目名：无绝对路径、无 `..` 穿越、无受禁文件/目录；
- 全量哈希：`manifest.files` 每个条目在 zip 内存在且 SHA-256 一致；zip 内除
  manifest 外无清单外条目；
- 受控内容（profile package.json / patch / README）：无本机路径、无敏感键名与
  私人主机/API 标记；`package.json` 无 `link:`/绝对 `file:` 残留；
- 全包文本扫描：无本机绝对用户路径；
- 可选：核对 zip 级 SHA-256 文本行。

全部通过退出码 0。

## 阶段边界与后续（阶段二）

- 本阶段不修改主安装包/安装器、不覆盖本机 DSH、不重启 DSH、不联网、不安装依赖。
- 阶段二将实现 Helper 的导入功能：按 `restorePlan` 解包、装配 profile 依赖图、
   落位 runtime，并做端到端验收；届时安装路径与 UI 才被修改。
