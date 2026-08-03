. (Join-Path $PSScriptRoot 'common.ps1')

$root = Get-RepositoryRoot
$dotnet = Get-DotNetExecutable
$version = Get-ProjectVersion -RepositoryRoot $root

# 精简发布策略（v3.3.3+）：GitHub Release 只发布依赖本机 .NET 8 Desktop Runtime 的
# 精简安装包与其 SHA-256 校验文件。不生成也不选入自包含完整安装包、便携 ZIP、
# 独立主 EXE、rescue 或 credential-helper 资产。
$artifactRoot = Join-Path $root "artifacts\v$version"
$publishRoot = Join-Path $root "artifacts\.publish-v$version"

foreach ($target in @($artifactRoot, $publishRoot)) {
    $resolvedParent = (Resolve-Path -LiteralPath (Split-Path -Parent $target)).Path
    if (-not $resolvedParent.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a path outside the repository: $target"
    }
    if (Test-Path -LiteralPath $target) {
        Remove-Item -Recurse -Force -LiteralPath $target
    }
}
New-Item -ItemType Directory -Path $artifactRoot | Out-Null

$targets = @(
    @{ Project = 'src\CodexHelper.App\CodexHelper.App.csproj'; Folder = 'main' },
    @{ Project = 'src\CodexHelper.CredentialHelper\CodexHelper.CredentialHelper.csproj'; Folder = 'helper' },
    @{ Project = 'src\CodexHelper.Rescue\CodexHelper.Rescue.csproj'; Folder = 'rescue' }
)
foreach ($item in $targets) {
    $output = Join-Path $publishRoot $item.Folder
    & $dotnet publish (Join-Path $root $item.Project) -c Release -r win-x64 --self-contained false -o $output --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$isccCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe'
)
$iscc = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 is required to build the installer. Install it with: winget install JRSoftware.InnoSetup' }

$installerScript = Join-Path $root 'installer\CodexHelperRuntimeRequired.iss'
& $iscc "/DMyAppVersion=$version" "/DMyMainDir=$(Join-Path $publishRoot 'main')" "/DMyHelperDir=$(Join-Path $publishRoot 'helper')" "/DMyRescueDir=$(Join-Path $publishRoot 'rescue')" "/DMyOutputDir=$artifactRoot" $installerScript
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# 只对实际发布资产（精简 setup.exe）生成 SHA-256，避免人工从完整包目录挑选。
$setupName = "codex-helper-v$version-setup.exe"
$setupPath = Join-Path $artifactRoot $setupName
if (-not (Test-Path -LiteralPath $setupPath)) {
    throw "Expected setup asset was not produced: $setupPath"
}
$hash = Get-FileHash -Algorithm SHA256 -LiteralPath $setupPath
$hashLine = "{0}  {1}" -f $hash.Hash.ToLowerInvariant(), $setupName
Set-Content -Encoding UTF8 -LiteralPath (Join-Path $artifactRoot "codex-helper-v$version-sha256.txt") -Value $hashLine

Remove-Item -Recurse -Force -LiteralPath $publishRoot
Write-Host "Thin release artifacts: $artifactRoot"
Get-ChildItem -File -LiteralPath $artifactRoot | ForEach-Object { Write-Host "  $($_.Name)" }
