[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$policyPath = Join-Path $repositoryRoot 'build\managed-assembly-policy.ps1'
if (-not (Test-Path -LiteralPath $policyPath -PathType Leaf)) {
    throw 'Required managed assembly policy is missing.'
}
. $policyPath

$probeProjectPath = Join-Path $repositoryRoot 'tools\CodexQuotaTaskbar.CompatibilityProbe\CodexQuotaTaskbar.CompatibilityProbe.csproj'
$probeProjectText = [System.IO.File]::ReadAllText($probeProjectPath, [System.Text.UTF8Encoding]::new($false))
foreach ($requiredTargetFragment in @(
        '<Target Name="AuditActualBundleInputs"',
        'BeforeTargets="GenerateSingleFileBundle"',
        'DependsOnTargets="PrepareForBundle"',
        '@(FilesToBundle',
        "@(IntermediateAssembly->'intermediate|%(FullPath)|')",
        'CqtbBundleAuditManifestPath',
        'CqtbBundleAuditScriptPath',
        '<Exec'
    )) {
    if (-not $probeProjectText.Contains($requiredTargetFragment, [System.StringComparison]::Ordinal)) {
        throw "Managed policy self-test requires probe project bundle-audit wiring: $requiredTargetFragment"
    }
}
$verifyScriptPath = Join-Path $repositoryRoot 'build\verify.ps1'
$verifyScriptText = [System.IO.File]::ReadAllText($verifyScriptPath, [System.Text.UTF8Encoding]::new($false))
if (-not $verifyScriptText.Contains('managed-assembly-policy.selftest.ps1', [System.StringComparison]::Ordinal)) {
    throw 'Managed policy self-test must be part of the non-live verification target.'
}
$runtimeSharedRoot = Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.NETCore.App'
$systemBuffers = if (Test-Path -LiteralPath $runtimeSharedRoot -PathType Container) {
    Get-ChildItem -LiteralPath $runtimeSharedRoot -Recurse -Filter 'System.Buffers.dll' -File | Select-Object -First 1
}
if ($null -eq $systemBuffers -or -not (Test-TrustedFrameworkAssembly -Identity (Test-IsManagedAssembly -Path $systemBuffers.FullName))) {
    throw 'Managed policy self-test requires System.Buffers from the installed .NET runtime to be an approved framework assembly.'
}
$systemCore = if (Test-Path -LiteralPath $runtimeSharedRoot -PathType Container) {
    Get-ChildItem -LiteralPath $runtimeSharedRoot -Recurse -Filter 'System.Core.dll' -File | Select-Object -First 1
}
if ($null -eq $systemCore -or -not (Test-TrustedFrameworkAssembly -Identity (Test-IsManagedAssembly -Path $systemCore.FullName))) {
    throw 'Managed policy self-test requires System.Core from the installed .NET runtime to be an approved framework assembly.'
}
foreach ($legacyRuntimeAssemblyName in @('mscorlib.dll', 'netstandard.dll')) {
    $legacyRuntimeAssembly = if (Test-Path -LiteralPath $runtimeSharedRoot -PathType Container) {
        Get-ChildItem -LiteralPath $runtimeSharedRoot -Recurse -Filter $legacyRuntimeAssemblyName -File | Select-Object -First 1
    }
    if ($null -eq $legacyRuntimeAssembly -or
        -not (Test-TrustedFrameworkAssembly -Identity (Test-IsManagedAssembly -Path $legacyRuntimeAssembly.FullName))) {
        throw "Managed policy self-test requires $legacyRuntimeAssemblyName from the installed .NET runtime to be an approved framework assembly."
    }
}
$desktopSharedRoot = Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.WindowsDesktop.App'
$windowsBase = if (Test-Path -LiteralPath $desktopSharedRoot -PathType Container) {
    Get-ChildItem -LiteralPath $desktopSharedRoot -Recurse -Filter 'WindowsBase.dll' -File | Select-Object -First 1
}
if ($null -eq $windowsBase -or -not (Test-TrustedFrameworkAssembly -Identity (Test-IsManagedAssembly -Path $windowsBase.FullName))) {
    throw 'Managed policy self-test requires WindowsBase from the installed desktop runtime to be an approved framework assembly.'
}

$script:ExpectedApplicationAssemblies = @(
    'CodexQuotaTaskbar.CompatibilityProbe.dll',
    'CodexQuotaTaskbar.BridgeControl.dll',
    'CodexQuotaTaskbar.Core.dll'
)

function Assert-Rejected {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [Parameter(Mandatory = $true)][string]$Message
    )

    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    if (-not $rejected) {
        throw $Message
    }
}

