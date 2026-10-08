[CmdletBinding()]
param(
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
$script:ExpectedForbiddenPatterns = @(
    'AccountManager'
    'auth.json'
    'Data[\\/]Accounts'
    'Data[\\/]IdeProfiles'
    'active-codex-account.txt'
    'chatgpt[.]com/backend-api'
    'TaskbarStats'
)
$script:ExpectedDocumentationAllowance = 'docs/privacy.md|auth.json'
$script:ExpectedHarnessAllowance = 'tools/CodexQuotaTaskbar.FileAccessAudit'
$script:ExpectedSensitiveAccessAllowances = @(
    'src/CodexQuotaTaskbar.Host/Provider/CodexAuthCredentialReader.cs|auth.json'
    'src/CodexQuotaTaskbar.Host/Provider/CodexSubscriptionMetadataService.cs|chatgpt[.]com/backend-api'
    'artifacts/host-publish/Debug/CodexQuotaTaskbar.exe|auth.json'
    'artifacts/host-publish/Debug/CodexQuotaTaskbar.exe|chatgpt[.]com/backend-api'
    'artifacts/host-publish/Release/CodexQuotaTaskbar.exe|auth.json'
    'artifacts/host-publish/Release/CodexQuotaTaskbar.exe|chatgpt[.]com/backend-api'
    'artifacts/releases/CodexQuotaTaskbar-win-x64.zip!/CodexQuotaTaskbar.exe|auth.json'
    'artifacts/releases/CodexQuotaTaskbar-win-x64.zip!/CodexQuotaTaskbar.exe|chatgpt[.]com/backend-api'
)
$script:HarnessDirectoryName = 'CodexQuotaTaskbar.FileAccessAudit'
$script:HarnessArtifactFileNames = @(
    'CodexQuotaTaskbar.FileAccessAudit.dll'
    'CodexQuotaTaskbar.FileAccessAudit.exe'
    'CodexQuotaTaskbar.FileAccessAudit.deps.json'
    'CodexQuotaTaskbar.FileAccessAudit.runtimeconfig.json'
    'CodexQuotaTaskbar.FileAccessAudit.pdb'
)
$script:ScanChunkBytes = 65536
$script:MaxFileBytes = 268435456
$script:MaxArchiveEntryBytes = 134217728
$script:MaxArchiveTotalUncompressedBytes = 268435456
$script:MaxArchiveEntries = 4096
$script:MaxZipEndRecordCandidates = 256
$script:MinimumRatioCheckBytes = 1048576
$script:MaxArchiveCompressionRatio = 200.0
$script:MaxNestedArchiveDepth = 0
$script:TrustedMicrosoftWindowsSdkFileName = 'Microsoft.Windows.SDK.NET.dll'
$script:TrustedMicrosoftWindowsSdkPublicKeyToken = '31bf3856ad364e35'

function New-Finding {
    param(
        [Parameter(Mandatory = $true)][string]$Kind,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Pattern
    )

    [pscustomobject]@{
        Kind = $Kind
        Path = $Path
        Pattern = $Pattern
    }
}

function Test-IsTrustedMicrosoftWindowsSdkAssembly {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$RulePath
    )

    if ([System.IO.Path]::GetFileName($RulePath) -cne $script:TrustedMicrosoftWindowsSdkFileName -or
        -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }

    try {
        $identity = [System.Reflection.AssemblyName]::GetAssemblyName($Path)
        $token = ([System.BitConverter]::ToString($identity.GetPublicKeyToken())).Replace('-', '').ToLowerInvariant()
        if ($identity.Name -cne 'Microsoft.Windows.SDK.NET' -or
            $token -cne $script:TrustedMicrosoftWindowsSdkPublicKeyToken) {
            return $false
        }

        $signature = Get-AuthenticodeSignature -LiteralPath $Path
        return $signature.Status -eq [System.Management.Automation.SignatureStatus]::Valid -and
            $null -ne $signature.SignerCertificate -and
            $signature.SignerCertificate.Subject.StartsWith('CN=Microsoft Corporation,', [StringComparison]::Ordinal)
    }
    catch {
        return $false
    }
}

function Test-IsTrustedRuntimeFindingAllowed {
    param(
        [Parameter(Mandatory = $true)][bool]$TrustedMicrosoftWindowsSdk,
        [Parameter(Mandatory = $true)]$Finding
    )

    return $TrustedMicrosoftWindowsSdk -and $Finding.Pattern -ceq 'AccountManager'
}

function Get-NormalizedRelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$BasePath,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $relative = [System.IO.Path]::GetRelativePath($BasePath, $Path).Replace('\', '/')
    if ([System.IO.Path]::IsPathRooted($relative) -or $relative -eq '..' -or $relative.StartsWith('../', [StringComparison]::Ordinal)) {
        throw "Path escapes the repository root: $Path"
    }

    return $relative
}

