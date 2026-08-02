[CmdletBinding()]
param(
    [string]$ManifestPath,
    [string]$StagingRoot,
    [string]$BridgePayloadPath,
    [string]$BridgePayloadSha256Path
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Core') {
    throw 'Managed assembly policy requires PowerShell 7 or newer; it must not downgrade to Windows PowerShell.'
}
Add-Type -AssemblyName System.Reflection.Metadata

$script:ExpectedApplicationAssemblyNames = @(
    'CodexQuotaTaskbar.CompatibilityProbe.dll',
    'CodexQuotaTaskbar.BridgeControl.dll',
    'CodexQuotaTaskbar.Core.dll'
)
$script:ExpectedApplicationAssemblySimpleNames = @(
    'CodexQuotaTaskbar.CompatibilityProbe',
    'CodexQuotaTaskbar.BridgeControl',
    'CodexQuotaTaskbar.Core'
)
$script:ExpectedBridgeResourceNames = @(
    'CodexQuotaTaskbar.CompatibilityProbe.Payload.CodexQuotaTaskbar.Bridge.dll',
    'CodexQuotaTaskbar.CompatibilityProbe.Payload.CodexQuotaTaskbar.Bridge.sha256'
)
$script:ApprovedStagedOpaqueRelativePaths = [System.Collections.Generic.HashSet[string]]::new(
    [string[]]@(
        'CodexQuotaTaskbar.CompatibilityProbe.deps.json',
        'CodexQuotaTaskbar.CompatibilityProbe.runtimeconfig.json'
    ),
    [System.StringComparer]::Ordinal)
$script:ApprovedSingleFileHost = [pscustomobject]@{
    RelativePath = 'CodexQuotaTaskbar.CompatibilityProbe.exe'
    FileName = 'singlefilehost.exe'
    Size = 9982464
    Sha256 = 'a7eb510e9a85d1dc26970bcca9d8bc4435b06b42e5ee631e567132b645cfe034'
}
$script:TrustedFrameworkPublicKeyTokens = [System.Collections.Generic.HashSet[string]]::new(
    [string[]]@(
        'b03f5f7f11d50a3a',
        '7cec85d7bea7798e',
        'cc7b13ffcd2ddd51',
        'b77a5c561934e089',
        '31bf3856ad364e35'
    ),
    [System.StringComparer]::Ordinal)
$script:TrustedPlatformAssemblyNames = [System.Collections.Generic.HashSet[string]]::new(
    [string[]]@(
        'Accessibility',
        'DirectWriteForwarder',
        'mscorlib',
        'netstandard',
        'PresentationCore',
        'PresentationFramework',
        'PresentationUI',
        'ReachFramework',
        'System.Xaml',
        'UIAutomationClient',
        'UIAutomationClientSideProviders',
        'UIAutomationProvider',
        'UIAutomationTypes',
        'WindowsBase',
        'WindowsFormsIntegration'
    ),
    [System.StringComparer]::Ordinal)
