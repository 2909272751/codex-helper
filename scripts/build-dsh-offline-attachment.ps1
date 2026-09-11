<#
.SYNOPSIS
    构建 DSH 离线附件包（阶段一产物）。

.DESCRIPTION
    从当前机器已验证的 DSH runtime 与 Web profile 收集运行所需的实体插件和
    profile 模板，生成一个可随 Codex Helper 发布的压缩附件包：
      artifacts/v<HelperVersion>/codex-helper-dsh-offline-v<HelperVersion>.zip
      及其 SHA-256 文本。

    附件包内使用相对布局，不含本机绝对用户路径；profile 中原先 link:/file:
    依赖被物化为附件内部可恢复的实体包引用（相对 file:）。严格排除
    auth/token/会话/聊天/缓存/日志/.git 等敏感与无关内容；profile patch 会
    在打包前脱敏（去掉含 apiKey*/私人主机等条目的内容）。

    本脚本不修改主安装包、不覆盖本机 DSH、不重启 DSH、不联网、不安装依赖。

.PARAMETER OutputDirectory
    输出目录。缺省为 <仓库根>/artifacts/v<HelperVersion>。
    测试/校验时传入隔离临时目录即可。

.PARAMETER ProfilePath
    DSH Web profile 目录，缺省为 $env:USERPROFILE\.dsh\profiles\web。

.PARAMETER DshPackagePath
    DSH runtime 包目录（npm 全局 @deepseek-ai/dsh），缺省自动探测。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\build-dsh-offline-attachment.ps1
    powershell -ExecutionPolicy Bypass -File scripts\build-dsh-offline-attachment.ps1 -OutputDirectory $env:TEMP\dsh-offline-check
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory = '',
    [string]$ProfilePath = (Join-Path $env:USERPROFILE '.dsh\profiles\web'),
    [string]$DshPackagePath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'common.ps1')

Add-Type -AssemblyName System.IO.Compression | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null

$repoRoot = Get-RepositoryRoot
$helperVersion = Get-ProjectVersion -RepositoryRoot $repoRoot

# ---------------------------------------------------------------------------
# 默认路径解析
# ---------------------------------------------------------------------------
if (-not $DshPackagePath) {
    $candidates = @(
        (Join-Path $env:APPDATA 'npm\node_modules\@deepseek-ai\dsh'),
        (Join-Path $env:USERPROFILE 'AppData\Roaming\npm\node_modules\@deepseek-ai\dsh')
    )
    $npmPrefix = & npm prefix -g 2>$null
    if ($npmPrefix) { $candidates += (Join-Path $npmPrefix 'node_modules\@deepseek-ai\dsh') }
    $DshPackagePath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $DshPackagePath -or -not (Test-Path -LiteralPath $DshPackagePath)) {
    throw "DSH runtime package not found. Pass -DshPackagePath."
}
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot "artifacts\v$helperVersion"
}

$profilePackagePath = Join-Path $ProfilePath 'package.json'
$profilePatchPath = Join-Path $ProfilePath 'cordis.patch.yml'
$dshPackageJsonPath = Join-Path $DshPackagePath 'package.json'
foreach ($p in @($profilePackagePath, $profilePatchPath, $dshPackageJsonPath)) {
    if (-not (Test-Path -LiteralPath $p)) { throw "Required input missing: $p" }
}

$attachmentName = "codex-helper-dsh-offline-v$helperVersion"
$zipPath = Join-Path $OutputDirectory "$attachmentName.zip"
$shaPath = Join-Path $OutputDirectory "$attachmentName-sha256.txt"
$zipRoot = $attachmentName

Write-Host "[dsh-offline] Helper version : $helperVersion"
Write-Host "[dsh-offline] Profile        : $ProfilePath"
Write-Host "[dsh-offline] Runtime pkg    : $DshPackagePath"
Write-Host "[dsh-offline] Output         : $zipPath"

if (-not (Test-Path -LiteralPath $OutputDirectory)) {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
}
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }

