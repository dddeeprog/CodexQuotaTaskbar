[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$installer = Join-Path $repositoryRoot 'artifacts\installers\CodexQuotaTaskbar-Setup-win-x64.exe'
$payload = Join-Path $repositoryRoot 'artifacts\installer-publish\Release\CodexQuotaTaskbar.exe'
$registryPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{ADC74E3C-B465-4C0A-9D0D-3D428A2AE5F7}_is1'
if (Test-Path -LiteralPath $registryPath) { throw 'An installed copy exists; smoke test refuses to modify it.' }
if (Get-Process -Name CodexQuotaTaskbar -ErrorAction SilentlyContinue) { throw 'Exit the quota application before this test.' }
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw 'Build the installer first.' }

$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$testId = [Guid]::NewGuid().ToString('N')
$testRoot = [IO.Path]::GetFullPath((Join-Path $artifactsRoot "installer-smoke-$testId"))
$installDirectory = Join-Path $testRoot 'installed'
if (-not $testRoot.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Test directory escapes artifacts.'
}
New-Item -ItemType Directory -Path $testRoot -ErrorAction Stop | Out-Null
$groupName = "CodexQuotaTaskbar Install Test $testId"
$groupDirectory = Join-Path ([Environment]::GetFolderPath('Programs')) $groupName
$settingsPath = Join-Path $env:LOCALAPPDATA 'CodexQuotaTaskbar\settings.json'
$settingsHashBefore = if (Test-Path -LiteralPath $settingsPath) { (Get-FileHash -LiteralPath $settingsPath).Hash } else { $null }
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$autostartBefore = (Get-ItemProperty -LiteralPath $runKey -ErrorAction SilentlyContinue).CodexQuotaTaskbar

function Invoke-InstallerProcess([string]$Executable, [string]$Arguments) {
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(45000)) { throw "Installer timed out; process $($process.Id) requires inspection." }
    return $process.ExitCode
}

$common = '/VERYSILENT /SUPPRESSMSGBOXES /SP- /NORESTART /TASKS=""'
$target = "/DIR=`"$installDirectory`" /GROUP=`"$groupName`""
$uninstaller = Join-Path $installDirectory 'unins000.exe'
try {
    # A running application's mutex must stop installation without killing it.
    $mutex = [Threading.Mutex]::new($false, 'Local\CodexQuotaTaskbar.Host')
    try {
        $blocked = Invoke-InstallerProcess $installer "$common $target /LOG=`"$testRoot\running.log`""
        if ($blocked -eq 0 -or (Test-Path -LiteralPath $registryPath)) { throw 'Running application guard failed.' }
    }
    finally { $mutex.Dispose() }

    foreach ($pass in 1..2) {
        $exitCode = Invoke-InstallerProcess $installer "$common $target /LOG=`"$testRoot\install-$pass.log`""
        if ($exitCode -ne 0) { throw "Install pass $pass failed: $exitCode" }
        $registration = Get-ItemProperty -LiteralPath $registryPath
        if ([IO.Path]::GetFullPath($registration.InstallLocation).TrimEnd('\') -ne $installDirectory) {
            throw 'Install registry points outside the isolated test target.'
        }
        if ($registration.DisplayVersion -ne (Get-Content -LiteralPath (Join-Path $repositoryRoot 'VERSION') -Raw).Trim()) {
            throw 'Uninstall entry version mismatch.'
        }
        $installedExe = Join-Path $installDirectory 'CodexQuotaTaskbar.exe'
        if ((Get-FileHash -LiteralPath $installedExe).Hash -ne (Get-FileHash -LiteralPath $payload).Hash) {
            throw 'Installed application does not match the self-contained payload.'
        }
        foreach ($file in 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'unins000.exe') {
            if (-not (Test-Path -LiteralPath (Join-Path $installDirectory $file))) { throw "Missing installed file: $file" }
        }
        $shortcut = Join-Path $groupDirectory '点岛.lnk'
        if (-not (Test-Path -LiteralPath $shortcut)) { throw 'Start menu shortcut is missing.' }
        $shell = New-Object -ComObject WScript.Shell
        if ($shell.CreateShortcut($shortcut).TargetPath -ne $installedExe) { throw 'Shortcut target mismatch.' }
    }
}
finally {
    if (Test-Path -LiteralPath $uninstaller) {
        $registration = Get-ItemProperty -LiteralPath $registryPath -ErrorAction Stop
        if ([IO.Path]::GetFullPath($registration.InstallLocation).TrimEnd('\') -ne $installDirectory) {
            throw 'Refusing to uninstall outside the isolated target.'
        }
        $exitCode = Invoke-InstallerProcess $uninstaller "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=`"$testRoot\uninstall.log`""
        if ($exitCode -ne 0) { throw "Uninstall failed: $exitCode" }
    }
}
if ((Test-Path -LiteralPath $registryPath) -or (Test-Path -LiteralPath $groupDirectory) -or
    (Test-Path -LiteralPath (Join-Path $installDirectory 'CodexQuotaTaskbar.exe'))) {
    throw 'Installed application, shortcut, or uninstall entry remains.'
}
$settingsHashAfter = if (Test-Path -LiteralPath $settingsPath) { (Get-FileHash -LiteralPath $settingsPath).Hash } else { $null }
if ($settingsHashBefore -ne $settingsHashAfter) { throw 'Installer changed existing personal settings.' }
if ($autostartBefore -ne (Get-ItemProperty -LiteralPath $runKey -ErrorAction SilentlyContinue).CodexQuotaTaskbar) {
    throw 'Installer changed an unrelated autostart entry.'
}
Write-Host 'PASS: running guard, install, reinstall, payload, version, shortcuts, uninstall, preserved settings/autostart.'
Write-Host "Logs retained: $testRoot"
