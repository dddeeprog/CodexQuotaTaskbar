$script:PePolicyProhibitedImports = @('winhttp.dll', 'wininet.dll', 'ws2_32.dll', 'wsock32.dll', 'urlmon.dll')

function Assert-PeByteRange {
    param(
        [Parameter(Mandatory = $true)][byte[]]$Bytes,
        [Parameter(Mandatory = $true)][long]$Offset,
        [Parameter(Mandatory = $true)][long]$Length,
        [Parameter(Mandatory = $true)][string]$Description
    )

    if ($Offset -lt 0 -or $Length -lt 0 -or $Offset -gt $Bytes.LongLength - $Length) {
        throw "Malformed PE while reading $Description."
    }
}

function Read-PeUInt16 {
    param([byte[]]$Bytes, [long]$Offset, [string]$Description)

    Assert-PeByteRange -Bytes $Bytes -Offset $Offset -Length 2 -Description $Description
    return [System.BitConverter]::ToUInt16($Bytes, [int]$Offset)
}

function Read-PeUInt32 {
    param([byte[]]$Bytes, [long]$Offset, [string]$Description)

    Assert-PeByteRange -Bytes $Bytes -Offset $Offset -Length 4 -Description $Description
    return [System.BitConverter]::ToUInt32($Bytes, [int]$Offset)
}

function Read-PeUInt64 {
    param([byte[]]$Bytes, [long]$Offset, [string]$Description)

    Assert-PeByteRange -Bytes $Bytes -Offset $Offset -Length 8 -Description $Description
    return [System.BitConverter]::ToUInt64($Bytes, [int]$Offset)
}