$dshJson = Get-Content -Raw -Encoding UTF8 -LiteralPath $dshPackageJsonPath | ConvertFrom-Json
$dshVersion = [string]$dshJson.version
$profileJson = Get-Content -Raw -Encoding UTF8 -LiteralPath $profilePackagePath | ConvertFrom-Json
$profileName = [string]$profileJson.name
if (-not $profileJson.dsh -or -not $profileJson.dsh.profile) {
    throw "Profile package.json does not look like a dsh profile (missing dsh.profile)."
}
$bundles = @($profileJson.dsh.profile.bundles)
$deps = $profileJson.dependencies

# ---------------------------------------------------------------------------
# 敏感规则（与 verify 脚本保持一致；集中定义）
# ---------------------------------------------------------------------------
# 本机用户路径标记（出现即视为泄露本机布局，禁止进入附件）
$script:LocalPathRx = 'C:(\\|/)Users/29092|C:(\\|/)实用软件开发'

# 打包排除：目录名（任意层级，不区分大小写）。注意：'node_modules' 不在全局名单，
# runtime 闭包必须保留其 node_modules；仅在复制插件实体目录时按需排除（见
# Get-IncludedFiles -ExcludeNodeModules）。
$script:ExcludedDirNames = @(
    '.git', '.hg', '.svn', '.github', '.vscode', '.idea', '.bin',
    '@types', '__pycache__', 'test', 'tests', '__tests__',
    'spec', 'coverage', 'examples', 'example', 'docs', 'tools'
)
# 打包排除：文件扩展名
$script:ExcludedFileExts = @('.map', '.tsbuildinfo', '.log', '.tmp', '.bak', '.orig', '.patch')
# 打包排除：文件名
$script:ExcludedFileNames = @('.DS_Store', 'Thumbs.db', 'auth.json', '.netrc', '.git', '.gitmodules')
# 文本内容过滤时的扩展名白名单（内容含 LocalPathRx 的文件将被剔除并记录）
$script:TextExts = @('.md', '.txt', '.json', '.yml', '.yaml', '.js', '.mjs', '.cjs', '.ts', '.ps1', '.xml', '.html', '.css', '.toml', '.ini', '.iss', '.properties')

# ---------------------------------------------------------------------------
# 工具函数
# ---------------------------------------------------------------------------
function Write-Utf8NoBom {
    param([string]$Path, [string]$Text)
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($Text)
    [System.IO.File]::WriteAllBytes($Path, $bytes)
}

function ConvertTo-HexLower {
    param([byte[]]$Bytes)
    return ([System.BitConverter]::ToString($Bytes)).Replace('-', '').ToLowerInvariant()
}

# 计算字节数组 SHA-256（PS 5.1 兼容：不使用 .NET 5+ 的 HashData）
function Get-BytesSha256 {
    param([byte[]]$Bytes)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $chunk = 262144
        for ($off = 0; $off -lt $Bytes.Length; $off += $chunk) {
            $len = [Math]::Min($chunk, $Bytes.Length - $off)
            [void]$sha.TransformBlock($Bytes, $off, $len, $null, 0)
        }
        [void]$sha.TransformFinalBlock([byte[]]@(), 0, 0)
        return ConvertTo-HexLower $sha.Hash
    }
    finally { $sha.Dispose() }
}

# 计算文件 SHA-256（.NET 流式，避免逐文件起进程）
function Get-FileSha256Stream {
    param([string]$Path)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $fs = [System.IO.File]::OpenRead($Path)
        try {
            $buf = New-Object byte[] 262144
            while (($n = $fs.Read($buf, 0, $buf.Length)) -gt 0) {
                [void]$sha.TransformBlock($buf, 0, $n, $null, 0)
            }
            [void]$sha.TransformFinalBlock([byte[]]@(), 0, 0)
        }
        finally { $fs.Dispose() }
        return ConvertTo-HexLower $sha.Hash
    }
    finally { $sha.Dispose() }
}

$script:ManifestFiles = New-Object System.Collections.Generic.List[object]

