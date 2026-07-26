. (Join-Path $PSScriptRoot 'common.ps1')

$root = Get-RepositoryRoot
$dotnet = Get-DotNetExecutable
$version = Get-ProjectVersion -RepositoryRoot $root
$artifactRoot = Join-Path $root "artifacts\v$version-runtime-required"
$publishRoot = Join-Path $root "artifacts\.publish-runtime-required-v$version"
foreach ($target in @($artifactRoot, $publishRoot)) {
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
}
New-Item -ItemType Directory -Path $artifactRoot | Out-Null

$targets = @(
    @{ Project = 'src\CodexHelper.App\CodexHelper.App.csproj'; Folder = 'main' },
    @{ Project = 'src\CodexHelper.CredentialHelper\CodexHelper.CredentialHelper.csproj'; Folder = 'helper' },
    @{ Project = 'src\CodexHelper.Rescue\CodexHelper.Rescue.csproj'; Folder = 'rescue' }
)
foreach ($target in $targets) {
    $output = Join-Path $publishRoot $target.Folder
    & $dotnet publish (Join-Path $root $target.Project) -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false -o $output --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
$iscc = @((Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'), 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe', 'C:\Program Files\Inno Setup 6\ISCC.exe') | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 is required.' }
& $iscc "/DMyAppVersion=$version" "/DMyMainDir=$(Join-Path $publishRoot 'main')" "/DMyHelperDir=$(Join-Path $publishRoot 'helper')" "/DMyRescueDir=$(Join-Path $publishRoot 'rescue')" "/DMyOutputDir=$artifactRoot" (Join-Path $root 'installer\CodexHelperRuntimeRequired.iss')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Remove-Item -LiteralPath $publishRoot -Recurse -Force
Write-Host "Runtime-required installer: $artifactRoot"