function ConvertTo-NormalizedEntryPath {
    param([Parameter(Mandatory = $true)][string]$EntryPath)

    if ($EntryPath.Contains('\')) {
        throw "Archive entry uses a non-normalized separator: $EntryPath"
    }
    $pathForSegments = if ($EntryPath.EndsWith('/', [StringComparison]::Ordinal)) {
        $EntryPath.Substring(0, $EntryPath.Length - 1)
    }
    else {
        $EntryPath
    }
    if ([string]::IsNullOrWhiteSpace($pathForSegments) -or
        $pathForSegments.StartsWith('/', [StringComparison]::Ordinal) -or
        [System.IO.Path]::IsPathRooted($pathForSegments) -or
        $pathForSegments.Contains(':')) {
        throw "Archive entry is not a relative normalized path: $EntryPath"
    }

    $segments = $pathForSegments.Split('/')
    $invalidSegments = @($segments | Where-Object { [string]::IsNullOrEmpty($_) -or $_ -eq '.' -or $_ -eq '..' })
    if ($invalidSegments.Count -gt 0) {
        throw "Archive entry contains an unsafe path segment: $EntryPath"
    }

    return $EntryPath
}

function Test-IsReparsePoint {
    param([Parameter(Mandatory = $true)][System.IO.FileAttributes]$Attributes)

    return (($Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)
}

function Assert-NoReparseAncestors {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $root = [System.IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\', '/')
    $current = Get-Item -LiteralPath $Path -Force
    while ($null -ne $current) {
        if (Test-IsReparsePoint -Attributes $current.Attributes) {
            throw "Reparse points are not allowed in scan paths: $($current.FullName)"
        }
        if ($current.FullName.TrimEnd('\', '/') -ieq $root) {
            return
        }
        if ($current -is [System.IO.FileInfo]) {
            $current = $current.Directory
        }
        else {
            $current = $current.Parent
        }
    }

    throw "Path does not resolve beneath the repository root: $Path"
}

function Get-StrictPolicyLines {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required boundary policy is missing: $Path"
    }

    $lines = @([System.IO.File]::ReadAllLines($Path))
    $invalidLines = @($lines | Where-Object { [string]::IsNullOrWhiteSpace($_) -or $_ -ne $_.Trim() })
    if ($lines.Count -eq 0 -or $invalidLines.Count -gt 0) {
        throw "Boundary policy contains blank or padded entries: $Path"
    }
    return $lines
}

function Read-BoundaryPolicy {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$SecurityDirectory
    )

    $repositoryFullPath = [System.IO.Path]::GetFullPath($RepositoryRoot)
    $securityFullPath = [System.IO.Path]::GetFullPath($SecurityDirectory)
    $null = Get-NormalizedRelativePath -BasePath $repositoryFullPath -Path $securityFullPath
    Assert-NoReparseAncestors -RepositoryRoot $repositoryFullPath -Path $securityFullPath

    $patternsPath = Join-Path $securityFullPath 'forbidden-production-patterns.txt'
    $documentationPath = Join-Path $securityFullPath 'allowed-documentation-paths.txt'
    $harnessPath = Join-Path $securityFullPath 'allowed-test-harness-paths.txt'
    $sensitiveAccessPath = Join-Path $securityFullPath 'allowed-production-sensitive-access.txt'
    foreach ($policyPath in @($patternsPath, $documentationPath, $harnessPath, $sensitiveAccessPath)) {
        Assert-NoReparseAncestors -RepositoryRoot $repositoryFullPath -Path $policyPath
    }

    $patterns = @(Get-StrictPolicyLines -Path $patternsPath)
    if ($patterns.Count -ne $script:ExpectedForbiddenPatterns.Count) {
        throw 'Forbidden-pattern policy must contain exactly the required entries.'
    }
    for ($index = 0; $index -lt $script:ExpectedForbiddenPatterns.Count; $index++) {
        if ($patterns[$index] -cne $script:ExpectedForbiddenPatterns[$index]) {
            throw 'Forbidden-pattern policy differs from the required fail-closed contract.'
        }
    }

    $documentationAllowances = @(Get-StrictPolicyLines -Path $documentationPath)
    if ($documentationAllowances.Count -ne 1 -or $documentationAllowances[0] -cne $script:ExpectedDocumentationAllowance) {
        throw 'Documentation policy must allow only docs/privacy.md|auth.json.'
    }

    $harnessAllowances = @(Get-StrictPolicyLines -Path $harnessPath)
    if ($harnessAllowances.Count -ne 1 -or $harnessAllowances[0] -cne $script:ExpectedHarnessAllowance) {
        throw 'Test-harness policy must contain only tools/CodexQuotaTaskbar.FileAccessAudit.'
    }
    if ($harnessAllowances[0].IndexOfAny([char[]]'*?[]') -ge 0 -or
        $harnessAllowances[0].Split('/') -contains '..' -or
        [System.IO.Path]::IsPathRooted($harnessAllowances[0])) {
        throw 'Test-harness policy cannot contain wildcards, rooted paths, or parent traversal.'
    }

    $sensitiveAccessAllowances = @(Get-StrictPolicyLines -Path $sensitiveAccessPath)
    if ($sensitiveAccessAllowances.Count -ne $script:ExpectedSensitiveAccessAllowances.Count) {
        throw 'Sensitive-access policy must contain exactly the reviewed entries.'
    }
    for ($index = 0; $index -lt $script:ExpectedSensitiveAccessAllowances.Count; $index++) {
        if ($sensitiveAccessAllowances[$index] -cne $script:ExpectedSensitiveAccessAllowances[$index]) {
            throw 'Sensitive-access policy differs from the reviewed fail-closed contract.'
        }
    }

    $compiledPatterns = foreach ($pattern in $patterns) {
        try {
            [pscustomobject]@{
                Text = $pattern
                Regex = [regex]::new(
                    $pattern,
                    [System.Text.RegularExpressions.RegexOptions]::CultureInvariant -bor
                    [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor
                    [System.Text.RegularExpressions.RegexOptions]::Compiled
                )
            }
        }
        catch {
            throw "Forbidden pattern is not a valid regular expression: $pattern"
        }
    }

    $compiledPatterns = @($compiledPatterns)
    [System.Text.RegularExpressions.Regex[]]$binaryScanRegexes = @($compiledPatterns | ForEach-Object { $_.Regex })
    $sensitiveAccessSet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($allowance in $sensitiveAccessAllowances) {
        $null = $sensitiveAccessSet.Add($allowance)
    }
    return [pscustomobject]@{
        Patterns = $compiledPatterns
        BinaryScanRegexes = $binaryScanRegexes
        DocumentationPath = 'docs/privacy.md'
        DocumentationPattern = 'auth.json'
        HarnessPath = $harnessAllowances[0]
        SensitiveAccessAllowances = $sensitiveAccessSet
    }
}

function Test-IsDocumentationMatchAllowed {
    param(
        [Parameter(Mandatory = $true)]$Policy,
        [Parameter(Mandatory = $true)][string]$EntryPath,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][string]$MatchedText
    )

    return $EntryPath -ceq $Policy.DocumentationPath -and
        $Pattern -ceq $Policy.DocumentationPattern -and
        $MatchedText -ceq $Policy.DocumentationPattern
}

function Test-IsSensitiveAccessMatchAllowed {
    param(
        [Parameter(Mandatory = $true)]$Policy,
        [Parameter(Mandatory = $true)][string]$EntryPath,
        [Parameter(Mandatory = $true)][string]$Pattern
    )

    return $Policy.SensitiveAccessAllowances.Contains("$EntryPath|$Pattern")
}

function Find-PatternsInText {
    param(
        [Parameter(Mandatory = $true)]$Policy,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text,
        [Parameter(Mandatory = $true)][string]$EntryPath,
        [Parameter(Mandatory = $true)][string]$Kind
    )

    foreach ($pattern in $Policy.Patterns) {
        foreach ($match in $pattern.Regex.Matches($Text)) {
            if (-not (Test-IsDocumentationMatchAllowed -Policy $Policy -EntryPath $EntryPath -Pattern $pattern.Text -MatchedText $match.Value) -and
                -not (Test-IsSensitiveAccessMatchAllowed -Policy $Policy -EntryPath $EntryPath -Pattern $pattern.Text)) {
                New-Finding -Kind $Kind -Path $EntryPath -Pattern $pattern.Text
                break
            }
        }
    }
}

function Find-PatternsInBytesFallback {
    param(
        [Parameter(Mandatory = $true)]$Policy,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][byte[]]$Bytes,
        [Parameter(Mandatory = $true)][string]$EntryPath,
        [Parameter(Mandatory = $true)][string]$Kind
    )

    $decoders = @(
        [pscustomobject]@{ Encoding = [System.Text.UTF8Encoding]::new($false, $false); CodeUnitWidth = 1 }
        [pscustomobject]@{ Encoding = [System.Text.UnicodeEncoding]::new($false, $false, $false); CodeUnitWidth = 2 }
        [pscustomobject]@{ Encoding = [System.Text.UnicodeEncoding]::new($true, $false, $false); CodeUnitWidth = 2 }
        [pscustomobject]@{ Encoding = [System.Text.UTF32Encoding]::new($false, $false, $false); CodeUnitWidth = 4 }
        [pscustomobject]@{ Encoding = [System.Text.UTF32Encoding]::new($true, $false, $false); CodeUnitWidth = 4 }
    )
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($decoder in $decoders) {
        for ($offset = 0; $offset -lt $decoder.CodeUnitWidth; $offset++) {
            $byteCount = $Bytes.Length - $offset
            if ($byteCount -le 0) {
                continue
            }
            $byteCount -= $byteCount % $decoder.CodeUnitWidth
            if ($byteCount -le 0) {
                continue
            }

            $text = $decoder.Encoding.GetString($Bytes, $offset, $byteCount)
            foreach ($finding in @(Find-PatternsInText -Policy $Policy -Text $text -EntryPath $EntryPath -Kind $Kind)) {
                if ($seen.Add($finding.Pattern)) {
                    $finding
                }
            }
            $text = $null
        }
    }
}

function Initialize-BoundaryBytePatternMatcher {
    if ($null -ne ('CodexQuotaTaskbarBoundaryBytePatternMatcher' -as [type])) {
        return
    }

    $typeDefinition = @'
using System;
using System.Text;
using System.Text.RegularExpressions;

public static class CodexQuotaTaskbarBoundaryBytePatternMatcher
{
    public static bool[] FindMatches(Regex[] patterns, byte[] bytes)
    {
        if (patterns == null)
        {
            throw new ArgumentNullException("patterns");
        }
        if (bytes == null)
        {
            throw new ArgumentNullException("bytes");
        }

        var matches = new bool[patterns.Length];
        var remainingPatterns = patterns.Length;
        var encodings = new Encoding[]
        {
            new UTF8Encoding(false, false),
            new UnicodeEncoding(false, false, false),
            new UnicodeEncoding(true, false, false),
            new UTF32Encoding(false, false, false),
            new UTF32Encoding(true, false, false)
        };
        var codeUnitWidths = new int[] { 1, 2, 2, 4, 4 };

        for (var encodingIndex = 0; encodingIndex < encodings.Length && remainingPatterns > 0; encodingIndex++)
        {
            var codeUnitWidth = codeUnitWidths[encodingIndex];
            for (var offset = 0; offset < codeUnitWidth && remainingPatterns > 0; offset++)
            {
                var byteCount = bytes.Length - offset;
                byteCount -= byteCount % codeUnitWidth;
                if (byteCount <= 0)
                {
                    continue;
                }

                var text = encodings[encodingIndex].GetString(bytes, offset, byteCount);
                for (var patternIndex = 0; patternIndex < patterns.Length; patternIndex++)
                {
                    if (!matches[patternIndex] && patterns[patternIndex].IsMatch(text))
                    {
                        matches[patternIndex] = true;
                        remainingPatterns--;
                    }
                }
            }
        }

        return matches;
    }
}
'@
    Add-Type -TypeDefinition $typeDefinition -Language CSharp -ErrorAction Stop
}

function Find-PatternsInBytes {
    param(
        [Parameter(Mandatory = $true)]$Policy,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][byte[]]$Bytes,
        [Parameter(Mandatory = $true)][string]$EntryPath,
        [Parameter(Mandatory = $true)][string]$Kind,
        [string]$SensitiveAccessPath
    )

    if ($EntryPath -ceq $Policy.DocumentationPath) {
        Find-PatternsInBytesFallback -Policy $Policy -Bytes $Bytes -EntryPath $EntryPath -Kind $Kind
        return
    }

    Initialize-BoundaryBytePatternMatcher
    if ([string]::IsNullOrEmpty($SensitiveAccessPath)) {
        $SensitiveAccessPath = $EntryPath
    }
    [System.Text.RegularExpressions.Regex[]]$regexes = $Policy.BinaryScanRegexes
    $matches = [CodexQuotaTaskbarBoundaryBytePatternMatcher]::FindMatches($regexes, $Bytes)
    for ($index = 0; $index -lt $Policy.Patterns.Count; $index++) {
        if ($matches[$index] -and
            -not (Test-IsSensitiveAccessMatchAllowed -Policy $Policy -EntryPath $SensitiveAccessPath -Pattern $Policy.Patterns[$index].Text)) {
            New-Finding -Kind $Kind -Path $EntryPath -Pattern $Policy.Patterns[$index].Text
        }
    }
}