function Add-BytesEntry {
    # 把文本内容作为 zip 条目写入（UTF-8 无 BOM），同时登记 manifest 记录
    param(
        [System.IO.Compression.ZipArchive]$Zip,
        [string]$EntryName,
        [string]$Text,
        [string]$Category
    )
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($Text)
    $entry = $Zip.CreateEntry("$zipRoot/$EntryName", [System.IO.Compression.CompressionLevel]::Optimal)
    $es = $entry.Open()
    try { $es.Write($bytes, 0, $bytes.Length) }
    finally { $es.Dispose() }
    $script:ManifestFiles.Add(@{
        path = $EntryName
        sha256 = (Get-BytesSha256 $bytes)
        size = $bytes.Length
        category = $Category
    })
}

function Add-FileEntry {
    # 从源文件写入 zip 条目：单次读流 = 哈希 + 写 zip 同时进行
    param(
        [System.IO.Compression.ZipArchive]$Zip,
        [string]$SourcePath,
        [string]$EntryName,
        [string]$Category
    )
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $fs = [System.IO.File]::OpenRead($SourcePath)
    $length = $fs.Length
    $entry = $Zip.CreateEntry("$zipRoot/$EntryName", [System.IO.Compression.CompressionLevel]::Optimal)
    $es = $entry.Open()
    $hashHex = ''
    try {
        $buf = New-Object byte[] 262144
        while (($n = $fs.Read($buf, 0, $buf.Length)) -gt 0) {
            [void]$sha.TransformBlock($buf, 0, $n, $null, 0)
            $es.Write($buf, 0, $n)
        }
        [void]$sha.TransformFinalBlock([byte[]]@(), 0, 0)
        $hashHex = ConvertTo-HexLower $sha.Hash   # 必须在 Dispose 之前取哈希
    }
    finally {
        $es.Dispose()
        $fs.Dispose()
        $sha.Dispose()
    }
    $script:ManifestFiles.Add(@{
        path = $EntryName
        sha256 = $hashHex
        size = $length
        category = $Category
    })
}

