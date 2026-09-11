<#
.SYNOPSIS
    验证 DSH 离线附件包（阶段一验证入口）。

.DESCRIPTION
    对 build-dsh-offline-attachment.ps1 生成的 zip 做独立审计：
      1. 条目名审计：无绝对本机路径、无 .. 穿越、无受禁文件/目录（auth/.git/
         缓存/日志/临时等）；
      2. 全量哈希审计：manifest.files 中每个条目在 zip 内存在且 SHA-256 与
         manifest 一致；zip 内除 manifest.json 外不存在清单外条目；
      3. 受控内容审计（profile/package.json、profile/cordis.patch.yml、
         README.md、manifest.json）：无本机路径、无敏感键名/私人主机标记；
         profile/package.json 无 link: 或绝对 file: 依赖；
      4. 全包文本审计（文本类扩展名、<=1MB）：无本机绝对用户路径；
      5. 顶层目录名/版本与清单一致。

    全部通过则退出码 0；任一项失败退出码 1 并列出失败明细。

.PARAMETER ArchivePath
    待验证的附件 zip 路径。

.PARAMETER ShaFile
    可选：构建器生成的 <name>-sha256.txt 路径；提供则核对 zip 级 SHA-256 行。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\verify-dsh-offline-attachment.ps1 `
        -ArchivePath C:\temp\codex-helper-dsh-offline-v4.3.1.zip
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ArchivePath,
    [string]$ShaFile = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.IO.Compression | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null

$failures = New-Object System.Collections.Generic.List[string]
$warnings = New-Object System.Collections.Generic.List[string]
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$entries = @()
$manifestFiles = @()
$manifest = $null

# ---------- 与构建器保持一致的规则 ----------
$LocalPathRx = 'C:(\\|/)Users/29092|C:(\\|/)实用软件开发'
$ForbiddenDirNames = @('.git', '.hg', '.svn', '.DS_Store', 'Thumbs.db', '__pycache__', 'node_modules\.cache')
$ForbiddenFileNames = @('auth.json', '.netrc', '.git', '.gitmodules')
$ForbiddenFileExts = @('.log', '.tmp', '.bak', '.orig')
$AuthoredKeysRx = 'apiKey|secret|cookie|authorization|token'          # 仅用于受控内容审计
$PrivateMarkersRx = '8\.163\.132\.151|112\.74\.85\.74|DEEPSEEK_API_KEY|api\.deepseek\.com/anthropic'
$LinkOrAbsFileRx = 'link:|file:[A-Za-z]:|file:\\|file:/|file://'
$TextExts = @('.md', '.txt', '.json', '.yml', '.yaml', '.js', '.mjs', '.cjs', '.ts', '.ps1', '.xml', '.html', '.css', '.toml', '.ini', '.iss', '.properties')

if (-not (Test-Path -LiteralPath $ArchivePath)) {
    Write-Error "Archive not found: $ArchivePath"
    exit 1
}
$ArchivePath = (Resolve-Path -LiteralPath $ArchivePath).Path

Write-Host "[verify] archive: $ArchivePath"
Write-Host "[verify] auditing..."

$zip = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
try {
    $entries = @($zip.Entries)
    if ($entries.Count -eq 0) { $failures.Add('zip has no entries'); throw 'stop' }

    # ---------- 确定顶层目录并核对一致性 ----------
    $topDirs = @($entries | ForEach-Object { ($_.FullName -split '/')[0] } | Sort-Object -Unique)
    if ($topDirs.Count -ne 1) { $failures.Add("expected single top folder, got: $($topDirs -join ', ')") }
    $top = $topDirs[0]
    if ($top -notmatch '^codex-helper-dsh-offline-v\d+\.\d+\.\d+') {
        $failures.Add("top folder '$top' does not match codex-helper-dsh-offline-v<version>")
    }

    $manifestEntry = $null
    $manifestName = "$top/manifest.json"
    foreach ($e in $entries) { if ($e.FullName -eq $manifestName) { $manifestEntry = $e; break } }
    if (-not $manifestEntry) { $failures.Add("manifest.json missing at $manifestName"); throw 'stop' }

    $reader = New-Object System.IO.StreamReader($manifestEntry.Open())
    $manifestText = $reader.ReadToEnd()
    $reader.Dispose()
    $manifest = $manifestText | ConvertFrom-Json

    foreach ($req in @('schemaVersion', 'kind', 'helperVersion', 'dshVersion', 'topFolder', 'files')) {
        if (-not ($manifest.PSObject.Properties.Name -contains $req)) { $failures.Add("manifest missing required field: $req") }
    }
    if ($manifest.kind -ne 'codex-helper-dsh-offline-attachment') { $failures.Add("manifest kind mismatch: $($manifest.kind)") }
    if ($manifest.topFolder -ne $top) { $failures.Add("manifest topFolder mismatch: $($manifest.topFolder) != $top") }
    $zipName = [System.IO.Path]::GetFileNameWithoutExtension($ArchivePath)
    if ($zipName -ne $manifest.attachmentName) { $failures.Add("zip name '$zipName' != manifest.attachmentName '$($manifest.attachmentName)'") }
    $manifestFiles = @($manifest.files)
    Write-Host "[verify] manifest: helper=$($manifest.helperVersion) dsh=$($manifest.dshVersion) entries=$($manifestFiles.Count)"

    # ---------- 1) 条目名审计 ----------
    foreach ($e in $entries) {
        $name = $e.FullName
        if ($name -match '^[A-Za-z]:[\\/]|^/|^\\') { $failures.Add("absolute path in entry name: $name"); continue }
        if ($name -match '(^|/)\.\.(/|$)') { $failures.Add("path traversal in entry name: $name"); continue }
        if ($name.Contains('\')) { $failures.Add("backslash in entry name: $name"); continue }
        if ($name -match $LocalPathRx) { $failures.Add("local user path in entry name: $name"); continue }
        $leaf = $name.Split('/')[-1]
        $parts = $name.Split('/')
        foreach ($part in $parts) {
            if ($ForbiddenDirNames -contains $part) { $failures.Add("forbidden directory in entry name: $name"); break }
        }
        if ($ForbiddenFileNames -contains $leaf) { $failures.Add("forbidden file in entry name: $name"); continue }
        $ext = [System.IO.Path]::GetExtension($leaf)
        if ($ext -and $ForbiddenFileExts -contains $ext.ToLowerInvariant()) { $failures.Add("forbidden file extension in entry name: $name") }
    }

    # ---------- 2) 全量哈希审计 ----------
    $byName = @{}
    foreach ($e in $entries) { $byName[$e.FullName] = $e }
    $hashOk = 0
    $hashBad = New-Object System.Collections.Generic.List[string]
    foreach ($rec in $manifestFiles) {
        $full = "$top/$($rec.path)"
        if (-not $byName.ContainsKey($full)) { $hashBad.Add("missing in zip: $($rec.path)"); continue }
        $entry = $byName[$full]
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try {
            $es = $entry.Open()
            try {
                $buf = New-Object byte[] 262144
                while (($n = $es.Read($buf, 0, $buf.Length)) -gt 0) {
                    [void]$sha.TransformBlock($buf, 0, $n, $null, 0)
                }
                [void]$sha.TransformFinalBlock([byte[]]@(), 0, 0)
            }
            finally { $es.Dispose() }
            $actual = ([System.BitConverter]::ToString($sha.Hash)).Replace('-', '').ToLowerInvariant()
        }
        finally { $sha.Dispose() }
        if ($actual -ne ([string]$rec.sha256).ToLowerInvariant()) {
            $hashBad.Add("hash mismatch: $($rec.path)")
        }
        else { $hashOk++ }
    }
    if ($hashBad.Count -gt 0) { $failures.Add("manifest hash audit failed ($($hashBad.Count))"); $hashBad | ForEach-Object { $failures.Add("  $_") } }
    Write-Host "[verify] hash audit: ok=$hashOk bad=$($hashBad.Count) (of $($manifestFiles.Count))"

    # 清单外条目
    $manifestSet = @{}
    foreach ($rec in $manifestFiles) { $manifestSet["$top/$($rec.path)"] = $true }
    $extra = New-Object System.Collections.Generic.List[string]
    foreach ($e in $entries) {
        if ($e.FullName -eq $manifestName) { continue }
        if (-not $manifestSet.ContainsKey($e.FullName)) { $extra.Add($e.FullName) }
    }
    if ($extra.Count -gt 0) { $failures.Add("zip entries not listed in manifest ($($extra.Count)): $($extra[0..([Math]::Min(4, $extra.Count - 1))] -join '; ')") }
    Write-Host "[verify] unlisted entries: $($extra.Count)"

    # ---------- 3) 受控内容审计 ----------
    $authoredPaths = @('profile/package.json', 'profile/cordis.patch.yml', 'README.md')
    foreach ($ap in $authoredPaths) {
        $full = "$top/$ap"
        if (-not $byName.ContainsKey($full)) { $failures.Add("expected authored file missing: $ap"); continue }
        $es = $byName[$full].Open()
        $rd = New-Object System.IO.StreamReader($es)
        $text = $rd.ReadToEnd()
        $rd.Dispose()
        $es.Dispose()
        if ($text -match $LocalPathRx) { $failures.Add("local user path inside $ap") }
        if ($ap -like '*.yml') {
            # 敏感键名/私人标记只审计机器配置类文件（patch），README 会自然列举受禁词，不套用
            if ($text -match $AuthoredKeysRx) { $failures.Add("sensitive key name inside $ap") }
            if ($text -match $PrivateMarkersRx) { $failures.Add("private host/API marker inside $ap") }
        }
        if ($ap -eq 'profile/package.json') {
            if ($text -match $LinkOrAbsFileRx) { $failures.Add("link:/absolute file: dependency left in $ap") }
            if ($text -match $AuthoredKeysRx) { $failures.Add("sensitive key name inside $ap") }
            $pkg = $text | ConvertFrom-Json
            $depCount = 0
            foreach ($p in $pkg.dependencies.PSObject.Properties) {
                $depCount++
                $v = [string]$p.Value
                if ($v -notlike 'file:../entities/*') { $failures.Add("dependency '$($p.Name)' not materialized to relative entities ref: $v") }
            }
            if ($depCount -eq 0) { $failures.Add('profile/package.json has no dependencies') }
        }
    }
    # manifest.json 自身（不含自哈希，只做受控文本审计）
    if ($manifestText -match $LocalPathRx) { $failures.Add('local user path inside manifest.json') }
    if ($manifestText -match $PrivateMarkersRx) { $failures.Add('private host/API marker inside manifest.json') }

    # ---------- 4) 全包文本审计（<=1MB 的文本类文件） ----------
    $scanCount = 0
    foreach ($e in $entries) {
        if ($e.FullName -eq $manifestName) { continue }
        if ($e.Length -gt 1MB) { continue }
        $leaf = $e.FullName.Split('/')[-1]
        $ext = [System.IO.Path]::GetExtension($leaf).ToLowerInvariant()
        if ($TextExts -notcontains $ext) { continue }
        $scanCount++
        $es = $e.Open()
        try {
            $rd = New-Object System.IO.StreamReader($es)
            $text = $rd.ReadToEnd()
            $rd.Dispose()
        }
        finally { $es.Dispose() }
        if ($text -match $LocalPathRx) { $failures.Add("local user path inside text file: $($e.FullName)") }
    }
    Write-Host "[verify] text content scan: files=$scanCount"

    # ---------- 5) 可选 zip 级 sha 文本核对 ----------
    if ($ShaFile) {
        if (-not (Test-Path -LiteralPath $ShaFile)) { $failures.Add("sha file not found: $ShaFile") }
        else {
            $line = ((Get-Content -LiteralPath $ShaFile) | Select-Object -First 1)
            if ($line -notmatch '^[0-9a-f]{64}\s{2}codex-helper-dsh-offline-v\d+\.\d+\.\d+\.zip$') {
                $failures.Add("sha file line format invalid: $line")
            }
            else {
                $expected = ($line -split '\s{2}')[0]
                $actualZipHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $ArchivePath).Hash.ToLowerInvariant()
                if ($expected -ne $actualZipHash) { $failures.Add("zip sha mismatch: expected $expected actual $actualZipHash") }
                else { Write-Host "[verify] zip-level sha: ok ($expected)" }
            }
        }
    }
}
catch {
    if ($_.Exception.Message -ne 'stop') { $failures.Add("verification aborted: $($_.Exception.Message)") }
}
finally {
    $zip.Dispose()
}
$sw.Stop()

Write-Host ''
if ($failures.Count -eq 0) {
    Write-Host "[verify] PASS ($($sw.Elapsed.TotalSeconds.ToString('F1'))s, entries=$($entries.Count), manifestFiles=$($manifestFiles.Count))"
    exit 0
}
else {
    Write-Host "[verify] FAIL ($($sw.Elapsed.TotalSeconds.ToString('F1'))s)"
    $failures | ForEach-Object { Write-Host "  FAIL: $_" }
    exit 1
}
