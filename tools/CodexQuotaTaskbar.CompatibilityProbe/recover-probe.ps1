[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

function Get-ValidatedActivation {
    param([Parameter(Mandatory = $true)][string]$JournalPath)

    if (-not (Test-Path -LiteralPath $JournalPath)) {
        return $null
    }

    if (-not (Test-Path -LiteralPath $JournalPath -PathType Leaf)) {
        throw [System.IO.InvalidDataException]::new(
            'The probe activation journal is not a regular file.')
    }

    $file = [System.IO.FileInfo]::new($JournalPath)
    if ($file.Length -le 0 -or $file.Length -gt 16384 -or
        (($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw [System.IO.InvalidDataException]::new(
            'The probe activation journal metadata is invalid.')
    }

    $record = [System.IO.File]::ReadAllText($JournalPath) | ConvertFrom-Json -ErrorAction Stop
    $expectedProperties = @(
        'activationId',
        'appVersion',
        'explorerCreationTimeFileTime100ns',
        'explorerProcessId',
        'explorerSignatureSha256',
        'schemaVersion',
        'startedUtc',
        'state',
        'taskbarSignatureSha256',
        'windowsBuild'
    )
    $actualProperties = @(
        $record.PSObject.Properties.Name | Sort-Object
    )
    if (($expectedProperties -join "`n") -cne ($actualProperties -join "`n")) {
        throw [System.IO.InvalidDataException]::new(
            'The probe activation journal schema is invalid.')
    }

    $activationId = [Guid]::ParseExact([string]$record.activationId, 'D')
    $processId = [int]$record.explorerProcessId
    $creationTime = [uint64]$record.explorerCreationTimeFileTime100ns
    $startedUtc = [DateTimeOffset]::Parse(
        [string]$record.startedUtc,
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind)
    if ([int]$record.schemaVersion -ne 1 -or
        [int]$record.windowsBuild -le 0 -or
        [string]$record.appVersion -notmatch '^[0-9A-Za-z._+-]{1,128}$' -or
        [string]$record.explorerSignatureSha256 -notmatch '^[0-9a-f]{64}$' -or
        [string]$record.taskbarSignatureSha256 -notmatch '^[0-9a-f]{64}$' -or
        $processId -le 0 -or
        $creationTime -eq 0 -or
        $activationId -eq [Guid]::Empty -or
        $startedUtc.Offset -ne [TimeSpan]::Zero -or
        [string]$record.state -notin @('Pending', 'Stable', 'Clean', 'Unsafe')) {
        throw [System.IO.InvalidDataException]::new(
            'The probe activation journal values are invalid.')
    }

    return [pscustomobject]@{
        ActivationId = $activationId
        ExplorerProcessId = $processId
        ExplorerCreationTimeFileTime100ns = $creationTime
    }
}

function Get-MatchingExplorer {
    param([Parameter(Mandatory = $true)]$Activation)

    $process = $null
    $matches = $false
    try {
        $process = [System.Diagnostics.Process]::GetProcessById(
            [int]$Activation.ExplorerProcessId)
        if ($process.HasExited) {
            return $null
        }

        $currentSession = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
        if ($process.SessionId -ne $currentSession) {
            return $null
        }

        $actualCreationTime = [uint64]$process.StartTime.ToUniversalTime().ToFileTimeUtc()
        if ($actualCreationTime -ne $Activation.ExplorerCreationTimeFileTime100ns) {
            return $null
        }

        $matches = $true
        return $process
    }
    catch [System.ArgumentException] {
        return $null
    }
    catch [System.InvalidOperationException] {
        return $null
    }
    finally {
        if (-not $matches -and $null -ne $process) {
            $process.Dispose()
        }
    }
}

function Send-KnownShutdown {
    param([Parameter(Mandatory = $true)]$Activation)

    $process = Get-MatchingExplorer -Activation $Activation
    if ($null -eq $process) {
        return 'ExplorerExitedOrReplaced'
    }

    $activationHex = ([System.BitConverter]::ToString(
            $Activation.ActivationId.ToByteArray())).Replace('-', '').ToLowerInvariant()
    $eventPrefix = 'Local\CQTB.Probe.v1.{0:x8}.{1:x16}.{2}' -f `
        [uint32]$Activation.ExplorerProcessId,
        [uint64]$Activation.ExplorerCreationTimeFileTime100ns,
        $activationHex
    $shutdownEventName = $eventPrefix + '.Shutdown'
    $quiescedEventName = $eventPrefix + '.Quiesced'
    $shutdown = $null
    $quiesced = $null

    try {
        try {
            $shutdown = [System.Threading.EventWaitHandle]::OpenExisting($shutdownEventName)
        }
        catch [System.Threading.WaitHandleCannotBeOpenedException] {
            return 'AlreadyDetached'
        }

        try {
            $quiesced = [System.Threading.EventWaitHandle]::OpenExisting($quiescedEventName)
        }
        catch [System.Threading.WaitHandleCannotBeOpenedException] {
            if (-not $shutdown.Set()) {
                throw [System.InvalidOperationException]::new(
                    'The matching probe shutdown event could not be signaled.')
            }

            throw [System.InvalidOperationException]::new(
                'The matching probe quiesced event is unavailable.')
        }

        if (-not $shutdown.Set()) {
            throw [System.InvalidOperationException]::new(
                'The matching probe shutdown event could not be signaled.')
        }

        if (-not $quiesced.WaitOne(10000)) {
            throw [System.TimeoutException]::new(
                'The matching probe did not quiesce within ten seconds.')
        }

        return 'Quiesced'
    }
    finally {
        if ($null -ne $quiesced) {
            $quiesced.Dispose()
        }

        if ($null -ne $shutdown) {
            $shutdown.Dispose()
        }

        $process.Dispose()
    }
}

try {
    $localAppData = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::LocalApplicationData)
    $journalPath = Join-Path $localAppData 'CodexQuotaTaskbar\Probe\activation.json'
    $activation = Get-ValidatedActivation -JournalPath $journalPath
    $shutdownStatus = 'NoKnownActivation'
    if ($null -ne $activation) {
        $shutdownStatus = Send-KnownShutdown -Activation $activation
    }

    Write-Output ('CodexQuotaTaskbar probe recovery request completed: {0}.' -f $shutdownStatus)
}
catch {
    Write-Error `
        ('CodexQuotaTaskbar probe recovery failed: {0}' -f $_.Exception.GetType().Name) `
        -ErrorAction Continue
    exit 1
}