# 枚举目录内应打包的相对文件列表（不跟随 reparse point / junction，防循环）
function Get-IncludedFiles {
    param(
        [string]$RootDir,
        [switch]$ContentFilter,
        [switch]$ExcludeNodeModules,
        [System.Collections.Generic.List[string]]$Dropped = $null
    )
    $relative = New-Object System.Collections.Generic.List[string]
    $stack = New-Object System.Collections.Generic.Stack[string]
    $stack.Push($RootDir)
    while ($stack.Count -gt 0) {
        $dir = $stack.Pop()
        foreach ($fsEntry in [System.IO.Directory]::EnumerateFileSystemEntries($dir)) {
            $name = [System.IO.Path]::GetFileName($fsEntry)
            $attr = [System.IO.File]::GetAttributes($fsEntry)
            $isDir = ($attr -band [System.IO.FileAttributes]::Directory) -ne 0
            $isReparse = ($attr -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
            if ($isDir) {
                if ($isReparse) { continue }  # junction/symlink 目录不进入附件
                if ($script:ExcludedDirNames -contains $name) { continue }
                if ($ExcludeNodeModules -and $name -ieq 'node_modules') { continue }
                $stack.Push($fsEntry)
            }
            else {
                if ($isReparse) { continue }
                if ($script:ExcludedFileNames -contains $name) { continue }
                $ext = [System.IO.Path]::GetExtension($name).ToLowerInvariant()
                if ($script:ExcludedFileExts -contains $ext) { continue }
                $rel = $fsEntry.Substring($RootDir.Length).TrimStart('\', '/').Replace('\', '/')
                if ($ContentFilter -and $script:TextExts -contains $ext) {
                    # 仅对文本类扩展名执行大小检查与内容过滤，避免对每个文件做 Get-Item
                    $big = $false
                    try { $big = ((Get-Item -LiteralPath $fsEntry -Force).Length -gt 2MB) } catch { $big = $true }
                    if (-not $big) {
                        $text = [System.IO.File]::ReadAllText($fsEntry)
                        if ([regex]::IsMatch($text, $script:LocalPathRx)) {
                            if ($Dropped) { $Dropped.Add($fsEntry) }
                            continue
                        }
                    }
                }
                $relative.Add($rel)
            }
        }
    }
    $relative.Sort()
    return $relative
}

# ---------------------------------------------------------------------------
# 依赖 → 实体来源解析
# ---------------------------------------------------------------------------
$profileNodeModules = Join-Path $ProfilePath 'node_modules'

function Get-DepSource {
    param([string]$Key, [string]$Spec, [string]$ProfileNm)
    if ($Spec -like 'link:*') {
        $p = $Spec.Substring(5).TrimEnd('\', '/').Trim('"')
        return @{ kind = 'dir'; specKind = 'link'; src = $p }
    }
    elseif ($Spec -like 'file:*') {
        $p = $Spec.Substring(5).Trim('"')
        if ($p -like '*.tgz') {
            return @{ kind = 'tgz'; specKind = 'file-tgz'; src = $p }
        }
        return @{ kind = 'dir'; specKind = 'file-dir'; src = $p.TrimEnd('\', '/') }
    }
    else {
        # registry 安装到 profile node_modules 的实体（当前已解析的物理目录）
        $p = Join-Path $ProfileNm ($Key.Replace('/', '\'))
        return @{ kind = 'dir'; specKind = 'registry-materialized'; src = $p }
    }
}

$depEntries = @{}   # key -> { key, specKind, kind, src, attachmentPath, version, dropped }
$restorePackages = @{}
foreach ($depKey in ($deps.PSObject.Properties.Name | Sort-Object)) {
    $spec = [string]$deps.$depKey
    $resolved = Get-DepSource -Key $depKey -Spec $spec -ProfileNm $profileNodeModules
    if (-not (Test-Path -LiteralPath $resolved.src)) {
        throw "Entity source missing for dependency '$depKey' ($($resolved.specKind)): $($resolved.src)"
    }
    if ($resolved.kind -eq 'tgz') {
        $attachmentPath = "entities/tgz/$([System.IO.Path]::GetFileName($resolved.src))"
    }
    else {
        $attachmentPath = "entities/$($depKey -replace '/', '/')"
    }
    $version = ''
    if ($resolved.kind -eq 'dir') {
        $pkgJson = Join-Path $resolved.src 'package.json'
        if (Test-Path -LiteralPath $pkgJson) {
            try { $version = [string]((Get-Content -Raw -Encoding UTF8 -LiteralPath $pkgJson | ConvertFrom-Json).version) } catch { }
        }
    }
    $depEntries[$depKey] = @{
        key = $depKey
        specKind = $resolved.specKind
        kind = $resolved.kind
        src = $resolved.src
        attachmentPath = $attachmentPath
        version = $version
        dropped = @()
    }
    $restorePackages[$depKey] = @{
        kind = $resolved.kind
        specKind = $resolved.specKind
        version = $version
        attachmentPath = $attachmentPath
        restoreTarget = "node_modules/$($depKey -replace '/', '/')"
    }
}

# ---------------------------------------------------------------------------
# 打开 zip 并逐类写入
# ---------------------------------------------------------------------------
$zipFs = [System.IO.File]::Open($zipPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::ReadWrite)
$zip = New-Object System.IO.Compression.ZipArchive($zipFs, [System.IO.Compression.ZipArchiveMode]::Create, $false)
$startedUtc = [DateTime]::UtcNow
try {
    # ---------- docs：附件根 README.md ----------
    $readme = @"
# Codex Helper — DSH 离线附件包 v$helperVersion

本附件由 Codex Helper 在 **阶段一（离线附件包构建器）** 中生成，用于离线携带
DSH runtime、Web profile 的必要清单/补丁与当前自定义插件实体，供未来 Helper
导入功能（阶段二）在没有网络时恢复一个可运行的 DSH Web profile。

## 本附件离线可用，且**不含**以下数据

- 不含 `auth.json`、任何 API Key / token / secret / cookie / authorization 等凭据内容；
- 不含会话、聊天记录、附件、用户技能数据；
- 不含缓存、日志、临时文件、`.git` 目录；
- 不含本机绝对用户路径（`C:\Users\...`、项目开发路径等）；
- profile 的机器/用户特定覆盖（私人代理主机、DeepSeek/第三方 API 端点等）
  已在打包时脱敏剔除，恢复后需由操作者重新按需填写；
- 构建机器上的 `link:`/绝对 `file:` 依赖已物化为附件内部相对布局。

## Node 前置条件

- 本附件不包含 Node.js/npm 运行时本身。目标机器需自行安装 Node.js
  （与当前 DSH runtime 匹配的版本）后才能由导入功能恢复运行；
- 恢复过程全程离线：不访问 npm registry，也不访问任何第三方服务。

## 布局

- `manifest.json` — 机器可读清单：Helper/DSH 版本、逐文件 SHA-256、来源类别
  （runtime / profile / entity / docs / manifest）与恢复目标相对路径；
- `README.md` — 本说明；
- `runtime/` — DSH CLI runtime 实体（`package.json` + `lib/` + `config/` +
  依赖闭包 `node_modules/`，已剔除开发期 @types、测试、日志、源映射等垃圾）；
- `profile/package.json` — 物化后的 profile 清单：`link:`/绝对 `file:` 依赖
  已改写为指向 `entities/` 的**相对** `file:` 引用；bundles 列表保持原样；
- `profile/cordis.patch.yml` — 脱敏后的 loader patch 模板（详见其中注释）；
- `entities/` — 当前自定义/已解析插件的实体副本（目录实体与 tgz 实体）。

## 如何被未来 Helper 导入功能使用（阶段二）

1. 把附件解压到目标机；Node 已安装；
2. 导入功能按 `manifest.json` 的 `restorePlan` 把 `runtime/` 放到 DSH CLI 安装
   位置、把 `profile/` 放到 `.dsh/profiles/<name>`、把 `entities/` 按相对
   `file:` 引用装配到 profile 的依赖图（base/web-app 等 runtime 内建 bundle
   沿用 DSH 的模块回退解析机制，与构建机行为一致）；
3. 操作者按需重新填写机器/用户特定的 patch 覆盖（密钥环境变量名、信任主机等）。

> 注意：**“只装 Helper 即可”的承诺需等安装器集成阶段（阶段二）完成并验收后
> 才会成立**。本附件目前只是可供导入功能消费的离线数据包，不是独立安装器。

## 校验

    powershell -ExecutionPolicy Bypass -File scripts\verify-dsh-offline-attachment.ps1 -ArchivePath <本zip路径>
"@
    Add-BytesEntry -Zip $zip -EntryName 'README.md' -Text $readme -Category 'docs'

    # ---------- profile：物化 package.json ----------
    $materialized = @{
        name = $profileJson.name
        private = $true
    }
    $materialized.dsh = $profileJson.dsh
    $newDeps = @{}
    foreach ($depKey in ($deps.PSObject.Properties.Name | Sort-Object)) {
        $e = $depEntries[$depKey]
        $newDeps[$depKey] = "file:../$($e.attachmentPath)"
    }
    $materialized.dependencies = $newDeps
    $profilePkgText = $materialized | ConvertTo-Json -Depth 8
    Add-BytesEntry -Zip $zip -EntryName 'profile/package.json' -Text $profilePkgText -Category 'profile'

    # ---------- profile：脱敏 cordis.patch.yml ----------
    # 通过 DSH runtime 自带依赖闭包中的 js-yaml 解析 → 按条目过滤 → 重新序列化，
    # 保证剔除后仍是合法 YAML。同时 fail-closed：若输出残留敏感键名/私人标记则报错。
    $nodeExe = (Get-Command node -ErrorAction Stop).Source
    $jsYamlPath = Join-Path $DshPackagePath 'node_modules\js-yaml\index.js'
    if (-not (Test-Path -LiteralPath $jsYamlPath)) { throw "js-yaml not found under DSH runtime: $jsYamlPath" }
    $patchDropIds = @('web-search-deepseek', 'web', 'tool-web', 'files-toolkit', 'web-runtime', 'index-polyfill')
    $patchSanitizedOut = Join-Path $OutputDirectory '.dsh-offline-patch-sanitized.yml'
    $sanitizerJs = Join-Path $OutputDirectory '.dsh-offline-sanitize-patch.cjs'
    $sanitizerCode = @'
const fs = require('fs');
const yaml = require(process.argv[2]);
const src = process.argv[3];
const out = process.argv[4];
const drop = new Set(process.argv[5].split(',').filter(Boolean));
const doc = yaml.load(fs.readFileSync(src, 'utf8'));
if (!Array.isArray(doc)) { console.error('patch root is not an array'); process.exit(2); }
const kept = doc.filter((e) => {
  if (!e || typeof e !== 'object') return true;
  if (typeof e.id === 'string' && drop.has(e.id)) return false;
  if (Array.isArray(e.insert)) {
    e.insert = e.insert.filter((i) => !(i && typeof i.id === 'string' && drop.has(i.id)));
    return e.insert.length > 0;
  }
  return true;
});
const body = yaml.dump(kept, { noRefs: true, lineWidth: 120, noCompatMode: true });
const header = '# Codex Helper — 离线附件化时自动脱敏的 profile patch 模板\n'
  + '# 机器/用户特定条目（DeepSeek/第三方 API 配置、私人信任主机、文件选择器/索引补丁等）\n'
  + '# 已在打包时移除，避免把密钥键名与私人服务地址带入离线附件。恢复后由操作者按需\n'
  + '# 重新填写。以下仅保留与运行时结构相关的通用覆盖。\n';
const outText = header + body;
const forbidden = /apiKey|token|secret|cookie|authorization|8\.163\.132\.151|112\.74\.85\.74|DEEPSEEK_API_KEY/i;
if (forbidden.test(outText)) { console.error('sanitized patch still contains forbidden markers'); process.exit(3); }
fs.writeFileSync(out, outText, 'utf8');
console.log('patch entries kept: ' + kept.length);
'@
    Write-Utf8NoBom -Path $sanitizerJs -Text $sanitizerCode
    try {
        $patchText = & $nodeExe $sanitizerJs $jsYamlPath $profilePatchPath $patchSanitizedOut ($patchDropIds -join ',')
        if ($LASTEXITCODE -ne 0) { throw "Patch sanitizer failed with exit code $LASTEXITCODE" }
    }
    finally {
        if (Test-Path -LiteralPath $sanitizerJs) { Remove-Item -LiteralPath $sanitizerJs -Force }
    }
    if (-not (Test-Path -LiteralPath $patchSanitizedOut)) { throw 'Patch sanitizer produced no output.' }
    $patchTextOut = [System.IO.File]::ReadAllText($patchSanitizedOut)
    Remove-Item -LiteralPath $patchSanitizedOut -Force
    Add-BytesEntry -Zip $zip -EntryName 'profile/cordis.patch.yml' -Text $patchTextOut -Category 'profile'
    Write-Host "[dsh-offline] patch sanitizer: $patchText"

    # ---------- entities：插件实体副本 ----------
    foreach ($depKey in ($depEntries.Keys | Sort-Object)) {
        $e = $depEntries[$depKey]
        $droppedLog = New-Object System.Collections.Generic.List[string]
        $entityFileCount = 0
        if ($e.kind -eq 'tgz') {
            Add-FileEntry -Zip $zip -SourcePath $e.src -EntryName $e.attachmentPath -Category 'entity'
            $entityFileCount = 1
        }
        else {
            $relFiles = Get-IncludedFiles -RootDir $e.src -ContentFilter -ExcludeNodeModules -Dropped $droppedLog
            foreach ($rel in $relFiles) {
                $srcFull = Join-Path $e.src ($rel.Replace('/', '\'))
                Add-FileEntry -Zip $zip -SourcePath $srcFull -EntryName "$($e.attachmentPath)/$rel" -Category 'entity'
            }
            $entityFileCount = $relFiles.Count
            $depEntries[$depKey].dropped = @($droppedLog)
        }
        $droppedCount = $droppedLog.Count
        Write-Host "[dsh-offline] entity  : $depKey  ($($e.specKind), files=$entityFileCount, dropped=$droppedCount)"
    }

    # ---------- runtime：DSH runtime 实体（整个 @deepseek-ai/dsh 包目录） ----------
    Write-Host "[dsh-offline] copying DSH runtime closure from $DshPackagePath ..."
    $runtimeDropped = New-Object System.Collections.Generic.List[string]
    $runtimeFiles = Get-IncludedFiles -RootDir $DshPackagePath -Dropped $runtimeDropped
    $count = 0
    foreach ($rel in $runtimeFiles) {
        $srcFull = Join-Path $DshPackagePath ($rel.Replace('/', '\'))
        Add-FileEntry -Zip $zip -SourcePath $srcFull -EntryName "runtime/$rel" -Category 'runtime'
        $count++
    }
    Write-Host "[dsh-offline] runtime : files=$count (excluded reparse/junk; dropped-path files=$($runtimeDropped.Count))"

    # ---------- manifest.json ----------
    $manifest = @{
        schemaVersion = 1
        kind = 'codex-helper-dsh-offline-attachment'
        attachmentName = $attachmentName
        topFolder = $zipRoot
        helperVersion = $helperVersion
        dshVersion = $dshVersion
        profileName = $profileName
        profileBundles = $bundles
        createdUtc = $startedUtc.ToString('o')
        policy = @{
            noMachinePaths = $true
            excludesAuthSessionsChatsCacheLogs = $true
            nodeModulesDevJunkExcluded = $true
            linkFileDepsMaterialized = $true
            patchSanitizedEntryIds = @($patchDropIds)
            patchKeptEntryCount = (($patchTextOut -split "^- id:|^- insert:").Count - 1)
        }
        restoreLayout = @{
            runtime = @{ source = 'runtime/'; target = '<DSH-CLI>/node_modules/@deepseek-ai/dsh'; note = 'runtime/* 相对路径原样映射到该目录' }
            profile = @{ source = 'profile/'; target = '<profiles>/<profileName>'; note = '物化 package.json + 脱敏 patch' }
            entities = @{ source = 'entities/'; target = '<附件解压根>/entities'; note = 'profile/package.json 以相对 file: 引用' }
            docs = @{ source = 'README.md'; target = '<只读说明>' }
        }
        restorePlan = @{
            packages = $restorePackages
            notes = @(
                '阶段一仅交付数据包与清单；由阶段二导入功能按 restorePlan 装配 node_modules 并恢复 profile。',
                'bundles 中的 @deepseek-ai/dsh-base / dsh-web-app 为 DSH runtime 内建包，位于 runtime/node_modules/@deepseek-ai/ 下，沿用 DSH 模块回退解析。',
                'profile/cordis.patch.yml 已脱敏：机器/用户特定条目被移除，恢复后需操作者按需重填。'
            )
        }
        # 注意：PS5.1 下 @(泛型List) 再放入 hashtable 会抛 'Argument types do not match'，
        # 因此直接赋值 List（ConvertTo-Json 可正常序列化 IEnumerable）。
        files = $script:ManifestFiles
    }
    $manifestText = $manifest | ConvertTo-Json -Depth 12
    Add-BytesEntry -Zip $zip -EntryName 'manifest.json' -Text $manifestText -Category 'manifest'
}
finally {
    $zip.Dispose()
    $zipFs.Dispose()
}

# ---------------------------------------------------------------------------
# zip 级 SHA-256 文本
# ---------------------------------------------------------------------------
$zipHash = Get-FileSha256Stream -Path $zipPath
$hashLine = "$zipHash  $attachmentName.zip"
Write-Utf8NoBom -Path $shaPath -Text $hashLine
Write-Host '[dsh-offline] SHA-256:'
Write-Host "  $hashLine"

$fileCount = $script:ManifestFiles.Count
Write-Host "[dsh-offline] DONE. manifest files=$fileCount artifact=$zipPath"
