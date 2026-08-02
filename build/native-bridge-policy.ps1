$script:NativeBridgeSourceForbiddenPatterns = @(
    'TerminateProcess',
    'NtTerminateProcess',
    'RtlExitUserProcess',
    'ExitProcess',
    'WinExec',
    'CreateProcess(?:A|W)?',
    'CreateRemoteThread(?:Ex)?',
    'OpenProcess',
    'VirtualAllocEx',
    'WriteProcessMemory',
    'ShellExecute(?:A|W|Ex)?',
    'WinHttp',
    'WinINet',
    'WinSock',
    '\bWSA[A-Za-z0-9_]*',
    'URLDownloadToFile',
    'Internet(?:Open|Connect|ReadFile|WriteFile)',
    'https?://',
    'schtasks',
    'StartupApproved',
    'CurrentVersion[\\/]Run',
    'Reg(?:Open|Create|Set|Delete)Key(?:Ex)?',
    'RegSetValue(?:Ex)?'
) | ForEach-Object {
    [pscustomobject]@{
        Pattern = $_
        Regex = [System.Text.RegularExpressions.Regex]::new(
            $_,
            [System.Text.RegularExpressions.RegexOptions]::CultureInvariant -bor
                [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    }
}

# This is the complete import set of the reviewed Release bridge. The one
# TerminateProcess entry is emitted by the MSVC DLL security-failure runtime,
# not by a first-party object; direct calls are blocked by the source and COFF
# object policies below.
$script:NativeBridgeExpectedImportEntries = @(
    'api-ms-win-core-winrt-error-l1-1-1.dll!rooriginatelanguageexception|delay=false',
    'api-ms-win-core-winrt-l1-1-0.dll!rogetactivationfactory|delay=false',
    'api-ms-win-crt-heap-l1-1-0.dll!_callnewh|delay=false',
    'api-ms-win-crt-heap-l1-1-0.dll!free|delay=false',
    'api-ms-win-crt-heap-l1-1-0.dll!malloc|delay=false',
    'api-ms-win-crt-math-l1-1-0.dll!ceilf|delay=false',
    'api-ms-win-crt-runtime-l1-1-0.dll!_cexit|delay=false',
    'api-ms-win-crt-runtime-l1-1-0.dll!_configure_narrow_argv|delay=false',
    'api-ms-win-crt-runtime-l1-1-0.dll!_crt_atexit|delay=false',
    'api-ms-win-crt-runtime-l1-1-0.dll!_errno|delay=false',
    'api-ms-win-crt-runtime-l1-1-0.dll!_execute_onexit_table|delay=false',
    'api-ms-win-crt-runtime-l1-1-0.dll!_initialize_narrow_environment|delay=false',
    'api-ms-win-crt-runtime-l1-1-0.dll!_initialize_onexit_table|delay=false',
    'api-ms-win-crt-runtime-l1-1-0.dll!_initterm|delay=false',
    'api-ms-win-crt-runtime-l1-1-0.dll!_initterm_e|delay=false',
    'api-ms-win-crt-runtime-l1-1-0.dll!_invalid_parameter_noinfo|delay=false',
    'api-ms-win-crt-runtime-l1-1-0.dll!_invoke_watson|delay=false',
    'api-ms-win-crt-runtime-l1-1-0.dll!_register_onexit_function|delay=false',
    'api-ms-win-crt-runtime-l1-1-0.dll!_seh_filter_dll|delay=false',
    'api-ms-win-crt-runtime-l1-1-0.dll!abort|delay=false',
    'api-ms-win-crt-runtime-l1-1-0.dll!terminate|delay=false',
    'api-ms-win-crt-stdio-l1-1-0.dll!__stdio_common_vswprintf_s|delay=false',
    'api-ms-win-crt-string-l1-1-0.dll!iswspace|delay=false',
    'api-ms-win-crt-string-l1-1-0.dll!strlen|delay=false',
    'api-ms-win-crt-string-l1-1-0.dll!wcscmp|delay=false',
    'api-ms-win-crt-string-l1-1-0.dll!wcslen|delay=false',
    'kernel32.dll!acquiresrwlockexclusive|delay=false',
    'kernel32.dll!closehandle|delay=false',
    'kernel32.dll!createeventw|delay=false',
    'kernel32.dll!createthread|delay=false',
    'kernel32.dll!disablethreadlibrarycalls|delay=false',
    'kernel32.dll!freelibrary|delay=false',
    'kernel32.dll!freelibraryandexitthread|delay=false',
    'kernel32.dll!getcurrentprocess|delay=false',
    'kernel32.dll!getcurrentprocessid|delay=false',
    'kernel32.dll!getcurrentthreadid|delay=false',
    'kernel32.dll!getlasterror|delay=false',
    'kernel32.dll!getmodulefilenamew|delay=false',
    'kernel32.dll!getmodulehandleexw|delay=false',
    'kernel32.dll!getprocaddress|delay=false',
    'kernel32.dll!getprocessheap|delay=false',
    'kernel32.dll!getprocesstimes|delay=false',
    'kernel32.dll!getsystemtimeasfiletime|delay=false',
    'kernel32.dll!gettickcount64|delay=false',
    'kernel32.dll!heapalloc|delay=false',
    'kernel32.dll!heapfree|delay=false',
    'kernel32.dll!initializeslisthead|delay=false',
    'kernel32.dll!interlockedpushentryslist|delay=false',
    'kernel32.dll!isdebuggerpresent|delay=false',
    'kernel32.dll!isprocessorfeaturepresent|delay=false',
    'kernel32.dll!loadlibraryexw|delay=false',
    'kernel32.dll!multibytetowidechar|delay=false',
    'kernel32.dll!queryperformancecounter|delay=false',
    'kernel32.dll!releasesrwlockexclusive|delay=false',
    'kernel32.dll!rtlcapturecontext|delay=false',
    'kernel32.dll!rtllookupfunctionentry|delay=false',
    'kernel32.dll!rtlvirtualunwind|delay=false',
    'kernel32.dll!setevent|delay=false',
    'kernel32.dll!setunhandledexceptionfilter|delay=false',
    'kernel32.dll!sleepconditionvariablesrw|delay=false',
    'kernel32.dll!terminateprocess|delay=false',
    'kernel32.dll!unhandledexceptionfilter|delay=false',
    'kernel32.dll!waitforsingleobject|delay=false',
    'kernel32.dll!wakeallconditionvariable|delay=false',
    'msvcp140.dll!_cnd_broadcast|delay=false',
    'msvcp140.dll!_mtx_lock|delay=false',
    'msvcp140.dll!_mtx_trylock|delay=false',
    'msvcp140.dll!_mtx_unlock|delay=false',
    'msvcp140.dll!_query_perf_counter|delay=false',
    'msvcp140.dll!_query_perf_frequency|delay=false',
    'msvcp140.dll!?_syserror_map@std@@yapebdh@z|delay=false',
    'msvcp140.dll!?_throw_cpp_error@std@@yaxh@z|delay=false',
    'msvcp140.dll!?_xbad_function_call@std@@yaxxz|delay=false',
    'msvcp140.dll!?_xlength_error@std@@yaxpebd@z|delay=false',
    'ole32.dll!cocreatefreethreadedmarshaler|delay=false',
    'oleaut32.dll!#200|delay=false',
    'oleaut32.dll!#201|delay=false',
    'oleaut32.dll!#6|delay=false',
    'oleaut32.dll!#7|delay=false',
    'user32.dll!callnexthookex|delay=false',
    'user32.dll!enumchildwindows|delay=false',
    'user32.dll!enumwindows|delay=false',
    'user32.dll!getclassnamew|delay=false',
    'user32.dll!getpropw|delay=false',
    'user32.dll!getwindowthreadprocessid|delay=false',
    'user32.dll!ischild|delay=false',
    'user32.dll!iswindow|delay=false',
    'user32.dll!registerwindowmessagew|delay=false',
    'user32.dll!removepropw|delay=false',
    'user32.dll!sendmessagetimeoutw|delay=false',
    'user32.dll!setpropw|delay=false',
    'user32.dll!setwindowshookexw|delay=false',
    'user32.dll!unhookwindowshookex|delay=false',
    'vcruntime140_1.dll!__cxxframehandler4|delay=false',
    'vcruntime140.dll!__c_specific_handler|delay=false',
    'vcruntime140.dll!__current_exception|delay=false',
    'vcruntime140.dll!__current_exception_context|delay=false',
    'vcruntime140.dll!__std_exception_copy|delay=false',
    'vcruntime140.dll!__std_exception_destroy|delay=false',
    'vcruntime140.dll!__std_terminate|delay=false',
    'vcruntime140.dll!__std_type_info_compare|delay=false',
    'vcruntime140.dll!__std_type_info_destroy_list|delay=false',
    'vcruntime140.dll!_cxxthrowexception|delay=false',
    'vcruntime140.dll!memcmp|delay=false',
    'vcruntime140.dll!memcpy|delay=false',
    'vcruntime140.dll!memmove|delay=false',
    'vcruntime140.dll!memset|delay=false'
)

function Assert-NativeBridgePolicyRegularFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [long]$MaximumBytes = 67108864
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required native bridge policy file is missing: $Path"
    }
    $file = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $file.Length -le 0 -or $file.Length -gt $MaximumBytes) {
        throw "Native bridge policy file metadata is invalid: $Path"
    }
    return $file
}

