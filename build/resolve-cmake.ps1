[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$cmakeCommand = Get-Command cmake.exe -CommandType Application -ErrorAction SilentlyContinue
if ($null -ne $cmakeCommand) {
    $cmakeCommand.Source
    return
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
    throw 'cmake.exe was not found on PATH, and the Visual Studio Installer vswhere.exe is unavailable.'
}

$visualStudioPath = & $vswhere `
    -latest `
    -products * `
    -version '[17.0,18.0)' `
    -requires Microsoft.VisualStudio.Component.VC.CMake.Project `
    -property installationPath

if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($visualStudioPath)) {
    throw 'cmake.exe was not found on PATH or in a compatible Visual Studio 2022 CMake installation.'
}

$cmake = Join-Path $visualStudioPath.Trim() 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
if (-not (Test-Path -LiteralPath $cmake -PathType Leaf)) {
    throw "The compatible Visual Studio 2022 instance does not contain cmake.exe at '$cmake'."
}

$cmake
