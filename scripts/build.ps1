. (Join-Path $PSScriptRoot 'common.ps1')

$root = Get-RepositoryRoot
$dotnet = Get-DotNetExecutable

& $dotnet build (Join-Path $root 'CodexHelper.sln') -c Release --nologo
exit $LASTEXITCODE

