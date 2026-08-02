[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $repositoryRoot 'artifacts'
$probeDirectory = Join-Path $artifactRoot 'probe'
$probeExecutableName = 'CodexQuotaTaskbar.CompatibilityProbe.exe'
$packageName = 'CodexQuotaTaskbar-compatibility-probe-x64.zip'
$packagePath = Join-Path $artifactRoot $packageName
$packageHashPath = $packagePath + '.sha256'
$peImportPolicyPath = Join-Path $PSScriptRoot 'pe-import-policy.ps1'
if (-not (Test-Path -LiteralPath $peImportPolicyPath -PathType Leaf)) {
    throw 'Required shared PE import policy is missing.'
}
. $peImportPolicyPath
$nativeBridgePolicyPath = Join-Path $PSScriptRoot 'native-bridge-policy.ps1'
if (-not (Test-Path -LiteralPath $nativeBridgePolicyPath -PathType Leaf)) {
    throw 'Required native bridge security policy is missing.'
}
. $nativeBridgePolicyPath

$script:ApplicationBoundaryPatterns = @(
    'AccountManager'
    'auth[.]json'
    'Data[\\/]Accounts'
    'Data[\\/]IdeProfiles'
    'active-codex-account[.]txt'
    'chatgpt[.]com/backend-api/wham'
    'TaskbarStats'
    'schtasks'
    'CurrentVersion[\\/]Run'
    'StartupApproved'
    'Stop-Process'
    'taskkill'
    'Process[.]Kill'
    'WinHttp'
    'WinINet'
    'WinSock'
    'http://'
    'https://'
    'WebRequest'
) | ForEach-Object {
    [pscustomobject]@{
        Pattern = $_
        Regex = [System.Text.RegularExpressions.Regex]::new(
            $_,
            [System.Text.RegularExpressions.RegexOptions]::CultureInvariant -bor
                [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    }
}

function Assert-NoReparseAncestors {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$RootPath
    )

    $root = [System.IO.Path]::GetFullPath($RootPath).TrimEnd('\', '/')
    $current = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    while ($null -ne $current) {
        if (($current.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Reparse points are not allowed in packaging paths: $($current.FullName)"
        }
        if ($current.FullName.TrimEnd('\', '/') -ieq $root) {
            return
        }
        $current = if ($current -is [System.IO.FileInfo]) { $current.Directory } else { $current.Parent }
    }

    throw "Packaging path escapes its approved root: $Path"
}

function Assert-RegularFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$RootPath,
        [long]$MaximumBytes = 134217728
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required packaging file is missing: $Path"
    }
    Assert-NoReparseAncestors -Path $Path -RootPath $RootPath
    $file = Get-Item -LiteralPath $Path -Force
    if ($file.Length -le 0 -or $file.Length -gt $MaximumBytes) {
        throw "Packaging file size is invalid: $Path"
    }
    return $file
}

function Remove-VerifiedDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [string]$RootPath = $repositoryRoot
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "Packaging target is not a directory: $Path"
    }
    Assert-NoReparseAncestors -Path $Path -RootPath $RootPath
    Remove-Item -LiteralPath $Path -Recurse -Force
}

function Remove-VerifiedFixtureDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fixture = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if ($fixture -isnot [System.IO.DirectoryInfo] -or
        ($fixture.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Temporary package fixture is not a regular directory.'
    }
    $descendants = @(Get-ChildItem -LiteralPath $fixture.FullName -Force -Recurse -ErrorAction Stop)
    foreach ($descendant in $descendants) {
        if (($descendant.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Temporary package fixture contains a reparse point: $($descendant.FullName)"
        }
    }
    Remove-Item -LiteralPath $fixture.FullName -Recurse -Force
}

function Get-StrictSha256Digest {
    param([Parameter(Mandatory = $true)][string]$InputPath)

    $hash = (Get-FileHash -LiteralPath $InputPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -notmatch '^[0-9a-f]{64}$') {
        throw "Unable to calculate a strict SHA-256 digest for: $InputPath"
    }
    return $hash
}

function Write-Sha256File {
    param(
        [Parameter(Mandatory = $true)][string]$InputPath,
        [Parameter(Mandatory = $true)][string]$OutputPath,
        [Parameter(Mandatory = $true)][string]$DisplayName
    )

    $hash = Get-StrictSha256Digest -InputPath $InputPath
    [System.IO.File]::WriteAllText(
        $OutputPath,
        ('{0} *{1}' -f $hash, $DisplayName) + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false))
}

function Write-EmbeddedPayloadSha256 {
    param(
        [Parameter(Mandatory = $true)][string]$InputPath,
        [Parameter(Mandatory = $true)][string]$OutputPath
    )

    $hash = Get-StrictSha256Digest -InputPath $InputPath
    [System.IO.File]::WriteAllText(
        $OutputPath,
        $hash,
        [System.Text.UTF8Encoding]::new($false))
}

function Assert-NativeBridgeBoundary {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$RootPath
    )

    $file = Assert-RegularFile -Path $Path -RootPath $RootPath -MaximumBytes 134217728
    $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
    foreach ($encoding in @(
            [System.Text.UTF8Encoding]::new($false, $false),
            [System.Text.UnicodeEncoding]::new($false, $false, $false),
            [System.Text.UnicodeEncoding]::new($true, $false, $false))) {
        $text = $encoding.GetString($bytes)
        foreach ($rule in $script:ApplicationBoundaryPatterns) {
            if ($rule.Regex.IsMatch($text)) {
                throw "Forbidden application boundary content '$($rule.Pattern)' found in $($file.Name)"
            }
        }
    }
}