function New-ManagedFixtureAssembly {
    param(
        [Parameter(Mandatory = $true)][string]$OutputPath,
        [Parameter(Mandatory = $true)][string]$TypeName,
        [Parameter(Mandatory = $true)][string]$Body
    )

    $source = @"
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;

public static class $TypeName
{
    $Body
}
"@
    $outputDirectory = [System.IO.Path]::GetDirectoryName($OutputPath)
    $sourceDirectory = Join-Path ([System.IO.Path]::GetDirectoryName($outputDirectory)) `
        ($TypeName + '-source')
    $null = New-Item -ItemType Directory -Path $sourceDirectory -Force
    $sourcePath = Join-Path $sourceDirectory 'Fixture.cs'
    $projectPath = Join-Path $sourceDirectory 'Fixture.csproj'
    $assemblyName = [System.IO.Path]::GetFileNameWithoutExtension($OutputPath)
    [System.IO.File]::WriteAllText($sourcePath, $source, [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText($projectPath, @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
    <AssemblyName>$assemblyName</AssemblyName>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>disable</Nullable>
  </PropertyGroup>
</Project>
"@, [System.Text.UTF8Encoding]::new($false))
    & dotnet build $projectPath -nologo -c Release -o $outputDirectory --ignore-failed-sources
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $OutputPath -PathType Leaf)) {
        throw "Managed policy self-test failed to compile fixture $assemblyName."
    }
}

function New-ManagedFixtureSet {
    param(
        [Parameter(Mandatory = $true)][string]$RootPath,
        [Parameter(Mandatory = $true)][string]$CoreBody,
        [Parameter(Mandatory = $true)][string]$Label,
        [string]$SingleFileHostSourcePath
    )

    $null = New-Item -ItemType Directory -Path $RootPath -Force
    New-ManagedFixtureAssembly -OutputPath (Join-Path $RootPath $script:ExpectedApplicationAssemblies[0]) `
        -TypeName ('Probe_' + $Label) -Body 'public static int Value() { return 1; }'
    New-ManagedFixtureAssembly -OutputPath (Join-Path $RootPath $script:ExpectedApplicationAssemblies[1]) `
        -TypeName ('Bridge_' + $Label) -Body 'public static int Value() { return 2; }'
    New-ManagedFixtureAssembly -OutputPath (Join-Path $RootPath $script:ExpectedApplicationAssemblies[2]) `
        -TypeName ('Core_' + $Label) -Body $CoreBody
    if (-not [string]::IsNullOrWhiteSpace($SingleFileHostSourcePath)) {
        Copy-Item -LiteralPath $SingleFileHostSourcePath -Destination (Join-Path $RootPath 'singlefilehost.exe') -Force
    }
}

