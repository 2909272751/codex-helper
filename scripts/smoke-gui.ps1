<#
.SYNOPSIS
    Real GUI smoke test: launches CodexHelper.exe --smoke-test against an isolated
    CODEX_HELPER_DATA_HOME and verifies the collaboration page window appears and
    stays alive for at least 2 seconds.
.DESCRIPTION
    Uses a fresh temporary directory (under the system temp root) for all data so
    the installed/real Helper data is never touched. The process is always killed
    at the end, and a crash / early exit fails the test with a non-zero exit code.
#>

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

. (Join-Path $PSScriptRoot 'common.ps1')

$ErrorActionPreference = 'Stop'

# Clean up the smoke process: returns whether the target has exited.
# Extracted to a plain function so it can be safely called inside finally
# (finally forbids break/continue/return) and to avoid StrictMode complaints
# about loop-condition variables referenced inside finally.
function Test-SmokeProcessExited([System.Diagnostics.Process]$Target) {
    $Target.Refresh()
    if ($Target.HasExited) { return $true }
    # Kill only while the target is alive; if the GUI already exited on its own
    # (the normal race after the smoke test passes) we do not fail cleanup.
    & taskkill /PID $Target.Id /T /F 2>$null | Out-Null
    # Poll until the process exits (up to ~10 seconds).
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        Start-Sleep -Milliseconds 200
        $Target.Refresh()
        if ($Target.HasExited) { return $true }
    }
    $Target.Refresh()
    return $Target.HasExited
}

$root = Get-RepositoryRoot
$dotnet = Get-DotNetExecutable

& $dotnet build (Join-Path $root 'src\CodexHelper.App\CodexHelper.App.csproj') -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "App build failed (exit $LASTEXITCODE)" }

$exe = Join-Path $root "src\CodexHelper.App\bin\$Configuration\net8.0-windows\CodexHelper.exe"
if (-not (Test-Path -LiteralPath $exe)) { throw "Smoke EXE not found: $exe" }

# Isolated data root inside the system temp directory only.
$dataHome = Join-Path ([System.IO.Path]::GetTempPath()) ("codexhelper-smoke-" + [Guid]::NewGuid().ToString('N'))
# Codex root points to a safe sub-directory inside the isolated data home so no
# real credentials are ever read.
$codexRoot = Join-Path $dataHome 'codex'
New-Item -ItemType Directory -Path $codexRoot | Out-Null

# Write a minimal, valid settings.json BEFORE launch so the app opens directly on
# the Collaboration page: onboarding completed and lastSelectedPage=Collaboration.
$settingsPath = Join-Path $dataHome 'settings.json'
$settingsJson = @{
    schemaVersion             = 1
    codexRoot                 = $codexRoot
    backupRepositoryPath      = ''
    workspaceRoots            = @()
    protectedProjectPaths     = @()
    includeSessions           = $true
    includeAttachments        = $true
    includeGeneratedImages    = $false
    closeToTray               = $false
    useDarkTheme              = $false
    hasCompletedOnboarding    = $true
    lastSelectedPage          = 'Collaboration'
    lastOfficialModel         = ''
    reasonixExecutionIntensity = 'auto'
    deepSeekCacheRange        = '14d'
} | ConvertTo-Json
[IO.File]::WriteAllText($settingsPath, $settingsJson, [System.Text.UTF8Encoding]::new($false))

$oldDataHome = $env:CODEX_HELPER_DATA_HOME
$process = $null
try {
    $env:CODEX_HELPER_DATA_HOME = $dataHome
    $process = Start-Process -FilePath $exe -ArgumentList '--smoke-test' -PassThru
    if (-not $process) { throw 'Failed to start smoke process.' }
}
finally {
    $env:CODEX_HELPER_DATA_HOME = $oldDataHome
}

try {
    # Wait for a main window to appear (window shown == collaboration page constructed).
    $windowDeadline = (Get-Date).AddSeconds(20)
    do {
        $process.Refresh()
        if ($process.HasExited) { throw "Smoke process exited early (code $($process.ExitCode))." }
        if ($process.MainWindowHandle -ne 0) { break }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $windowDeadline)

    $process.Refresh()
    if ($process.MainWindowHandle -eq 0) { throw 'Smoke process did not show a main window within 20s.' }
    if (-not $process.Responding) { throw 'Smoke process main window is not responding.' }

    # Must stay alive and responsive for at least 2 seconds.
    Start-Sleep -Seconds 2
    $process.Refresh()
    if ($process.HasExited) { throw "Smoke process exited (code $($process.ExitCode)) before the 2s liveness window." }
    if (-not $process.Responding) { throw 'Smoke process stopped responding during the 2s liveness window.' }

    Write-Host "GUI smoke OK: collaboration page opened under isolated data home, main window stayed alive and responding for 2s."
}
finally {
    $processAlive = $false
    if ($process) {
        $processAlive = -not (Test-SmokeProcessExited -Target $process)
    }
    if (Test-Path -LiteralPath $dataHome) {
        try { Remove-Item -Recurse -Force -LiteralPath $dataHome }
        catch {
            # A real cleanup failure only fails the test while the process is still alive;
            # once it has exited, ignore any leftover temp directory.
            if ($processAlive) { throw }
        }
    }
    if ($processAlive) { throw 'Failed to terminate smoke GUI process; it is still alive.' }
}