function Get-PeImageMetadata {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    Assert-PeByteRange -Bytes $Bytes -Offset 0 -Length 64 -Description 'DOS header'
    if ($Bytes[0] -ne 0x4D -or $Bytes[1] -ne 0x5A) {
        throw 'PE image does not have an MZ header.'
    }
    $peOffset = [long](Read-PeUInt32 -Bytes $Bytes -Offset 60 -Description 'PE offset')
    Assert-PeByteRange -Bytes $Bytes -Offset $peOffset -Length 24 -Description 'PE header'
    if ((Read-PeUInt32 -Bytes $Bytes -Offset $peOffset -Description 'PE signature') -ne 0x00004550) {
        throw 'PE image signature is invalid.'
    }

    $machine = Read-PeUInt16 -Bytes $Bytes -Offset ($peOffset + 4) -Description 'machine'
    $sectionCount = Read-PeUInt16 -Bytes $Bytes -Offset ($peOffset + 6) -Description 'section count'
    $optionalHeaderSize = Read-PeUInt16 -Bytes $Bytes -Offset ($peOffset + 20) -Description 'optional header size'
    if ($sectionCount -eq 0 -or $sectionCount -gt 96) {
        throw 'PE image section count is invalid.'
    }

    $optionalOffset = $peOffset + 24
    Assert-PeByteRange -Bytes $Bytes -Offset $optionalOffset -Length $optionalHeaderSize -Description 'optional header'
    $magic = Read-PeUInt16 -Bytes $Bytes -Offset $optionalOffset -Description 'optional header magic'
    if ($magic -eq 0x10B) {
        $dataDirectoryRelativeOffset = 96
        $numberOfRvaAndSizesRelativeOffset = 92
        $minimumOptionalHeaderSize = 112
    }
    elseif ($magic -eq 0x20B) {
        $dataDirectoryRelativeOffset = 112
        $numberOfRvaAndSizesRelativeOffset = 108
        $minimumOptionalHeaderSize = 128
    }
    else {
        throw 'PE image is not PE32 or PE32+.'
    }
    if ($optionalHeaderSize -lt $minimumOptionalHeaderSize) {
        throw 'PE image optional header does not declare a complete import directory.'
    }

    $optionalHeaderEnd = $optionalOffset + $optionalHeaderSize
    $dataDirectoryOffset = $optionalOffset + $dataDirectoryRelativeOffset
    if ($dataDirectoryOffset + 16 -gt $optionalHeaderEnd) {
        throw 'PE image import directory escapes the declared optional header.'
    }
    $numberOfRvaAndSizes = Read-PeUInt32 -Bytes $Bytes -Offset ($optionalOffset + $numberOfRvaAndSizesRelativeOffset) `
        -Description 'NumberOfRvaAndSizes'
    if ($numberOfRvaAndSizes -lt 2 -or $numberOfRvaAndSizes -gt 32) {
        throw 'PE image does not declare a supported import data-directory count.'
    }

    $sectionTableOffset = $optionalOffset + $optionalHeaderSize
    Assert-PeByteRange -Bytes $Bytes -Offset $sectionTableOffset -Length ([long]$sectionCount * 40) -Description 'section table'
    $sizeOfHeaders = Read-PeUInt32 -Bytes $Bytes -Offset ($optionalOffset + 60) -Description 'SizeOfHeaders'
    if ($sizeOfHeaders -le 0 -or $sizeOfHeaders -gt $Bytes.LongLength) {
        throw 'PE image SizeOfHeaders is invalid.'
    }

    return [pscustomobject]@{
        Bytes = $Bytes
        Machine = $machine
        Magic = $magic
        SizeOfHeaders = $sizeOfHeaders
        SectionTableOffset = $sectionTableOffset
        SectionCount = $sectionCount
        OptionalHeaderEnd = $optionalHeaderEnd
        DataDirectoryOffset = $dataDirectoryOffset
        NumberOfRvaAndSizes = $numberOfRvaAndSizes
    }
}

function Get-PeDataDirectory {
    param(
        [Parameter(Mandatory = $true)]$Image,
        [Parameter(Mandatory = $true)][int]$Index
    )

    if ($Index -lt 0 -or $Index -ge $Image.NumberOfRvaAndSizes) {
        return $null
    }
    $offset = [long]$Image.DataDirectoryOffset + ($Index * 8)
    if ($offset + 8 -gt $Image.OptionalHeaderEnd) {
        throw 'PE image data directory escapes the declared optional header.'
    }
    return [pscustomobject]@{
        Rva = Read-PeUInt32 -Bytes $Image.Bytes -Offset $offset -Description "data directory $Index RVA"
        Size = Read-PeUInt32 -Bytes $Image.Bytes -Offset ($offset + 4) -Description "data directory $Index size"
    }
}

function Convert-PeRvaToOffset {
    param(
        [Parameter(Mandatory = $true)]$Image,
        [Parameter(Mandatory = $true)][uint32]$Rva
    )

    if ($Rva -lt $Image.SizeOfHeaders) {
        Assert-PeByteRange -Bytes $Image.Bytes -Offset $Rva -Length 1 -Description 'header RVA'
        return [long]$Rva
    }

    for ($index = 0; $index -lt $Image.SectionCount; $index++) {
        $offset = [long]$Image.SectionTableOffset + ($index * 40)
        Assert-PeByteRange -Bytes $Image.Bytes -Offset $offset -Length 40 -Description 'section table'
        $virtualSize = Read-PeUInt32 -Bytes $Image.Bytes -Offset ($offset + 8) -Description 'section virtual size'
        $virtualAddress = Read-PeUInt32 -Bytes $Image.Bytes -Offset ($offset + 12) -Description 'section virtual address'
        $rawSize = Read-PeUInt32 -Bytes $Image.Bytes -Offset ($offset + 16) -Description 'section raw size'
        $rawOffset = Read-PeUInt32 -Bytes $Image.Bytes -Offset ($offset + 20) -Description 'section raw offset'
        $span = [Math]::Max([uint64]$virtualSize, [uint64]$rawSize)
        if ($span -gt 0 -and [uint64]$Rva -ge [uint64]$virtualAddress -and
            [uint64]$Rva -lt ([uint64]$virtualAddress + $span)) {
            $result = [uint64]$rawOffset + ([uint64]$Rva - [uint64]$virtualAddress)
            if ($result -gt [uint64][Int32]::MaxValue) {
                throw 'PE RVA resolves outside supported file bounds.'
            }
            Assert-PeByteRange -Bytes $Image.Bytes -Offset ([long]$result) -Length 1 -Description 'section RVA'
            return [long]$result
        }
    }

    throw 'PE RVA does not map to a section.'
}

function Read-PeAsciiNullTerminated {
    param(
        [Parameter(Mandatory = $true)]$Image,
        [Parameter(Mandatory = $true)][uint32]$Rva,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $offset = Convert-PeRvaToOffset -Image $Image -Rva $Rva
    $nameBytes = [System.Collections.Generic.List[byte]]::new()
    for ($index = 0; $index -lt 260; $index++) {
        Assert-PeByteRange -Bytes $Image.Bytes -Offset ($offset + $index) -Length 1 -Description $Description
        $value = $Image.Bytes[$offset + $index]
        if ($value -eq 0) {
            break
        }
        if ($value -lt 0x20 -or $value -gt 0x7E) {
            throw "PE $Description contains invalid characters."
        }
        $nameBytes.Add($value)
    }
    if ($nameBytes.Count -eq 0 -or $nameBytes.Count -ge 260) {
        throw "PE $Description is malformed."
    }
    return [System.Text.Encoding]::ASCII.GetString($nameBytes.ToArray()).ToLowerInvariant()
}

function Get-NormalImportDescriptors {
    param([Parameter(Mandatory = $true)]$Image)

    $directory = Get-PeDataDirectory -Image $Image -Index 1
    if ($null -eq $directory) {
        throw 'PE image does not declare a normal import directory.'
    }
    if ($directory.Rva -eq 0 -and $directory.Size -eq 0) {
        return @()
    }
    if ($directory.Rva -eq 0 -or $directory.Size -lt 20 -or $directory.Size -gt 1048576 -or
        ($directory.Size % 20) -ne 0) {
        throw 'PE normal import directory is malformed.'
    }

    $offset = Convert-PeRvaToOffset -Image $Image -Rva $directory.Rva
    $descriptors = [System.Collections.Generic.List[object]]::new()
    $count = [int]($directory.Size / 20)
    for ($index = 0; $index -lt $count; $index++) {
        $descriptorOffset = $offset + ($index * 20)
        Assert-PeByteRange -Bytes $Image.Bytes -Offset $descriptorOffset -Length 20 -Description 'normal import descriptor'
        $values = @(0..4 | ForEach-Object { Read-PeUInt32 -Bytes $Image.Bytes -Offset ($descriptorOffset + ($_ * 4)) -Description 'normal import descriptor field' })
        if (@($values | Where-Object { $_ -ne 0 }).Count -eq 0) {
            return $descriptors.ToArray()
        }
        $nameRva = [uint32]$values[3]
        if ($nameRva -eq 0) {
            throw 'PE normal import descriptor has no name RVA.'
        }
        $descriptors.Add([pscustomobject]@{
                Module = Read-PeAsciiNullTerminated -Image $Image -Rva $nameRva -Description 'normal import name'
                LookupTableRva = [uint32]$values[0]
                AddressTableRva = [uint32]$values[4]
                DelayLoad = $false
            })
    }

    throw 'PE normal import table does not terminate.'
}

function Get-NormalImportNames {
    param([Parameter(Mandatory = $true)]$Image)

    return @((Get-NormalImportDescriptors -Image $Image) | ForEach-Object { $_.Module })
}

function Get-DelayImportDescriptors {
    param([Parameter(Mandatory = $true)]$Image)

    $directory = Get-PeDataDirectory -Image $Image -Index 13
    if ($null -eq $directory -or ($directory.Rva -eq 0 -and $directory.Size -eq 0)) {
        return @()
    }
    if ($directory.Rva -eq 0 -or $directory.Size -lt 32 -or $directory.Size -gt 1048576 -or
        ($directory.Size % 32) -ne 0) {
        throw 'PE delay-load import directory is malformed.'
    }

    $offset = Convert-PeRvaToOffset -Image $Image -Rva $directory.Rva
    $descriptors = [System.Collections.Generic.List[object]]::new()
    $count = [int]($directory.Size / 32)
    for ($index = 0; $index -lt $count; $index++) {
        $descriptorOffset = $offset + ($index * 32)
        Assert-PeByteRange -Bytes $Image.Bytes -Offset $descriptorOffset -Length 32 -Description 'delay-load import descriptor'
        $values = @(0..7 | ForEach-Object { Read-PeUInt32 -Bytes $Image.Bytes -Offset ($descriptorOffset + ($_ * 4)) -Description 'delay-load import descriptor field' })
        if (@($values | Where-Object { $_ -ne 0 }).Count -eq 0) {
            return $descriptors.ToArray()
        }
        if ($values[0] -ne 1 -or $values[1] -eq 0) {
            throw 'PE delay-load import descriptor uses an unsupported or malformed format.'
        }
        $descriptors.Add([pscustomobject]@{
                Module = Read-PeAsciiNullTerminated -Image $Image -Rva ([uint32]$values[1]) -Description 'delay-load import name'
                LookupTableRva = [uint32]$values[4]
                AddressTableRva = [uint32]$values[3]
                DelayLoad = $true
            })
    }

    throw 'PE delay-load import table does not terminate.'
}

function Get-DelayImportNames {
    param([Parameter(Mandatory = $true)]$Image)

    return @((Get-DelayImportDescriptors -Image $Image) | ForEach-Object { $_.Module })
}

function Get-PeImportThunkNames {
    param(
        [Parameter(Mandatory = $true)]$Image,
        [Parameter(Mandatory = $true)][uint32]$LookupTableRva,
        [Parameter(Mandatory = $true)][string]$Description
    )

    if ($LookupTableRva -eq 0) {
        return @()
    }
    $offset = Convert-PeRvaToOffset -Image $Image -Rva $LookupTableRva
    $names = [System.Collections.Generic.List[string]]::new()
    if ($Image.Magic -eq 0x20B) {
        $thunkSize = 8
        $ordinalFlag = [uint64]::Parse(
            '8000000000000000',
            [System.Globalization.NumberStyles]::AllowHexSpecifier,
            [System.Globalization.CultureInfo]::InvariantCulture)
    }
    elseif ($Image.Magic -eq 0x10B) {
        $thunkSize = 4
        $ordinalFlag = [uint64]::Parse(
            '80000000',
            [System.Globalization.NumberStyles]::AllowHexSpecifier,
            [System.Globalization.CultureInfo]::InvariantCulture)
    }
    else {
        throw 'PE image uses an unsupported import thunk format.'
    }
    for ($index = 0; $index -lt 65536; $index++) {
        $thunkOffset = $offset + ($index * $thunkSize)
        $value = if ($thunkSize -eq 8) {
            Read-PeUInt64 -Bytes $Image.Bytes -Offset $thunkOffset -Description "$Description thunk"
        }
        else {
            [uint64](Read-PeUInt32 -Bytes $Image.Bytes -Offset $thunkOffset -Description "$Description thunk")
        }
        if ($value -eq 0) {
            return $names.ToArray()
        }
        if (($value -band $ordinalFlag) -ne 0) {
            $names.Add(('#' + [uint16]($value -band [uint64]0xFFFF)))
            continue
        }
        if ($value -gt [uint64][uint32]::MaxValue -or $value -gt ([uint64][uint32]::MaxValue - 2)) {
            throw "PE $Description thunk has an invalid import-by-name RVA."
        }
        $nameRva = [uint32]$value
        $nameOffset = Convert-PeRvaToOffset -Image $Image -Rva $nameRva
        Assert-PeByteRange -Bytes $Image.Bytes -Offset $nameOffset -Length 2 -Description "$Description import hint"
        $names.Add((Read-PeAsciiNullTerminated -Image $Image -Rva ([uint32]([uint64]$nameRva + 2)) -Description "$Description import name"))
    }

    throw "PE $Description thunk table does not terminate."
}

function Get-PeImportEntries {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    $image = Get-PeImageMetadata -Bytes $Bytes
    $entries = [System.Collections.Generic.List[object]]::new()
    $descriptors = @(
        @(Get-NormalImportDescriptors -Image $image) +
        @(Get-DelayImportDescriptors -Image $image))
    foreach ($descriptor in $descriptors) {
        if ($descriptor.LookupTableRva -eq 0 -and $descriptor.AddressTableRva -eq 0) {
            throw "PE import descriptor has no thunk table: $($descriptor.Module)"
        }
        $lookupRva = if ($descriptor.LookupTableRva -ne 0) {
            [uint32]$descriptor.LookupTableRva
        }
        else {
            [uint32]$descriptor.AddressTableRva
        }
        foreach ($name in (Get-PeImportThunkNames -Image $image -LookupTableRva $lookupRva `
                -Description ($descriptor.Module + ' import'))) {
            $entries.Add([pscustomobject]@{
                    Module = [string]$descriptor.Module
                    Name = [string]$name
                    DelayLoad = [bool]$descriptor.DelayLoad
                })
        }
    }
    return $entries.ToArray()
}