function Test-ArtifactContainsHarnessPath {
    param([Parameter(Mandatory = $true)][string]$EntryPath)

    $segments = @($EntryPath.Split('/'))
    foreach ($segment in $segments) {
        if ($segment.Equals($script:HarnessDirectoryName, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }
    $fileName = $segments[$segments.Count - 1]
    return $script:HarnessArtifactFileNames -contains $fileName
}

function Test-HarnessIsNonPackable {
    param([Parameter(Mandatory = $true)][string]$HarnessDirectory)

    if (Test-IsReparsePoint -Attributes (Get-Item -LiteralPath $HarnessDirectory -Force).Attributes) {
        return $false
    }

    $projectFiles = @(Get-ChildItem -LiteralPath $HarnessDirectory -Filter '*.csproj' -File -Force)
    if ($projectFiles.Count -ne 1) {
        return $false
    }
    try {
        [xml]$project = [System.IO.File]::ReadAllText($projectFiles[0].FullName)
        $nodes = @($project.SelectNodes("//*[local-name()='IsPackable']"))
        return $nodes.Count -eq 1 -and
            $nodes[0].Attributes.Count -eq 0 -and
            $nodes[0].InnerText.Trim() -ceq 'false'
    }
    catch {
        return $false
    }
}

function Invoke-BoundedStreamScan {
    param(
        [Parameter(Mandatory = $true)]$Policy,
        [Parameter(Mandatory = $true)][System.IO.Stream]$Stream,
        [Parameter(Mandatory = $true)][string]$RulePath,
        [Parameter(Mandatory = $true)][string]$DisplayPath,
        [Parameter(Mandatory = $true)][string]$Kind,
        [Parameter(Mandatory = $true)][long]$MaxBytes,
        [Parameter(Mandatory = $true)][long]$ExpectedLength,
        [System.IO.Stream]$MirrorStream
    )

    $findings = [System.Collections.Generic.List[object]]::new()
    $seenPatterns = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $longestPattern = [int](($Policy.Patterns.Text | Measure-Object -Maximum Length).Maximum)
    $overlapCapacity = ($longestPattern * 4) + 3
    $overlap = [byte[]]::new($overlapCapacity)
    $overlapCount = 0
    $buffer = [byte[]]::new($script:ScanChunkBytes)
    [long]$totalRead = 0
    $limitExceeded = $false
    $readFailed = $false

    try {
        while ($true) {
            $remaining = $MaxBytes - $totalRead
            $requestCount = if ($remaining -le 0) { 1 } else { [int][Math]::Min($buffer.Length, $remaining) }
            $read = $Stream.Read($buffer, 0, $requestCount)
            if ($read -eq 0) {
                break
            }

            $totalRead += $read
            if ($totalRead -gt $MaxBytes) {
                $finding = New-Finding -Kind 'read-limit' -Path $DisplayPath -Pattern 'stream exceeded the bounded read limit'
                $findings.Add($finding)
                $limitExceeded = $true
                break
            }
            if ($null -ne $MirrorStream) {
                $MirrorStream.Write($buffer, 0, $read)
            }

            $window = [byte[]]::new($overlapCount + $read)
            if ($overlapCount -gt 0) {
                [Array]::Copy($overlap, 0, $window, 0, $overlapCount)
            }
            [Array]::Copy($buffer, 0, $window, $overlapCount, $read)
            foreach ($finding in @(Find-PatternsInBytes -Policy $Policy -Bytes $window -EntryPath $RulePath -Kind $Kind -SensitiveAccessPath $DisplayPath)) {
                if ($seenPatterns.Add($finding.Pattern)) {
                    $finding.Path = $DisplayPath
                    $findings.Add($finding)
                }
            }

            $overlapCount = [Math]::Min($overlapCapacity, $window.Length)
            if ($overlapCount -gt 0) {
                [Array]::Copy($window, $window.Length - $overlapCount, $overlap, 0, $overlapCount)
            }
        }
    }
    catch {
        $finding = New-Finding -Kind 'read' -Path $DisplayPath -Pattern 'stream could not be scanned'
        $findings.Add($finding)
        $readFailed = $true
    }

    if (-not $limitExceeded -and -not $readFailed -and $totalRead -ne $ExpectedLength) {
        $finding = New-Finding -Kind 'read-race' -Path $DisplayPath -Pattern 'stream length changed while it was scanned'
        $findings.Add($finding)
    }

    return [pscustomobject]@{
        Findings = @($findings)
        BytesRead = $totalRead
        LimitExceeded = $limitExceeded
        ReadFailed = $readFailed
    }
}

function Read-StreamRange {
    param(
        [Parameter(Mandatory = $true)][System.IO.Stream]$Stream,
        [Parameter(Mandatory = $true)][long]$Offset,
        [Parameter(Mandatory = $true)][int]$Count
    )

    if ($Offset -lt 0 -or $Count -lt 0 -or $Offset -gt ($Stream.Length - $Count)) {
        return $null
    }

    $bytes = [byte[]]::new($Count)
    $Stream.Position = $Offset
    $readTotal = 0
    while ($readTotal -lt $bytes.Length) {
        $read = $Stream.Read($bytes, $readTotal, $bytes.Length - $readTotal)
        if ($read -eq 0) {
            return $null
        }
        $readTotal += $read
    }
    return ,$bytes
}

function Find-ZipEndRecordCandidates {
    param([Parameter(Mandatory = $true)][System.IO.Stream]$Stream)

    $candidates = [System.Collections.Generic.List[long]]::new()
    $buffer = [byte[]]::new($script:ScanChunkBytes)
    $overlap = [byte[]]::new(3)
    $overlapCount = 0
    [long]$totalRead = 0
    $Stream.Position = 0

    while ($true) {
        $read = $Stream.Read($buffer, 0, $buffer.Length)
        if ($read -eq 0) {
            break
        }

        $window = [byte[]]::new($overlapCount + $read)
        if ($overlapCount -gt 0) {
            [Array]::Copy($overlap, 0, $window, 0, $overlapCount)
        }
        [Array]::Copy($buffer, 0, $window, $overlapCount, $read)
        [long]$windowOffset = $totalRead - $overlapCount
        for ($index = 0; $index -le $window.Length - 4; $index++) {
            if ($window[$index] -eq 0x50 -and $window[$index + 1] -eq 0x4B -and
                $window[$index + 2] -eq 0x05 -and $window[$index + 3] -eq 0x06) {
                [void]$candidates.Add($windowOffset + $index)
                if ($candidates.Count -gt $script:MaxZipEndRecordCandidates) {
                    return [pscustomobject]@{
                        Offsets = [long[]]@()
                        LimitExceeded = $true
                    }
                }
            }
        }

        $overlapCount = [Math]::Min($overlap.Length, $window.Length)
        if ($overlapCount -gt 0) {
            [Array]::Copy($window, $window.Length - $overlapCount, $overlap, 0, $overlapCount)
        }
        $totalRead += $read
    }

    if ($totalRead -ne $Stream.Length) {
        throw 'ZIP candidate scan did not read the complete stream.'
    }
    return [pscustomobject]@{
        Offsets = [long[]]$candidates.ToArray()
        LimitExceeded = $false
    }
}

function Get-ZipCandidateMetadata {
    param(
        [Parameter(Mandatory = $true)][System.IO.Stream]$Stream,
        [Parameter(Mandatory = $true)][long]$CandidateOffset,
        [Parameter(Mandatory = $true)][string]$DisplayPath
    )

    [byte[]]$endRecord = Read-StreamRange -Stream $Stream -Offset $CandidateOffset -Count 22
    if ($null -eq $endRecord -or
        $endRecord[0] -ne 0x50 -or $endRecord[1] -ne 0x4B -or
        $endRecord[2] -ne 0x05 -or $endRecord[3] -ne 0x06) {
        return $null
    }

    $commentLength = [BitConverter]::ToUInt16($endRecord, 20)
    [long]$recordEnd = $CandidateOffset + 22 + $commentLength
    if ($recordEnd -gt $Stream.Length) {
        return $null
    }

    $diskNumber = [BitConverter]::ToUInt16($endRecord, 4)
    $centralDisk = [BitConverter]::ToUInt16($endRecord, 6)
    $entriesOnDisk = [BitConverter]::ToUInt16($endRecord, 8)
    $entryCount = [BitConverter]::ToUInt16($endRecord, 10)
    $centralSize = [BitConverter]::ToUInt32($endRecord, 12)
    $centralOffset = [BitConverter]::ToUInt32($endRecord, 16)
    $usesZip64 = $entriesOnDisk -eq [uint16]::MaxValue -or $entryCount -eq [uint16]::MaxValue -or
        $centralSize -eq [uint32]::MaxValue -or $centralOffset -eq [uint32]::MaxValue

    if ($usesZip64) {
        [long]$locatorOffset = $CandidateOffset - 20
        [byte[]]$locator = Read-StreamRange -Stream $Stream -Offset $locatorOffset -Count 20
        if ($null -eq $locator -or
            $locator[0] -ne 0x50 -or $locator[1] -ne 0x4B -or
            $locator[2] -ne 0x06 -or $locator[3] -ne 0x07) {
            return $null
        }

        $zip64Disk = [BitConverter]::ToUInt32($locator, 4)
        $zip64OffsetValue = [BitConverter]::ToUInt64($locator, 8)
        $diskCount = [BitConverter]::ToUInt32($locator, 16)
        if ($diskCount -eq 0 -or $zip64Disk -ge $diskCount -or
            $zip64OffsetValue -gt [uint64]([long]::MaxValue)) {
            return $null
        }
        [long]$zip64Offset = [long]$zip64OffsetValue
        [byte[]]$zip64Record = Read-StreamRange -Stream $Stream -Offset $zip64Offset -Count 56
        if ($null -eq $zip64Record -or
            $zip64Record[0] -ne 0x50 -or $zip64Record[1] -ne 0x4B -or
            $zip64Record[2] -ne 0x06 -or $zip64Record[3] -ne 0x06) {
            return $null
        }

        $zip64RecordSize = [BitConverter]::ToUInt64($zip64Record, 4)
        if ($zip64RecordSize -lt 44 -or $zip64RecordSize -gt [uint64]([long]::MaxValue - 12) -or
            $zip64Offset + 12 + [long]$zip64RecordSize -ne $locatorOffset) {
            return $null
        }
        $zip64EntriesOnDisk = [BitConverter]::ToUInt64($zip64Record, 24)
        $zip64EntryCount = [BitConverter]::ToUInt64($zip64Record, 32)
        $zip64CentralSize = [BitConverter]::ToUInt64($zip64Record, 40)
        $zip64CentralOffset = [BitConverter]::ToUInt64($zip64Record, 48)
        if ($zip64EntriesOnDisk -gt $zip64EntryCount -or
            $zip64CentralSize -gt [uint64]([long]::MaxValue) -or
            $zip64CentralOffset -gt [uint64]([long]::MaxValue)) {
            return $null
        }

        [long]$zip64CentralStart = [long]$zip64CentralOffset
        [long]$zip64CentralLength = [long]$zip64CentralSize
        if ($zip64CentralStart + $zip64CentralLength -ne $zip64Offset) {
            return $null
        }
        if ($zip64EntryCount -eq 0) {
            if ($zip64CentralLength -ne 0 -or $zip64CentralStart -ne $zip64Offset) {
                return $null
            }
        }
        else {
            [byte[]]$centralSignature = Read-StreamRange -Stream $Stream -Offset $zip64CentralStart -Count 4
            if ($zip64CentralLength -lt 46 -or $null -eq $centralSignature -or
                $centralSignature[0] -ne 0x50 -or $centralSignature[1] -ne 0x4B -or
                $centralSignature[2] -ne 0x01 -or $centralSignature[3] -ne 0x02) {
                return $null
            }
        }

        return [pscustomobject]@{
            EntryCount = 0
            Finding = New-Finding -Kind 'archive-read' -Path $DisplayPath -Pattern 'multi-disk and ZIP64 archives are outside the supported boundary'
        }
    }

    if ($entriesOnDisk -gt $entryCount) {
        return $null
    }
    [long]$centralStart = [long]$centralOffset
    [long]$centralLength = [long]$centralSize
    if ($centralStart + $centralLength -ne $CandidateOffset) {
        return $null
    }
    if ($entryCount -eq 0) {
        if ($centralLength -ne 0 -or $centralStart -ne $CandidateOffset) {
            return $null
        }
    }
    else {
        [byte[]]$centralSignature = Read-StreamRange -Stream $Stream -Offset $centralStart -Count 4
        if ($centralLength -lt 46 -or $null -eq $centralSignature -or
            $centralSignature[0] -ne 0x50 -or $centralSignature[1] -ne 0x4B -or
            $centralSignature[2] -ne 0x01 -or $centralSignature[3] -ne 0x02) {
            return $null
        }
    }

    $usesMultipleDisks = $diskNumber -ne 0 -or $centralDisk -ne 0 -or $entriesOnDisk -ne $entryCount
    if ($usesMultipleDisks) {
        return [pscustomobject]@{
            EntryCount = 0
            Finding = New-Finding -Kind 'archive-read' -Path $DisplayPath -Pattern 'multi-disk and ZIP64 archives are outside the supported boundary'
        }
    }
    if ($entryCount -gt $script:MaxArchiveEntries) {
        return [pscustomobject]@{
            EntryCount = [int]$entryCount
            Finding = New-Finding -Kind 'archive-entry-count' -Path $DisplayPath -Pattern 'archive exceeds the 4096-entry limit'
        }
    }

    [long]$cursor = $centralStart
    $usesZip64Entry = $false
    for ($entryIndex = 0; $entryIndex -lt $entryCount; $entryIndex++) {
        [byte[]]$centralHeader = Read-StreamRange -Stream $Stream -Offset $cursor -Count 46
        if ($null -eq $centralHeader -or
            $centralHeader[0] -ne 0x50 -or $centralHeader[1] -ne 0x4B -or
            $centralHeader[2] -ne 0x01 -or $centralHeader[3] -ne 0x02) {
            return $null
        }

        $nameLength = [BitConverter]::ToUInt16($centralHeader, 28)
        $extraLength = [BitConverter]::ToUInt16($centralHeader, 30)
        $entryCommentLength = [BitConverter]::ToUInt16($centralHeader, 32)
        $entryDisk = [BitConverter]::ToUInt16($centralHeader, 34)
        $localOffset = [BitConverter]::ToUInt32($centralHeader, 42)
        [long]$nextCursor = $cursor + 46 + $nameLength + $extraLength + $entryCommentLength
        if ($nameLength -eq 0 -or $nextCursor -gt $CandidateOffset) {
            return $null
        }

        if ($entryDisk -eq [uint16]::MaxValue -or $localOffset -eq [uint32]::MaxValue) {
            $usesZip64Entry = $true
        }
        elseif ($entryDisk -ne 0) {
            $usesMultipleDisks = $true
        }
        else {
            [byte[]]$localSignature = Read-StreamRange -Stream $Stream -Offset ([long]$localOffset) -Count 4
            if ([long]$localOffset -ge $centralStart -or $null -eq $localSignature -or
                $localSignature[0] -ne 0x50 -or $localSignature[1] -ne 0x4B -or
                $localSignature[2] -ne 0x03 -or $localSignature[3] -ne 0x04) {
                return $null
            }
        }
        $cursor = $nextCursor
    }
    if ($cursor -ne $CandidateOffset) {
        return $null
    }
    if ($usesMultipleDisks -or $usesZip64Entry) {
        return [pscustomobject]@{
            EntryCount = 0
            Finding = New-Finding -Kind 'archive-read' -Path $DisplayPath -Pattern 'multi-disk and ZIP64 archives are outside the supported boundary'
        }
    }

    return [pscustomobject]@{
        EntryCount = [int]$entryCount
        Finding = $null
    }
}

function Get-ZipDirectoryMetadata {
    param(
        [Parameter(Mandatory = $true)][System.IO.Stream]$Stream,
        [Parameter(Mandatory = $true)][string]$DisplayPath
    )

    $originalPosition = $Stream.Position
    try {
        if ($Stream.Length -lt 22) {
            return [pscustomobject]@{
                IsZip = $false
                EntryCount = 0
                Finding = $null
            }
        }

        $candidateSearch = Find-ZipEndRecordCandidates -Stream $Stream
        if ($candidateSearch.LimitExceeded) {
            return [pscustomobject]@{
                IsZip = $false
                EntryCount = 0
                Finding = New-Finding -Kind 'archive-read' -Path $DisplayPath -Pattern 'stream exceeds the 256-candidate ZIP end-record limit'
            }
        }
        $selectedMetadata = $null
        for ($index = $candidateSearch.Offsets.Count - 1; $index -ge 0; $index--) {
            $candidateMetadata = Get-ZipCandidateMetadata -Stream $Stream -CandidateOffset $candidateSearch.Offsets[$index] -DisplayPath $DisplayPath
            if ($null -ne $candidateMetadata) {
                if ($null -ne $selectedMetadata) {
                    return [pscustomobject]@{
                        IsZip = $true
                        EntryCount = 0
                        Finding = New-Finding -Kind 'archive-read' -Path $DisplayPath -Pattern 'archive contains multiple structurally valid end records'
                    }
                }
                $selectedMetadata = $candidateMetadata
            }
        }
        if ($null -ne $selectedMetadata) {
            return [pscustomobject]@{
                IsZip = $true
                EntryCount = $selectedMetadata.EntryCount
                Finding = $selectedMetadata.Finding
            }
        }
        return [pscustomobject]@{
            IsZip = $false
            EntryCount = 0
            Finding = $null
        }
    }
    catch {
        return [pscustomobject]@{
            IsZip = $false
            EntryCount = 0
            Finding = New-Finding -Kind 'archive-read' -Path $DisplayPath -Pattern 'archive directory metadata could not be inspected'
        }
    }
    finally {
        $Stream.Position = $originalPosition
    }
}

function Invoke-ZipScan {
    param(
        [Parameter(Mandatory = $true)]$Policy,
        [Parameter(Mandatory = $true)][System.IO.Stream]$ArchiveStream,
        [Parameter(Mandatory = $true)][string]$DisplayPath,
        $DirectoryMetadata
    )

    $directoryMetadata = $DirectoryMetadata
    if ($null -eq $directoryMetadata) {
        $directoryMetadata = Get-ZipDirectoryMetadata -Stream $ArchiveStream -DisplayPath $DisplayPath
    }
    if (-not $directoryMetadata.IsZip) {
        New-Finding -Kind 'archive-read' -Path $DisplayPath -Pattern 'archive end record is missing or unsupported'
        return
    }
    if ($null -ne $directoryMetadata.Finding) {
        $directoryMetadata.Finding
        return
    }
    try {
        $ArchiveStream.Position = 0
        $archive = [System.IO.Compression.ZipArchive]::new($ArchiveStream, [System.IO.Compression.ZipArchiveMode]::Read, $true)
        try {
            if ($archive.Entries.Count -ne $directoryMetadata.EntryCount) {
                New-Finding -Kind 'archive-read' -Path $DisplayPath -Pattern 'archive entry count does not match its directory metadata'
                return
            }

            $seenEntries = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            [long]$totalUncompressedBytes = 0
            $totalBudgetExceeded = $false
            foreach ($entry in $archive.Entries) {
                $tempPath = $null
                $tempStream = $null
                $stream = $null
                try {
                    $entryPath = ConvertTo-NormalizedEntryPath -EntryPath $entry.FullName
                }
                catch {
                    New-Finding -Kind 'archive-path' -Path "$DisplayPath!/[unsafe-entry]" -Pattern 'unsafe or non-normalized archive entry'
                    continue
                }

                $entryDisplayPath = "$DisplayPath!/$entryPath"
                if (-not $seenEntries.Add($entryPath)) {
                    New-Finding -Kind 'archive-path' -Path $entryDisplayPath -Pattern 'duplicate normalized archive entry'
                    continue
                }
                if (Test-ArtifactContainsHarnessPath -EntryPath $entryPath) {
                    New-Finding -Kind 'packaging' -Path $entryDisplayPath -Pattern 'test harness must never be packaged'
                }

                $unixMode = ($entry.ExternalAttributes -shr 16) -band 0xF000
                $windowsAttributes = [System.IO.FileAttributes]($entry.ExternalAttributes -band 0xFFFF)
                if ($unixMode -eq 0xA000 -or (Test-IsReparsePoint -Attributes $windowsAttributes)) {
                    New-Finding -Kind 'reparse' -Path $entryDisplayPath -Pattern 'archive links are forbidden'
                    continue
                }

                foreach ($finding in @(Find-PatternsInText -Policy $Policy -Text $entryPath -EntryPath $entryPath -Kind 'archive-path')) {
                    $finding.Path = $entryDisplayPath
                    $finding
                }
                if ([string]::IsNullOrEmpty($entry.Name) -and $entry.Length -eq 0) {
                    continue
                }

                if (-not $totalBudgetExceeded) {
                    if ($entry.Length -gt ($script:MaxArchiveTotalUncompressedBytes - $totalUncompressedBytes)) {
                        New-Finding -Kind 'archive-total-size' -Path $entryDisplayPath -Pattern 'archive exceeds the 256 MiB cumulative uncompressed limit'
                        $totalBudgetExceeded = $true
                        continue
                    }
                    $totalUncompressedBytes += $entry.Length
                }
                else {
                    continue
                }
                if ($entry.Length -gt $script:MaxArchiveEntryBytes) {
                    New-Finding -Kind 'archive-entry-size' -Path $entryDisplayPath -Pattern 'entry exceeds the 128 MiB uncompressed limit'
                    continue
                }
                if ($entry.Length -ge $script:MinimumRatioCheckBytes) {
                    $ratio = if ($entry.CompressedLength -le 0) {
                        [double]::PositiveInfinity
                    }
                    else {
                        [double]$entry.Length / [double]$entry.CompressedLength
                    }
                    if ($ratio -gt $script:MaxArchiveCompressionRatio) {
                        New-Finding -Kind 'archive-compression-ratio' -Path $entryDisplayPath -Pattern 'entry exceeds the 200:1 compression-ratio limit'
                        continue
                    }
                }

                try {
                    $tempExtension = [System.IO.Path]::GetExtension($entry.Name)
                    $tempPath = Join-Path ([System.IO.Path]::GetTempPath()) (
                        'codex-boundary-{0}{1}' -f [Guid]::NewGuid().ToString('N'), $tempExtension)
                    $tempStream = [System.IO.FileStream]::new(
                        $tempPath,
                        [System.IO.FileMode]::CreateNew,
                        [System.IO.FileAccess]::ReadWrite,
                        [System.IO.FileShare]::Read,
                        $script:ScanChunkBytes,
                        [System.IO.FileOptions]::SequentialScan)
                    try {
                        $stream = $entry.Open()
                        try {
                            $scanResult = Invoke-BoundedStreamScan -Policy $Policy -Stream $stream -RulePath $entryPath -DisplayPath $entryDisplayPath -Kind 'archive-content' -MaxBytes $script:MaxArchiveEntryBytes -ExpectedLength $entry.Length -MirrorStream $tempStream
                        }
                        finally {
                            $stream.Dispose()
                        }
                        $tempStream.Flush()
                        $tempStream.Dispose()
                        $tempStream = $null
                        $trustedMicrosoftWindowsSdk = -not $scanResult.LimitExceeded -and
                            -not $scanResult.ReadFailed -and
                            $scanResult.BytesRead -eq $entry.Length -and
                            (Test-IsTrustedMicrosoftWindowsSdkAssembly -Path $tempPath -RulePath $entryPath)
                        foreach ($finding in $scanResult.Findings) {
                            if (-not (Test-IsTrustedRuntimeFindingAllowed -TrustedMicrosoftWindowsSdk $trustedMicrosoftWindowsSdk -Finding $finding)) {
                                $finding
                            }
                        }
                        if (-not $scanResult.LimitExceeded -and -not $scanResult.ReadFailed -and $scanResult.BytesRead -eq $entry.Length) {
                            $nestedStream = [System.IO.FileStream]::new(
                                $tempPath,
                                [System.IO.FileMode]::Open,
                                [System.IO.FileAccess]::Read,
                                [System.IO.FileShare]::Read)
                            try {
                                $nestedMetadata = Get-ZipDirectoryMetadata -Stream $nestedStream -DisplayPath $entryDisplayPath
                                if ($nestedMetadata.IsZip) {
                                    New-Finding -Kind 'archive-depth' -Path $entryDisplayPath -Pattern "nested archives exceed maximum depth $($script:MaxNestedArchiveDepth)"
                                }
                                elseif ($null -ne $nestedMetadata.Finding) {
                                    $nestedMetadata.Finding
                                }
                            }
                            finally {
                                $nestedStream.Dispose()
                            }
                        }
                    }
                    finally {
                        if ($null -ne $tempStream) {
                            $tempStream.Dispose()
                        }
                        if ($null -ne $tempPath -and [System.IO.File]::Exists($tempPath)) {
                            try {
                                [System.IO.File]::Delete($tempPath)
                            }
                            catch {
                            }
                        }
                    }
                }
                catch {
                    New-Finding -Kind 'archive-read' -Path $entryDisplayPath -Pattern 'entry could not be scanned'
                }
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    catch {
        New-Finding -Kind 'archive-read' -Path $DisplayPath -Pattern 'archive could not be opened'
    }
}

function Invoke-FileScan {
    param(
        [Parameter(Mandatory = $true)]$Policy,
        [Parameter(Mandatory = $true)][System.IO.FileInfo]$File,
        [Parameter(Mandatory = $true)][string]$EntryPath,
        [string]$RulePath,
        [switch]$Artifact
    )

    if ([string]::IsNullOrEmpty($RulePath)) {
        $RulePath = $EntryPath
    }
    if (Test-IsReparsePoint -Attributes $File.Attributes) {
        New-Finding -Kind 'reparse' -Path $EntryPath -Pattern 'reparse points are forbidden'
        return
    }
    if ($Artifact -and (Test-ArtifactContainsHarnessPath -EntryPath $RulePath)) {
        New-Finding -Kind 'packaging' -Path $EntryPath -Pattern 'test harness must never be packaged'
    }
    $pathCandidates = @($RulePath)
    if ($Artifact -and $RulePath -cne $EntryPath) {
        $pathCandidates += $EntryPath
    }
    $seenPathPatterns = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($pathCandidate in $pathCandidates) {
        foreach ($finding in @(Find-PatternsInText -Policy $Policy -Text $pathCandidate -EntryPath $pathCandidate -Kind 'path')) {
            if (-not $seenPathPatterns.Add($finding.Pattern)) {
                continue
            }
            $finding.Path = $EntryPath
            $finding
        }
    }

    try {
        $metadataLength = $File.Length
        if ($metadataLength -gt $script:MaxFileBytes) {
            New-Finding -Kind 'size' -Path $EntryPath -Pattern 'file exceeds the 256 MiB scan limit'
            return
        }
        $stream = [System.IO.FileStream]::new($File.FullName, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
        try {
            if ($stream.Length -ne $metadataLength) {
                New-Finding -Kind 'read-race' -Path $EntryPath -Pattern 'file length changed before it could be scanned'
                return
            }
            if ($stream.Length -gt $script:MaxFileBytes) {
                New-Finding -Kind 'size' -Path $EntryPath -Pattern 'file exceeds the 256 MiB scan limit'
                return
            }

            if ($Artifact) {
                $hasArchiveExtension = $File.Extension -in @('.zip', '.nupkg')
                $archiveMetadata = Get-ZipDirectoryMetadata -Stream $stream -DisplayPath $EntryPath
                if ($archiveMetadata.IsZip -and -not $hasArchiveExtension) {
                    New-Finding -Kind 'archive-type' -Path $EntryPath -Pattern 'ZIP content requires a .zip or .nupkg extension'
                    return
                }
                if ($hasArchiveExtension -and -not $archiveMetadata.IsZip) {
                    New-Finding -Kind 'archive-read' -Path $EntryPath -Pattern 'archive extension does not contain a supported ZIP signature'
                    return
                }
                if ($hasArchiveExtension) {
                    Invoke-ZipScan -Policy $Policy -ArchiveStream $stream -DisplayPath $EntryPath -DirectoryMetadata $archiveMetadata
                    if ($stream.Length -ne $metadataLength) {
                        New-Finding -Kind 'read-race' -Path $EntryPath -Pattern 'archive length changed while it was scanned'
                    }
                    return
                }
                if ($null -ne $archiveMetadata.Finding) {
                    $archiveMetadata.Finding
                    return
                }
            }

            $scanResult = Invoke-BoundedStreamScan -Policy $Policy -Stream $stream -RulePath $RulePath -DisplayPath $EntryPath -Kind 'content' -MaxBytes $script:MaxFileBytes -ExpectedLength $stream.Length
            $trustedMicrosoftWindowsSdk = $Artifact -and
                (Test-IsTrustedMicrosoftWindowsSdkAssembly -Path $File.FullName -RulePath $RulePath)
            foreach ($finding in $scanResult.Findings) {
                if (-not (Test-IsTrustedRuntimeFindingAllowed -TrustedMicrosoftWindowsSdk $trustedMicrosoftWindowsSdk -Finding $finding)) {
                    $finding
                }
            }
        }
        finally {
            if ($null -ne $stream) {
                $stream.Dispose()
            }
        }
    }
    catch {
        New-Finding -Kind 'read' -Path $EntryPath -Pattern 'file could not be scanned'
    }
}

function Get-ArtifactRulePath {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactsRoot,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $relative = Get-NormalizedRelativePath -BasePath $ArtifactsRoot -Path $Path
    $segments = @($relative.Split('/'))
    if ($segments.Count -le 1) {
        return $relative
    }
    return ($segments[1..($segments.Count - 1)] -join '/')
}

function Invoke-DirectoryScan {
    param(
        [Parameter(Mandatory = $true)]$Policy,
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$ScanRoot,
        [switch]$Artifact
    )

    $rootInfo = Get-Item -LiteralPath $ScanRoot -Force
    $rootRelative = Get-NormalizedRelativePath -BasePath $RepositoryRoot -Path $rootInfo.FullName
    if (Test-IsReparsePoint -Attributes $rootInfo.Attributes) {
        New-Finding -Kind 'reparse' -Path $rootRelative -Pattern 'production roots cannot be reparse points'
        return
    }

    $directories = [System.Collections.Generic.Stack[System.IO.DirectoryInfo]]::new()
    $directories.Push([System.IO.DirectoryInfo]$rootInfo)
    while ($directories.Count -gt 0) {
        $directory = $directories.Pop()
        try {
            $entries = @($directory.GetFileSystemInfos() | Sort-Object Name)
        }
        catch {
            $relative = Get-NormalizedRelativePath -BasePath $RepositoryRoot -Path $directory.FullName
            New-Finding -Kind 'read' -Path $relative -Pattern 'directory could not be enumerated'
            continue
        }

        foreach ($entry in $entries) {
            $relative = Get-NormalizedRelativePath -BasePath $RepositoryRoot -Path $entry.FullName
            $rulePath = if ($Artifact) {
                Get-ArtifactRulePath -ArtifactsRoot $ScanRoot -Path $entry.FullName
            }
            else {
                $relative
            }
            if ($entry -is [System.IO.DirectoryInfo]) {
                if (-not $Artifact -and $entry.Name -in @('obj', 'bin')) {
                    continue
                }
                if (-not $Artifact -and $relative -ceq $Policy.HarnessPath) {
                    if (Test-IsReparsePoint -Attributes $entry.Attributes) {
                        New-Finding -Kind 'reparse' -Path $relative -Pattern 'the test harness cannot be a reparse point'
                    }
                    elseif (-not (Test-HarnessIsNonPackable -HarnessDirectory $entry.FullName)) {
                        New-Finding -Kind 'policy' -Path $relative -Pattern 'the exempt harness must contain exactly one IsPackable=false project'
                    }
                    continue
                }
                if (Test-IsReparsePoint -Attributes $entry.Attributes) {
                    New-Finding -Kind 'reparse' -Path $relative -Pattern 'reparse points are forbidden'
                    continue
                }
                if ($Artifact -and (Test-ArtifactContainsHarnessPath -EntryPath $rulePath)) {
                    New-Finding -Kind 'packaging' -Path $relative -Pattern 'test harness must never be packaged'
                }
                $pathCandidates = @($rulePath)
                if ($Artifact -and $rulePath -cne $relative) {
                    $pathCandidates += $relative
                }
                $seenPathPatterns = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
                foreach ($pathCandidate in $pathCandidates) {
                    foreach ($finding in @(Find-PatternsInText -Policy $Policy -Text $pathCandidate -EntryPath $pathCandidate -Kind 'path')) {
                        if (-not $seenPathPatterns.Add($finding.Pattern)) {
                            continue
                        }
                        $finding.Path = $relative
                        $finding
                    }
                }
                $directories.Push($entry)
            }
            else {
                Invoke-FileScan -Policy $Policy -File $entry -EntryPath $relative -RulePath $rulePath -Artifact:$Artifact
            }
        }
    }
}

function Invoke-RepositoryScan {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$SecurityDirectory,
        [string[]]$ProductionRoots = @('src', 'tools', 'installer', 'artifacts'),
        [switch]$IncludeSourcePrivacyDocument
    )

    $repositoryFullPath = [System.IO.Path]::GetFullPath($RepositoryRoot)
    $policy = Read-BoundaryPolicy -RepositoryRoot $repositoryFullPath -SecurityDirectory $SecurityDirectory
    foreach ($rootName in $ProductionRoots) {
        if ($rootName -notin @('src', 'tools', 'installer', 'artifacts', 'docs')) {
            throw "Unsupported scan root: $rootName"
        }
        $scanRoot = Join-Path $repositoryFullPath $rootName
        if (-not (Test-Path -LiteralPath $scanRoot)) {
            continue
        }
        Assert-NoReparseAncestors -RepositoryRoot $repositoryFullPath -Path $scanRoot
        Invoke-DirectoryScan -Policy $policy -RepositoryRoot $repositoryFullPath -ScanRoot $scanRoot -Artifact:($rootName -eq 'artifacts')
    }

    if ($IncludeSourcePrivacyDocument) {
        $privacyPath = Join-Path $repositoryFullPath 'docs\privacy.md'
        if (Test-Path -LiteralPath $privacyPath -PathType Leaf) {
            try {
                Assert-NoReparseAncestors -RepositoryRoot $repositoryFullPath -Path $privacyPath
                Invoke-FileScan -Policy $policy -File (Get-Item -LiteralPath $privacyPath -Force) -EntryPath 'docs/privacy.md'
            }
            catch {
                New-Finding -Kind 'reparse' -Path 'docs/privacy.md' -Pattern 'privacy document path must not contain reparse points'
            }
        }
    }
}
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ($SelfTest) {
    try {
        . (Join-Path $repositoryRoot 'tests\security\verify-sensitive-boundary.selftest.ps1')
        Invoke-SelfTest -ScannerPath $PSCommandPath -BuildDirectory $PSScriptRoot
        exit 0
    }
    catch {
        Write-Host 'Sensitive boundary self-test: FAIL'
        if ($_.Exception.Message.StartsWith('Sensitive boundary self-test failed:', [StringComparison]::Ordinal)) {
            Write-Host $_.Exception.Message
        }
        else {
            Write-Host '[self-test] fixture setup or execution failed'
        }
        exit 1
    }
}

$securityDirectory = Join-Path $repositoryRoot 'tests\security'
try {
    $findings = @(Invoke-RepositoryScan -RepositoryRoot $repositoryRoot -SecurityDirectory $securityDirectory -IncludeSourcePrivacyDocument)
}
catch {
    Write-Host 'Sensitive boundary: FAIL'
    Write-Host '[policy] boundary policy is invalid or inaccessible'
    exit 1
}

if ($findings.Count -gt 0) {
    Write-Host 'Sensitive boundary: FAIL'
    foreach ($finding in $findings) {
        Write-Host ("[{0}] {1} [{2}]" -f $finding.Kind, $finding.Path, $finding.Pattern)
    }
    exit 1
}

Write-Host 'Sensitive boundary: PASS'
exit 0
