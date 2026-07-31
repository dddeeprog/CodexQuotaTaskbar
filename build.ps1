[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Verify', 'Build', 'Probe')]
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
    'Probe' {
        & dotnet run --project (Join-Path $repositoryRoot 'tools\\CodexQuotaTaskbar.CompatibilityProbe\\CodexQuotaTaskbar.CompatibilityProbe.csproj') -c $Configuration
        exit $LASTEXITCODE
    }
}
