. (Join-Path $PSScriptRoot 'common.ps1')

$root = Get-RepositoryRoot
$dotnet = Get-DotNetExecutable
$version = Get-ProjectVersion -RepositoryRoot $root
$artifactBase = Join-Path $root 'artifacts'
if (-not (Test-Path -LiteralPath $artifactBase)) {
    New-Item -ItemType Directory -Path $artifactBase | Out-Null
}
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

$projects = @(
    @{ Project = 'src\CodexHelper.App\CodexHelper.App.csproj'; Name = "codex-helper-v$version-windows-x64.exe"; Source = 'CodexHelper.exe'; Folder = 'main' },
    @{ Project = 'src\CodexHelper.Rescue\CodexHelper.Rescue.csproj'; Name = "codex-helper-rescue-v$version-windows-x64.exe"; Source = 'CodexHelperRescue.exe'; Folder = 'rescue' },
    @{ Project = 'src\CodexHelper.CredentialHelper\CodexHelper.CredentialHelper.csproj'; Name = "codex-helper-credential-helper-v$version-windows-x64.exe"; Source = 'CodexHelperCredentialHelper.exe'; Folder = 'credential-helper' }
)

foreach ($item in $projects) {
    $output = Join-Path $publishRoot $item.Folder
    & $dotnet publish (Join-Path $root $item.Project) -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -o $output --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Copy-Item -LiteralPath (Join-Path $output $item.Source) -Destination (Join-Path $artifactRoot $item.Name)
}

$readme = Join-Path $root 'README.md'
Copy-Item -LiteralPath $readme -Destination (Join-Path $artifactRoot "codex-helper-v$version-README-zh-CN.md")
Copy-Item -LiteralPath (Join-Path $root 'docs\SECURITY.md') -Destination (Join-Path $artifactRoot "codex-helper-v$version-SECURITY-zh-CN.md")
Copy-Item -LiteralPath (Join-Path $root 'docs\USER_GUIDE_zh-CN.md') -Destination (Join-Path $artifactRoot "codex-helper-v$version-USER-GUIDE-zh-CN.md")
Copy-Item -LiteralPath (Join-Path $root 'THIRD_PARTY_NOTICES.md') -Destination (Join-Path $artifactRoot "codex-helper-v$version-THIRD-PARTY-NOTICES.md")

$zipName = "codex-helper-v$version-windows-x64-portable.zip"
Compress-Archive -Path (Join-Path $artifactRoot '*') -DestinationPath (Join-Path $artifactRoot $zipName) -CompressionLevel Optimal

$isccCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe'
)
$iscc = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 is required to build the installer. Install it with: winget install JRSoftware.InnoSetup' }

$installerScript = Join-Path $root 'installer\CodexHelper.iss'
& $iscc "/DMyAppVersion=$version" "/DMyMainExe=$(Join-Path $publishRoot 'main\CodexHelper.exe')" "/DMyCredentialHelperExe=$(Join-Path $publishRoot 'credential-helper\CodexHelperCredentialHelper.exe')" "/DMyRescueExe=$(Join-Path $publishRoot 'rescue\CodexHelperRescue.exe')" "/DMyOutputDir=$artifactRoot" "/DMyGuideFile=$(Join-Path $root 'docs\INSTALLATION_GUIDE_zh-CN.txt')" $installerScript
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$hashes = Get-ChildItem -File -LiteralPath $artifactRoot | ForEach-Object {
    $hash = Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName
    "{0}  {1}" -f $hash.Hash.ToLowerInvariant(), $_.Name
}
Set-Content -Encoding UTF8 -LiteralPath (Join-Path $artifactRoot "codex-helper-v$version-sha256.txt") -Value $hashes

Remove-Item -Recurse -Force -LiteralPath $publishRoot
Write-Host "Release artifacts: $artifactRoot"