function New-PreparedSingleFileHost {
    param([Parameter(Mandatory = $true)][string]$RootPath)

    $artifactPath = Join-Path $RootPath 'single-file-host-artifacts'
    $publishPath = Join-Path $RootPath 'single-file-host-publish'
    $bridgePayloadPath = Join-Path $RootPath 'fixture-bridge.dll'
    $bridgePayloadSha256Path = Join-Path $RootPath 'fixture-bridge.sha256'
    [System.IO.File]::WriteAllBytes($bridgePayloadPath, [byte[]](1, 3, 3, 7, 9, 2, 4, 6))
    $bridgePayloadHash = (Get-FileHash -LiteralPath $bridgePayloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText($bridgePayloadSha256Path, $bridgePayloadHash, [System.Text.UTF8Encoding]::new($false))
    $null = & dotnet publish $probeProjectPath -nologo -c Release -r win-x64 --self-contained true `
        '-p:UseArtifactsOutput=true' ('-p:ArtifactsPath=' + $artifactPath) `
        ('-p:PublishDir=' + $publishPath + '\') '-p:PublishSingleFile=true' `
        '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:DebugType=None' `
        '-p:EmbedBridgePayload=true' ('-p:BridgePayloadPath=' + $bridgePayloadPath) `
        ('-p:BridgePayloadSha256Path=' + $bridgePayloadSha256Path) --ignore-failed-sources
    if ($LASTEXITCODE -ne 0) {
        throw 'Managed policy self-test could not publish the prepared single-file host fixture.'
    }
    $hosts = @(Get-ChildItem -LiteralPath $artifactPath -Recurse -Filter 'singlefilehost.exe' -File)
    if ($hosts.Count -ne 1) {
        throw 'Managed policy self-test did not produce exactly one prepared single-file host.'
    }
    $probeAssemblyPath = Join-Path $hosts[0].Directory.FullName 'CodexQuotaTaskbar.CompatibilityProbe.dll'
    if (-not (Test-Path -LiteralPath $probeAssemblyPath -PathType Leaf)) {
        throw 'Managed policy self-test could not locate the prepared probe assembly.'
    }
    return [pscustomobject]@{
        HostPath = $hosts[0].FullName
        ProbeAssemblyPath = $probeAssemblyPath
        BridgePayloadPath = $bridgePayloadPath
        BridgePayloadSha256Path = $bridgePayloadSha256Path
    }
}

function New-Manifest {
    param(
        [Parameter(Mandatory = $true)][string]$StagingRoot,
        [Parameter(Mandatory = $true)][string]$ManifestPath,
        [string[]]$AdditionalInputPaths = @()
    )

    $rows = foreach ($name in $script:ExpectedApplicationAssemblies) {
        ('input|{0}|{1}' -f (Join-Path $StagingRoot $name), $name)
    }
    $candidateInputPaths = [System.Collections.Generic.List[string]]::new()
    $stagedSingleFileHostPath = Join-Path $StagingRoot 'singlefilehost.exe'
    if (Test-Path -LiteralPath $stagedSingleFileHostPath -PathType Leaf) {
        $candidateInputPaths.Add($stagedSingleFileHostPath)
    }
    foreach ($additionalPath in $AdditionalInputPaths) {
        $candidateInputPaths.Add($additionalPath)
    }
    $seenInputPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($additionalPath in $candidateInputPaths) {
        if (-not $seenInputPaths.Add([System.IO.Path]::GetFullPath($additionalPath))) {
            continue
        }
        $relativePath = if ([System.IO.Path]::GetFileName($additionalPath) -ceq 'singlefilehost.exe') {
            'CodexQuotaTaskbar.CompatibilityProbe.exe'
        }
        else {
            [System.IO.Path]::GetFileName($additionalPath)
        }
        $rows += ('input|{0}|{1}' -f $additionalPath, $relativePath)
    }
    [System.IO.File]::WriteAllLines($ManifestPath, $rows, [System.Text.UTF8Encoding]::new($false))
}

$fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('cqtb-managed-policy-' + [Guid]::NewGuid().ToString('N'))
try {
    $null = New-Item -ItemType Directory -Path $fixtureRoot -Force

    $safeRoot = Join-Path $fixtureRoot 'safe'
    New-ManagedFixtureSet -RootPath $safeRoot -Label 'Safe' -CoreBody 'public static int Value() { return 3; }'
    $safeDepsPath = Join-Path $safeRoot 'CodexQuotaTaskbar.CompatibilityProbe.deps.json'
    $safeRuntimeConfigPath = Join-Path $safeRoot 'CodexQuotaTaskbar.CompatibilityProbe.runtimeconfig.json'
    $preparedSingleFileHost = New-PreparedSingleFileHost -RootPath $fixtureRoot
    $singleFileHostSource = $preparedSingleFileHost.HostPath
    Assert-ApplicationAssemblyPolicy -Path $preparedSingleFileHost.ProbeAssemblyPath `
        -ExpectedSimpleName 'CodexQuotaTaskbar.CompatibilityProbe' -StagingRoot $fixtureRoot `
        -BridgePayloadPath $preparedSingleFileHost.BridgePayloadPath `
        -BridgePayloadSha256Path $preparedSingleFileHost.BridgePayloadSha256Path
    $safeSingleFileHostPath = Join-Path $safeRoot 'singlefilehost.exe'
    Copy-Item -LiteralPath $singleFileHostSource -Destination $safeSingleFileHostPath -Force
    [System.IO.File]::WriteAllText($safeDepsPath, '{}', [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText($safeRuntimeConfigPath, '{}', [System.Text.UTF8Encoding]::new($false))
    $safeManifest = Join-Path $fixtureRoot 'safe.manifest'
    New-Manifest -StagingRoot $safeRoot -ManifestPath $safeManifest -AdditionalInputPaths @($safeDepsPath, $safeRuntimeConfigPath, $safeSingleFileHostPath)
    Assert-ManagedBundleInputPolicy -ManifestPath $safeManifest -StagingRoot $safeRoot

    $singleFileHostTamperedRoot = Join-Path $fixtureRoot 'single-file-host-tampered'
    New-ManagedFixtureSet -RootPath $singleFileHostTamperedRoot -Label 'SingleFileHostTampered' -CoreBody 'public static int Value() { return 31; }'
    $tamperedSingleFileHostPath = Join-Path $singleFileHostTamperedRoot 'singlefilehost.exe'
    Copy-Item -LiteralPath $singleFileHostSource -Destination $tamperedSingleFileHostPath -Force
    $tamperedHostBytes = [System.IO.File]::ReadAllBytes($tamperedSingleFileHostPath)
    $tamperedHostBytes[$tamperedHostBytes.Length - 1] = $tamperedHostBytes[$tamperedHostBytes.Length - 1] -bxor 0x01
    [System.IO.File]::WriteAllBytes($tamperedSingleFileHostPath, $tamperedHostBytes)
    $tamperedSingleFileHostManifest = Join-Path $fixtureRoot 'single-file-host-tampered.manifest'
    New-Manifest -StagingRoot $singleFileHostTamperedRoot -ManifestPath $tamperedSingleFileHostManifest -AdditionalInputPaths @($tamperedSingleFileHostPath)
    Assert-Rejected -Action { Assert-ManagedBundleInputPolicy -ManifestPath $tamperedSingleFileHostManifest -StagingRoot $singleFileHostTamperedRoot } `
        -Message 'Managed policy self-test accepted a tampered staged single-file host.'

    $externalHostInputRoot = Join-Path $fixtureRoot 'external-host-input'
    New-ManagedFixtureSet -RootPath $externalHostInputRoot -Label 'ExternalHostInput' -CoreBody 'public static int Value() { return 32; }'
    $externalHostDirectory = Join-Path $fixtureRoot 'external-host'
    $null = New-Item -ItemType Directory -Path $externalHostDirectory -Force
    $externalHostPath = Join-Path $externalHostDirectory 'singlefilehost.exe'
    Copy-Item -LiteralPath $singleFileHostSource -Destination $externalHostPath -Force
    $externalHostManifest = Join-Path $fixtureRoot 'external-host.manifest'
    New-Manifest -StagingRoot $externalHostInputRoot -ManifestPath $externalHostManifest -AdditionalInputPaths @($externalHostPath)
    Assert-Rejected -Action { Assert-ManagedBundleInputPolicy -ManifestPath $externalHostManifest -StagingRoot $externalHostInputRoot } `
        -Message 'Managed policy self-test accepted a single-file host outside the isolated staging directory.'

    $networkRoot = Join-Path $fixtureRoot 'network'
    New-ManagedFixtureSet -RootPath $networkRoot -Label 'Network' -SingleFileHostSourcePath $singleFileHostSource -CoreBody @'
[DllImport("ws2_32.dll")]
public static extern int WSAStartup();
public static void UseSocket() { var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp); }
'@
    $networkManifest = Join-Path $fixtureRoot 'network.manifest'
    New-Manifest -StagingRoot $networkRoot -ManifestPath $networkManifest
    Assert-Rejected -Action { Assert-ManagedBundleInputPolicy -ManifestPath $networkManifest -StagingRoot $networkRoot } `
        -Message 'Managed policy self-test accepted network P/Invoke or Socket usage.'

    $loadRoot = Join-Path $fixtureRoot 'dynamic-load'
    New-ManagedFixtureSet -RootPath $loadRoot -Label 'DynamicLoad' -SingleFileHostSourcePath $singleFileHostSource -CoreBody @'
[DllImport("kernel32.dll", EntryPoint = "LoadLibraryW")]
public static extern IntPtr LoadLibrary(string name);
'@
    $loadManifest = Join-Path $fixtureRoot 'dynamic-load.manifest'
    New-Manifest -StagingRoot $loadRoot -ManifestPath $loadManifest
    Assert-Rejected -Action { Assert-ManagedBundleInputPolicy -ManifestPath $loadManifest -StagingRoot $loadRoot } `
        -Message 'Managed policy self-test accepted a dynamic library loader.'

    $killRoot = Join-Path $fixtureRoot 'kill'
    New-ManagedFixtureSet -RootPath $killRoot -Label 'Kill' -SingleFileHostSourcePath $singleFileHostSource -CoreBody @'
public static void KillProcess() { Process.GetCurrentProcess().Kill(); }
[DllImport("ntdll.dll")]
public static extern int NtTerminateProcess(IntPtr process, int status);
'@
    $killManifest = Join-Path $fixtureRoot 'kill.manifest'
    New-Manifest -StagingRoot $killRoot -ManifestPath $killManifest
    Assert-Rejected -Action { Assert-ManagedBundleInputPolicy -ManifestPath $killManifest -StagingRoot $killRoot } `
        -Message 'Managed policy self-test accepted process termination behavior.'

    $autostartRoot = Join-Path $fixtureRoot 'autostart'
    New-ManagedFixtureSet -RootPath $autostartRoot -Label 'Autostart' -SingleFileHostSourcePath $singleFileHostSource -CoreBody @'
public static string StartupFolder() { return Environment.GetFolderPath(Environment.SpecialFolder.Startup); }
public static void RegistryRun() { Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run"); }
public static object TaskScheduler() { return Type.GetTypeFromProgID("Schedule.Service"); }
'@
    $autostartManifest = Join-Path $fixtureRoot 'autostart.manifest'
    New-Manifest -StagingRoot $autostartRoot -ManifestPath $autostartManifest
    Assert-Rejected -Action { Assert-ManagedBundleInputPolicy -ManifestPath $autostartManifest -StagingRoot $autostartRoot } `
        -Message 'Managed policy self-test accepted an autostart behavior.'

    $helperRoot = Join-Path $fixtureRoot 'helper'
    New-ManagedFixtureSet -RootPath $helperRoot -Label 'Helper' -CoreBody 'public static int Value() { return 4; }' `
        -SingleFileHostSourcePath $singleFileHostSource
    $helperPath = Join-Path $helperRoot 'Helper.dll'
    New-ManagedFixtureAssembly -OutputPath $helperPath -TypeName 'UnexpectedHelper' -Body 'public static int Value() { return 5; }'
    $helperManifest = Join-Path $fixtureRoot 'helper.manifest'
    New-Manifest -StagingRoot $helperRoot -ManifestPath $helperManifest -AdditionalInputPaths @($helperPath)
    Assert-Rejected -Action { Assert-ManagedBundleInputPolicy -ManifestPath $helperManifest -StagingRoot $helperRoot } `
        -Message 'Managed policy self-test accepted an unapproved local dependency.'

    $runtimeProjection = Get-ChildItem -LiteralPath (Join-Path $env:USERPROFILE '.nuget\packages') -Recurse `
        -Filter 'Microsoft.Windows.SDK.NET.dll' -File | Where-Object {
            $runtimeProjectionIdentity = Test-IsManagedAssembly -Path $_.FullName
            $null -ne $runtimeProjectionIdentity -and
                $runtimeProjectionIdentity.Name -ceq 'Microsoft.Windows.SDK.NET'
        } | Select-Object -First 1
    if ($null -eq $runtimeProjection) {
        throw 'Managed policy self-test could not locate Microsoft.Windows.SDK.NET.dll.'
    }
    $projectionManifest = Join-Path $fixtureRoot 'runtime-projection.manifest'
    New-Manifest -StagingRoot $safeRoot -ManifestPath $projectionManifest -AdditionalInputPaths @($runtimeProjection.FullName)
    Assert-Rejected -Action {
        Assert-ManagedBundleInputPolicy -ManifestPath $projectionManifest -StagingRoot $safeRoot
    } -Message 'Managed policy self-test accepted the Windows SDK managed projection.'

    Write-Host 'Managed assembly policy self-test: PASS'
}
finally {
    if (Test-Path -LiteralPath $fixtureRoot) {
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    }
}
