function Write-TestPolicy {
    param(
        [Parameter(Mandatory = $true)][string]$SecurityDirectory,
        [string[]]$HarnessLines = @($script:ExpectedHarnessAllowance)
    )

    $null = New-Item -ItemType Directory -Path $SecurityDirectory -Force
    [System.IO.File]::WriteAllLines((Join-Path $SecurityDirectory 'forbidden-production-patterns.txt'), $script:ExpectedForbiddenPatterns)
    [System.IO.File]::WriteAllLines((Join-Path $SecurityDirectory 'allowed-documentation-paths.txt'), @($script:ExpectedDocumentationAllowance))
    [System.IO.File]::WriteAllLines((Join-Path $SecurityDirectory 'allowed-test-harness-paths.txt'), $HarnessLines)
    [System.IO.File]::WriteAllLines((Join-Path $SecurityDirectory 'allowed-production-sensitive-access.txt'), $script:ExpectedSensitiveAccessAllowances)
}

function Assert-SelfTest {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        $script:SelfTestFailures.Add($Message)
    }
}

function Invoke-SelfTestProcess {
    param(
        [Parameter(Mandatory = $true)][string]$FileName,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [hashtable]$Environment = @{}
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }
    foreach ($entry in $Environment.GetEnumerator()) {
        $startInfo.Environment[$entry.Key] = $entry.Value
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw 'Self-test subprocess did not start.'
        }
        $standardOutput = $process.StandardOutput.ReadToEndAsync()
        $standardError = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            Output = $standardOutput.GetAwaiter().GetResult()
            ErrorOutput = $standardError.GetAwaiter().GetResult()
        }
    }
    finally {
        $process.Dispose()
    }
}

function Add-ZipTextEntry {
    param(
        [Parameter(Mandatory = $true)][System.IO.Compression.ZipArchive]$Archive,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )

    $entry = $Archive.CreateEntry($Path)
    $stream = $entry.Open()
    try {
        $writer = [System.IO.StreamWriter]::new($stream, [System.Text.UTF8Encoding]::new($false), 1024, $true)
        try { $writer.Write($Content) } finally { $writer.Dispose() }
    }
    finally {
        $stream.Dispose()
    }
}

function Add-ZipBytesEntry {
    param(
        [Parameter(Mandatory = $true)][System.IO.Compression.ZipArchive]$Archive,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][byte[]]$Bytes
    )

    $entry = $Archive.CreateEntry($Path, [System.IO.Compression.CompressionLevel]::Optimal)
    $stream = $entry.Open()
    try {
        $stream.Write($Bytes, 0, $Bytes.Length)
    }
    finally {
        $stream.Dispose()
    }
}

function Add-ZipRepeatedByteEntry {
    param(
        [Parameter(Mandatory = $true)][System.IO.Compression.ZipArchive]$Archive,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][long]$Length
    )

    $entry = $Archive.CreateEntry($Path, [System.IO.Compression.CompressionLevel]::Optimal)
    $stream = $entry.Open()
    try {
        $buffer = [byte[]]::new(65536)
        $remaining = $Length
        while ($remaining -gt 0) {
            $count = [int][Math]::Min($buffer.Length, $remaining)
            $stream.Write($buffer, 0, $count)
            $remaining -= $count
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Set-ZipDeclaredUncompressedSizes {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][uint32[]]$Sizes
    )

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $localIndex = 0
    $centralIndex = 0
    for ($index = 0; $index -le $bytes.Length - 4; $index++) {
        $signature = [BitConverter]::ToUInt32($bytes, $index)
        if ($signature -eq 0x04034B50 -and $localIndex -lt $Sizes.Count) {
            [BitConverter]::GetBytes($Sizes[$localIndex]).CopyTo($bytes, $index + 22)
            $localIndex++
        }
        elseif ($signature -eq 0x02014B50 -and $centralIndex -lt $Sizes.Count) {
            [BitConverter]::GetBytes($Sizes[$centralIndex]).CopyTo($bytes, $index + 24)
            $centralIndex++
        }
    }
    if ($localIndex -ne $Sizes.Count -or $centralIndex -ne $Sizes.Count) {
        throw 'Self-test could not patch ZIP size metadata.'
    }
    [System.IO.File]::WriteAllBytes($Path, $bytes)
}

function Add-SelfExtractingZipPrefix {
    param(
        [Parameter(Mandatory = $true)][string]$InputPath,
        [Parameter(Mandatory = $true)][string]$OutputPath,
        [Parameter(Mandatory = $true)][byte[]]$Prefix
    )

    $bytes = [System.IO.File]::ReadAllBytes($InputPath)
    $endIndex = -1
    for ($index = $bytes.Length - 22; $index -ge 0; $index--) {
        if ([BitConverter]::ToUInt32($bytes, $index) -eq 0x06054B50) {
            $commentLength = [BitConverter]::ToUInt16($bytes, $index + 20)
            if ($index + 22 + $commentLength -eq $bytes.Length) {
                $endIndex = $index
                break
            }
        }
    }
    if ($endIndex -lt 0) {
        throw 'Self-test could not locate the ZIP end record.'
    }

    $entryCount = [BitConverter]::ToUInt16($bytes, $endIndex + 10)
    $centralOffset = [BitConverter]::ToUInt32($bytes, $endIndex + 16)
    [int]$cursor = $centralOffset
    for ($entryIndex = 0; $entryIndex -lt $entryCount; $entryIndex++) {
        if ([BitConverter]::ToUInt32($bytes, $cursor) -ne 0x02014B50) {
            throw 'Self-test ZIP central directory is malformed.'
        }
        $localOffset = [BitConverter]::ToUInt32($bytes, $cursor + 42)
        [BitConverter]::GetBytes([uint32]($localOffset + $Prefix.Length)).CopyTo($bytes, $cursor + 42)
        $nameLength = [BitConverter]::ToUInt16($bytes, $cursor + 28)
        $extraLength = [BitConverter]::ToUInt16($bytes, $cursor + 30)
        $commentLength = [BitConverter]::ToUInt16($bytes, $cursor + 32)
        $cursor += 46 + $nameLength + $extraLength + $commentLength
    }
    [BitConverter]::GetBytes([uint32]($centralOffset + $Prefix.Length)).CopyTo($bytes, $endIndex + 16)

    $prefixed = [byte[]]::new($Prefix.Length + $bytes.Length)
    [Array]::Copy($Prefix, 0, $prefixed, 0, $Prefix.Length)
    [Array]::Copy($bytes, 0, $prefixed, $Prefix.Length, $bytes.Length)
    [System.IO.File]::WriteAllBytes($OutputPath, $prefixed)
}

