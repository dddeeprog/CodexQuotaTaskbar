[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$IsccPath = $env:ISCC_PATH,
    [string]$ArtifactsDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
if ($ArtifactsDirectory) {
    $requestedRoot = [IO.Path]::GetFullPath($ArtifactsDirectory)
    if (-not $requestedRoot.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Custom installer output must remain within repository artifacts.'
    }
    $artifactsRoot = $requestedRoot
}
$publishDirectory = Join-Path $repositoryRoot "artifacts\installer-publish\$Configuration"
if ($ArtifactsDirectory) { $publishDirectory = Join-Path $artifactsRoot "installer-publish\$Configuration" }
$outputDirectory = Join-Path $artifactsRoot 'installers'
$version = (Get-Content -LiteralPath (Join-Path $repositoryRoot 'VERSION') -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'VERSION must contain a three-part numeric version.' }

if (-not $IsccPath) {
    $command = Get-Command ISCC.exe -CommandType Application -ErrorAction SilentlyContinue
    if ($command) { $IsccPath = $command.Source }
    else {
        $candidates = @(
            (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
        )
        $IsccPath = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    }
}
if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
    throw 'Inno Setup 6.4+ is required. Set ISCC_PATH or pass -IsccPath with the full path to ISCC.exe.'
}

# Separate staging avoids deleting or locking the running portable application.
New-Item -ItemType Directory -Path $publishDirectory, $outputDirectory -Force | Out-Null
& dotnet publish (Join-Path $repositoryRoot 'src\CodexQuotaTaskbar.Host\CodexQuotaTaskbar.Host.csproj') `
    -c $Configuration -r win-x64 --self-contained true -o $publishDirectory `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "Host publish failed: $LASTEXITCODE" }
$executable = Join-Path $publishDirectory 'CodexQuotaTaskbar.exe'
if ((Get-Item -LiteralPath $executable).VersionInfo.FileVersion -ne "$version.0") {
    throw 'Published executable version differs from VERSION.'
}

& $IsccPath "/DAppVersion=$version" "/DRepoRoot=$repositoryRoot" "/DPublishDir=$publishDirectory" `
    "/O$outputDirectory" (Join-Path $PSScriptRoot 'installer\QuotaIsland.iss')
if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed: $LASTEXITCODE" }
$installer = Join-Path $outputDirectory 'CodexQuotaTaskbar-Setup-win-x64.exe'
Get-Item -LiteralPath $installer | Select-Object FullName, Length
Get-FileHash -LiteralPath $installer -Algorithm SHA256