function Assert-ExactPeImportEntries {
    param(
        [Parameter(Mandatory = $true)][byte[]]$Bytes,
        [Parameter(Mandatory = $true)][string[]]$ExpectedEntries,
        [uint16]$ExpectedMachine = 0x8664
    )

    $null = Assert-ApprovedPeImage -Bytes $Bytes -ExpectedMachine $ExpectedMachine
    $expected = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($entry in $ExpectedEntries) {
        if ([string]::IsNullOrWhiteSpace($entry) -or -not $expected.Add($entry)) {
            throw 'PE import policy contains an invalid or duplicate expected entry.'
        }
    }
    $actual = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($entry in (Get-PeImportEntries -Bytes $Bytes)) {
        $normalized = ('{0}!{1}|delay={2}' -f $entry.Module, $entry.Name,
            ([bool]$entry.DelayLoad).ToString().ToLowerInvariant())
        if (-not $actual.Add($normalized)) {
            throw "PE image imports a duplicate function entry: $normalized"
        }
        if (-not $expected.Contains($normalized)) {
            throw "PE image imports an unapproved function entry: $normalized"
        }
    }
    if ($actual.Count -ne $expected.Count) {
        $missing = @($expected | Where-Object { -not $actual.Contains($_) })
        throw ('PE image is missing required function import entries: {0}' -f ($missing -join ', '))
    }
}

