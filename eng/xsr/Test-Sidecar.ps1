[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$taskRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskProject = Join-Path $taskRepo 'tests/Nexa.Sidecar.Tests/Nexa.Sidecar.Tests.csproj'

# Use the managed test assembly locally. The suite still exercises real OS pipes/sockets;
# dedicated CI publishes/runs NativeAOT separately. Do not alter antivirus or allow lists.
& dotnet run --project $taskProject --configuration $Configuration -p:PublishAot=false -p:UseAppHost=false
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$taskHostProject = Join-Path $taskRepo 'tests/Nexa.Xsr.Runtime.Tests/Nexa.Xsr.Runtime.Tests.csproj'
# Physical Host fixtures rename this apphost to .nsc and authenticate over real IPC.
& dotnet run --project $taskHostProject --configuration $Configuration -p:PublishAot=false -p:UseAppHost=true
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
