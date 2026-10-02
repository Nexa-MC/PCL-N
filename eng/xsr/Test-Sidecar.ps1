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
