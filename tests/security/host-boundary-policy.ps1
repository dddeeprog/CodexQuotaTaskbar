[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath
)

$ErrorActionPreference = 'Stop'
$package = [System.IO.Path]::GetFullPath($PackagePath)
if (-not (Test-Path -LiteralPath $package -PathType Leaf) -or [System.IO.Path]::GetExtension($package) -ne '.zip') {
    throw 'Host package must be an existing ZIP file.'
}

Add-Type -AssemblyName System.IO.Compression
$stream = [System.IO.File]::OpenRead($package)
try {
    $archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Read, $false)
    try {
        $allowed = @(
            'CodexQuotaTaskbar.exe',
            'CodexQuotaTaskbar.dll',
            'CodexQuotaTaskbar.deps.json',
            'CodexQuotaTaskbar.runtimeconfig.json',
            'CodexQuotaTaskbar.Core.dll',
            'BlurredBackground.WPF.dll'
        )
        $names = @($archive.Entries | ForEach-Object { $_.FullName })
        if ($names.Count -ne $allowed.Count -or @($names | Where-Object { $_ -notin $allowed }).Count -gt 0) {
            throw 'Production package contains files outside the exact Host allowlist.'
        }

        # The shared Core assembly retains compatibility model type names for the separate
        # diagnostic tool. Exact package filenames above prove those tools are not shipped;
        # binary content scanning focuses on executable injection capabilities.
        $forbidden = @('VirtualAllocEx', 'WriteProcessMemory', 'CreateRemoteThread', 'InitializeXamlDiagnosticsEx')
        foreach ($entry in $archive.Entries) {
            $entryStream = $entry.Open()
            try {
                $memory = [System.IO.MemoryStream]::new()
                $entryStream.CopyTo($memory)
                $bytes = $memory.ToArray()
                $utf8 = [System.Text.Encoding]::UTF8.GetString($bytes)
                $utf16 = [System.Text.Encoding]::Unicode.GetString($bytes)
                foreach ($pattern in $forbidden) {
                    if ($utf8.Contains($pattern, [StringComparison]::OrdinalIgnoreCase) -or
                        $utf16.Contains($pattern, [StringComparison]::OrdinalIgnoreCase)) {
                        throw "Production package contains forbidden injection boundary: $pattern"
                    }
                }
            }
            finally {
                $entryStream.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}
finally {
    $stream.Dispose()
}

Write-Host 'Host zero-injection boundary: PASS'