function Add-FileOverlay {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][int]$Length,
        [byte]$Value = 0x4D
    )

    $stream = [System.IO.FileStream]::new($Path, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    try {
        $buffer = [byte[]]::new([Math]::Min(65536, [Math]::Max(1, $Length)))
        for ($index = 0; $index -lt $buffer.Length; $index++) {
            $buffer[$index] = $Value
        }
        $remaining = $Length
        while ($remaining -gt 0) {
            $count = [Math]::Min($buffer.Length, $remaining)
            $stream.Write($buffer, 0, $count)
            $remaining -= $count
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Invoke-SelfTest {
    param(
        [Parameter(Mandatory = $true)][string]$ScannerPath,
        [Parameter(Mandatory = $true)][string]$BuildDirectory
    )

    $script:SelfTestFailures = [System.Collections.Generic.List[string]]::new()
    $fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("codex-sensitive-boundary-{0}" -f [Guid]::NewGuid().ToString('N'))
    $outsideRoot = "$fixtureRoot-outside"
    try {
        $securityDirectory = Join-Path $fixtureRoot 'tests\security'
        Write-TestPolicy -SecurityDirectory $securityDirectory

        $processTestRoot = Join-Path $fixtureRoot 'subprocess-behavior'
        $processBuildDirectory = Join-Path $processTestRoot 'build'
        $processSecurityDirectory = Join-Path $processTestRoot 'tests\security'
        $processFakeBin = Join-Path $processTestRoot 'fake-bin'
        $null = New-Item -ItemType Directory -Path $processBuildDirectory -Force
        $null = New-Item -ItemType Directory -Path $processFakeBin -Force
        $null = New-Item -ItemType Directory -Path (Join-Path $processTestRoot 'src') -Force
        Write-TestPolicy -SecurityDirectory $processSecurityDirectory
        [System.IO.File]::WriteAllText(
            (Join-Path $processSecurityDirectory 'managed-assembly-policy.selftest.ps1'),
            "Write-Output 'MANAGED_SENTINEL'`nexit 0`n",
            [System.Text.UTF8Encoding]::new($false)
        )
        [System.IO.File]::Copy($ScannerPath, (Join-Path $processBuildDirectory 'verify-sensitive-boundary.ps1'))
        [System.IO.File]::Copy((Join-Path $BuildDirectory 'verify.ps1'), (Join-Path $processBuildDirectory 'verify.ps1'))
        [System.IO.File]::WriteAllText(
            (Join-Path $processBuildDirectory 'package-host.ps1'),
            "Write-Output 'HOST_PACKAGE_SENTINEL'`nexit 0`n",
            [System.Text.UTF8Encoding]::new($false)
        )
        $fakeDotnet = Join-Path $processFakeBin 'dotnet.cmd'
        $fakeCmake = Join-Path $processFakeBin 'fake-cmake.cmd'
        [System.IO.File]::WriteAllLines($fakeDotnet, @('@echo off', 'echo VERIFY_SENTINEL', 'exit /b 0'))
        [System.IO.File]::WriteAllLines($fakeCmake, @('@echo off', 'echo CMAKE_SENTINEL', 'exit /b 0'))
        [System.IO.File]::WriteAllText(
            (Join-Path $processBuildDirectory 'resolve-cmake.ps1'),
            "Write-Output '$($fakeCmake.Replace("'", "''"))'"
        )
        $pwshPath = [System.Environment]::ProcessPath
        if ([string]::IsNullOrWhiteSpace($pwshPath)) {
            $pwshPath = (Get-Process -Id $PID).Path
        }
        $processEnvironment = @{
            PATH = "$processFakeBin$([System.IO.Path]::PathSeparator)$([System.Environment]::GetEnvironmentVariable('PATH'))"
        }
        $verifyArguments = @('-NoProfile', '-File', (Join-Path $processBuildDirectory 'verify.ps1'), '-Configuration', 'Release')
        $cleanVerifyResult = Invoke-SelfTestProcess -FileName $pwshPath -Arguments $verifyArguments -WorkingDirectory $processTestRoot -Environment $processEnvironment
        Assert-SelfTest -Condition (
            $cleanVerifyResult.ExitCode -eq 0 -and
            $cleanVerifyResult.Output.Contains('Sensitive boundary: PASS') -and
            $cleanVerifyResult.Output.Contains('MANAGED_SENTINEL') -and
            $cleanVerifyResult.Output.Contains('VERIFY_SENTINEL') -and
            $cleanVerifyResult.Output.Contains('HOST_PACKAGE_SENTINEL') -and
            $cleanVerifyResult.Output.Contains('CMAKE_SENTINEL')
        ) -Message 'a clean scanner subprocess did not return control to Verify for managed/native commands'

        [System.IO.File]::WriteAllText((Join-Path $processTestRoot 'src\blocked.txt'), 'AccountManager')
        $blockedVerifyResult = Invoke-SelfTestProcess -FileName $pwshPath -Arguments $verifyArguments -WorkingDirectory $processTestRoot -Environment $processEnvironment
        Assert-SelfTest -Condition (
            $blockedVerifyResult.ExitCode -ne 0 -and
            $blockedVerifyResult.Output.Contains('Sensitive boundary: FAIL') -and
            -not $blockedVerifyResult.Output.Contains('VERIFY_SENTINEL') -and
            -not $blockedVerifyResult.Output.Contains('CMAKE_SENTINEL')
        ) -Message 'a failing scanner subprocess allowed Verify commands to continue'

        $null = New-Item -ItemType Directory -Path (Join-Path $fixtureRoot 'src') -Force
        [System.IO.File]::WriteAllText(
            (Join-Path $fixtureRoot 'src\production.txt'),
            "AccountManager`nauth.json`nData\Accounts",
            [System.Text.UTF8Encoding]::new($false)
        )
        $requiredFindings = @(Invoke-RepositoryScan -RepositoryRoot $fixtureRoot -SecurityDirectory $securityDirectory -ProductionRoots @('src'))
        foreach ($expected in @('AccountManager', 'auth.json', 'Data[\\/]Accounts')) {
            Assert-SelfTest -Condition ($expected -in $requiredFindings.Pattern) -Message "production fixture did not reject $expected"
            Write-Host "Self-test expected rejection: src/production.txt [$expected]"
        }

        $allowedReaderPath = Join-Path $fixtureRoot 'src\CodexQuotaTaskbar.Host\Provider\CodexAuthCredentialReader.cs'
        $allowedServicePath = Join-Path $fixtureRoot 'src\CodexQuotaTaskbar.Host\Provider\CodexSubscriptionMetadataService.cs'
        $null = New-Item -ItemType Directory -Path ([System.IO.Path]::GetDirectoryName($allowedReaderPath)) -Force
        [System.IO.File]::WriteAllText($allowedReaderPath, 'auth.json')
        [System.IO.File]::WriteAllText($allowedServicePath, 'https://chatgpt.com/backend-api/subscriptions')
        $reviewedSourceFindings = @(Invoke-RepositoryScan -RepositoryRoot $fixtureRoot -SecurityDirectory $securityDirectory -ProductionRoots @('src'))
        Assert-SelfTest -Condition (@($reviewedSourceFindings | Where-Object {
            $_.Path -in @(
                'src/CodexQuotaTaskbar.Host/Provider/CodexAuthCredentialReader.cs',
                'src/CodexQuotaTaskbar.Host/Provider/CodexSubscriptionMetadataService.cs')
        }).Count -eq 0) -Message 'an exact reviewed sensitive-access source was rejected'

        $secondReaderPath = Join-Path $fixtureRoot 'src\CodexQuotaTaskbar.Host\Provider\SecondReader.cs'
        [System.IO.File]::WriteAllText($secondReaderPath, 'auth.json')
        $secondReaderFindings = @(Invoke-RepositoryScan -RepositoryRoot $fixtureRoot -SecurityDirectory $securityDirectory -ProductionRoots @('src'))
        Assert-SelfTest -Condition (@($secondReaderFindings | Where-Object {
            $_.Path -ceq 'src/CodexQuotaTaskbar.Host/Provider/SecondReader.cs' -and $_.Pattern -ceq 'auth.json'
        }).Count -eq 1) -Message 'a second credential reader received the reviewed source allowance'
        Remove-Item -LiteralPath $secondReaderPath -Force

        $allowedArtifactDirectory = Join-Path $fixtureRoot 'artifacts\host-publish\Release'
        $null = New-Item -ItemType Directory -Path $allowedArtifactDirectory -Force
        [System.IO.File]::WriteAllText(
            (Join-Path $allowedArtifactDirectory 'CodexQuotaTaskbar.exe'),
            'auth.json https://chatgpt.com/backend-api/subscriptions')
        [System.IO.File]::WriteAllText(
            (Join-Path $allowedArtifactDirectory 'Renamed.exe'),
            'auth.json')
        $reviewedArtifactFindings = @(Invoke-RepositoryScan -RepositoryRoot $fixtureRoot -SecurityDirectory $securityDirectory -ProductionRoots @('artifacts'))
        Assert-SelfTest -Condition (@($reviewedArtifactFindings | Where-Object {
            $_.Path -ceq 'artifacts/host-publish/Release/CodexQuotaTaskbar.exe' -and $_.Pattern -in @('auth.json', 'chatgpt[.]com/backend-api')
        }).Count -eq 0) -Message 'the exact reviewed executable was rejected'
        Assert-SelfTest -Condition (@($reviewedArtifactFindings | Where-Object {
            $_.Path -ceq 'artifacts/host-publish/Release/Renamed.exe' -and $_.Pattern -ceq 'auth.json'
        }).Count -eq 1) -Message 'a renamed executable received the reviewed artifact allowance'

        $releaseFixtureDirectory = Join-Path $fixtureRoot 'artifacts\releases'
        $null = New-Item -ItemType Directory -Path $releaseFixtureDirectory -Force
        foreach ($zipName in @('CodexQuotaTaskbar-win-x64.zip', 'renamed-host.zip')) {
            $zip = [System.IO.Compression.ZipFile]::Open(
                (Join-Path $releaseFixtureDirectory $zipName),
                [System.IO.Compression.ZipArchiveMode]::Create)
            try {
                Add-ZipTextEntry -Archive $zip -Path 'CodexQuotaTaskbar.exe' -Content 'auth.json'
                Add-ZipTextEntry -Archive $zip -Path 'src/CodexQuotaTaskbar.Host/Provider/CodexAuthCredentialReader.cs' -Content 'auth.json'
            }
            finally { $zip.Dispose() }
        }
        $zipAllowanceFindings = @(Invoke-RepositoryScan -RepositoryRoot $fixtureRoot -SecurityDirectory $securityDirectory -ProductionRoots @('artifacts'))
        Assert-SelfTest -Condition (@($zipAllowanceFindings | Where-Object {
            $_.Path -ceq 'artifacts/releases/CodexQuotaTaskbar-win-x64.zip!/CodexQuotaTaskbar.exe'
        }).Count -eq 0) -Message 'the exact release archive executable was rejected'
        Assert-SelfTest -Condition (@($zipAllowanceFindings | Where-Object {
            $_.Path -ceq 'artifacts/releases/renamed-host.zip!/CodexQuotaTaskbar.exe' -and $_.Pattern -ceq 'auth.json'
        }).Count -eq 1) -Message 'a renamed archive inherited the release allowance'
        Assert-SelfTest -Condition (@($zipAllowanceFindings | Where-Object {
            $_.Path -ceq 'artifacts/releases/CodexQuotaTaskbar-win-x64.zip!/src/CodexQuotaTaskbar.Host/Provider/CodexAuthCredentialReader.cs' -and $_.Pattern -ceq 'auth.json'
        }).Count -eq 1) -Message 'an archived source path inherited the production source allowance'

        $invalidSensitiveDirectory = Join-Path $fixtureRoot 'policy-extra-sensitive-source'
        Write-TestPolicy -SecurityDirectory $invalidSensitiveDirectory
        [System.IO.File]::AppendAllLines(
            (Join-Path $invalidSensitiveDirectory 'allowed-production-sensitive-access.txt'),
            [string[]]@('src/SecondReader.cs|auth.json'))
        $extraSensitiveAllowanceRejected = $false
        try { $null = Read-BoundaryPolicy -RepositoryRoot $fixtureRoot -SecurityDirectory $invalidSensitiveDirectory }
        catch { $extraSensitiveAllowanceRejected = $true }
        Assert-SelfTest -Condition $extraSensitiveAllowanceRejected -Message 'an unreviewed sensitive-source policy entry was accepted'

        $docsDirectory = Join-Path $fixtureRoot 'docs'
        $null = New-Item -ItemType Directory -Path $docsDirectory -Force
        [System.IO.File]::WriteAllText((Join-Path $docsDirectory 'privacy.md'), 'auth.json')
        $allowedDocFindings = @(Invoke-RepositoryScan -RepositoryRoot $fixtureRoot -SecurityDirectory $securityDirectory -ProductionRoots @('docs'))
        Assert-SelfTest -Condition ($allowedDocFindings.Count -eq 0) -Message 'the exact privacy disclosure was rejected'

        [System.IO.File]::WriteAllText((Join-Path $docsDirectory 'privacy.md'), "auth.json`nTaskbarStats")
        $secondTokenFindings = @(Invoke-RepositoryScan -RepositoryRoot $fixtureRoot -SecurityDirectory $securityDirectory -ProductionRoots @('docs'))
        Assert-SelfTest -Condition ('TaskbarStats' -in $secondTokenFindings.Pattern) -Message 'a second forbidden token in privacy.md was allowed'
        [System.IO.File]::WriteAllText((Join-Path $docsDirectory 'privacy.md'), 'clean')
        [System.IO.File]::WriteAllText((Join-Path $docsDirectory 'renamed.md'), 'auth.json')
        $renamedFindings = @(Invoke-RepositoryScan -RepositoryRoot $fixtureRoot -SecurityDirectory $securityDirectory -ProductionRoots @('docs'))
        Assert-SelfTest -Condition ('auth.json' -in $renamedFindings.Pattern) -Message 'a renamed privacy document received the allowance'
        Remove-Item -LiteralPath (Join-Path $docsDirectory 'renamed.md') -Force

        $harnessDirectory = Join-Path $fixtureRoot 'tools\CodexQuotaTaskbar.FileAccessAudit'
        $null = New-Item -ItemType Directory -Path $harnessDirectory -Force
        [System.IO.File]::WriteAllText((Join-Path $harnessDirectory 'CodexQuotaTaskbar.FileAccessAudit.csproj'), '<Project><PropertyGroup><IsPackable>false</IsPackable></PropertyGroup></Project>')
        [System.IO.File]::WriteAllText((Join-Path $harnessDirectory 'auth.json'), 'sentinel')
        $harnessFindings = @(Invoke-RepositoryScan -RepositoryRoot $fixtureRoot -SecurityDirectory $securityDirectory -ProductionRoots @('tools'))
        Assert-SelfTest -Condition ($harnessFindings.Count -eq 0) -Message 'the exact non-packable source harness was rejected'

        $invalidAllowances = @(
            @('tools/*'),
            @('tools/../CodexQuotaTaskbar.FileAccessAudit'),
            @($script:ExpectedHarnessAllowance, 'tools/AnotherHarness')
        )
        foreach ($invalidAllowance in $invalidAllowances) {
            $invalidSecurityDirectory = Join-Path $fixtureRoot ("policy-{0}" -f [Guid]::NewGuid().ToString('N'))
            Write-TestPolicy -SecurityDirectory $invalidSecurityDirectory -HarnessLines $invalidAllowance
            $rejected = $false
            try {
                $null = @(Invoke-RepositoryScan -RepositoryRoot $fixtureRoot -SecurityDirectory $invalidSecurityDirectory -ProductionRoots @('tools'))
            }
            catch {
                $rejected = $true
            }
            Assert-SelfTest -Condition $rejected -Message "unsafe harness allowance was accepted: $($invalidAllowance -join ', ')"
        }

        $artifactHarnessDirectory = Join-Path $fixtureRoot 'artifacts\tools\CodexQuotaTaskbar.FileAccessAudit'
        $null = New-Item -ItemType Directory -Path $artifactHarnessDirectory -Force
        [System.IO.File]::WriteAllText((Join-Path $artifactHarnessDirectory 'auth.json'), 'sentinel')
        $artifactHarnessFindings = @(Invoke-RepositoryScan -RepositoryRoot $fixtureRoot -SecurityDirectory $securityDirectory -ProductionRoots @('artifacts'))
        Assert-SelfTest -Condition ('auth.json' -in $artifactHarnessFindings.Pattern) -Message 'artifact content received the source harness exemption'
        Assert-SelfTest -Condition ('packaging' -in $artifactHarnessFindings.Kind) -Message 'packaged harness path was not rejected independently'

        $sourceExcludedRoot = Join-Path $fixtureRoot 'src\excluded-project'
        $null = New-Item -ItemType Directory -Path (Join-Path $sourceExcludedRoot 'bin') -Force
        $null = New-Item -ItemType Directory -Path (Join-Path $sourceExcludedRoot 'obj') -Force
        [System.IO.File]::WriteAllText((Join-Path $sourceExcludedRoot 'bin\excluded.txt'), 'AccountManager')
        [System.IO.File]::WriteAllText((Join-Path $sourceExcludedRoot 'obj\excluded.txt'), 'TaskbarStats')
        $sourceExclusionFindings = @(Invoke-RepositoryScan -RepositoryRoot $fixtureRoot -SecurityDirectory $securityDirectory -ProductionRoots @('src'))
        $excludedSourceFindings = @($sourceExclusionFindings | Where-Object { $_.Path -in @('src/excluded-project/bin/excluded.txt', 'src/excluded-project/obj/excluded.txt') })
        Assert-SelfTest -Condition ($excludedSourceFindings.Count -eq 0) -Message 'source bin/obj directories were scanned'

        $artifactPackageRoot = Join-Path $fixtureRoot 'artifacts\package'
        $null = New-Item -ItemType Directory -Path (Join-Path $artifactPackageRoot 'bin') -Force
        $null = New-Item -ItemType Directory -Path (Join-Path $artifactPackageRoot 'obj') -Force
        [System.IO.File]::WriteAllText((Join-Path $artifactPackageRoot 'bin\blocked.txt'), 'AccountManager')
        [System.IO.File]::WriteAllText((Join-Path $artifactPackageRoot 'obj\blocked.txt'), 'TaskbarStats')
        [System.IO.File]::WriteAllText((Join-Path $fixtureRoot 'artifacts\CodexQuotaTaskbar.FileAccessAudit.dll'), 'clean artifact output')
        [System.IO.File]::WriteAllText((Join-Path $fixtureRoot 'artifacts\Microsoft.Windows.SDK.NET.dll'), 'AccountManager')
        $artifactBypassFindings = @(Invoke-RepositoryScan -RepositoryRoot $fixtureRoot -SecurityDirectory $securityDirectory -ProductionRoots @('artifacts'))
        $artifactBinFinding = @($artifactBypassFindings | Where-Object { $_.Path -ceq 'artifacts/package/bin/blocked.txt' -and $_.Pattern -ceq 'AccountManager' })
        $artifactObjFinding = @($artifactBypassFindings | Where-Object { $_.Path -ceq 'artifacts/package/obj/blocked.txt' -and $_.Pattern -ceq 'TaskbarStats' })
        $flattenedHarnessFinding = @($artifactBypassFindings | Where-Object { $_.Path -ceq 'artifacts/CodexQuotaTaskbar.FileAccessAudit.dll' -and $_.Kind -ceq 'packaging' })
        $unsignedSdkFinding = @($artifactBypassFindings | Where-Object { $_.Path -ceq 'artifacts/Microsoft.Windows.SDK.NET.dll' -and $_.Pattern -ceq 'AccountManager' })
        Assert-SelfTest -Condition ($artifactBinFinding.Count -eq 1) -Message 'artifact bin content bypassed the scanner'
        Assert-SelfTest -Condition ($artifactObjFinding.Count -eq 1) -Message 'artifact obj content bypassed the scanner'
        Assert-SelfTest -Condition ($flattenedHarnessFinding.Count -eq 1) -Message 'a flattened test-harness DLL was accepted as an artifact'
        Assert-SelfTest -Condition ($unsignedSdkFinding.Count -eq 1) -Message 'an unsigned file named like the Microsoft SDK assembly bypassed the scanner'

        $binaryDirectory = Join-Path $fixtureRoot 'artifacts\binary-test'
        $null = New-Item -ItemType Directory -Path $binaryDirectory -Force
        [System.IO.File]::WriteAllBytes((Join-Path $binaryDirectory 'sample.exe'), [System.Text.Encoding]::ASCII.GetBytes('AccountManager'))
        [System.IO.File]::WriteAllBytes((Join-Path $binaryDirectory 'sample.dll'), [System.Text.Encoding]::Unicode.GetBytes('TaskbarStats'))
        [System.IO.File]::WriteAllBytes((Join-Path $binaryDirectory 'empty.dll'), [byte[]]::new(0))
        [System.IO.File]::WriteAllBytes((Join-Path $binaryDirectory 'short.dll'), [byte[]](0xFF, 0xFE, 0xFD))
        $alignmentCases = @(
            [pscustomobject]@{ Name = 'utf16le'; Encoding = [System.Text.UnicodeEncoding]::new($false, $false, $false); Offsets = 0..1 }
            [pscustomobject]@{ Name = 'utf16be'; Encoding = [System.Text.UnicodeEncoding]::new($true, $false, $false); Offsets = 0..1 }
            [pscustomobject]@{ Name = 'utf32le'; Encoding = [System.Text.UTF32Encoding]::new($false, $false, $false); Offsets = 0..3 }
            [pscustomobject]@{ Name = 'utf32be'; Encoding = [System.Text.UTF32Encoding]::new($true, $false, $false); Offsets = 0..3 }
        )
        $alignmentFixturePaths = @()
        foreach ($alignmentCase in $alignmentCases) {
            foreach ($offset in $alignmentCase.Offsets) {
                $encoded = $alignmentCase.Encoding.GetBytes('AccountManager')
                $prefixed = [byte[]]::new($offset + $encoded.Length)
                for ($prefixIndex = 0; $prefixIndex -lt $offset; $prefixIndex++) {
                    $prefixed[$prefixIndex] = 0xFF
                }
                [Array]::Copy($encoded, 0, $prefixed, $offset, $encoded.Length)
                $fixtureName = "{0}-offset-{1}.dll" -f $alignmentCase.Name, $offset
                [System.IO.File]::WriteAllBytes((Join-Path $binaryDirectory $fixtureName), $prefixed)
                $alignmentFixturePaths += "artifacts/binary-test/$fixtureName"
            }
        }
        $splitCases = @(
            [pscustomobject]@{ Name = 'utf8'; Encoding = [System.Text.UTF8Encoding]::new($false, $false) }
            [pscustomobject]@{ Name = 'utf16le'; Encoding = [System.Text.UnicodeEncoding]::new($false, $false, $false) }
            [pscustomobject]@{ Name = 'utf16be'; Encoding = [System.Text.UnicodeEncoding]::new($true, $false, $false) }
            [pscustomobject]@{ Name = 'utf32le'; Encoding = [System.Text.UTF32Encoding]::new($false, $false, $false) }
            [pscustomobject]@{ Name = 'utf32be'; Encoding = [System.Text.UTF32Encoding]::new($true, $false, $false) }
        )
        $splitFixturePaths = @()
        foreach ($splitCase in $splitCases) {
            $encoded = $splitCase.Encoding.GetBytes('AccountManager')
            $tokenStart = 65536 - [Math]::Max(1, [int]($encoded.Length / 2))
            $splitBytes = [byte[]]::new($tokenStart + $encoded.Length)
            [Array]::Copy($encoded, 0, $splitBytes, $tokenStart, $encoded.Length)
            $fixtureName = "split-{0}.dll" -f $splitCase.Name
            [System.IO.File]::WriteAllBytes((Join-Path $binaryDirectory $fixtureName), $splitBytes)
            $splitFixturePaths += "artifacts/binary-test/$fixtureName"
        }
        $binaryFindings = @(Invoke-RepositoryScan -RepositoryRoot $fixtureRoot -SecurityDirectory $securityDirectory -ProductionRoots @('artifacts'))
        $asciiUtf8Findings = @($binaryFindings | Where-Object { $_.Path -ceq 'artifacts/binary-test/sample.exe' -and $_.Pattern -ceq 'AccountManager' })
        $alignedUtf16Findings = @($binaryFindings | Where-Object { $_.Path -ceq 'artifacts/binary-test/sample.dll' -and $_.Pattern -ceq 'TaskbarStats' })
        Assert-SelfTest -Condition ($asciiUtf8Findings.Count -eq 1 -and $alignedUtf16Findings.Count -eq 1) -Message 'ASCII/UTF-8 or aligned UTF-16 binary bytes were not scanned exactly once'
        foreach ($fixturePath in $alignmentFixturePaths) {
            $fixtureFindings = @($binaryFindings | Where-Object { $_.Path -ceq $fixturePath -and $_.Pattern -ceq 'AccountManager' })
            Assert-SelfTest -Condition ($fixtureFindings.Count -eq 1) -Message "multibyte binary alignment was missed: $fixturePath"
        }
        foreach ($fixturePath in $splitFixturePaths) {
            $fixtureFindings = @($binaryFindings | Where-Object { $_.Path -ceq $fixturePath -and $_.Pattern -ceq 'AccountManager' })
            Assert-SelfTest -Condition ($fixtureFindings.Count -eq 1) -Message "a chunk-boundary pattern was missed or duplicated: $fixturePath"
        }
        foreach ($shortFixturePath in @('artifacts/binary-test/empty.dll', 'artifacts/binary-test/short.dll')) {
            $shortFixtureFindings = @($binaryFindings | Where-Object { $_.Path -ceq $shortFixturePath })
            Assert-SelfTest -Condition ($shortFixtureFindings.Count -eq 0) -Message "an empty or sub-code-unit binary could not be scanned: $shortFixturePath"
        }

        $performancePolicy = Read-BoundaryPolicy -RepositoryRoot $fixtureRoot -SecurityDirectory $securityDirectory
        $performanceBytes = [byte[]]::new(8MB)
        $performanceStream = [System.IO.MemoryStream]::new($performanceBytes, $false)
        try {
            $performanceStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
            $performanceResult = Invoke-BoundedStreamScan -Policy $performancePolicy -Stream $performanceStream `
                -RulePath 'artifacts/binary-test/performance.dll' -DisplayPath 'artifacts/binary-test/performance.dll' `
                -Kind 'content' -MaxBytes $performanceBytes.Length -ExpectedLength $performanceBytes.Length
            $performanceStopwatch.Stop()
        }
        finally {
            $performanceStream.Dispose()
        }
        Assert-SelfTest -Condition ($performanceResult.Findings.Count -eq 0) -Message 'large binary scanner fixture unexpectedly produced a finding'
        Assert-SelfTest -Condition ($performanceStopwatch.ElapsedMilliseconds -lt 10000) `
            -Message ("large binary scanner exceeded the 10 second bounded-scan budget: {0} ms" -f $performanceStopwatch.ElapsedMilliseconds)

        $zipPath = Join-Path $fixtureRoot 'artifacts\documentation.zip'
        $zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            Add-ZipTextEntry -Archive $zip -Path 'docs/privacy.md' -Content 'auth.json'
            Add-ZipTextEntry -Archive $zip -Path 'docs/elsewhere.md' -Content 'auth.json'
            Add-ZipTextEntry -Archive $zip -Path 'opaque-directory/' -Content 'AccountManager'
        }
        finally {
            $zip.Dispose()
        }
        $zipFindings = @(Invoke-RepositoryScan -RepositoryRoot $fixtureRoot -SecurityDirectory $securityDirectory -ProductionRoots @('artifacts'))
        $elsewhereFinding = @($zipFindings | Where-Object { $_.Path -like '*documentation.zip!/docs/elsewhere.md' -and $_.Pattern -ceq 'auth.json' })
        $privacyFinding = @($zipFindings | Where-Object { $_.Path -like '*documentation.zip!/docs/privacy.md' -and $_.Pattern -ceq 'auth.json' })
        $opaqueDirectoryFinding = @($zipFindings | Where-Object { $_.Path -like '*documentation.zip!/opaque-directory/' -and $_.Pattern -ceq 'AccountManager' })
        Assert-SelfTest -Condition ($elsewhereFinding.Count -gt 0) -Message 'the ZIP allowance leaked to another entry'
        Assert-SelfTest -Condition ($privacyFinding.Count -eq 0) -Message 'the exact ZIP privacy disclosure was rejected'
        Assert-SelfTest -Condition ($opaqueDirectoryFinding.Count -eq 1) -Message 'a ZIP directory entry carrying bytes bypassed content scanning'

        $documentationPathCases = @(
            [pscustomobject]@{ FileName = 'trailing-doc.zip'; EntryPath = 'docs/privacy.md/'; ExpectedKind = 'archive-content' }
            [pscustomobject]@{ FileName = 'backslash-doc.zip'; EntryPath = 'docs\privacy.md'; ExpectedKind = 'archive-path' }
            [pscustomobject]@{ FileName = 'double-slash-doc.zip'; EntryPath = 'docs//privacy.md'; ExpectedKind = 'archive-path' }
            [pscustomobject]@{ FileName = 'dot-segment-doc.zip'; EntryPath = 'docs/./privacy.md'; ExpectedKind = 'archive-path' }
        )
        foreach ($pathCase in $documentationPathCases) {
            $pathCaseArchivePath = Join-Path $fixtureRoot "artifacts\$($pathCase.FileName)"
            $pathCaseArchive = [System.IO.Compression.ZipFile]::Open($pathCaseArchivePath, [System.IO.Compression.ZipArchiveMode]::Create)
            try {
                Add-ZipTextEntry -Archive $pathCaseArchive -Path $pathCase.EntryPath -Content 'auth.json'
            }
            finally {
                $pathCaseArchive.Dispose()
            }
        }
        $trueDirectoryPath = Join-Path $fixtureRoot 'artifacts\true-directory.zip'
        $trueDirectoryArchive = [System.IO.Compression.ZipFile]::Open($trueDirectoryPath, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            $null = $trueDirectoryArchive.CreateEntry('docs/')
        }
        finally {
            $trueDirectoryArchive.Dispose()
        }
        $documentationPathFindings = @(Invoke-RepositoryScan -RepositoryRoot $fixtureRoot -SecurityDirectory $securityDirectory -ProductionRoots @('artifacts'))
        foreach ($pathCase in $documentationPathCases) {
            $caseFindings = @($documentationPathFindings | Where-Object {
                $_.Path -like "artifacts/$($pathCase.FileName)!/*" -and $_.Kind -ceq $pathCase.ExpectedKind
            })
            Assert-SelfTest -Condition ($caseFindings.Count -eq 1) -Message "documentation path variant was not rejected: $($pathCase.EntryPath)"
        }
        $trueDirectoryFindings = @($documentationPathFindings | Where-Object { $_.Path -like 'artifacts/true-directory.zip!*' })
        Assert-SelfTest -Condition ($trueDirectoryFindings.Count -eq 0) -Message 'a true zero-length ZIP directory was not skipped cleanly'

        $renamedZipPath = Join-Path $fixtureRoot 'artifacts\renamed-archive.exe'
        $renamedZip = [System.IO.Compression.ZipFile]::Open($renamedZipPath, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            Add-ZipTextEntry -Archive $renamedZip -Path 'payload.txt' -Content 'AccountManager'
        }
        finally {
            $renamedZip.Dispose()
        }

        $innerZipPath = Join-Path $fixtureRoot 'inner.zip'
        $innerZip = [System.IO.Compression.ZipFile]::Open($innerZipPath, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            Add-ZipTextEntry -Archive $innerZip -Path 'payload.txt' -Content 'AccountManager'
        }
        finally {
            $innerZip.Dispose()
        }
        $nestedZipPath = Join-Path $fixtureRoot 'artifacts\nested.zip'
        $nestedZip = [System.IO.Compression.ZipFile]::Open($nestedZipPath, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            Add-ZipBytesEntry -Archive $nestedZip -Path 'payload/inner.zip' -Bytes ([System.IO.File]::ReadAllBytes($innerZipPath))
        }
        finally {
            $nestedZip.Dispose()
        }

        $sfxSourcePath = Join-Path $fixtureRoot 'sfx-source.zip'
        $sfxSource = [System.IO.Compression.ZipFile]::Open($sfxSourcePath, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            Add-ZipTextEntry -Archive $sfxSource -Path 'payload.bin' -Content (('safe-prefix-' * 2048) + 'AccountManager')
        }
        finally {
            $sfxSource.Dispose()
        }
        $sfxPrefix = [byte[]](0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00)
        $prefixedArchivePath = Join-Path $fixtureRoot 'artifacts\prefixed-archive.exe'
        Add-SelfExtractingZipPrefix -InputPath $sfxSourcePath -OutputPath $prefixedArchivePath -Prefix $sfxPrefix
        $prefixedValidation = [System.IO.Compression.ZipFile]::OpenRead($prefixedArchivePath)
        try {
            if ($prefixedValidation.Entries.Count -ne 1) {
                throw 'Self-test SFX ZIP was not readable by .NET.'
            }
            $prefixedReader = [System.IO.StreamReader]::new($prefixedValidation.Entries[0].Open())
            try {
                $prefixedContent = $prefixedReader.ReadToEnd()
            }
            finally {
                $prefixedReader.Dispose()
            }
            if (-not $prefixedContent.Contains('AccountManager', [StringComparison]::Ordinal)) {
                throw 'Self-test SFX ZIP payload could not be read by .NET.'
            }
        }
        finally {
            $prefixedValidation.Dispose()
        }

        $prefixedNestedPath = Join-Path $fixtureRoot 'artifacts\prefixed-nested.zip'
        $prefixedNested = [System.IO.Compression.ZipFile]::Open($prefixedNestedPath, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            Add-ZipBytesEntry -Archive $prefixedNested -Path 'payload/prefixed.bin' -Bytes ([System.IO.File]::ReadAllBytes($prefixedArchivePath))
        }
        finally {
            $prefixedNested.Dispose()
        }

        $overlaySourcePath = Join-Path $fixtureRoot 'overlay-source.zip'
        $overlaySource = [System.IO.Compression.ZipFile]::Open($overlaySourcePath, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            Add-ZipTextEntry -Archive $overlaySource -Path 'payload.bin' -Content (('safe-prefix-' * 2048) + 'AccountManager')
        }
        finally {
            $overlaySource.Dispose()
        }

        $twoByteOverlayPath = Join-Path $fixtureRoot 'artifacts\two-byte-overlay.exe'
        [System.IO.File]::Copy($overlaySourcePath, $twoByteOverlayPath)
        Add-FileOverlay -Path $twoByteOverlayPath -Length 2 -Value 0x4D
        $overlayValidation = [System.IO.Compression.ZipFile]::OpenRead($twoByteOverlayPath)
        try {
            if ($overlayValidation.Entries.Count -ne 1) {
                throw 'Self-test overlay ZIP was not readable by .NET.'
            }
            $overlayReader = [System.IO.StreamReader]::new($overlayValidation.Entries[0].Open())
            try {
                $overlayContent = $overlayReader.ReadToEnd()
            }
            finally {
                $overlayReader.Dispose()
            }
            if (-not $overlayContent.Contains('AccountManager', [StringComparison]::Ordinal)) {
                throw 'Self-test overlay ZIP payload could not be read by .NET.'
            }
        }
        finally {
            $overlayValidation.Dispose()
        }

        $largeOverlayPath = Join-Path $fixtureRoot 'artifacts\large-overlay.exe'
        [System.IO.File]::Copy($overlaySourcePath, $largeOverlayPath)
        Add-FileOverlay -Path $largeOverlayPath -Length 70000 -Value 0x4D

        $fakeEndRecordPath = Join-Path $fixtureRoot 'artifacts\fake-end-record.bin'
        [System.IO.File]::WriteAllText($fakeEndRecordPath, 'ordinary binary prefix', [System.Text.UTF8Encoding]::new($false))
        $fakeEndRecord = [byte[]]::new(22)
        ([byte[]](0x50, 0x4B, 0x05, 0x06)).CopyTo($fakeEndRecord, 0)
        $fakeStream = [System.IO.FileStream]::new($fakeEndRecordPath, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try {
            $fakeStream.Write($fakeEndRecord, 0, $fakeEndRecord.Length)
        }
        finally {
            $fakeStream.Dispose()
        }

        $candidateFloodPath = Join-Path $fixtureRoot 'artifacts\candidate-flood.bin'
        $candidateFloodStream = [System.IO.FileStream]::new($candidateFloodPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try {
            $candidateBytes = [byte[]](0x50, 0x4B, 0x05, 0x06)
            for ($candidateIndex = 0; $candidateIndex -lt 300; $candidateIndex++) {
                $candidateFloodStream.Write($candidateBytes, 0, $candidateBytes.Length)
            }
        }
        finally {
            $candidateFloodStream.Dispose()
        }

        $ambiguousArchivePath = Join-Path $fixtureRoot 'artifacts\ambiguous-candidates.zip'
        [System.IO.File]::Copy($overlaySourcePath, $ambiguousArchivePath)
        $olderCandidateValidation = [System.IO.Compression.ZipFile]::OpenRead($overlaySourcePath)
        try {
            $olderCandidateReader = [System.IO.StreamReader]::new($olderCandidateValidation.Entries[0].Open())
            try {
                $olderCandidateContent = $olderCandidateReader.ReadToEnd()
            }
            finally {
                $olderCandidateReader.Dispose()
            }
            if (-not $olderCandidateContent.Contains('AccountManager', [StringComparison]::Ordinal)) {
                throw 'Self-test older ZIP candidate payload could not be read by .NET.'
            }
        }
        finally {
            $olderCandidateValidation.Dispose()
        }
        $newestCandidateOffset = (Get-Item -LiteralPath $ambiguousArchivePath).Length
        $emptyEndRecord = [byte[]]::new(22)
        ([byte[]](0x50, 0x4B, 0x05, 0x06)).CopyTo($emptyEndRecord, 0)
        [BitConverter]::GetBytes([uint32]$newestCandidateOffset).CopyTo($emptyEndRecord, 16)
        $ambiguousStream = [System.IO.FileStream]::new($ambiguousArchivePath, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try {
            $ambiguousStream.Write($emptyEndRecord, 0, $emptyEndRecord.Length)
        }
        finally {
            $ambiguousStream.Dispose()
        }
        $newestCandidateValidation = [System.IO.Compression.ZipFile]::OpenRead($ambiguousArchivePath)
        try {
            if ($newestCandidateValidation.Entries.Count -ne 0) {
                throw 'Self-test newest empty ZIP candidate was not readable by .NET.'
            }
        }
        finally {
            $newestCandidateValidation.Dispose()
        }

        $overlayNestedPath = Join-Path $fixtureRoot 'artifacts\overlay-nested.zip'
        $overlayNested = [System.IO.Compression.ZipFile]::Open($overlayNestedPath, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            Add-ZipBytesEntry -Archive $overlayNested -Path 'payload/two-byte.bin' -Bytes ([System.IO.File]::ReadAllBytes($twoByteOverlayPath))
            Add-ZipBytesEntry -Archive $overlayNested -Path 'payload/large.bin' -Bytes ([System.IO.File]::ReadAllBytes($largeOverlayPath))
            Add-ZipBytesEntry -Archive $overlayNested -Path 'payload/fake.bin' -Bytes ([System.IO.File]::ReadAllBytes($fakeEndRecordPath))
        }
        finally {
            $overlayNested.Dispose()
        }

        $perEntryBudgetPath = Join-Path $fixtureRoot 'artifacts\per-entry-budget.zip'
        $perEntryBudgetZip = [System.IO.Compression.ZipFile]::Open($perEntryBudgetPath, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            Add-ZipBytesEntry -Archive $perEntryBudgetZip -Path 'payload.bin' -Bytes ([byte[]](0x01))
        }
        finally {
            $perEntryBudgetZip.Dispose()
        }
        Set-ZipDeclaredUncompressedSizes -Path $perEntryBudgetPath -Sizes @([uint32]134217729)

        $totalBudgetPath = Join-Path $fixtureRoot 'artifacts\total-budget.zip'
        $totalBudgetZip = [System.IO.Compression.ZipFile]::Open($totalBudgetPath, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($entryName in @('first.bin', 'second.bin', 'third.bin')) {
                Add-ZipBytesEntry -Archive $totalBudgetZip -Path $entryName -Bytes ([byte[]](0x01))
            }
        }
        finally {
            $totalBudgetZip.Dispose()
        }
        Set-ZipDeclaredUncompressedSizes -Path $totalBudgetPath -Sizes @([uint32]104857600, [uint32]104857600, [uint32]104857600)

        $entryCountPath = Join-Path $fixtureRoot 'artifacts\entry-count.zip'
        $entryCountZip = [System.IO.Compression.ZipFile]::Open($entryCountPath, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            for ($entryIndex = 0; $entryIndex -lt 4097; $entryIndex++) {
                $null = $entryCountZip.CreateEntry(("entries/{0:D4}.txt" -f $entryIndex))
            }
        }
        finally {
            $entryCountZip.Dispose()
        }

        $compressionRatioPath = Join-Path $fixtureRoot 'artifacts\compression-ratio.zip'
        $compressionRatioZip = [System.IO.Compression.ZipFile]::Open($compressionRatioPath, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            Add-ZipRepeatedByteEntry -Archive $compressionRatioZip -Path 'repetitive.bin' -Length 2097152
        }
        finally {
            $compressionRatioZip.Dispose()
        }

        $archiveBoundaryFindings = @(Invoke-RepositoryScan -RepositoryRoot $fixtureRoot -SecurityDirectory $securityDirectory -ProductionRoots @('artifacts'))
        $renamedArchiveFinding = @($archiveBoundaryFindings | Where-Object { $_.Path -ceq 'artifacts/renamed-archive.exe' -and $_.Kind -ceq 'archive-type' })
        $nestedArchiveFinding = @($archiveBoundaryFindings | Where-Object { $_.Path -ceq 'artifacts/nested.zip!/payload/inner.zip' -and $_.Kind -ceq 'archive-depth' })
        $prefixedArchiveFinding = @($archiveBoundaryFindings | Where-Object { $_.Path -ceq 'artifacts/prefixed-archive.exe' -and $_.Kind -ceq 'archive-type' })
        $prefixedNestedFinding = @($archiveBoundaryFindings | Where-Object { $_.Path -ceq 'artifacts/prefixed-nested.zip!/payload/prefixed.bin' -and $_.Kind -ceq 'archive-depth' })
        $twoByteOverlayFinding = @($archiveBoundaryFindings | Where-Object { $_.Path -ceq 'artifacts/two-byte-overlay.exe' -and $_.Kind -ceq 'archive-type' })
        $largeOverlayFinding = @($archiveBoundaryFindings | Where-Object { $_.Path -ceq 'artifacts/large-overlay.exe' -and $_.Kind -ceq 'archive-type' })
        $twoByteNestedFinding = @($archiveBoundaryFindings | Where-Object { $_.Path -ceq 'artifacts/overlay-nested.zip!/payload/two-byte.bin' -and $_.Kind -ceq 'archive-depth' })
        $largeNestedFinding = @($archiveBoundaryFindings | Where-Object { $_.Path -ceq 'artifacts/overlay-nested.zip!/payload/large.bin' -and $_.Kind -ceq 'archive-depth' })
        $fakeEndRecordFindings = @($archiveBoundaryFindings | Where-Object { $_.Path -ceq 'artifacts/fake-end-record.bin' })
        $fakeNestedFindings = @($archiveBoundaryFindings | Where-Object { $_.Path -ceq 'artifacts/overlay-nested.zip!/payload/fake.bin' })
        $candidateFloodFinding = @($archiveBoundaryFindings | Where-Object { $_.Path -ceq 'artifacts/candidate-flood.bin' -and $_.Kind -ceq 'archive-read' })
        $ambiguousCandidateFinding = @($archiveBoundaryFindings | Where-Object { $_.Path -ceq 'artifacts/ambiguous-candidates.zip' -and $_.Kind -ceq 'archive-read' })
        $perEntryBudgetFinding = @($archiveBoundaryFindings | Where-Object { $_.Path -ceq 'artifacts/per-entry-budget.zip!/payload.bin' -and $_.Kind -ceq 'archive-entry-size' })
        $totalBudgetFinding = @($archiveBoundaryFindings | Where-Object { $_.Path -ceq 'artifacts/total-budget.zip!/third.bin' -and $_.Kind -ceq 'archive-total-size' })
        $entryCountFinding = @($archiveBoundaryFindings | Where-Object { $_.Path -ceq 'artifacts/entry-count.zip' -and $_.Kind -ceq 'archive-entry-count' })
        $compressionRatioFinding = @($archiveBoundaryFindings | Where-Object { $_.Path -ceq 'artifacts/compression-ratio.zip!/repetitive.bin' -and $_.Kind -ceq 'archive-compression-ratio' })
        Assert-SelfTest -Condition ($renamedArchiveFinding.Count -eq 1) -Message 'ZIP content renamed to .exe bypassed archive handling'
        Assert-SelfTest -Condition ($nestedArchiveFinding.Count -eq 1) -Message 'a ZIP nested inside a ZIP was accepted as opaque bytes'
        Assert-SelfTest -Condition ($prefixedArchiveFinding.Count -eq 1) -Message 'a prefixed self-extracting ZIP bypassed top-level archive identity checks'
        Assert-SelfTest -Condition ($prefixedNestedFinding.Count -eq 1) -Message 'a prefixed self-extracting ZIP bypassed nested archive identity checks'
        Assert-SelfTest -Condition ($twoByteOverlayFinding.Count -eq 1) -Message 'a .NET-readable ZIP with a two-byte overlay bypassed top-level archive identity checks'
        Assert-SelfTest -Condition ($largeOverlayFinding.Count -eq 1) -Message 'a ZIP beyond the old tail window bypassed top-level archive identity checks'
        Assert-SelfTest -Condition ($twoByteNestedFinding.Count -eq 1) -Message 'a ZIP with a two-byte overlay bypassed nested archive identity checks'
        Assert-SelfTest -Condition ($largeNestedFinding.Count -eq 1) -Message 'a ZIP beyond the old tail window bypassed nested archive identity checks'
        Assert-SelfTest -Condition ($fakeEndRecordFindings.Count -eq 0) -Message 'fake EOCD bytes in a normal binary were treated as a ZIP'
        Assert-SelfTest -Condition ($fakeNestedFindings.Count -eq 0) -Message 'nested fake EOCD bytes were treated as a ZIP'
        Assert-SelfTest -Condition ($candidateFloodFinding.Count -eq 1) -Message 'an excessive EOCD candidate count did not fail closed'
        Assert-SelfTest -Condition ($ambiguousCandidateFinding.Count -eq 1) -Message 'multiple structurally valid ZIP end records did not fail closed'
        Assert-SelfTest -Condition ($perEntryBudgetFinding.Count -eq 1) -Message 'an over-budget archive entry was not rejected'
        Assert-SelfTest -Condition ($totalBudgetFinding.Count -eq 1) -Message 'an archive over the cumulative byte budget was not rejected'
        Assert-SelfTest -Condition ($entryCountFinding.Count -eq 1) -Message 'an archive over the entry-count budget was not rejected'
        Assert-SelfTest -Condition ($compressionRatioFinding.Count -eq 1) -Message 'an archive with an excessive compression ratio was not rejected'

        $stagedPrivacyDirectory = Join-Path $fixtureRoot 'artifacts\staged-package\docs'
        $null = New-Item -ItemType Directory -Path $stagedPrivacyDirectory -Force
        [System.IO.File]::WriteAllText((Join-Path $stagedPrivacyDirectory 'privacy.md'), 'auth.json')
        $stagedFindings = @(Invoke-RepositoryScan -RepositoryRoot $fixtureRoot -SecurityDirectory $securityDirectory -ProductionRoots @('artifacts'))
        $stagedPrivacyFinding = @($stagedFindings | Where-Object { $_.Path -ceq 'artifacts/staged-package/docs/privacy.md' -and $_.Pattern -ceq 'auth.json' })
        Assert-SelfTest -Condition ($stagedPrivacyFinding.Count -eq 0) -Message 'the exact staged-package privacy disclosure was rejected'

        $null = New-Item -ItemType Directory -Path $outsideRoot -Force
        [System.IO.File]::WriteAllText((Join-Path $outsideRoot 'outside.txt'), 'clean')
        $junctionPath = Join-Path $fixtureRoot 'src\escape'
        $junctionCreated = $false
        try {
            $null = New-Item -ItemType Junction -Path $junctionPath -Target $outsideRoot -ErrorAction Stop
            $junctionCreated = $true
        }
        catch {
            $syntheticRejected = Test-IsReparsePoint -Attributes ([System.IO.FileAttributes]::Directory -bor [System.IO.FileAttributes]::ReparsePoint)
            Assert-SelfTest -Condition $syntheticRejected -Message 'reparse behavior seam did not reject reparse attributes'
            Write-Host 'Self-test reparse fixture unavailable; verified the attribute-level rejection seam.'
        }
        if ($junctionCreated) {
            $reparseFindings = @(Invoke-RepositoryScan -RepositoryRoot $fixtureRoot -SecurityDirectory $securityDirectory -ProductionRoots @('src'))
            Assert-SelfTest -Condition ('reparse' -in $reparseFindings.Kind) -Message 'a junction escape was followed or ignored'
        }
    }
    finally {
        if ($junctionCreated -and (Test-Path -LiteralPath $junctionPath)) {
            Remove-Item -LiteralPath $junctionPath -Force
        }
        if (Test-Path -LiteralPath $fixtureRoot) {
            Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
        }
        if (Test-Path -LiteralPath $outsideRoot) {
            Remove-Item -LiteralPath $outsideRoot -Recurse -Force
        }
    }

    if ($script:SelfTestFailures.Count -gt 0) {
        throw ("Sensitive boundary self-test failed:`n - {0}" -f ($script:SelfTestFailures -join "`n - "))
    }
    Write-Host 'Sensitive boundary self-test: PASS'
}
