# Codex Helper v3.3.3 发布说明

`v3.3.3` 是精简安装包（thin-only）发布策略的第一版：GitHub Release 只发布依赖本机 `.NET 8 Desktop Runtime` 的精简一键安装包和对应的 SHA-256 校验文件，不再发布自包含完整安装包、便携 ZIP、独立主 EXE、rescue 或 credential-helper 资产。

## 修复

- 保留本轮探测修复：Helper 启动扫描不再执行 Reasonix Desktop / launcher / update-helper，只探测真实 CLI。任何来源（保存路径、注册表、运行中进程、常见位置、PATH）一旦命中 Desktop 启动器或辅助可执行文件即被排除，形成单一安全边界；运行中的 Desktop 路径仍可作定位线索派生 `reasonix-cli.exe`。

## 下载要求

- 精简安装包依赖 Windows x64 的 **.NET 8 Desktop Runtime**（安装 .NET 8 SDK 也可满足）。
- 安装器会可靠检测运行库（注册表权威记录 + 文件系统兜底，不依赖单个固定目录）。若缺失，会显示中文说明并打开微软官方 `.NET 8` 下载选择页（`https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0`）；安装完成后重新运行本安装包即可。
- 项目不再发布完整离线安装包或便携 ZIP，请勿再按旧下载策略寻找这些资产。

## 唯一发布资产

- `codex-helper-v3.3.3-setup.exe`：精简一键安装包。
- `codex-helper-v3.3.3-sha256.txt`：上方的 SHA-256 校验文件。

## 验证结果

- Codex Helper 完整测试全部通过。
- 精简发布构建只产出版本化 setup 与 SHA-256 文件，不生成 full/portable 资产。
