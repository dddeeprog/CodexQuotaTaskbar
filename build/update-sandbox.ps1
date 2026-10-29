[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

# Build/package preparation only. Never runs the driver, a fixture, a test host or the product.
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectDirectory = Join-Path $repositoryRoot 'tools\CodexQuotaTaskbar.UpdateSandbox'
$project = Join-Path $projectDirectory 'CodexQuotaTaskbar.UpdateSandbox.csproj'
$outputDirectory = Join-Path $projectDirectory "bin\$Configuration\net10.0"

# No package references: this existing local directory is the only restore source; no remote feed.
& dotnet restore $project --source $projectDirectory --ignore-failed-sources
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& dotnet build $project -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$packageFiles = [ordered]@{}
$packageNames = @(
    'CodexQuotaTaskbar.UpdateSandbox.exe',
    'CodexQuotaTaskbar.UpdateSandbox.dll',
    'CodexQuotaTaskbar.UpdateSandbox.deps.json',
    'CodexQuotaTaskbar.UpdateSandbox.runtimeconfig.json',
    'fixture-old.payload',
    'fixture-new.payload'
)
foreach ($name in $packageNames) {
    $path = Join-Path $outputDirectory $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing built fixture package file: $name" }
    $packageFiles[$name] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
}
$manifest = [ordered]@{ Schema = 1; Kind = 'untrusted-test-fixture-only'; Files = $packageFiles }
$manifestPath = Join-Path $outputDirectory 'sandbox-package.json'
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
$manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
Write-Output "Built fixture package only: $outputDirectory"
Write-Output "Manifest SHA256: $manifestHash"
Write-Output 'No sandbox entry point or subprocess was executed. Any later driver invocation defaults to dry-run and requires an explicit absolute fresh GUID sandbox root.'