function Get-NativeBridgeSourceFiles {
    param([Parameter(Mandatory = $true)][string]$SourceRoot)

    if (-not (Test-Path -LiteralPath $SourceRoot -PathType Container)) {
        throw "Native bridge source root is missing: $SourceRoot"
    }
    $root = Get-Item -LiteralPath $SourceRoot -Force -ErrorAction Stop
    if (($root.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Native bridge source root must not be a reparse point.'
    }
    $allItems = @(Get-ChildItem -LiteralPath $root.FullName -Force -Recurse -ErrorAction Stop)
    foreach ($item in $allItems) {
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Native bridge source must not contain a reparse point: $($item.FullName)"
        }
    }
    $files = @($allItems | Where-Object {
            $_ -is [System.IO.FileInfo] -and (
                $_.Name -ieq 'CMakeLists.txt' -or
                $_.Extension -in @('.cpp', '.c', '.h', '.hpp', '.def', '.cmake'))
        })
    if ($files.Count -eq 0) {
        throw 'Native bridge source policy found no source files to audit.'
    }
    return $files
}

function Assert-NativeBridgeSourceBoundary {
    param([Parameter(Mandatory = $true)][string]$SourceRoot)

    $files = @(Get-NativeBridgeSourceFiles -SourceRoot $SourceRoot)
    $sourceText = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::Ordinal)
    $utf8 = [System.Text.UTF8Encoding]::new($false, $true)
    foreach ($file in $files) {
        $regular = Assert-NativeBridgePolicyRegularFile -Path $file.FullName -MaximumBytes 8388608
        try {
            $text = $utf8.GetString([System.IO.File]::ReadAllBytes($regular.FullName))
        }
        catch {
            throw "Native bridge source is not strict UTF-8: $($regular.FullName)"
        }
        foreach ($rule in $script:NativeBridgeSourceForbiddenPatterns) {
            if ($rule.Regex.IsMatch($text)) {
                throw "Forbidden native bridge source content '$($rule.Pattern)' found in $($regular.Name)"
            }
        }
        $sourceText.Add($regular.FullName, $text)
    }

    $implementationText = ($sourceText.GetEnumerator() |
        Where-Object { [System.IO.Path]::GetExtension($_.Key) -ieq '.cpp' } |
        ForEach-Object { $_.Value }) -join "`n"
    $loadLibraryCalls = [regex]::Matches($implementationText, '(?<![A-Za-z0-9_])LoadLibraryExW\s*\(').Count
    $getProcAddressCalls = [regex]::Matches($implementationText, '(?<![A-Za-z0-9_])GetProcAddress\s*\(').Count
    if ($loadLibraryCalls -ne 1 -or $getProcAddressCalls -ne 1 -or
        $implementationText -notmatch '"Windows\.UI\.Xaml\.dll"' -or
        $implementationText -notmatch '"InitializeXamlDiagnosticsEx"') {
        throw 'Native bridge dynamic-loading profile differs from the reviewed XAML diagnostics path.'
    }
}

