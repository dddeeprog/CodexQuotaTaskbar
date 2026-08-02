[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$ZipPath,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$peImportPolicyPath = Join-Path $repositoryRoot 'build\pe-import-policy.ps1'
if (-not (Test-Path -LiteralPath $peImportPolicyPath -PathType Leaf)) {
    throw 'Required shared PE import policy is missing.'
}
. $peImportPolicyPath
$script:ExpectedEntries = @(
    'CodexQuotaTaskbar.CompatibilityProbe.exe'
    'CodexQuotaTaskbar.CompatibilityProbe.exe.sha256'
    'recover-probe.ps1'
    'probe-runbook.md'
)
$script:ProbeExecutableName = $script:ExpectedEntries[0]
$script:ExpectedEntryNames = [System.Collections.Generic.HashSet[string]]::new(
    [string[]]$script:ExpectedEntries,
    [System.StringComparer]::Ordinal)
$script:RegexOptions = [System.Text.RegularExpressions.RegexOptions]::CultureInvariant -bor
    [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
$script:NonExecutablePatternStrings = @(
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
    'TerminateProcess'
) | ForEach-Object {
    [pscustomobject]@{
        Pattern = $_
        Regex = [System.Text.RegularExpressions.Regex]::new($_, $script:RegexOptions)
    }
}
$script:ForbiddenApplicationTextPatterns = @(
    'WinHttp'
    'WinINet'
    'WinSock'
    'http://'
    'https://'
    'WebRequest'
) | ForEach-Object {
    [pscustomobject]@{
        Pattern = $_
        Regex = [System.Text.RegularExpressions.Regex]::new($_, $script:RegexOptions)
    }
}

function Assert-RegularFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Probe package is not a regular file: $Path"
    }
    $file = Get-Item -LiteralPath $Path -Force
    if (($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $file.Length -le 0 -or $file.Length -gt 268435456) {
        throw "Probe package metadata is invalid: $Path"
    }
    return $file
}

function Remove-VerifiedFixtureDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fixture = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if ($fixture -isnot [System.IO.DirectoryInfo] -or
        ($fixture.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Temporary scanner fixture is not a regular directory.'
    }
    $descendants = @(Get-ChildItem -LiteralPath $fixture.FullName -Force -Recurse -ErrorAction Stop)
    foreach ($descendant in $descendants) {
        if (($descendant.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Temporary scanner fixture contains a reparse point: $($descendant.FullName)"
        }
    }
    Remove-Item -LiteralPath $fixture.FullName -Recurse -Force
}

function Get-StrictSha256File {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedName
    )

    $content = [System.IO.File]::ReadAllText($Path, [System.Text.UTF8Encoding]::new($false))
    if ($content.Length -gt 512 -or
        $content -notmatch ('^(?<hash>[0-9a-f]{64}) \*' + [regex]::Escape($ExpectedName) + "\r?\n$")) {
        throw "SHA-256 file is not in the required strict format: $Path"
    }
    return $matches['hash']
}

function ConvertTo-SafeEntryName {
    param([Parameter(Mandatory = $true)][string]$EntryName)

    if ([string]::IsNullOrWhiteSpace($EntryName) -or
        $EntryName.Contains([string][char]92) -or
        $EntryName.StartsWith('/', [StringComparison]::Ordinal) -or
        $EntryName.Contains(':')) {
        throw "Unsafe archive entry name: $EntryName"
    }
    $segments = $EntryName.Split('/')
    if (@($segments | Where-Object { [string]::IsNullOrWhiteSpace($_) -or $_ -eq '.' -or $_ -eq '..' }).Count -gt 0) {
        throw "Unsafe archive entry path segment: $EntryName"
    }
    return $EntryName
}

function Test-ArchiveEntryIsLink {
    param([Parameter(Mandatory = $true)][System.IO.Compression.ZipArchiveEntry]$Entry)

    $attributes = [uint32]$Entry.ExternalAttributes
    return ((($attributes -shr 16) -band 0xF000) -eq 0xA000) -or
        (($attributes -band [uint32][System.IO.FileAttributes]::ReparsePoint) -ne 0)
}

function Get-EntryBytes {
    param([Parameter(Mandatory = $true)][System.IO.Compression.ZipArchiveEntry]$Entry)

    if ($Entry.Length -le 0 -or $Entry.Length -gt 134217728 -or $Entry.CompressedLength -le 0) {
        throw "Archive entry has invalid size metadata: $($Entry.FullName)"
    }
    if ($Entry.Length -ge 1048576 -and ([double]$Entry.Length / [double]$Entry.CompressedLength) -gt 200.0) {
        throw "Archive entry compression ratio is unsafe: $($Entry.FullName)"
    }
    $stream = $Entry.Open()
    try {
        $memory = [System.IO.MemoryStream]::new()
        try {
            $stream.CopyTo($memory)
            $bytes = $memory.ToArray()
            if ($bytes.Length -ne $Entry.Length) {
                throw "Archive entry length changed while reading: $($Entry.FullName)"
            }
            return ,$bytes
        }
        finally {
            $memory.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Find-ForbiddenNonExecutableContent {
    param(
        [Parameter(Mandatory = $true)][byte[]]$Bytes,
        [Parameter(Mandatory = $true)][string]$EntryName
    )

    $encodings = @(
        [System.Text.UTF8Encoding]::new($false, $false),
        [System.Text.UnicodeEncoding]::new($false, $false, $false),
        [System.Text.UnicodeEncoding]::new($true, $false, $false)
    )
    foreach ($encoding in $encodings) {
        $text = $encoding.GetString($Bytes)
        foreach ($rule in $script:NonExecutablePatternStrings) {
            if ($rule.Regex.IsMatch($text)) {
                throw "Forbidden package content '$($rule.Pattern)' found in $EntryName"
            }
        }
        foreach ($rule in $script:ForbiddenApplicationTextPatterns) {
            if ($rule.Regex.IsMatch($text)) {
                throw "Forbidden application text '$($rule.Pattern)' found in $EntryName"
            }
        }
    }
}

function Get-Sha256HexFromBytes {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString($algorithm.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
    }
}

function Invoke-ProbeArtifactScan {
    param([Parameter(Mandatory = $true)][string]$InputZipPath)

    $zipFile = Assert-RegularFile -Path $InputZipPath
    $sidecarPath = $zipFile.FullName + '.sha256'
    $null = Assert-RegularFile -Path $sidecarPath
    $declaredZipHash = Get-StrictSha256File -Path $sidecarPath -ExpectedName $zipFile.Name
    $actualZipHash = (Get-FileHash -LiteralPath $zipFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualZipHash -cne $declaredZipHash) {
        throw 'Probe ZIP SHA-256 does not match its sidecar file.'
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zipFile.FullName)
    try {
        if ($archive.Entries.Count -ne $script:ExpectedEntries.Count) {
            throw 'Probe ZIP has an unexpected entry count.'
        }
        $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $entryBytes = [System.Collections.Generic.Dictionary[string, byte[]]]::new([StringComparer]::Ordinal)
        foreach ($entry in $archive.Entries) {
            $entryName = ConvertTo-SafeEntryName -EntryName $entry.FullName
            if (-not $seen.Add($entryName)) {
                throw "Probe ZIP has a duplicate entry name: $entryName"
            }
            if (Test-ArchiveEntryIsLink -Entry $entry) {
                throw "Probe ZIP links are not allowed: $entryName"
            }
            if (-not $script:ExpectedEntryNames.Contains($entryName)) {
                throw "Probe ZIP has an unexpected entry: $entryName"
            }
            $bytes = Get-EntryBytes -Entry $entry
            $isExecutable = $entryName -ceq $script:ProbeExecutableName
            if ($isExecutable) {
                $null = Assert-ApprovedPeImage -Bytes $bytes
            }
            else {
                Find-ForbiddenNonExecutableContent -Bytes $bytes -EntryName $entryName
            }
            $entryBytes[$entryName] = $bytes
        }
        foreach ($expected in $script:ExpectedEntries) {
            if (-not $seen.Contains($expected)) {
                throw "Probe ZIP is missing required entry: $expected"
            }
        }

        $executableHashText = [System.Text.UTF8Encoding]::new($false, $true).GetString(
            [byte[]]$entryBytes[($script:ProbeExecutableName + '.sha256')])
        if ($executableHashText -notmatch ('^(?<hash>[0-9a-f]{64}) \*' +
                [regex]::Escape($script:ProbeExecutableName) + "\r?\n$")) {
            throw 'Probe executable SHA-256 entry is not in the required strict format.'
        }
        $actualExecutableHash = Get-Sha256HexFromBytes -Bytes ([byte[]]$entryBytes[$script:ProbeExecutableName])
        if ($actualExecutableHash -cne $matches['hash']) {
            throw 'Probe executable SHA-256 does not match its package entry.'
        }
    }
    finally {
        $archive.Dispose()
    }
}

function New-TestZip {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [string]$ExecutableContent = 'offline probe executable',
        [string]$ExecutableEntryName = $script:ProbeExecutableName,
        [string]$ImportName,
        [string]$DelayImportName,
        [uint32]$DelayImportAttributes = 1,
        [hashtable]$EntryContentOverrides = @{}
    )

    $stage = Join-Path ([System.IO.Path]::GetDirectoryName($Path)) 'stage'
    $null = New-Item -ItemType Directory -Path $stage -Force
    $exePath = Join-Path $stage $ExecutableEntryName
    [System.IO.File]::WriteAllBytes($exePath, (New-TestPeBytes -Content $ExecutableContent -ImportName $ImportName -DelayImportName $DelayImportName -DelayImportAttributes $DelayImportAttributes))
    $exeHash = (Get-FileHash -LiteralPath $exePath -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText((Join-Path $stage $script:ExpectedEntries[1]),
        ('{0} *{1}' -f $exeHash, $script:ExpectedEntries[0]) + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false))
    foreach ($entryName in @($script:ExpectedEntries[2], $script:ExpectedEntries[3])) {
        $content = if ($EntryContentOverrides.ContainsKey($entryName)) { $EntryContentOverrides[$entryName] } else { '只读离线' }
        [System.IO.File]::WriteAllText((Join-Path $stage $entryName), $content, [System.Text.UTF8Encoding]::new($false))
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $Path, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    $zipHash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText($Path + '.sha256',
        ('{0} *{1}' -f $zipHash, [System.IO.Path]::GetFileName($Path)) + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false))
}

function Update-TestZipSidecar {
    param([Parameter(Mandatory = $true)][string]$Path)

    $zipHash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText($Path + '.sha256',
        ('{0} *{1}' -f $zipHash, [System.IO.Path]::GetFileName($Path)) + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false))
}

function Assert-TestZipRejected {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Message
    )

    $rejected = $false
    try { Invoke-ProbeArtifactScan -InputZipPath $Path } catch { $rejected = $true }
    if (-not $rejected) {
        throw $Message
    }
}

function Set-TestPeUInt16 {
    param([byte[]]$Bytes, [int]$Offset, [uint16]$Value)

    [System.BitConverter]::GetBytes($Value).CopyTo($Bytes, $Offset)
}

function Set-TestPeUInt32 {
    param([byte[]]$Bytes, [int]$Offset, [uint32]$Value)

    [System.BitConverter]::GetBytes($Value).CopyTo($Bytes, $Offset)
}

function Set-TestPeUInt64 {
    param([byte[]]$Bytes, [int]$Offset, [uint64]$Value)

    [System.BitConverter]::GetBytes($Value).CopyTo($Bytes, $Offset)
}

function New-TestPeBytes {
    param(
        [Parameter(Mandatory = $true)][string]$Content,
        [string]$ImportName,
        [string]$ImportFunctionName,
        [uint16]$ImportOrdinal = 0,
        [string]$DelayImportName,
        [uint32]$DelayImportAttributes = 1,
        [uint16]$Magic = 0x20B
    )

    if ($Magic -eq 0x10B) {
        $machine = 0x014C
        $optionalHeaderSize = 0xE0
        $dataDirectoryRelativeOffset = 96
        $numberOfRvaAndSizesRelativeOffset = 92
    }
    elseif ($Magic -eq 0x20B) {
        $machine = 0x8664
        $optionalHeaderSize = 0xF0
        $dataDirectoryRelativeOffset = 112
        $numberOfRvaAndSizesRelativeOffset = 108
    }
    else {
        throw 'Test PE magic is unsupported.'
    }
    $bytes = [byte[]]::new(1024)
    $bytes[0] = 0x4D
    $bytes[1] = 0x5A
    Set-TestPeUInt32 -Bytes $bytes -Offset 60 -Value 0x80
    Set-TestPeUInt32 -Bytes $bytes -Offset 0x80 -Value 0x00004550
    Set-TestPeUInt16 -Bytes $bytes -Offset 0x84 -Value $machine
    Set-TestPeUInt16 -Bytes $bytes -Offset 0x86 -Value 1
    Set-TestPeUInt16 -Bytes $bytes -Offset 0x94 -Value $optionalHeaderSize
    Set-TestPeUInt16 -Bytes $bytes -Offset 0x98 -Value $Magic
    Set-TestPeUInt32 -Bytes $bytes -Offset 0xD4 -Value 0x200
    Set-TestPeUInt32 -Bytes $bytes -Offset (0x98 + $numberOfRvaAndSizesRelativeOffset) -Value 16
    $sectionHeaderOffset = 0x98 + $optionalHeaderSize
    Set-TestPeUInt32 -Bytes $bytes -Offset ($sectionHeaderOffset + 8) -Value 0x200
    Set-TestPeUInt32 -Bytes $bytes -Offset ($sectionHeaderOffset + 12) -Value 0x1000
    Set-TestPeUInt32 -Bytes $bytes -Offset ($sectionHeaderOffset + 16) -Value 0x200
    Set-TestPeUInt32 -Bytes $bytes -Offset ($sectionHeaderOffset + 20) -Value 0x200

    if (-not [string]::IsNullOrWhiteSpace($ImportName)) {
        $importBytes = [System.Text.Encoding]::ASCII.GetBytes($ImportName + [char]0)
        if ($importBytes.Length -gt 128) {
            throw 'Test PE import name is too long.'
        }
        Set-TestPeUInt32 -Bytes $bytes -Offset (0x98 + $dataDirectoryRelativeOffset + 8) -Value 0x1000
        Set-TestPeUInt32 -Bytes $bytes -Offset (0x98 + $dataDirectoryRelativeOffset + 12) -Value 40
        if (-not [string]::IsNullOrWhiteSpace($ImportFunctionName) -or $ImportOrdinal -ne 0) {
            Set-TestPeUInt32 -Bytes $bytes -Offset 0x200 -Value 0x1060
            Set-TestPeUInt32 -Bytes $bytes -Offset 0x210 -Value 0x1070
            $thunkSize = if ($Magic -eq 0x20B) { 8 } else { 4 }
            if ($ImportOrdinal -ne 0) {
                $ordinalFlag = if ($thunkSize -eq 8) {
                    [uint64]::Parse(
                        '8000000000000000',
                        [System.Globalization.NumberStyles]::AllowHexSpecifier,
                        [System.Globalization.CultureInfo]::InvariantCulture)
                }
                else {
                    [uint64]0x80000000
                }
                $thunkValue = $ordinalFlag -bor [uint64]$ImportOrdinal
            }
            else {
                $thunkValue = [uint64]0x1080
                $functionBytes = [System.Text.Encoding]::ASCII.GetBytes($ImportFunctionName + [char]0)
                if ($functionBytes.Length -gt 120) {
                    throw 'Test PE import function name is too long.'
                }
                Set-TestPeUInt16 -Bytes $bytes -Offset 0x280 -Value 0
                [Array]::Copy($functionBytes, 0, $bytes, 0x282, $functionBytes.Length)
            }
            if ($thunkSize -eq 8) {
                Set-TestPeUInt64 -Bytes $bytes -Offset 0x260 -Value $thunkValue
                Set-TestPeUInt64 -Bytes $bytes -Offset 0x270 -Value $thunkValue
            }
            else {
                Set-TestPeUInt32 -Bytes $bytes -Offset 0x260 -Value ([uint32]$thunkValue)
                Set-TestPeUInt32 -Bytes $bytes -Offset 0x270 -Value ([uint32]$thunkValue)
            }
        }
        Set-TestPeUInt32 -Bytes $bytes -Offset 0x20C -Value 0x1040
        [Array]::Copy($importBytes, 0, $bytes, 0x240, $importBytes.Length)
    }

    if (-not [string]::IsNullOrWhiteSpace($DelayImportName)) {
        $delayImportBytes = [System.Text.Encoding]::ASCII.GetBytes($DelayImportName + [char]0)
        if ($delayImportBytes.Length -gt 128) {
            throw 'Test PE delay-load import name is too long.'
        }
        Set-TestPeUInt32 -Bytes $bytes -Offset (0x98 + $dataDirectoryRelativeOffset + (13 * 8)) -Value 0x1080
        Set-TestPeUInt32 -Bytes $bytes -Offset (0x98 + $dataDirectoryRelativeOffset + (13 * 8) + 4) -Value 64
        Set-TestPeUInt32 -Bytes $bytes -Offset 0x280 -Value $DelayImportAttributes
        Set-TestPeUInt32 -Bytes $bytes -Offset 0x284 -Value 0x10C0
        [Array]::Copy($delayImportBytes, 0, $bytes, 0x2C0, $delayImportBytes.Length)
    }

    $contentBytes = [System.Text.Encoding]::UTF8.GetBytes($Content)
    if ($contentBytes.Length -gt 200) {
        throw 'Test PE content is too long.'
    }
    [Array]::Copy($contentBytes, 0, $bytes, 0x300, $contentBytes.Length)
    return ,$bytes
}

if ($SelfTest) {
    $fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('cqtb-artifact-scan-' + [Guid]::NewGuid().ToString('N'))
    try {
        $null = New-Item -ItemType Directory -Path $fixtureRoot -Force
        $recoveryScriptPath = Join-Path $repositoryRoot 'tools\CodexQuotaTaskbar.CompatibilityProbe\recover-probe.ps1'
        $recoveryScriptText = [System.IO.File]::ReadAllText($recoveryScriptPath, [System.Text.UTF8Encoding]::new($false))
        if ($recoveryScriptText -match '\bStart-Process\b|\bStart-ExplorerIfExited\b|\bGet-CurrentSessionExplorer\b') {
            throw 'Probe artifact self-test recovery script contains an Explorer lifecycle action.'
        }
        $validPe32 = New-TestPeBytes -Content 'valid PE32' -Magic 0x10B
        if ((Read-PeUInt16 -Bytes $validPe32 -Offset 0x84 -Description 'test PE32 machine') -ne 0x014C) {
            throw 'Probe artifact self-test PE32 fixture has an incorrect machine type.'
        }
        $validPe32Imports = @(Get-PeImportNames -Bytes $validPe32)
        if ($validPe32Imports.Count -ne 0) {
            throw ('Probe artifact self-test valid PE32 fixture had unexpected imports: {0}' -f ($validPe32Imports -join ', '))
        }
        if ($null -eq (Get-Command Get-PeImportEntries -CommandType Function -ErrorAction SilentlyContinue)) {
            throw 'Probe artifact self-test requires PE import-function parsing.'
        }
        $functionImportPe = New-TestPeBytes -Content 'function import fixture' -ImportName 'kernel32.dll' -ImportFunctionName 'WinExec'
        $functionImports = @(Get-PeImportEntries -Bytes $functionImportPe)
        if ($functionImports.Count -ne 1 -or
            $functionImports[0].Module -cne 'kernel32.dll' -or
            $functionImports[0].Name -cne 'winexec' -or
            $functionImports[0].DelayLoad) {
            throw 'Probe artifact self-test did not parse a normal import function exactly.'
        }
        $functionImportPe32 = New-TestPeBytes -Content 'PE32 function import fixture' -Magic 0x10B -ImportName 'kernel32.dll' -ImportFunctionName 'WinExec'
        $functionImportsPe32 = @(Get-PeImportEntries -Bytes $functionImportPe32)
        if ($functionImportsPe32.Count -ne 1 -or
            $functionImportsPe32[0].Module -cne 'kernel32.dll' -or
            $functionImportsPe32[0].Name -cne 'winexec') {
            throw 'Probe artifact self-test did not parse a PE32 import function exactly.'
        }
        $ordinalImportPe = New-TestPeBytes -Content 'ordinal import fixture' -ImportName 'oleaut32.dll' -ImportOrdinal 6
        $ordinalImports = @(Get-PeImportEntries -Bytes $ordinalImportPe)
        if ($ordinalImports.Count -ne 1 -or
            $ordinalImports[0].Module -cne 'oleaut32.dll' -or
            $ordinalImports[0].Name -cne '#6') {
            throw 'Probe artifact self-test did not parse an ordinal import exactly.'
        }
        $emptyThunkImportPe = New-TestPeBytes -Content 'empty thunk fixture' -ImportName 'kernel32.dll'
        $emptyThunkRejected = $false
        try { Get-PeImportEntries -Bytes $emptyThunkImportPe | Out-Null } catch { $emptyThunkRejected = $true }
        if (-not $emptyThunkRejected) {
            throw 'Probe artifact self-test accepted an import descriptor without a thunk table.'
        }
        if ($null -eq (Get-Command Assert-ExactPeImportEntries -CommandType Function -ErrorAction SilentlyContinue)) {
            throw 'Probe artifact self-test requires exact PE import-entry policy enforcement.'
        }
        Assert-ExactPeImportEntries -Bytes $functionImportPe -ExpectedEntries @('kernel32.dll!winexec|delay=false')
        $unexpectedFunctionRejected = $false
        try {
            Assert-ExactPeImportEntries -Bytes $functionImportPe -ExpectedEntries @('kernel32.dll!createprocessw|delay=false')
        }
        catch {
            $unexpectedFunctionRejected = $true
        }
        if (-not $unexpectedFunctionRejected) {
            throw 'Probe artifact self-test accepted an unexpected PE import function.'
        }
        $x86Rejected = $false
        try { Assert-ApprovedPeImage -Bytes $validPe32 | Out-Null } catch { $x86Rejected = $true }
        if (-not $x86Rejected) {
            throw 'Probe artifact self-test accepted a non-x64 bridge image.'
        }
        $malformedPe32Plus = New-TestPeBytes -Content 'malformed optional header'
        Set-TestPeUInt16 -Bytes $malformedPe32Plus -Offset 0x94 -Value 120
        $malformedRejected = $false
        try { Get-PeImportNames -Bytes $malformedPe32Plus | Out-Null } catch { $malformedRejected = $true }
        if (-not $malformedRejected) {
            throw 'Probe artifact self-test accepted a PE32+ optional header that does not declare its import directory.'
        }
        $mismatchedX64Pe32 = New-TestPeBytes -Content 'mismatched x64 PE32' -Magic 0x10B
        Set-TestPeUInt16 -Bytes $mismatchedX64Pe32 -Offset 0x84 -Value 0x8664
        $mismatchedArchitectureRejected = $false
        try { Assert-ApprovedPeImage -Bytes $mismatchedX64Pe32 -ExpectedMachine 0x8664 | Out-Null } catch { $mismatchedArchitectureRejected = $true }
        if (-not $mismatchedArchitectureRejected) {
            throw 'Probe artifact self-test accepted an x64 machine field with a PE32 optional header.'
        }
        $cleanZip = Join-Path $fixtureRoot 'clean.zip'
        New-TestZip -Path $cleanZip
        $cleanArchive = [System.IO.Compression.ZipFile]::OpenRead($cleanZip)
        try {
            $cleanExecutableEntry = $cleanArchive.GetEntry($script:ProbeExecutableName)
            $cleanExecutableBytes = Get-EntryBytes -Entry $cleanExecutableEntry
            if ($cleanExecutableBytes -isnot [byte[]] -or $cleanExecutableBytes.Length -ne $cleanExecutableEntry.Length) {
                throw 'Probe artifact self-test requires archive entries to remain a single byte array instead of pipeline-enumerated bytes.'
            }
        }
        finally {
            $cleanArchive.Dispose()
        }
        Invoke-ProbeArtifactScan -InputZipPath $cleanZip
        $runtimeStringZip = Join-Path $fixtureRoot 'runtime-strings.zip'
        New-TestZip -Path $runtimeStringZip -ExecutableContent 'System.Net HttpClient TerminateProcess'
        Invoke-ProbeArtifactScan -InputZipPath $runtimeStringZip
        $executableSensitiveZip = Join-Path $fixtureRoot 'executable-sensitive.zip'
        New-TestZip -Path $executableSensitiveZip -ExecutableContent 'AccountManager'
        Invoke-ProbeArtifactScan -InputZipPath $executableSensitiveZip
        $forbiddenZip = Join-Path $fixtureRoot 'forbidden.zip'
        New-TestZip -Path $forbiddenZip -EntryContentOverrides @{ 'recover-probe.ps1' = 'AccountManager' }
        Assert-TestZipRejected -Path $forbiddenZip -Message 'Probe artifact self-test accepted forbidden script content.'
        foreach ($case in @(
                @{ Name = 'accounts-backslash'; Content = 'Data\Accounts' },
                @{ Name = 'autostart-slash'; Content = 'CurrentVersion/Run' },
                @{ Name = 'terminate'; Content = 'TerminateProcess' },
                @{ Name = 'winhttp'; Content = 'WinHttp' },
                @{ Name = 'url'; Content = 'http://example.invalid' }
            )) {
            $path = Join-Path $fixtureRoot ($case.Name + '.zip')
            $overrides = @{ 'recover-probe.ps1' = $case.Content }
            New-TestZip -Path $path -EntryContentOverrides $overrides
            Assert-TestZipRejected -Path $path -Message ("Probe artifact self-test accepted forbidden content: {0}" -f $case.Name)
        }
        $importZip = Join-Path $fixtureRoot 'winhttp-import.zip'
        New-TestZip -Path $importZip -ImportName 'WINHTTP.dll'
        Assert-TestZipRejected -Path $importZip -Message 'Probe artifact self-test accepted a prohibited PE import.'
        $wsockImportZip = Join-Path $fixtureRoot 'wsock32-import.zip'
        New-TestZip -Path $wsockImportZip -ImportName 'WSOCK32.dll'
        Assert-TestZipRejected -Path $wsockImportZip -Message 'Probe artifact self-test accepted a prohibited Winsock PE import.'
        $delayImportZip = Join-Path $fixtureRoot 'winhttp-delay-import.zip'
        New-TestZip -Path $delayImportZip -DelayImportName 'WINHTTP.dll'
        Assert-TestZipRejected -Path $delayImportZip -Message 'Probe artifact self-test accepted a prohibited delay-load PE import.'
        $wsockDelayImportZip = Join-Path $fixtureRoot 'wsock32-delay-import.zip'
        New-TestZip -Path $wsockDelayImportZip -DelayImportName 'WSOCK32.dll'
        Assert-TestZipRejected -Path $wsockDelayImportZip -Message 'Probe artifact self-test accepted a prohibited delay-load Winsock PE import.'
        $caseVariantExecutableZip = Join-Path $fixtureRoot 'case-variant-executable.zip'
        New-TestZip -Path $caseVariantExecutableZip -ExecutableEntryName 'CodexQuotaTaskbar.CompatibilityProbe.EXE' -ImportName 'URLMON.dll'
        Assert-TestZipRejected -Path $caseVariantExecutableZip -Message 'Probe artifact self-test accepted a case-variant executable entry that bypasses PE import validation.'
        $malformedDelayImportZip = Join-Path $fixtureRoot 'malformed-delay-import.zip'
        New-TestZip -Path $malformedDelayImportZip -DelayImportName 'kernel32.dll' -DelayImportAttributes 0
        Assert-TestZipRejected -Path $malformedDelayImportZip -Message 'Probe artifact self-test accepted an unsupported delay-load descriptor.'
        $backslashRejected = $false
        try { ConvertTo-SafeEntryName -EntryName 'folder\file.txt' | Out-Null } catch { $backslashRejected = $true }
        if (-not $backslashRejected) {
            throw 'Probe artifact self-test accepted a backslash archive entry path.'
        }
        $linkZip = Join-Path $fixtureRoot 'reparse.zip'
        New-TestZip -Path $linkZip
        $linkArchive = [System.IO.Compression.ZipFile]::Open($linkZip, [System.IO.Compression.ZipArchiveMode]::Update)
        try {
            $linkEntry = $linkArchive.GetEntry('recover-probe.ps1')
            $linkEntry.ExternalAttributes = [int][System.IO.FileAttributes]::ReparsePoint
        }
        finally {
            $linkArchive.Dispose()
        }
        Update-TestZipSidecar -Path $linkZip
        Assert-TestZipRejected -Path $linkZip -Message 'Probe artifact self-test accepted a reparse-point archive entry.'
        Write-Host 'Probe artifact scan self-test: PASS'
    }
    finally {
        if (Test-Path -LiteralPath $fixtureRoot) {
            Remove-VerifiedFixtureDirectory -Path $fixtureRoot
        }
    }
    exit 0
}

if ([string]::IsNullOrWhiteSpace($ZipPath)) {
    throw 'Provide the probe ZIP path to scan.'
}
Invoke-ProbeArtifactScan -InputZipPath $ZipPath
Write-Host 'Probe artifact scan: PASS'