function Assert-PackageFileSet {
    param([Parameter(Mandatory = $true)][string]$Path)

    $expected = @(
        $probeExecutableName,
        ($probeExecutableName + '.sha256'),
        'recover-probe.ps1',
        'probe-runbook.md'
    )
    $entries = @(Get-ChildItem -LiteralPath $Path -Force)
    $nonFiles = @($entries | Where-Object { $_ -isnot [System.IO.FileInfo] })
    if ($nonFiles.Count -gt 0) {
        throw 'The probe package directory contains an unexpected non-file entry.'
    }
    $actual = @(
        $entries |
            ForEach-Object { $_.Name } |
            Sort-Object
    )
    if (($actual -join "`n") -cne (($expected | Sort-Object) -join "`n")) {
        throw ('The probe package directory contains unexpected or missing files. Actual: {0}' -f ($actual -join ', '))
    }
    foreach ($name in $expected) {
        $null = Assert-RegularFile -Path (Join-Path $Path $name) -RootPath $Path
    }
}

function Assert-PromotionFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$RootPath,
        [long]$MaximumBytes = 268435456
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Promotion input is not a regular file: $Path"
    }
    Assert-NoReparseAncestors -Path $Path -RootPath $RootPath
    $file = Get-Item -LiteralPath $Path -Force
    if (($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $file.Length -le 0 -or $file.Length -gt $MaximumBytes) {
        throw "Promotion input metadata is invalid: $Path"
    }
    return $file
}

function Remove-VerifiedArtifactFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$RootPath
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }
    $null = Assert-PromotionFile -Path $Path -RootPath $RootPath
    Remove-Item -LiteralPath $Path -Force
}

function Remove-PromotionCreatedFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$RootPath
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }
    Assert-NoReparseAncestors -Path $Path -RootPath $RootPath
    $file = Get-Item -LiteralPath $Path -Force
    if ($file -isnot [System.IO.FileInfo] -or
        ($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Promotion-created path is not a regular file: $Path"
    }
    Remove-Item -LiteralPath $file.FullName -Force
}

function Move-VerifiedDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$DestinationPath,
        [Parameter(Mandatory = $true)][string]$RootPath
    )

    if (-not (Test-Path -LiteralPath $SourcePath -PathType Container)) {
        throw "Promotion source directory is missing: $SourcePath"
    }
    if (Test-Path -LiteralPath $DestinationPath) {
        throw "Promotion destination already exists: $DestinationPath"
    }
    Assert-NoReparseAncestors -Path $SourcePath -RootPath $RootPath
    Move-Item -LiteralPath $SourcePath -Destination $DestinationPath -ErrorAction Stop
}

function Move-VerifiedArtifactFile {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$DestinationPath,
        [Parameter(Mandatory = $true)][string]$RootPath
    )

    $null = Assert-PromotionFile -Path $SourcePath -RootPath $RootPath
    if (Test-Path -LiteralPath $DestinationPath) {
        throw "Promotion destination already exists: $DestinationPath"
    }
    Move-Item -LiteralPath $SourcePath -Destination $DestinationPath -ErrorAction Stop
}

function Get-Sha256HexFromStream {
    param([Parameter(Mandatory = $true)][System.IO.Stream]$Stream)

    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString($algorithm.ComputeHash($Stream))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
    }
}