function Get-NativeBridgeSourceManifest {
    param([Parameter(Mandatory = $true)][string]$SourceRoot)

    $records = [System.Collections.Generic.List[string]]::new()
    $root = (Get-Item -LiteralPath $SourceRoot -Force -ErrorAction Stop).FullName.TrimEnd([char[]]@('\', '/'))
    foreach ($file in @(Get-NativeBridgeSourceFiles -SourceRoot $SourceRoot | Sort-Object FullName)) {
        $regular = Assert-NativeBridgePolicyRegularFile -Path $file.FullName -MaximumBytes 8388608
        $relativePath = $regular.FullName.Substring($root.Length).TrimStart([char[]]@('\', '/')).Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $regular.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $records.Add(($relativePath + '|' + $hash))
    }
    return $records.ToArray()
}

function Resolve-NativeBridgeDumpbin {
    $command = Get-Command dumpbin.exe -CommandType Application -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return (Assert-NativeBridgePolicyRegularFile -Path $command.Source -MaximumBytes 67108864).FullName
    }
    $vswherePath = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswherePath -PathType Leaf)) {
        throw 'dumpbin.exe is required for the native bridge object security audit.'
    }
    $candidates = @(& $vswherePath -latest -products * -find 'VC\Tools\MSVC\**\bin\Hostx64\x64\dumpbin.exe' |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    foreach ($candidate in ($candidates | Select-Object -Last 1)) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Assert-NativeBridgePolicyRegularFile -Path $candidate -MaximumBytes 67108864).FullName
        }
    }
    throw 'Visual Studio did not provide a usable x64 dumpbin.exe for the native bridge object security audit.'
}

