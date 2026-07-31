[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$cmake = & (Join-Path $PSScriptRoot 'resolve-cmake.ps1')
$nativeBuildDirectory = Join-Path $repositoryRoot 'artifacts\\native-verify'

& dotnet test (Join-Path $repositoryRoot 'CodexQuotaTaskbar.slnx') -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $cmake -S (Join-Path $repositoryRoot 'src\\native') -B $nativeBuildDirectory -A x64 -DBUILD_TESTING=ON
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $cmake --build $nativeBuildDirectory --config $Configuration --parallel
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $cmake --build $nativeBuildDirectory --target RUN_TESTS --config $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
