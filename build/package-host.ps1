[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$publishDirectory = [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot "host-publish\$Configuration"))
$releaseDirectory = [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot 'releases'))
$archivePath = Join-Path $releaseDirectory 'CodexQuotaTaskbar-win-x64.zip'

foreach ($path in @($publishDirectory, $releaseDirectory)) {
    if (-not $path.StartsWith($artifactsRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Package path escapes the artifacts directory: $path"
    }
}

if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null

& dotnet publish (Join-Path $repositoryRoot 'src\CodexQuotaTaskbar.Host\CodexQuotaTaskbar.Host.csproj') `
    -c $Configuration -r win-x64 --self-contained true -o $publishDirectory `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$allowedFiles = @(
    'CodexQuotaTaskbar.exe'
)
$actualFiles = @(Get-ChildItem -LiteralPath $publishDirectory -File | Select-Object -ExpandProperty Name | Sort-Object)
$unexpected = @($actualFiles | Where-Object { $_ -notin $allowedFiles })
$missing = @($allowedFiles | Where-Object { $_ -notin $actualFiles })
if ($unexpected.Count -gt 0 -or $missing.Count -gt 0) {
    throw "Host publish allowlist mismatch. Unexpected: $($unexpected -join ', '); Missing: $($missing -join ', ')"
}

if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}
Compress-Archive -LiteralPath ($allowedFiles | ForEach-Object { Join-Path $publishDirectory $_ }) -DestinationPath $archivePath -CompressionLevel Optimal

& (Join-Path $repositoryRoot 'tests\security\host-boundary-policy.ps1') -PackagePath $archivePath
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
Write-Host "Host package: $archivePath"
Write-Host "SHA256: $hash"