function Assert-PackageDirectoryMatchesZip {
    param(
        [Parameter(Mandatory = $true)][string]$PackageDirectory,
        [Parameter(Mandatory = $true)][string]$ZipPath,
        [Parameter(Mandatory = $true)][string]$RootPath
    )

    $expected = @(
        $probeExecutableName,
        ($probeExecutableName + '.sha256'),
        'recover-probe.ps1',
        'probe-runbook.md'
    )
    Assert-PackageFileSet -Path $PackageDirectory
    $null = Assert-PromotionFile -Path $ZipPath -RootPath $RootPath
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        if ($archive.Entries.Count -ne $expected.Count) {
            throw 'Promotion ZIP has an unexpected entry count.'
        }
        foreach ($name in $expected) {
            $matchingEntries = @($archive.Entries | Where-Object { $_.FullName -ceq $name })
            if ($matchingEntries.Count -ne 1) {
                throw "Promotion ZIP does not contain exactly one expected entry: $name"
            }
            $file = Assert-RegularFile -Path (Join-Path $PackageDirectory $name) -RootPath $PackageDirectory
            $stream = $matchingEntries[0].Open()
            try {
                $entryHash = Get-Sha256HexFromStream -Stream $stream
            }
            finally {
                $stream.Dispose()
            }
            if ((Get-StrictSha256Digest -InputPath $file.FullName) -cne $entryHash) {
                throw "Promotion package directory does not match its verified ZIP entry: $name"
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Test-ValidationAction {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$ValidationAction,
        [Parameter(Mandatory = $true)][string]$ZipPath,
        [Parameter(Mandatory = $true)][string]$FailureMessage
    )

    $validationResult = @(& $ValidationAction $ZipPath)
    if ($validationResult.Count -ne 1 -or $validationResult[0] -ne $true) {
        throw $FailureMessage
    }
}

function Invoke-VerifiedPromotion {
    param(
        [Parameter(Mandatory = $true)][string]$StagingZipPath,
        [Parameter(Mandatory = $true)][string]$FinalProbeDirectory,
        [Parameter(Mandatory = $true)][string]$FinalZipPath,
        [Parameter(Mandatory = $true)][string]$FinalHashPath,
        [Parameter(Mandatory = $true)][scriptblock]$ValidationAction,
        [Parameter(Mandatory = $true)][string]$RootPath,
        [scriptblock]$AfterValidationAction,
        [scriptblock]$BeforeCandidateCommitAction,
        [scriptblock]$AfterFinalHashMoveAction
    )

    $artifactDirectory = [System.IO.Path]::GetFullPath((Split-Path -Parent $FinalZipPath))
    if ($artifactDirectory -ine [System.IO.Path]::GetFullPath((Split-Path -Parent $FinalProbeDirectory)) -or
        $artifactDirectory -ine [System.IO.Path]::GetFullPath((Split-Path -Parent $FinalHashPath))) {
        throw 'Promotion final artifacts must share one artifact directory.'
    }
    $promotionId = [Guid]::NewGuid().ToString('N')
    $candidateDirectory = Join-Path $artifactDirectory ('probe-promotion-candidate-' + $promotionId)
    $candidateZipPath = Join-Path $artifactDirectory ($packageName + '.candidate-' + $promotionId)
    $candidateHashPath = $candidateZipPath + '.sha256'
    $finalHashCandidatePath = $candidateZipPath + '.final.sha256'
    $backupDirectory = Join-Path $artifactDirectory ('probe-promotion-backup-' + $promotionId)
    $backupZipPath = Join-Path $artifactDirectory ($packageName + '.backup-' + $promotionId)
    $backupHashPath = $backupZipPath + '.sha256'
    foreach ($path in @($candidateDirectory, $candidateZipPath, $candidateHashPath, $finalHashCandidatePath, $backupDirectory, $backupZipPath, $backupHashPath)) {
        if (Test-Path -LiteralPath $path) {
            throw "Promotion scratch path already exists: $path"
        }
    }

    $oldProbeMoved = $false
    $oldZipMoved = $false
    $oldHashMoved = $false
    $newProbeMoved = $false
    $newZipMoved = $false
    $newHashMoved = $false
    $succeeded = $false
    try {
        $null = Assert-PromotionFile -Path $StagingZipPath -RootPath $RootPath
        Copy-Item -LiteralPath $StagingZipPath -Destination $candidateZipPath -ErrorAction Stop
        $null = Assert-PromotionFile -Path $candidateZipPath -RootPath $RootPath
        Write-Sha256File -InputPath $candidateZipPath -OutputPath $candidateHashPath `
            -DisplayName ([System.IO.Path]::GetFileName($candidateZipPath))
        $null = Assert-PromotionFile -Path $candidateHashPath -RootPath $RootPath
        Write-Sha256File -InputPath $candidateZipPath -OutputPath $finalHashCandidatePath `
            -DisplayName ([System.IO.Path]::GetFileName($FinalZipPath))
        $null = Assert-PromotionFile -Path $finalHashCandidatePath -RootPath $RootPath
        Test-ValidationAction -ValidationAction $ValidationAction -ZipPath $candidateZipPath `
            -FailureMessage 'Candidate artifact validation did not succeed; final artifacts were not changed.'
        if ($null -ne $AfterValidationAction) {
            $null = @(& $AfterValidationAction $StagingZipPath)
        }

        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::ExtractToDirectory($candidateZipPath, $candidateDirectory)
        Assert-PackageDirectoryMatchesZip -PackageDirectory $candidateDirectory -ZipPath $candidateZipPath -RootPath $RootPath
        Test-ValidationAction -ValidationAction $ValidationAction -ZipPath $candidateZipPath `
            -FailureMessage 'Candidate artifact changed after validation.'

        if (Test-Path -LiteralPath $FinalProbeDirectory) {
            Move-VerifiedDirectory -SourcePath $FinalProbeDirectory -DestinationPath $backupDirectory -RootPath $RootPath
            $oldProbeMoved = $true
        }
        if (Test-Path -LiteralPath $FinalZipPath) {
            Move-VerifiedArtifactFile -SourcePath $FinalZipPath -DestinationPath $backupZipPath -RootPath $RootPath
            $oldZipMoved = $true
        }
        if (Test-Path -LiteralPath $FinalHashPath) {
            Move-VerifiedArtifactFile -SourcePath $FinalHashPath -DestinationPath $backupHashPath -RootPath $RootPath
            $oldHashMoved = $true
        }
        if ($null -ne $BeforeCandidateCommitAction) {
            $null = @(& $BeforeCandidateCommitAction)
        }

        Move-VerifiedDirectory -SourcePath $candidateDirectory -DestinationPath $FinalProbeDirectory -RootPath $RootPath
        $newProbeMoved = $true
        Move-VerifiedArtifactFile -SourcePath $candidateZipPath -DestinationPath $FinalZipPath -RootPath $RootPath
        $newZipMoved = $true
        Move-VerifiedArtifactFile -SourcePath $finalHashCandidatePath -DestinationPath $FinalHashPath -RootPath $RootPath
        $newHashMoved = $true
        if ($null -ne $AfterFinalHashMoveAction) {
            $null = @(& $AfterFinalHashMoveAction $FinalHashPath)
        }
        Assert-PackageDirectoryMatchesZip -PackageDirectory $FinalProbeDirectory -ZipPath $FinalZipPath -RootPath $RootPath
        Test-ValidationAction -ValidationAction $ValidationAction -ZipPath $FinalZipPath `
            -FailureMessage 'Final artifact validation did not succeed; previous artifacts were restored.'
        $succeeded = $true
    }
    catch {
        $promotionFailure = $_
        $rollbackFailure = $null
        try {
            if ($newHashMoved -and (Test-Path -LiteralPath $FinalHashPath)) {
                Remove-PromotionCreatedFile -Path $FinalHashPath -RootPath $RootPath
            }
            if ($newZipMoved -and (Test-Path -LiteralPath $FinalZipPath)) {
                Remove-PromotionCreatedFile -Path $FinalZipPath -RootPath $RootPath
            }
            if ($newProbeMoved -and (Test-Path -LiteralPath $FinalProbeDirectory)) {
                Remove-VerifiedDirectory -Path $FinalProbeDirectory -RootPath $RootPath
            }
            if ($oldProbeMoved -and (Test-Path -LiteralPath $backupDirectory)) {
                Move-VerifiedDirectory -SourcePath $backupDirectory -DestinationPath $FinalProbeDirectory -RootPath $RootPath
            }
            if ($oldZipMoved -and (Test-Path -LiteralPath $backupZipPath)) {
                Move-VerifiedArtifactFile -SourcePath $backupZipPath -DestinationPath $FinalZipPath -RootPath $RootPath
            }
            if ($oldHashMoved -and (Test-Path -LiteralPath $backupHashPath)) {
                Move-VerifiedArtifactFile -SourcePath $backupHashPath -DestinationPath $FinalHashPath -RootPath $RootPath
            }
        }
        catch {
            $rollbackFailure = $_
        }
        if ($null -ne $rollbackFailure) {
            throw ('Promotion failed and rollback did not complete: {0}' -f $rollbackFailure.Exception.Message)
        }
        throw $promotionFailure
    }
    finally {
        if ($succeeded) {
            Remove-VerifiedDirectory -Path $backupDirectory -RootPath $RootPath
            Remove-VerifiedArtifactFile -Path $backupZipPath -RootPath $RootPath
            Remove-VerifiedArtifactFile -Path $backupHashPath -RootPath $RootPath
        }
        Remove-VerifiedDirectory -Path $candidateDirectory -RootPath $RootPath
        Remove-PromotionCreatedFile -Path $candidateZipPath -RootPath $RootPath
        Remove-PromotionCreatedFile -Path $candidateHashPath -RootPath $RootPath
        Remove-PromotionCreatedFile -Path $finalHashCandidatePath -RootPath $RootPath
    }
}

function Resolve-ProbeArtifactScannerPowerShell {
    $powerShell = (Get-Command pwsh.exe -CommandType Application -ErrorAction Stop).Source
    if (-not (Test-Path -LiteralPath $powerShell -PathType Leaf)) {
        throw 'PowerShell 7 is required for compatibility-probe artifact scanning.'
    }
    return $powerShell
}

function Invoke-ProbeArtifactScanner {
    param(
        [Parameter(Mandatory = $true)][string]$ScannerPath,
        [Parameter(Mandatory = $true)][string[]]$ScannerArguments
    )

    $scannerPowerShell = Resolve-ProbeArtifactScannerPowerShell
    $arguments = @('-NoProfile', '-NonInteractive', '-File', $ScannerPath) + $ScannerArguments
    $null = & $scannerPowerShell @arguments
    if ($LASTEXITCODE -ne 0) {
        throw 'Probe artifact scanner failed.'
    }
}

function Invoke-StagedArtifactScan {
    param([Parameter(Mandatory = $true)][string]$ZipPath)

    $scannerPath = Join-Path $repositoryRoot 'tests\security\probe-artifact-scan.ps1'
    if (-not (Test-Path -LiteralPath $scannerPath -PathType Leaf)) {
        throw 'Required probe artifact scanner is missing.'
    }
    Invoke-ProbeArtifactScanner -ScannerPath $scannerPath -ScannerArguments @($ZipPath)
    return $true
}

if ($SelfTest) {
    $fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('cqtb-package-selftest-' + [Guid]::NewGuid().ToString('N'))
    try {
        $null = New-Item -ItemType Directory -Path $fixtureRoot -Force
        $nativeBridgePolicyPath = Join-Path $PSScriptRoot 'native-bridge-policy.ps1'
        if (-not (Test-Path -LiteralPath $nativeBridgePolicyPath -PathType Leaf)) {
            throw 'Probe packager self-test requires the native bridge security policy.'
        }
        . $nativeBridgePolicyPath
        $scannerPowerShell = Resolve-ProbeArtifactScannerPowerShell
        if ([System.IO.Path]::GetFileName($scannerPowerShell) -notmatch '^pwsh(?:\.exe)?$') {
            throw 'Probe packager self-test requires the staged artifact scanner to run under PowerShell 7.'
        }
        $scannerPath = Join-Path $repositoryRoot 'tests\security\probe-artifact-scan.ps1'
        Invoke-ProbeArtifactScanner -ScannerPath $scannerPath -ScannerArguments @('-SelfTest')
        $nativeSourceFixture = Join-Path $fixtureRoot 'native-source'
        $null = New-Item -ItemType Directory -Path $nativeSourceFixture -Force
        [System.IO.File]::WriteAllText((Join-Path $nativeSourceFixture 'safe.cpp'), @'
void bridge_safe(void* module) {
    auto xaml = LoadLibraryExW("Windows.UI.Xaml.dll", nullptr, 0);
    auto initialize = GetProcAddress(xaml, "InitializeXamlDiagnosticsEx");
}
'@)
        Assert-NativeBridgeSourceBoundary -SourceRoot $nativeSourceFixture
        [System.IO.File]::WriteAllText((Join-Path $nativeSourceFixture 'forbidden.cpp'),
            'void bridge_unsafe() { TerminateProcess(GetCurrentProcess(), 1); }')
        $nativeSourceRejected = $false
        try { Assert-NativeBridgeSourceBoundary -SourceRoot $nativeSourceFixture } catch { $nativeSourceRejected = $true }
        if (-not $nativeSourceRejected) {
            throw 'Native bridge source policy self-test accepted a direct process-termination call.'
        }
        Remove-Item -LiteralPath (Join-Path $nativeSourceFixture 'forbidden.cpp') -Force
        Assert-NativeBridgeSourceBoundary -SourceRoot $nativeSourceFixture
        Remove-VerifiedFixtureDirectory -Path $nativeSourceFixture
        foreach ($name in @($probeExecutableName, ($probeExecutableName + '.sha256'), 'recover-probe.ps1', 'probe-runbook.md')) {
            [System.IO.File]::WriteAllText((Join-Path $fixtureRoot $name), 'safe')
        }
        Assert-PackageFileSet -Path $fixtureRoot
        [System.IO.File]::WriteAllText((Join-Path $fixtureRoot 'unexpected.pdb'), 'unsafe')
        $rejected = $false
        try { Assert-PackageFileSet -Path $fixtureRoot } catch { $rejected = $true }
        if (-not $rejected) {
            throw 'Package file-set self-test accepted an unexpected artifact.'
        }
        Remove-Item -LiteralPath (Join-Path $fixtureRoot 'unexpected.pdb') -Force
        $null = New-Item -ItemType Directory -Path (Join-Path $fixtureRoot 'unexpected-directory') -Force
        $rejected = $false
        try { Assert-PackageFileSet -Path $fixtureRoot } catch { $rejected = $true }
        if (-not $rejected) {
            throw 'Package file-set self-test accepted an unexpected directory.'
        }
        Remove-Item -LiteralPath (Join-Path $fixtureRoot 'unexpected-directory') -Force
        $payloadSource = Join-Path $fixtureRoot 'bridge.dll'
        $payloadDigest = Join-Path $fixtureRoot 'bridge.sha256'
        [System.IO.File]::WriteAllText($payloadSource, 'bridge payload', [System.Text.UTF8Encoding]::new($false))
        Write-EmbeddedPayloadSha256 -InputPath $payloadSource -OutputPath $payloadDigest
        $payloadText = [System.IO.File]::ReadAllText($payloadDigest, [System.Text.UTF8Encoding]::new($false))
        if ($payloadText -notmatch '^[0-9a-f]{64}\r?\n?$') {
            throw 'Embedded bridge digest does not satisfy EmbeddedBridgePayloadSource strict input contract.'
        }
        $forbiddenNativeBridge = Join-Path $fixtureRoot 'forbidden-native-bridge.dll'
        [System.IO.File]::WriteAllText($forbiddenNativeBridge, 'AccountManager')
        $nativeBridgeBoundaryRejected = $false
        try { Assert-NativeBridgeBoundary -Path $forbiddenNativeBridge -RootPath $fixtureRoot } catch { $nativeBridgeBoundaryRejected = $true }
        if (-not $nativeBridgeBoundaryRejected) {
            throw 'Native bridge boundary self-test accepted an account-management marker.'
        }
        $crtRuntimeBridge = Join-Path $fixtureRoot 'crt-runtime-native-bridge.dll'
        [System.IO.File]::WriteAllText($crtRuntimeBridge, 'TerminateProcess')
        Assert-NativeBridgeBoundary -Path $crtRuntimeBridge -RootPath $fixtureRoot
        $promotionStaging = Join-Path $fixtureRoot 'promotion-staging'
        $promotionFinalProbe = Join-Path $fixtureRoot 'promotion-final-probe'
        $promotionFinalZip = Join-Path $fixtureRoot 'promotion-final.zip'
        $promotionFinalHash = $promotionFinalZip + '.sha256'
        $promotionStagingZip = Join-Path $fixtureRoot 'promotion-staging.zip'
        $null = New-Item -ItemType Directory -Path $promotionStaging -Force
        foreach ($name in @($probeExecutableName, ($probeExecutableName + '.sha256'), 'recover-probe.ps1', 'probe-runbook.md')) {
            [System.IO.File]::WriteAllText((Join-Path $promotionStaging $name), 'new')
        }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::CreateFromDirectory($promotionStaging, $promotionStagingZip)
        Write-Sha256File -InputPath $promotionStagingZip -OutputPath ($promotionStagingZip + '.sha256') `
            -DisplayName 'promotion-staging.zip'
        $null = New-Item -ItemType Directory -Path $promotionFinalProbe -Force
        [System.IO.File]::WriteAllText((Join-Path $promotionFinalProbe 'old.txt'), 'old')
        [System.IO.File]::WriteAllText($promotionFinalZip, 'old zip')
        [System.IO.File]::WriteAllText($promotionFinalHash, 'old hash')

        $failedValidation = $false
        try {
            Invoke-VerifiedPromotion -StagingZipPath $promotionStagingZip `
                -FinalProbeDirectory $promotionFinalProbe -FinalZipPath $promotionFinalZip `
                -FinalHashPath $promotionFinalHash -RootPath $fixtureRoot `
                -ValidationAction { param($zipPath) return $false }
        }
        catch { $failedValidation = $true }
        if (-not $failedValidation -or -not (Test-Path -LiteralPath (Join-Path $promotionFinalProbe 'old.txt')) -or
            [System.IO.File]::ReadAllText($promotionFinalZip) -cne 'old zip') {
            throw 'Package promotion self-test removed final artifacts before validation succeeded.'
        }

        $failedCommit = $false
        try {
            Invoke-VerifiedPromotion -StagingZipPath $promotionStagingZip `
                -FinalProbeDirectory $promotionFinalProbe -FinalZipPath $promotionFinalZip `
                -FinalHashPath $promotionFinalHash -RootPath $fixtureRoot `
                -ValidationAction { param($zipPath) return $true } `
                -BeforeCandidateCommitAction { throw 'Synthetic promotion I/O failure.' }
        }
        catch { $failedCommit = $true }
        if (-not $failedCommit -or -not (Test-Path -LiteralPath (Join-Path $promotionFinalProbe 'old.txt')) -or
            [System.IO.File]::ReadAllText($promotionFinalZip) -cne 'old zip' -or
            [System.IO.File]::ReadAllText($promotionFinalHash) -cne 'old hash') {
            throw 'Package promotion self-test did not roll back old artifacts after a commit failure.'
        }

        $failedPartialHash = $false
        try {
            Invoke-VerifiedPromotion -StagingZipPath $promotionStagingZip `
                -FinalProbeDirectory $promotionFinalProbe -FinalZipPath $promotionFinalZip `
                -FinalHashPath $promotionFinalHash -RootPath $fixtureRoot `
                -ValidationAction { param($zipPath) return $true } `
                -AfterFinalHashMoveAction {
                    param($finalHashPath)
                    [System.IO.File]::WriteAllBytes($finalHashPath, [byte[]]@())
                    throw 'Synthetic partial final hash failure.'
                }
        }
        catch { $failedPartialHash = $true }
        if (-not $failedPartialHash -or -not (Test-Path -LiteralPath (Join-Path $promotionFinalProbe 'old.txt')) -or
            [System.IO.File]::ReadAllText($promotionFinalZip) -cne 'old zip' -or
            [System.IO.File]::ReadAllText($promotionFinalHash) -cne 'old hash') {
            throw 'Package promotion self-test did not restore old artifacts after a partial final hash failure.'
        }

        Invoke-VerifiedPromotion -StagingZipPath $promotionStagingZip `
            -FinalProbeDirectory $promotionFinalProbe -FinalZipPath $promotionFinalZip `
            -FinalHashPath $promotionFinalHash -RootPath $fixtureRoot `
            -ValidationAction { param($zipPath) return $true } `
            -AfterValidationAction {
                param($sourceZipPath)
                [System.IO.File]::WriteAllText($sourceZipPath, 'tampered staging source')
            }
        if (-not (Test-Path -LiteralPath (Join-Path $promotionFinalProbe $probeExecutableName)) -or
            [System.IO.File]::ReadAllText((Join-Path $promotionFinalProbe $probeExecutableName)) -cne 'new') {
            throw 'Package promotion self-test did not promote the validated candidate ZIP contents.'
        }
        Write-Host 'Probe packager self-test: PASS'
    }
    finally {
        if (Test-Path -LiteralPath $fixtureRoot) {
            Remove-VerifiedFixtureDirectory -Path $fixtureRoot
        }
    }
    exit 0
}

if (-not (Test-Path -LiteralPath $artifactRoot -PathType Container)) {
    $null = New-Item -ItemType Directory -Path $artifactRoot -Force
}
Assert-NoReparseAncestors -Path $artifactRoot -RootPath $repositoryRoot

$stagingDirectory = Join-Path $artifactRoot ('p-' + [Guid]::NewGuid().ToString('N'))
$publishDirectory = Join-Path $stagingDirectory 'publish'
$packageDirectory = Join-Path $stagingDirectory 'package'
try {
    $inputDirectory = Join-Path $stagingDirectory 'input'
    $nativeBuildDirectory = Join-Path $stagingDirectory 'native'
    $msbuildArtifactDirectory = Join-Path $stagingDirectory 'msbuild'
    $auditDirectory = Join-Path $stagingDirectory 'audit'
    $null = New-Item -ItemType Directory -Path $inputDirectory -Force
    $null = New-Item -ItemType Directory -Path $publishDirectory -Force
    $null = New-Item -ItemType Directory -Path $auditDirectory -Force

    $nativeSourceRoot = Join-Path $repositoryRoot 'src\native'
    Assert-NativeBridgeSourceBoundary -SourceRoot $nativeSourceRoot
    $sourceManifestBefore = @(Get-NativeBridgeSourceManifest -SourceRoot $nativeSourceRoot)
    $nativeBridgePath = & (Join-Path $PSScriptRoot 'build-native.ps1') -Configuration $Configuration `
        -BuildDirectory $nativeBuildDirectory
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Assert-NativeBridgeSourceBoundary -SourceRoot $nativeSourceRoot
    $sourceManifestAfter = @(Get-NativeBridgeSourceManifest -SourceRoot $nativeSourceRoot)
    if (($sourceManifestBefore -join "`n") -cne ($sourceManifestAfter -join "`n")) {
        throw 'Native bridge source changed during probe-package construction.'
    }
    $nativeBridgePath = @($nativeBridgePath | Select-Object -Last 1)[0]
    $null = Assert-RegularFile -Path $nativeBridgePath -RootPath $repositoryRoot -MaximumBytes 67108864
    $stagedBridgePath = Join-Path $inputDirectory 'CodexQuotaTaskbar.Bridge.dll'
    Copy-Item -LiteralPath $nativeBridgePath -Destination $stagedBridgePath -Force
    $null = Assert-RegularFile -Path $stagedBridgePath -RootPath $repositoryRoot -MaximumBytes 67108864
    $null = Assert-ApprovedPeFile -Path $stagedBridgePath -ExpectedMachine 0x8664
    Assert-NativeBridgeBinaryImportBoundary -Path $stagedBridgePath
    $null = Assert-NativeBridgeBoundary -Path $stagedBridgePath -RootPath $repositoryRoot
    $payloadHashPath = Join-Path $inputDirectory 'CodexQuotaTaskbar.Bridge.sha256'
    Write-EmbeddedPayloadSha256 -InputPath $stagedBridgePath -OutputPath $payloadHashPath

    $projectPath = Join-Path $repositoryRoot 'tools\CodexQuotaTaskbar.CompatibilityProbe\CodexQuotaTaskbar.CompatibilityProbe.csproj'
    $managedPolicyPath = Join-Path $PSScriptRoot 'managed-assembly-policy.ps1'
    $null = Assert-RegularFile -Path $managedPolicyPath -RootPath $repositoryRoot
    $policyPowerShell = Resolve-ProbeArtifactScannerPowerShell
    $auditManifestPath = Join-Path $auditDirectory 'bundle-inputs.manifest'
    $publishDirectoryWithSeparator = [System.IO.Path]::GetFullPath($publishDirectory).TrimEnd('\', '/') +
        [System.IO.Path]::DirectorySeparatorChar
    $packagingBuildProperties = @(
        '-p:UseArtifactsOutput=true',
        ('-p:ArtifactsPath=' + $msbuildArtifactDirectory),
        ('-p:PublishDir=' + $publishDirectoryWithSeparator),
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=None',
        '-p:EmbedBridgePayload=true',
        ('-p:BridgePayloadPath=' + $stagedBridgePath),
        ('-p:BridgePayloadSha256Path=' + $payloadHashPath),
        '-p:CqtbAuditBundle=true',
        ('-p:CqtbBundleAuditManifestPath=' + $auditManifestPath),
        ('-p:CqtbBundleAuditScriptPath=' + $managedPolicyPath),
        ('-p:CqtbAuditStagingRoot=' + $stagingDirectory),
        ('-p:CqtbPolicyPowerShellPath=' + $policyPowerShell)
    )
    & dotnet publish $projectPath -c $Configuration -r win-x64 --self-contained true @packagingBuildProperties
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $policyPowerShell -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File $managedPolicyPath `
        -ManifestPath $auditManifestPath -StagingRoot $stagingDirectory `
        -BridgePayloadPath $stagedBridgePath -BridgePayloadSha256Path $payloadHashPath
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    Assert-NoReparseAncestors -Path $publishDirectory -RootPath $repositoryRoot
    $publishedFiles = @(Get-ChildItem -LiteralPath $publishDirectory -Force -File)
    if ($publishedFiles.Count -ne 1 -or $publishedFiles[0].Name -cne $probeExecutableName) {
        throw 'Self-contained publish emitted unexpected files.'
    }
    $null = Assert-RegularFile -Path $publishedFiles[0].FullName -RootPath $repositoryRoot

    $recoverySource = Join-Path $repositoryRoot 'tools\CodexQuotaTaskbar.CompatibilityProbe\recover-probe.ps1'
    $runbookSource = Join-Path $repositoryRoot 'docs\compatibility\probe-runbook.md'
    $null = Assert-RegularFile -Path $recoverySource -RootPath $repositoryRoot
    $null = Assert-RegularFile -Path $runbookSource -RootPath $repositoryRoot

    $null = New-Item -ItemType Directory -Path $packageDirectory -Force
    Copy-Item -LiteralPath $publishedFiles[0].FullName -Destination (Join-Path $packageDirectory $probeExecutableName) -Force
    Copy-Item -LiteralPath $recoverySource -Destination (Join-Path $packageDirectory 'recover-probe.ps1') -Force
    Copy-Item -LiteralPath $runbookSource -Destination (Join-Path $packageDirectory 'probe-runbook.md') -Force
    Write-Sha256File -InputPath (Join-Path $packageDirectory $probeExecutableName) `
        -OutputPath (Join-Path $packageDirectory ($probeExecutableName + '.sha256')) -DisplayName $probeExecutableName
    Assert-PackageFileSet -Path $packageDirectory
    $stagingZipPath = Join-Path $stagingDirectory $packageName
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $packageDirectory,
        $stagingZipPath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)
    $null = Assert-RegularFile -Path $stagingZipPath -RootPath $repositoryRoot
    Invoke-VerifiedPromotion -StagingZipPath $stagingZipPath `
        -FinalProbeDirectory $probeDirectory -FinalZipPath $packagePath `
        -FinalHashPath $packageHashPath -RootPath $repositoryRoot `
        -ValidationAction { param($candidateZipPath) Invoke-StagedArtifactScan -ZipPath $candidateZipPath }
    Write-Output $packagePath
}
finally {
    if (Test-Path -LiteralPath $stagingDirectory) {
        Assert-NoReparseAncestors -Path $stagingDirectory -RootPath $repositoryRoot
        Remove-VerifiedFixtureDirectory -Path $stagingDirectory
    }
}
