[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$nativeBuildDirectory = Join-Path $repositoryRoot 'artifacts\\native-verify'
$powerShell = [System.Environment]::ProcessPath
if ([string]::IsNullOrWhiteSpace($powerShell)) {
    $powerShell = (Get-Process -Id $PID).Path
}

& $powerShell -NoProfile -NonInteractive -File (Join-Path $PSScriptRoot 'verify-sensitive-boundary.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$policyPowerShell = (Get-Command pwsh.exe -CommandType Application -ErrorAction Stop).Source
& $policyPowerShell -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File `
    (Join-Path $repositoryRoot 'tests\security\managed-assembly-policy.selftest.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$cmake = & (Join-Path $PSScriptRoot 'resolve-cmake.ps1')

& dotnet test (Join-Path $repositoryRoot 'CodexQuotaTaskbar.slnx') -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& (Join-Path $PSScriptRoot 'package-host.ps1') -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $cmake -S (Join-Path $repositoryRoot 'src\\native') -B $nativeBuildDirectory -A x64 -DBUILD_TESTING=ON
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $cmake --build $nativeBuildDirectory --config $Configuration --parallel
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $cmake --build $nativeBuildDirectory --target RUN_TESTS --config $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