function Get-PeImportNames {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    $image = Get-PeImageMetadata -Bytes $Bytes
    return @(
        (@(Get-NormalImportNames -Image $image) + @(Get-DelayImportNames -Image $image)) |
            Where-Object { $null -ne $_ }
    )
}

function Assert-ApprovedPeImage {
    param(
        [Parameter(Mandatory = $true)][byte[]]$Bytes,
        [uint16]$ExpectedMachine = 0x8664,
        [string[]]$ProhibitedImports = $script:PePolicyProhibitedImports
    )

    $image = Get-PeImageMetadata -Bytes $Bytes
    if ($image.Machine -ne $ExpectedMachine) {
        throw ('PE machine 0x{0:x4} does not match required 0x{1:x4}.' -f $image.Machine, $ExpectedMachine)
    }
    if ($ExpectedMachine -eq 0x8664 -and $image.Magic -ne 0x20B) {
        throw 'A required x64 PE image must use the PE32+ optional-header format.'
    }
    foreach ($import in @((Get-NormalImportNames -Image $image) + (Get-DelayImportNames -Image $image))) {
        if ($import -in $ProhibitedImports) {
            throw "PE image imports prohibited library: $import"
        }
    }
    return $image
}

function Assert-ApprovedPeFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [uint16]$ExpectedMachine = 0x8664,
        [string[]]$ProhibitedImports = $script:PePolicyProhibitedImports,
        [long]$MaximumBytes = 67108864
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "PE image file is missing: $Path"
    }
    $file = Get-Item -LiteralPath $Path -Force
    if (($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $file.Length -le 0 -or $file.Length -gt $MaximumBytes) {
        throw "PE image file metadata is invalid: $Path"
    }
    return Assert-ApprovedPeImage -Bytes ([System.IO.File]::ReadAllBytes($file.FullName)) `
        -ExpectedMachine $ExpectedMachine -ProhibitedImports $ProhibitedImports
}