function Assert-NativeBridgeObjectImportBoundary {
    param(
        [Parameter(Mandatory = $true)][string]$BuildDirectory,
        [Parameter(Mandatory = $true)][ValidateSet('Debug', 'Release')][string]$Configuration
    )

    if (-not (Test-Path -LiteralPath $BuildDirectory -PathType Container)) {
        throw 'Native bridge object audit build directory is missing.'
    }
    $dumpbin = Resolve-NativeBridgeDumpbin
    $expectedRelativePaths = @(
        ('CodexQuotaTaskbar.Bridge.dir\{0}\dllmain.obj' -f $Configuration),
        ('cq_bridge_lifecycle.dir\{0}\bridge_runtime.obj' -f $Configuration),
        ('cq_bridge_lifecycle.dir\{0}\probe_capsule.obj' -f $Configuration),
        ('cq_bridge_lifecycle.dir\{0}\probe_safety.obj' -f $Configuration),
        ('cq_bridge_lifecycle.dir\{0}\xaml_taskbar_probe.obj' -f $Configuration)
    )
    $forbidden = [System.Text.RegularExpressions.Regex]::new(
        '(?i)\b(?:__imp_)?(?:TerminateProcess|NtTerminateProcess|RtlExitUserProcess|ExitProcess|WinHttp\w*|WinInet\w*|WSA\w*|Internet\w*|URLDownloadToFile)\b',
        [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    foreach ($relativePath in $expectedRelativePaths) {
        $objectPath = Join-Path $BuildDirectory $relativePath
        $object = Assert-NativeBridgePolicyRegularFile -Path $objectPath -MaximumBytes 67108864
        $symbols = @(& $dumpbin /symbols $object.FullName)
        if ($LASTEXITCODE -ne 0) {
            throw "dumpbin could not inspect native bridge object: $($object.Name)"
        }
        foreach ($line in $symbols) {
            if ($forbidden.IsMatch([string]$line)) {
                throw "Forbidden native bridge object import found in $($object.Name): $line"
            }
        }
    }
}

function Assert-NativeBridgeBinaryImportBoundary {
    param([Parameter(Mandatory = $true)][string]$Path)

    $file = Assert-NativeBridgePolicyRegularFile -Path $Path -MaximumBytes 67108864
    Assert-ExactPeImportEntries -Bytes ([System.IO.File]::ReadAllBytes($file.FullName)) `
        -ExpectedEntries $script:NativeBridgeExpectedImportEntries -ExpectedMachine 0x8664
}
