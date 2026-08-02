[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Verify', 'Build', 'Host', 'Package', 'Probe')]
    [string]$Target,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = $PSScriptRoot

switch ($Target) {
    'Verify' {
        & (Join-Path $repositoryRoot 'build\\verify.ps1') -Configuration $Configuration
        exit $LASTEXITCODE
    }
    'Build' {
        & dotnet build (Join-Path $repositoryRoot 'CodexQuotaTaskbar.slnx') -c $Configuration
        exit $LASTEXITCODE
    }
    'Host' {
        & dotnet build (Join-Path $repositoryRoot 'src\CodexQuotaTaskbar.Host\CodexQuotaTaskbar.Host.csproj') -c $Configuration
        exit $LASTEXITCODE
    }
    'Package' {
        & (Join-Path $repositoryRoot 'build\package-host.ps1') -Configuration $Configuration
        exit $LASTEXITCODE
    }
    'Probe' {
        & (Join-Path $repositoryRoot 'build\\package-probe.ps1') -Configuration $Configuration
        exit $LASTEXITCODE
    }
}
