Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-DotNetExecutable {
    if ($env:DOTNET_EXE -and (Test-Path -LiteralPath $env:DOTNET_EXE)) {
        return (Resolve-Path -LiteralPath $env:DOTNET_EXE).Path
    }

    $localRuntime = 'C:\Users\29092\.cache\codex-runtimes\dotnet-sdk-8\dotnet.exe'
    if (Test-Path -LiteralPath $localRuntime) {
        return $localRuntime
    }

    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    throw 'The .NET 8 SDK was not found. Set DOTNET_EXE or install the .NET 8 SDK.'
}

function Get-ProjectVersion {
    param([Parameter(Mandatory)][string]$RepositoryRoot)

    [xml]$props = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $RepositoryRoot 'Directory.Build.props')
    $version = [string]$props.Project.PropertyGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$') {
        throw "Invalid project version: $version"
    }
    return $version
}

function Get-RepositoryRoot {
    $root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
    if (-not (Test-Path -LiteralPath (Join-Path $root 'CodexHelper.sln'))) {
        throw "Unable to confirm the Codex Helper repository root: $root"
    }
    return $root
}