$script:AllowedPInvokeImports = @{
    'CodexQuotaTaskbar.CompatibilityProbe' = [System.Collections.Generic.HashSet[string]]::new(
        [string[]]@(
            'user32.dll!EnumWindows',
            'user32.dll!EnumDisplayMonitors',
            'user32.dll!GetMonitorInfo',
            'user32.dll!GetClassName',
            'user32.dll!GetWindowThreadProcessId',
            'user32.dll!GetShellWindow',
            'user32.dll!MonitorFromWindow',
            'user32.dll!GetWindowRect',
            'user32.dll!GetDpiForWindow',
            'user32.dll!SetThreadDpiAwarenessContext',
            'kernel32.dll!QueryFullProcessImageName',
            'kernel32.dll!IsWow64Process2',
            'advapi32.dll!OpenProcessToken',
            'kernel32.dll!GetFileInformationByHandle',
            'kernel32.dll!GetFinalPathNameByHandle',
            'user32.dll!GetClassNameW',
            'user32.dll!SendMessageTimeoutW',
            'ntdll.dll!RtlGetVersion'
        ),
        [System.StringComparer]::OrdinalIgnoreCase)
    'CodexQuotaTaskbar.BridgeControl' = [System.Collections.Generic.HashSet[string]]::new(
        [string[]]@(
            'kernel32.dll!OpenProcess',
            'kernel32.dll!ProcessIdToSessionId',
            'kernel32.dll!GetProcessTimes',
            'kernel32.dll!IsWow64Process2',
            'kernel32.dll!QueryFullProcessImageName',
            'advapi32.dll!OpenProcessToken',
            'advapi32.dll!GetTokenInformation',
            'advapi32.dll!ConvertSidToStringSid',
            'kernel32.dll!LocalFree',
            'kernel32.dll!K32EnumProcessModulesEx',
            'kernel32.dll!K32GetModuleInformation',
            'kernel32.dll!K32GetModuleFileNameExW',
            'kernel32.dll!GetModuleFileName',
            'kernel32.dll!GetCurrentProcess',
            'kernel32.dll!LoadLibraryEx',
            'kernel32.dll!GetProcAddress',
            'kernel32.dll!GetModuleHandleExW',
            'kernel32.dll!FreeLibrary',
            'kernel32.dll!VirtualAllocEx',
            'kernel32.dll!VirtualFreeEx',
            'kernel32.dll!WriteProcessMemory',
            'kernel32.dll!CreateRemoteThread',
            'kernel32.dll!OpenEvent',
            'kernel32.dll!SetEvent',
            'kernel32.dll!WaitForSingleObject',
            'kernel32.dll!GetExitCodeThread'
        ),
        [System.StringComparer]::OrdinalIgnoreCase)
    'CodexQuotaTaskbar.Core' = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
}
$script:ForbiddenMemberReferences = [System.Collections.Generic.HashSet[string]]::new(
    [string[]]@(
        'System.Diagnostics.Process!Kill',
        'System.Diagnostics.Process!Start',
        'System.Reflection.Assembly!Load',
        'System.Reflection.Assembly!LoadFile',
        'System.Reflection.Assembly!LoadFrom',
        'System.Reflection.Assembly!UnsafeLoadFrom',
        'System.Reflection.Assembly!LoadWithPartialName',
        'System.Runtime.Loader.AssemblyLoadContext!LoadFromAssemblyPath',
        'System.Runtime.Loader.AssemblyLoadContext!LoadFromStream',
        'System.Runtime.Loader.AssemblyLoadContext!LoadFromAssemblyName',
        'System.Runtime.InteropServices.NativeLibrary!Load',
        'System.Runtime.InteropServices.NativeLibrary!TryLoad',
        'System.Type!GetType',
        'System.Type!GetTypeFromProgID',
        'System.Activator!CreateInstance',
        'Microsoft.Win32.Registry!SetValue',
        'Microsoft.Win32.RegistryKey!CreateSubKey',
        'Microsoft.Win32.RegistryKey!SetValue',
        'Microsoft.Win32.RegistryKey!DeleteSubKey',
        'Microsoft.Win32.RegistryKey!DeleteSubKeyTree',
        'Microsoft.Win32.RegistryKey!DeleteValue'
    ),
    [System.StringComparer]::Ordinal)
$script:ForbiddenLiteralPatterns = @(
    [regex]::new('(?i)\b(?:winhttp|wininet|ws2_32|wsock32|urlmon)[.]dll\b', [Text.RegularExpressions.RegexOptions]::CultureInvariant),
    [regex]::new('(?i)\b(?:https?://|schtasks\b|Schedule[.]Service\b|StartupApproved\b|CurrentVersion[\\/]Run\b)', [Text.RegularExpressions.RegexOptions]::CultureInvariant)
)
$script:IlOpcodeMap = @{}
foreach ($field in [System.Reflection.Emit.OpCodes].GetFields([System.Reflection.BindingFlags]'Public,Static')) {
    $opcode = [System.Reflection.Emit.OpCode]$field.GetValue($null)
    if ($opcode.Size -eq 0) {
        continue
    }
    $value = [int]$opcode.Value
    if ($value -lt 0) {
        $value += 65536
    }
    $script:IlOpcodeMap[[uint16]$value] = $opcode
}

function Get-NormalizedFullPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    try {
        return [System.IO.Path]::GetFullPath($Path)
    }
    catch {
        throw "Managed policy received an invalid path: $Path"
    }
}

