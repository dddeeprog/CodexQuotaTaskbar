[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$BuildDirectory,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$nativeSource = Join-Path $repositoryRoot 'src\native'
$repositoryRootFullPath = [System.IO.Path]::GetFullPath($repositoryRoot).TrimEnd([char[]]@('\', '/'))
$nativeBuildDirectory = if ([string]::IsNullOrWhiteSpace($BuildDirectory)) {
    Join-Path $repositoryRoot 'artifacts\native'
}
else {
    [System.IO.Path]::GetFullPath($BuildDirectory)
}
$bridgePath = Join-Path $nativeBuildDirectory ($Configuration + '\CodexQuotaTaskbar.Bridge.dll')
$peImportPolicyPath = Join-Path $PSScriptRoot 'pe-import-policy.ps1'
$nativeBridgePolicyPath = Join-Path $PSScriptRoot 'native-bridge-policy.ps1'
if (-not (Test-Path -LiteralPath $peImportPolicyPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $nativeBridgePolicyPath -PathType Leaf)) {
    throw 'Native build requires the shared PE and native bridge security policies.'
}
. $peImportPolicyPath
. $nativeBridgePolicyPath

function Assert-BuildDirectoryWithinRepository {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd([char[]]@('\', '/'))
    if (-not $fullPath.StartsWith($repositoryRootFullPath + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Native build directory must stay within the repository.'
    }
    return $fullPath
}

function Assert-NoReparseAncestors {
    param([Parameter(Mandatory = $true)][string]$Path)

    $current = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    while ($null -ne $current) {
        if (($current.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Reparse points are not allowed in native build paths: $($current.FullName)"
        }
        if ($current.FullName.TrimEnd('\', '/') -ieq $repositoryRoot.TrimEnd('\', '/')) {
            return
        }
        $current = if ($current -is [System.IO.FileInfo]) { $current.Directory } else { $current.Parent }
    }

    throw "Native build path escapes the repository root: $Path"
}

function Assert-ResolvedCmakePath {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Candidate)

    if ([string]::IsNullOrWhiteSpace($Candidate)) {
        throw 'CMake resolver returned an empty path.'
    }
    try {
        $path = [System.IO.Path]::GetFullPath($Candidate)
    }
    catch [System.Exception] {
        throw 'CMake resolver returned an invalid path.'
    }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw 'CMake resolver path is not a regular file.'
    }
    $file = Get-Item -LiteralPath $path -Force
    if (($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'CMake resolver path is a reparse point.'
    }
    return $file.FullName
}

if ($SelfTest) {
    if ($null -eq (Get-Command Assert-NativeBridgeSourceBoundary -CommandType Function -ErrorAction SilentlyContinue) -or
        $null -eq (Get-Command Assert-NativeBridgeObjectImportBoundary -CommandType Function -ErrorAction SilentlyContinue) -or
        $null -eq (Get-Command Assert-NativeBridgeBinaryImportBoundary -CommandType Function -ErrorAction SilentlyContinue)) {
        throw 'Native build self-test requires the native bridge security policy.'
    }
    $LASTEXITCODE = $null
    $candidate = (Get-Command powershell.exe -CommandType Application -ErrorAction Stop).Source
    $resolved = Assert-ResolvedCmakePath -Candidate $candidate
    if ($resolved -cne $candidate) {
        throw 'Native build self-test changed a valid resolver result.'
    }
    $rejected = $false
    try { Assert-ResolvedCmakePath -Candidate '' | Out-Null } catch { $rejected = $true }
    if (-not $rejected) {
        throw 'Native build self-test accepted an empty resolver result.'
    }
    $sourceManifest = @(Get-NativeBridgeSourceManifest -SourceRoot $nativeSource)
    if ($sourceManifest.Count -eq 0) {
        throw 'Native build self-test did not produce a native source manifest.'
    }
    Write-Host 'Native build self-test: PASS'
    exit 0
}

Assert-NoReparseAncestors -Path $nativeSource
$nativeBuildDirectory = Assert-BuildDirectoryWithinRepository -Path $nativeBuildDirectory
if (Test-Path -LiteralPath $nativeBuildDirectory) {
    Assert-NoReparseAncestors -Path $nativeBuildDirectory
}
Assert-NativeBridgeSourceBoundary -SourceRoot $nativeSource
$sourceManifestBefore = @(Get-NativeBridgeSourceManifest -SourceRoot $nativeSource)

$cmakeResults = @(& (Join-Path $PSScriptRoot 'resolve-cmake.ps1'))
if ($cmakeResults.Count -ne 1) {
    throw 'CMake resolver returned an unexpected number of paths.'
}
$cmake = Assert-ResolvedCmakePath -Candidate ([string]$cmakeResults[0])

& $cmake -S $nativeSource -B $nativeBuildDirectory -A x64 -DBUILD_TESTING=OFF
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $cmake --build $nativeBuildDirectory --target CodexQuotaTaskbar.Bridge --config $Configuration --parallel
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Assert-NativeBridgeSourceBoundary -SourceRoot $nativeSource
$sourceManifestAfter = @(Get-NativeBridgeSourceManifest -SourceRoot $nativeSource)
if (($sourceManifestBefore -join "`n") -cne ($sourceManifestAfter -join "`n")) {
    throw 'Native bridge source changed between security audit and compilation.'
}

if (-not (Test-Path -LiteralPath $bridgePath -PathType Leaf)) {
    throw "Native bridge was not produced at the required path: $bridgePath"
}
Assert-NoReparseAncestors -Path $bridgePath
$bridgeFile = Get-Item -LiteralPath $bridgePath -Force
if ($bridgeFile.Length -le 0 -or $bridgeFile.Length -gt 67108864) {
    throw 'Native bridge size is outside the permitted range.'
}
Assert-NativeBridgeObjectImportBoundary -BuildDirectory $nativeBuildDirectory -Configuration $Configuration
Assert-NativeBridgeBinaryImportBoundary -Path $bridgeFile.FullName

Write-Output $bridgeFile.FullName
