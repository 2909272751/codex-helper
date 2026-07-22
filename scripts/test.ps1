. (Join-Path $PSScriptRoot 'common.ps1')

$root = Get-RepositoryRoot
$dotnet = Get-DotNetExecutable

& $dotnet build (Join-Path $root 'CodexHelper.sln') -c Debug --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $dotnet run --project (Join-Path $root 'tests\CodexHelper.Core.Tests\CodexHelper.Core.Tests.csproj') -c Debug --no-build
exit $LASTEXITCODE