function Test-PathIsWithinRoot {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$RootPath
    )

    $fullPath = (Get-NormalizedFullPath -Path $Path).TrimEnd('\', '/')
    $fullRoot = (Get-NormalizedFullPath -Path $RootPath).TrimEnd('\', '/')
    return $fullPath.Equals($fullRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
        $fullPath.StartsWith($fullRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)
}

function Assert-NoReparseAncestors {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$RootPath
    )

    $root = (Get-NormalizedFullPath -Path $RootPath).TrimEnd('\', '/')
    $current = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    while ($null -ne $current) {
        if (($current.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Managed policy does not allow reparse points: $($current.FullName)"
        }
        if ($current.FullName.TrimEnd('\', '/').Equals($root, [System.StringComparison]::OrdinalIgnoreCase)) {
            return
        }
        $current = if ($current -is [System.IO.FileInfo]) { $current.Directory } else { $current.Parent }
    }

    throw "Managed policy path escapes its staging root: $Path"
}

function Assert-RegularFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$StagingRoot
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Managed policy input is missing: $Path"
    }
    $file = Get-Item -LiteralPath $Path -Force
    if ($file -isnot [System.IO.FileInfo] -or
        ($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $file.Length -le 0 -or $file.Length -gt 536870912) {
        throw "Managed policy input is not a regular bounded file: $Path"
    }
    if (Test-PathIsWithinRoot -Path $file.FullName -RootPath $StagingRoot) {
        Assert-NoReparseAncestors -Path $file.FullName -RootPath $StagingRoot
    }
    return $file
}

function Get-Sha256Hex {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString($algorithm.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
    }
}

function Get-Sha256FileHex {
    param([Parameter(Mandatory = $true)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Test-ApprovedStagedOpaqueInput {
    param(
        [Parameter(Mandatory = $true)][System.IO.FileInfo]$File,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    if ($script:ApprovedStagedOpaqueRelativePaths.Contains($RelativePath)) {
        return $true
    }
    $singleFileHost = $script:ApprovedSingleFileHost
    return $RelativePath -ceq $singleFileHost.RelativePath -and
        $File.Name -ceq $singleFileHost.FileName -and
        $File.Length -eq $singleFileHost.Size -and
        (Get-Sha256FileHex -Path $File.FullName) -ceq $singleFileHost.Sha256
}

function Get-PublicKeyToken {
    param([Parameter(Mandatory = $true)][AllowEmptyCollection()][byte[]]$PublicKey)

    if ($PublicKey.Length -eq 0) {
        return ''
    }
    $algorithm = [System.Security.Cryptography.SHA1]::Create()
    try {
        $hash = $algorithm.ComputeHash($PublicKey)
        $tokenBytes = [byte[]]::new(8)
        for ($index = 0; $index -lt 8; $index++) {
            $tokenBytes[$index] = $hash[$hash.Length - 1 - $index]
        }
        return ([System.BitConverter]::ToString($tokenBytes)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
    }
}

function Get-AssemblyIdentity {
    param([Parameter(Mandatory = $true)][System.Reflection.Metadata.MetadataReader]$Reader)

    $definition = $Reader.GetAssemblyDefinition()
    return [pscustomobject]@{
        Name = $Reader.GetString($definition.Name)
        PublicKeyToken = Get-PublicKeyToken -PublicKey ([byte[]]$Reader.GetBlobBytes($definition.PublicKey))
    }
}

function Test-TrustedFrameworkAssembly {
    param([Parameter(Mandatory = $true)]$Identity)

    $isFrameworkName = $Identity.Name -eq 'System' -or
        $Identity.Name.StartsWith('System.', [System.StringComparison]::Ordinal) -or
        $Identity.Name -eq 'Microsoft.CSharp' -or
        $Identity.Name -eq 'Microsoft.VisualBasic' -or
        $Identity.Name.StartsWith('Microsoft.VisualBasic.', [System.StringComparison]::Ordinal) -or
        $Identity.Name.StartsWith('Microsoft.Win32.', [System.StringComparison]::Ordinal) -or
        $script:TrustedPlatformAssemblyNames.Contains($Identity.Name)
    return $isFrameworkName -and $script:TrustedFrameworkPublicKeyTokens.Contains($Identity.PublicKeyToken)
}

function Get-QualifiedTypeName {
    param(
        [Parameter(Mandatory = $true)][System.Reflection.Metadata.MetadataReader]$Reader,
        [Parameter(Mandatory = $true)][System.Reflection.Metadata.EntityHandle]$Handle
    )

    switch ($Handle.Kind.ToString()) {
        'TypeReference' {
            [System.Reflection.Metadata.TypeReferenceHandle]$typedHandle = $Handle
            $type = $Reader.GetTypeReference($typedHandle)
            $namespace = $Reader.GetString($type.Namespace)
            $name = $Reader.GetString($type.Name)
            if ([string]::IsNullOrEmpty($namespace)) {
                return $name
            }
            return $namespace + '.' + $name
        }
        'TypeDefinition' {
            [System.Reflection.Metadata.TypeDefinitionHandle]$typedHandle = $Handle
            $type = $Reader.GetTypeDefinition($typedHandle)
            $namespace = $Reader.GetString($type.Namespace)
            $name = $Reader.GetString($type.Name)
            if ([string]::IsNullOrEmpty($namespace)) {
                return $name
            }
            return $namespace + '.' + $name
        }
        'MethodDefinition' {
            [System.Reflection.Metadata.MethodDefinitionHandle]$methodHandle = $Handle
            $method = $Reader.GetMethodDefinition($methodHandle)
            [System.Reflection.Metadata.EntityHandle]$declaringType = $method.GetDeclaringType()
            return Get-QualifiedTypeName -Reader $Reader -Handle $declaringType
        }
        default {
            return ''
        }
    }
}

function Get-MemberReferenceSignature {
    param(
        [Parameter(Mandatory = $true)][System.Reflection.Metadata.MetadataReader]$Reader,
        [Parameter(Mandatory = $true)][System.Reflection.Metadata.MemberReferenceHandle]$Handle
    )

    $member = $Reader.GetMemberReference($Handle)
    [System.Reflection.Metadata.EntityHandle]$parent = $member.Parent
    $typeName = Get-QualifiedTypeName -Reader $Reader -Handle $parent
    if ([string]::IsNullOrEmpty($typeName)) {
        return ''
    }
    return $typeName + '!' + $Reader.GetString($member.Name)
}

function Get-MetadataTokenSignature {
    param(
        [Parameter(Mandatory = $true)][System.Reflection.Metadata.MetadataReader]$Reader,
        [Parameter(Mandatory = $true)][uint32]$Token
    )

    try {
        [System.Reflection.Metadata.EntityHandle]$handle = [System.Reflection.Metadata.Ecma335.MetadataTokens]::EntityHandle([int]$Token)
        switch ($handle.Kind.ToString()) {
            'MemberReference' {
                [System.Reflection.Metadata.MemberReferenceHandle]$memberHandle = $handle
                return Get-MemberReferenceSignature -Reader $Reader -Handle $memberHandle
            }
            'MethodDefinition' {
                [System.Reflection.Metadata.MethodDefinitionHandle]$methodHandle = $handle
                $method = $Reader.GetMethodDefinition($methodHandle)
                [System.Reflection.Metadata.EntityHandle]$parent = $method.GetDeclaringType()
                $typeName = Get-QualifiedTypeName -Reader $Reader -Handle $parent
                if ([string]::IsNullOrEmpty($typeName)) {
                    return ''
                }
                return $typeName + '!' + $Reader.GetString($method.Name)
            }
            default {
                return ''
            }
        }
    }
    catch {
        throw 'Managed policy encountered an invalid IL metadata token.'
    }
}

function Get-ILInstructions {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    $offset = 0
    while ($offset -lt $Bytes.Length) {
        $instructionOffset = $offset
        $firstByte = [int]$Bytes[$offset]
        $offset++
        $opcodeKey = [uint16]$firstByte
        if ($firstByte -eq 0xFE) {
            if ($offset -ge $Bytes.Length) {
                throw 'Managed policy encountered a truncated two-byte IL opcode.'
            }
            $opcodeKey = [uint16](0xFE00 -bor [int]$Bytes[$offset])
            $offset++
        }
        if (-not $script:IlOpcodeMap.ContainsKey($opcodeKey)) {
            throw 'Managed policy encountered an unknown IL opcode.'
        }
        $opcode = $script:IlOpcodeMap[$opcodeKey]
        $operandOffset = $offset
        $operandLength = switch ($opcode.OperandType.ToString()) {
            'InlineNone' { 0 }
            'ShortInlineBrTarget' { 1 }
            'ShortInlineI' { 1 }
            'ShortInlineVar' { 1 }
            'InlineVar' { 2 }
            'InlineI' { 4 }
            'InlineBrTarget' { 4 }
            'InlineField' { 4 }
            'InlineMethod' { 4 }
            'InlineSig' { 4 }
            'InlineString' { 4 }
            'InlineTok' { 4 }
            'InlineType' { 4 }
            'ShortInlineR' { 4 }
            'InlineI8' { 8 }
            'InlineR' { 8 }
            'InlineSwitch' {
                if ($operandOffset + 4 -gt $Bytes.Length) {
                    throw 'Managed policy encountered a truncated IL switch operand.'
                }
                $caseCount = [System.BitConverter]::ToInt32($Bytes, $operandOffset)
                if ($caseCount -lt 0 -or $caseCount -gt 65536) {
                    throw 'Managed policy encountered an invalid IL switch operand.'
                }
                4 + (4 * $caseCount)
            }
            default { throw "Managed policy does not support IL operand type $($opcode.OperandType)." }
        }
        if ($operandOffset + $operandLength -gt $Bytes.Length) {
            throw 'Managed policy encountered a truncated IL operand.'
        }
        $operand = if ($operandLength -eq 4) { [System.BitConverter]::ToUInt32($Bytes, $operandOffset) } else { $null }
        $offset += $operandLength
        [pscustomobject]@{
            Offset = $instructionOffset
            Name = $opcode.Name
            Operand = $operand
        }
    }
}

function Get-ILIntegerConstant {
    param([Parameter(Mandatory = $true)]$Instruction)

    switch ($Instruction.Name) {
        'ldc.i4.m1' { return -1 }
        'ldc.i4.0' { return 0 }
        'ldc.i4.1' { return 1 }
        'ldc.i4.2' { return 2 }
        'ldc.i4.3' { return 3 }
        'ldc.i4.4' { return 4 }
        'ldc.i4.5' { return 5 }
        'ldc.i4.6' { return 6 }
        'ldc.i4.7' { return 7 }
        'ldc.i4.8' { return 8 }
        'ldc.i4.s' { return [int][sbyte]($Instruction.Operand -band 0xFF) }
        'ldc.i4' { return [System.BitConverter]::ToInt32([System.BitConverter]::GetBytes([uint32]$Instruction.Operand), 0) }
        default { return $null }
    }
}

function Assert-MethodIlPolicy {
    param(
        [Parameter(Mandatory = $true)][System.Reflection.Metadata.MetadataReader]$Reader,
        [Parameter(Mandatory = $true)][System.Reflection.PortableExecutable.PEReader]$PeReader,
        [Parameter(Mandatory = $true)][System.Reflection.Metadata.MethodDefinitionHandle]$MethodHandle,
        [Parameter(Mandatory = $true)][string]$AssemblyName
    )

    $method = $Reader.GetMethodDefinition($MethodHandle)
    if ($method.RelativeVirtualAddress -eq 0) {
        return
    }
    $body = [System.Reflection.Metadata.PEReaderExtensions]::GetMethodBody($PeReader, $method.RelativeVirtualAddress)
    $instructions = @(Get-ILInstructions -Bytes ([byte[]]$body.GetILBytes()))
    $recentIntegerConstants = [System.Collections.Generic.List[int]]::new()
    foreach ($instruction in $instructions) {
        $constant = Get-ILIntegerConstant -Instruction $instruction
        if ($null -ne $constant) {
            $recentIntegerConstants.Add([int]$constant)
        }
        if ($instruction.Name -eq 'ldstr') {
            if (($instruction.Operand -band 0xFF000000) -ne 0x70000000) {
                throw 'Managed policy encountered an invalid IL user-string token.'
            }
            try {
                [System.Reflection.Metadata.UserStringHandle]$stringHandle = [System.Reflection.Metadata.Ecma335.MetadataTokens]::UserStringHandle([int]$instruction.Operand)
                $literal = $Reader.GetUserString($stringHandle)
            }
            catch {
                throw 'Managed policy could not decode an IL user string.'
            }
            foreach ($pattern in $script:ForbiddenLiteralPatterns) {
                if ($pattern.IsMatch($literal)) {
                    throw "Managed policy rejected a forbidden literal in $AssemblyName."
                }
            }
        }
        if ($instruction.Name -in @('call', 'callvirt', 'newobj')) {
            $signature = Get-MetadataTokenSignature -Reader $Reader -Token ([uint32]$instruction.Operand)
            if ($script:ForbiddenMemberReferences.Contains($signature)) {
                throw "Managed policy rejected forbidden managed API $signature in $AssemblyName."
            }
            if ($signature -eq 'System.Environment!GetFolderPath' -and
                (@($recentIntegerConstants | Where-Object { $_ -in @(7, 24) }).Count -gt 0)) {
                throw "Managed policy rejected a Startup-folder request in $AssemblyName."
            }
            $recentIntegerConstants.Clear()
        }
        elseif ($instruction.Name -notmatch '^ldc[.]i4') {
            if ($recentIntegerConstants.Count -gt 16) {
                $recentIntegerConstants.RemoveRange(0, $recentIntegerConstants.Count - 16)
            }
        }
    }
}

function Get-EmbeddedManifestResourceBytes {
    param(
        [Parameter(Mandatory = $true)][System.Reflection.Metadata.MetadataReader]$Reader,
        [Parameter(Mandatory = $true)][System.Reflection.PortableExecutable.PEReader]$PeReader,
        [Parameter(Mandatory = $true)][string]$ResourceName
    )

    $matching = @()
    foreach ($handle in $Reader.ManifestResources) {
        $resource = $Reader.GetManifestResource($handle)
        if (($Reader.GetString($resource.Name)) -ceq $ResourceName) {
            $matching += $resource
        }
    }
    if ($matching.Count -ne 1 -or -not $matching[0].Implementation.IsNil) {
        throw "Managed policy did not find exactly one embedded resource: $ResourceName"
    }
    $corHeader = $PeReader.PEHeaders.CorHeader
    if ($null -eq $corHeader -or $corHeader.ResourcesDirectory.RelativeVirtualAddress -le 0) {
        throw 'Managed policy assembly does not contain an embedded managed-resource section.'
    }
    $resourceBlock = $PeReader.GetSectionData($corHeader.ResourcesDirectory.RelativeVirtualAddress)
    $offset = [int]$matching[0].Offset
    if ($offset -lt 0 -or $offset + 4 -gt $resourceBlock.Length) {
        throw 'Managed policy found an invalid embedded resource offset.'
    }
    $lengthContent = $resourceBlock.GetContent($offset, 4)
    $lengthPrefix = [byte[]]($lengthContent.AsMemory().ToArray())
    $length = [System.BitConverter]::ToInt32($lengthPrefix, 0)
    if ($length -le 0 -or $length -gt 134217728 -or $offset + 4 + $length -gt $resourceBlock.Length) {
        throw 'Managed policy found an invalid embedded resource length.'
    }
    $payloadContent = $resourceBlock.GetContent($offset + 4, $length)
    $payload = [byte[]]($payloadContent.AsMemory().ToArray())
    return ,$payload
}

function Assert-ApplicationAssemblyPolicy {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedSimpleName,
        [Parameter(Mandatory = $true)][string]$StagingRoot,
        [string]$BridgePayloadPath,
        [string]$BridgePayloadSha256Path
    )

    $file = Assert-RegularFile -Path $Path -StagingRoot $StagingRoot
    $stream = [System.IO.File]::Open($file.FullName, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    try {
        $peReader = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            if (-not $peReader.HasMetadata) {
                throw "Managed policy expected a managed application assembly: $($file.Name)"
            }
            $reader = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($peReader)
            $identity = Get-AssemblyIdentity -Reader $reader
            if ($identity.Name -cne $ExpectedSimpleName) {
                throw "Managed policy assembly identity does not match expected application assembly: $($file.Name)"
            }
            foreach ($typeHandle in $reader.TypeReferences) {
                $type = $reader.GetTypeReference($typeHandle)
                $namespace = $reader.GetString($type.Namespace)
                $name = $reader.GetString($type.Name)
                $qualifiedName = if ([string]::IsNullOrEmpty($namespace)) { $name } else { $namespace + '.' + $name }
                if ($qualifiedName -eq 'System.Runtime.InteropServices.NativeLibrary' -or
                    $qualifiedName -eq 'System.Web.HttpRequest' -or
                    $namespace -eq 'System.Net' -or
                    $namespace.StartsWith('System.Net.', [System.StringComparison]::Ordinal)) {
                    throw "Managed policy rejected network or dynamic-loader type $qualifiedName in $($file.Name)."
                }
            }
            $allowedPInvokeImports = $script:AllowedPInvokeImports[$identity.Name]
            if ($null -eq $allowedPInvokeImports) {
                throw "Managed policy has no P/Invoke allowlist for $($identity.Name)."
            }
            foreach ($methodHandle in $reader.MethodDefinitions) {
                $method = $reader.GetMethodDefinition($methodHandle)
                if (($method.Attributes -band [System.Reflection.MethodAttributes]::PinvokeImpl) -ne 0) {
                    $import = $method.GetImport()
                    $module = $reader.GetModuleReference($import.Module)
                    $importKey = ($reader.GetString($module.Name)) + '!' + ($reader.GetString($import.Name))
                    if (-not $allowedPInvokeImports.Contains($importKey)) {
                        throw "Managed policy rejected P/Invoke $importKey in $($file.Name)."
                    }
                }
                Assert-MethodIlPolicy -Reader $reader -PeReader $peReader -MethodHandle $methodHandle -AssemblyName $identity.Name
            }
            if ($identity.Name -ceq 'CodexQuotaTaskbar.CompatibilityProbe' -and
                -not [string]::IsNullOrWhiteSpace($BridgePayloadPath)) {
                $snapshotBridge = Assert-RegularFile -Path $BridgePayloadPath -StagingRoot $StagingRoot
                $snapshotDigest = Assert-RegularFile -Path $BridgePayloadSha256Path -StagingRoot $StagingRoot
                $declaredDigest = [System.IO.File]::ReadAllText($snapshotDigest.FullName, [System.Text.UTF8Encoding]::new($false))
                if ($declaredDigest -notmatch '^[0-9a-f]{64}$') {
                    throw 'Managed policy bridge SHA-256 sidecar must be a pure lowercase digest.'
                }
                $actualDigest = Get-Sha256FileHex -Path $snapshotBridge.FullName
                if ($actualDigest -cne $declaredDigest) {
                    throw 'Managed policy bridge SHA-256 sidecar does not match the staged bridge.'
                }
                $embeddedBridge = Get-EmbeddedManifestResourceBytes -Reader $reader -PeReader $peReader -ResourceName $script:ExpectedBridgeResourceNames[0]
                $embeddedDigestBytes = Get-EmbeddedManifestResourceBytes -Reader $reader -PeReader $peReader -ResourceName $script:ExpectedBridgeResourceNames[1]
                $embeddedDigest = [System.Text.UTF8Encoding]::new($false, $true).GetString($embeddedDigestBytes)
                if ($embeddedDigest -notmatch '^[0-9a-f]{64}$' -or $embeddedDigest -cne $actualDigest -or
                    (Get-Sha256Hex -Bytes $embeddedBridge) -cne $actualDigest) {
                    throw 'Managed policy embedded bridge payload does not match the verified staging snapshot.'
                }
            }
        }
        finally {
            $peReader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Test-IsManagedAssembly {
    param([Parameter(Mandatory = $true)][string]$Path)

    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    try {
        $peReader = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            if (-not $peReader.HasMetadata) {
                return $null
            }
            $reader = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($peReader)
            return Get-AssemblyIdentity -Reader $reader
        }
        finally {
            $peReader.Dispose()
        }
    }
    catch [System.BadImageFormatException] {
        return $null
    }
    finally {
        $stream.Dispose()
    }
}

function Read-BundleAuditManifest {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$StagingRoot
    )

    $manifest = Assert-RegularFile -Path $Path -StagingRoot $StagingRoot
    $inputs = @()
    $intermediates = @()
    $resources = @()
    $lines = @([System.IO.File]::ReadAllLines($manifest.FullName, [System.Text.UTF8Encoding]::new($false, $true)))
    if ($lines.Count -eq 0) {
        throw 'Managed policy bundle manifest is empty.'
    }
    foreach ($line in $lines) {
        $parts = $line.Split('|')
        if ($parts.Count -ne 3 -or [string]::IsNullOrWhiteSpace($parts[1])) {
            throw 'Managed policy bundle manifest contains an invalid record.'
        }
        switch ($parts[0]) {
            'input' {
                if ([string]::IsNullOrWhiteSpace($parts[2])) {
                    throw 'Managed policy bundle input is missing its relative path.'
                }
                $inputs += [pscustomobject]@{ Path = Get-NormalizedFullPath -Path $parts[1]; RelativePath = $parts[2] }
            }
            'intermediate' {
                $intermediates += Get-NormalizedFullPath -Path $parts[1]
            }
            'resource' {
                if ([string]::IsNullOrWhiteSpace($parts[2])) {
                    throw 'Managed policy resource record is incomplete.'
                }
                $resources += [pscustomobject]@{ Path = Get-NormalizedFullPath -Path $parts[1]; LogicalName = $parts[2] }
            }
            default { throw 'Managed policy bundle manifest contains an unknown record kind.' }
        }
    }
    return [pscustomobject]@{ Inputs = @($inputs); Intermediates = @($intermediates); Resources = @($resources) }
}

function Assert-ManagedBundleInputPolicy {
    param(
        [Parameter(Mandatory = $true)][string]$ManifestPath,
        [Parameter(Mandatory = $true)][string]$StagingRoot,
        [string]$BridgePayloadPath,
        [string]$BridgePayloadSha256Path
    )

    $staging = Get-NormalizedFullPath -Path $StagingRoot
    if (-not (Test-Path -LiteralPath $staging -PathType Container)) {
        throw 'Managed policy staging root is missing.'
    }
    Assert-NoReparseAncestors -Path $staging -RootPath $staging
    $manifest = Read-BundleAuditManifest -Path $ManifestPath -StagingRoot $staging
    $singleFileHostInputs = @($manifest.Inputs | Where-Object {
            $_.RelativePath -ceq $script:ApprovedSingleFileHost.RelativePath
        })
    if ($singleFileHostInputs.Count -ne 1) {
        throw 'Managed policy bundle manifest must contain exactly one single-file host input.'
    }
    $singleFileHostInput = $singleFileHostInputs[0]
    $singleFileHostFile = Assert-RegularFile -Path $singleFileHostInput.Path -StagingRoot $staging
    if (-not (Test-PathIsWithinRoot -Path $singleFileHostFile.FullName -RootPath $staging) -or
        -not (Test-ApprovedStagedOpaqueInput -File $singleFileHostFile -RelativePath $singleFileHostInput.RelativePath)) {
        throw 'Managed policy single-file host is not the approved isolated SDK output.'
    }
    $seenRelativePaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $seenApplicationAssemblies = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::Ordinal)
    foreach ($input in $manifest.Inputs) {
        if (-not $seenRelativePaths.Add($input.RelativePath)) {
            throw "Managed policy bundle manifest has a duplicate relative path: $($input.RelativePath)"
        }
        $file = Assert-RegularFile -Path $input.Path -StagingRoot $staging
        if ($input.RelativePath -ceq $script:ApprovedSingleFileHost.RelativePath) {
            continue
        }
        $identity = Test-IsManagedAssembly -Path $file.FullName
        if ($null -eq $identity) {
            if ((Test-PathIsWithinRoot -Path $file.FullName -RootPath $staging) -and
                -not (Test-ApprovedStagedOpaqueInput -File $file -RelativePath $input.RelativePath)) {
                throw "Managed policy rejected an untrusted native or opaque staging bundle input: $($file.Name)"
            }
            continue
        }
        $isExpectedApplicationAssembly = $script:ExpectedApplicationAssemblySimpleNames -ccontains $identity.Name
        if ($isExpectedApplicationAssembly) {
            $expectedFileName = $identity.Name + '.dll'
            if ($file.Name -cne $expectedFileName -or $input.RelativePath -cne $expectedFileName -or
                -not (Test-PathIsWithinRoot -Path $file.FullName -RootPath $staging)) {
                throw "Managed policy application bundle input is not an isolated exact entry: $($file.FullName)"
            }
            if ($seenApplicationAssemblies.ContainsKey($identity.Name)) {
                throw "Managed policy bundle has a duplicate application assembly: $($identity.Name)"
            }
            $seenApplicationAssemblies.Add($identity.Name, $file.FullName)
            continue
        }
        if (-not (Test-TrustedFrameworkAssembly -Identity $identity)) {
            throw "Managed policy rejected an unapproved managed bundle dependency: $($identity.Name)"
        }
    }
    foreach ($expectedSimpleName in $script:ExpectedApplicationAssemblySimpleNames) {
        if (-not $seenApplicationAssemblies.ContainsKey($expectedSimpleName)) {
            throw "Managed policy bundle is missing the application assembly: $expectedSimpleName"
        }
    }

    if ($manifest.Intermediates.Count -gt 0) {
        if ($manifest.Intermediates.Count -ne 1 -or -not (Test-PathIsWithinRoot -Path $manifest.Intermediates[0] -RootPath $staging)) {
            throw 'Managed policy intermediate assembly record is not an isolated singleton.'
        }
        $probeAssembly = $seenApplicationAssemblies['CodexQuotaTaskbar.CompatibilityProbe']
        if ($manifest.Intermediates[0] -cne $probeAssembly) {
            throw 'Managed policy bundle does not use the recorded isolated probe intermediate assembly.'
        }
    }

    $requireBridgeBinding = -not [string]::IsNullOrWhiteSpace($BridgePayloadPath) -or
        -not [string]::IsNullOrWhiteSpace($BridgePayloadSha256Path)
    if ($requireBridgeBinding) {
        if ([string]::IsNullOrWhiteSpace($BridgePayloadPath) -or [string]::IsNullOrWhiteSpace($BridgePayloadSha256Path)) {
            throw 'Managed policy bridge binding requires both staged bridge inputs.'
        }
        $bridgePath = Get-NormalizedFullPath -Path $BridgePayloadPath
        $bridgeHashPath = Get-NormalizedFullPath -Path $BridgePayloadSha256Path
        $expectedResources = @{
            $script:ExpectedBridgeResourceNames[0] = $bridgePath
            $script:ExpectedBridgeResourceNames[1] = $bridgeHashPath
        }
        if ($manifest.Resources.Count -ne 2) {
            throw 'Managed policy bundle manifest must name exactly two bridge resources.'
        }
        foreach ($resource in $manifest.Resources) {
            if (-not $expectedResources.ContainsKey($resource.LogicalName) -or
                $expectedResources[$resource.LogicalName] -cne $resource.Path -or
                -not (Test-PathIsWithinRoot -Path $resource.Path -RootPath $staging)) {
                throw 'Managed policy bundle resource record does not match the staging snapshot.'
            }
        }
    }

    foreach ($expectedSimpleName in $script:ExpectedApplicationAssemblySimpleNames) {
        $payloadPath = if ($expectedSimpleName -ceq 'CodexQuotaTaskbar.CompatibilityProbe') { $BridgePayloadPath } else { $null }
        $payloadHashPath = if ($expectedSimpleName -ceq 'CodexQuotaTaskbar.CompatibilityProbe') { $BridgePayloadSha256Path } else { $null }
        Assert-ApplicationAssemblyPolicy -Path $seenApplicationAssemblies[$expectedSimpleName] `
            -ExpectedSimpleName $expectedSimpleName -StagingRoot $staging `
            -BridgePayloadPath $payloadPath -BridgePayloadSha256Path $payloadHashPath
    }
}

if (-not [string]::IsNullOrWhiteSpace($ManifestPath) -or -not [string]::IsNullOrWhiteSpace($StagingRoot) -or
    -not [string]::IsNullOrWhiteSpace($BridgePayloadPath) -or -not [string]::IsNullOrWhiteSpace($BridgePayloadSha256Path)) {
    if ([string]::IsNullOrWhiteSpace($ManifestPath) -or [string]::IsNullOrWhiteSpace($StagingRoot)) {
        throw 'Managed policy command mode requires both ManifestPath and StagingRoot.'
    }
    Assert-ManagedBundleInputPolicy -ManifestPath $ManifestPath -StagingRoot $StagingRoot `
        -BridgePayloadPath $BridgePayloadPath -BridgePayloadSha256Path $BridgePayloadSha256Path
}
