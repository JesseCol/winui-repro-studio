<#
.SYNOPSIS
    Builds and smoke-tests ReproStudio, including a portable folder.
.DESCRIPTION
    Run from a working Windows desktop with the .NET 10 SDK:
        .\tools\smoke.ps1

    Uses Windows PowerShell 5.1 or newer; installs no tools. The first cold
    runtime provision needs network access to NuGet. Headless still needs a
    working graphical session. Blank images and capture timeouts are failures.

    Each invocation owns a fresh out\smoke-<guid> folder, build output, runtime
    cache, and TEMP/TMP. It never clears your cache or changes registrations.
    Builds share the usual SDK/NuGet package cache and project intermediates:
    do not run another build or pack command at the same time.

    Logs and images stay on failure. Successful runs remove their workspace
    unless -KeepArtifacts is supplied (one workspace, potentially several GB).
.PARAMETER WasdkVersion
    Exact runtime version to test. Defaults to the Runner project's WasdkVersion.
.PARAMETER SkipPackaging
    Skip the second build, portable-folder checks, and staged-app run.
.PARAMETER ReloadCount
    Number of alternating real scene edits in the same headless Runner (default 6).
    A final comment-only save also checks that unchanged pixels remain capturable.
.PARAMETER RequireWgc
    Fail if any successful screenshot uses RenderTargetBitmap or reports a capture
    warning. Use on a supported rendering desktop to catch WGC regressions rather
    than allowing the documented XAML-only fallback to satisfy the pixel checks.
.EXAMPLE
    .\tools\smoke.ps1 -Configuration Debug -SkipPackaging
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'x86', 'ARM64')]
    [string] $Platform = 'x64',
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',
    [string] $WasdkVersion,
    [ValidateRange(1, 100)]
    [int] $ReloadCount = 6,
    [switch] $RequireWgc,
    [switch] $SkipPackaging,
    [switch] $KeepArtifacts
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$workspace = Join-Path $repo ('out\smoke-' + [guid]::NewGuid().ToString('N'))
$bundle = Join-Path $workspace 'build\'
$project = Join-Path $repo 'ReproStudio.csproj'
$runnerProject = Join-Path $repo 'src\ReproStudio.Runner\ReproStudio.Runner.csproj'
$roots = New-Object System.Collections.ArrayList
$failures = New-Object System.Collections.ArrayList
$passed = 0
$savedEnvironment = @{}
$environmentNames = @('TEMP', 'TMP', 'REPROSTUDIO_CACHE', 'BundleRoot',
    'MSBUILDDISABLENODEREUSE', 'DOTNET_CLI_USE_MSBUILD_SERVER', 'UseSharedCompilation')

function Assert([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

function Quote-Argument([string] $Value) {
    # Start-Process joins ArgumentList on .NET Framework. Apply Windows CRT
    # quoting ourselves, including quotes and a trailing backslash inside quotes.
    return '"' + [regex]::Replace(
        [regex]::Replace($Value, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
}

function Start-Logged([string] $Name, [string] $Exe, [string[]] $Arguments,
    [string] $Cache = (Join-Path $workspace 'cache')) {
    $directory = Join-Path $workspace $Name
    $temp = Join-Path $directory 'temp'
    New-Item -ItemType Directory -Path $temp -Force | Out-Null
    $env:TEMP = $temp
    $env:TMP = $temp
    $env:REPROSTUDIO_CACHE = $Cache
    $stdout = Join-Path $directory 'stdout.log'
    $stderr = Join-Path $directory 'stderr.log'
    $commandLine = ($Arguments | ForEach-Object { Quote-Argument $_ }) -join ' '
    Set-Content -LiteralPath (Join-Path $directory 'command.txt') -Encoding UTF8 `
        -Value "$Exe $commandLine"
    # File redirection, not ReadToEnd: GUI children may inherit stdout and keep
    # a pipe open after the CLI exits. All process waits below are bounded.
    $process = Start-Process -FilePath $Exe -ArgumentList $commandLine `
        -WorkingDirectory $repo -RedirectStandardOutput $stdout `
        -RedirectStandardError $stderr -WindowStyle Hidden -PassThru
    # Keep the handle: Windows PowerShell otherwise loses ExitCode on fast exits.
    $null = $process.Handle
    $null = $roots.Add($process)
    return [pscustomobject]@{
        Process = $process; Directory = $directory; Temp = $temp
        Stdout = $stdout; Stderr = $stderr
    }
}

function Read-Log($Run) {
    return ((Get-Content -LiteralPath $Run.Stdout -Raw) + "`n" +
        (Get-Content -LiteralPath $Run.Stderr -Raw))
}

function Wait-Exit($Run, [int] $Expected = 0, [int] $Seconds = 300) {
    Assert ($Run.Process.WaitForExit($Seconds * 1000)) `
        "Timed out after ${Seconds}s. See $($Run.Directory). Headless requires a working graphical session."
    $Run.Process.Refresh()
    Assert ($Run.Process.ExitCode -eq $Expected) `
        "Expected exit $Expected, got $($Run.Process.ExitCode). See $($Run.Directory)."
}

function Get-PrivateProcesses {
    return @(Get-CimInstance Win32_Process -OperationTimeoutSec 10 | Where-Object {
        $_.ExecutablePath -and $_.ExecutablePath.StartsWith(
            $workspace + '\', [StringComparison]::OrdinalIgnoreCase)
    })
}

function Stop-OwnedProcesses {
    $snapshot = @(Get-CimInstance Win32_Process -OperationTimeoutSec 10)
    $owned = @{}
    foreach ($row in $snapshot) {
        if ($row.ExecutablePath -and $row.ExecutablePath.StartsWith(
            $workspace + '\', [StringComparison]::OrdinalIgnoreCase)) {
            $owned[[int] $row.ProcessId] = $row
        }
    }
    foreach ($root in $roots) {
        if (-not $root.HasExited) {
            foreach ($row in $snapshot | Where-Object { $_.ProcessId -eq $root.Id }) {
                $owned[[int] $row.ProcessId] = $row
            }
        }
        else {
            foreach ($row in $snapshot | Where-Object {
                $_.ParentProcessId -eq $root.Id -and
                $_.CreationDate -ge $root.StartTime -and $_.CreationDate -le $root.ExitTime
            }) {
                $owned[[int] $row.ProcessId] = $row
            }
        }
    }
    do {
        $added = $false
        foreach ($row in $snapshot) {
            $parent = $owned[[int] $row.ParentProcessId]
            if ($parent -and -not $owned.ContainsKey([int] $row.ProcessId) -and
                $row.CreationDate -ge $parent.CreationDate) {
                $owned[[int] $row.ProcessId] = $row
                $added = $true
            }
        }
    } while ($added)

    $errors = @()
    foreach ($row in $owned.Values | Sort-Object CreationDate -Descending) {
        try {
            $current = Get-CimInstance Win32_Process -Filter "ProcessId=$($row.ProcessId)" `
                -OperationTimeoutSec 10
            # A recycled PID is not ours. Never stop by process name.
            if ($current -and $current.CreationDate -eq $row.CreationDate) {
                $process = [Diagnostics.Process]::GetProcessById([int] $row.ProcessId)
                try {
                    if (-not $process.HasExited) { $process.Kill() }
                    Assert ($process.WaitForExit(5000)) "PID $($row.ProcessId) did not stop."
                }
                finally { $process.Dispose() }
            }
        }
        catch [ArgumentException] {
            # GetProcessById raced a normal process exit.
        }
        catch [InvalidOperationException] {
            # Kill raced a normal process exit.
        }
        catch { $errors += "PID $($row.ProcessId): $($_.Exception.Message)" }
    }
    foreach ($root in $roots) { $root.Dispose() }
    $roots.Clear()
    Assert ($errors.Count -eq 0) ("Process cleanup failed: " + ($errors -join '; '))
}

function Run-Case([string] $Name, [scriptblock] $Body) {
    Write-Host "RUN  $Name"
    $errorText = $null
    try { & $Body }
    catch { $errorText = $_.Exception.Message }
    finally {
        try { Stop-OwnedProcesses }
        catch { $errorText = "$errorText Cleanup: $($_.Exception.Message)" }
    }
    if ($errorText) {
        $null = $failures.Add("${Name}: $errorText")
        Write-Host "FAIL $Name - $errorText" -ForegroundColor Red
        return $false
    }
    $script:passed++
    Write-Host "PASS $Name" -ForegroundColor Green
    return $true
}

function Get-Property([string] $ProjectPath, [string] $Property, [string] $Label) {
    $run = Start-Logged $Label 'dotnet' @('msbuild', $ProjectPath, '-nologo',
        "-p:Configuration=$Configuration", "-p:Platform=$Platform",
        "-p:BundleRoot=$bundle", "-getProperty:$Property")
    Wait-Exit $run -Seconds 60
    $value = (Get-Content -LiteralPath $run.Stdout | Select-Object -Last 1).Trim()
    Assert (-not [string]::IsNullOrWhiteSpace($value)) "MSBuild returned no $Property."
    return $value
}

function Assert-Layout([string] $AppDirectory) {
    foreach ($relative in @('ReproStudio.exe', 'ReproStudio.deps.json',
        'System.Private.CoreLib.dll', 'coreclr.dll', 'hostfxr.dll',
        'RunnerIdentity\Package.appxmanifest', 'samples\hello.cs',
        'runner-base\ReproStudio.Runner.exe', 'runner-base\ReproStudio.Runner.deps.json',
        'runner-base\System.Private.CoreLib.dll', 'runner-base\coreclr.dll',
        'runner-base\ReproStudio.Runner.pri', 'runner-base\Microsoft.ui.xaml.dll',
        'runner-base\Microsoft.Windows.CsWin32.dll', 'runner-base\CsWin32\Windows.Win32.winmd')) {
        Assert (Test-Path -LiteralPath (Join-Path $AppDirectory $relative) -PathType Leaf) `
            "Missing build/package asset: $relative"
    }
    Assert (-not (Test-Path -LiteralPath (Join-Path $AppDirectory 'runner-base\resources.pri'))) `
        'A resources.pri would shadow ReproStudio.Runner.pri.'
    $wasdk = @(Get-ChildItem -LiteralPath $AppDirectory -File | Where-Object {
        $_.Name -match '^(Microsoft\.(UI\.|WinUI|WindowsApp|Windows\.App)|MRM\.dll)'
    })
    Assert ($wasdk.Count -eq 0) ("WASDK assets leaked into the CLI root: " +
        (($wasdk | ForEach-Object { $_.Name }) -join ', '))
    $dependencies = Get-Content -LiteralPath (Join-Path $AppDirectory 'ReproStudio.deps.json') -Raw |
        ConvertFrom-Json
    $leaked = @($dependencies.libraries.PSObject.Properties.Name | Where-Object {
        $_ -match '^(Microsoft\.WindowsAppSDK(?:[./])|Microsoft\.WinUI/|ReproStudio\.Runner/)'
    })
    Assert ($leaked.Count -eq 0) ("Runner/WASDK dependencies leaked into the host: " + ($leaked -join ', '))
}

function Read-IpcJson([string] $Path) {
    $stream = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read,
        [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
    $reader = [IO.StreamReader]::new($stream)
    try { return $reader.ReadToEnd() | ConvertFrom-Json }
    finally { $reader.Dispose() }
}

function Get-Result($Run, [string] $PreviousId = '') {
    $ipc = Join-Path $Run.Temp 'winui-repro-app'
    if (-not (Test-Path -LiteralPath $ipc)) { return $null }
    foreach ($directory in Get-ChildItem -LiteralPath $ipc -Directory -Filter 'runner-*') {
        $requestPath = Join-Path $directory.FullName 'request.json'
        $resultPath = Join-Path $directory.FullName 'request.result.json'
        if ((Test-Path -LiteralPath $requestPath) -and (Test-Path -LiteralPath $resultPath)) {
            try {
                $request = Read-IpcJson $requestPath
                $result = Read-IpcJson $resultPath
            }
            catch [IO.IOException] { continue } # Atomic replacement can race a read.
            if ($result.requestId -eq $request.requestId -and $result.requestId -ne $PreviousId) {
                Assert ([guid] $result.requestId -ne [guid]::Empty) 'Missing request correlation ID.'
                return [pscustomobject]@{ Request = $request; Result = $result; Path = $resultPath }
            }
        }
    }
    return $null
}

function Wait-Result($Run, [string] $PreviousId = '', [int] $Seconds = 300) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        $match = Get-Result $Run $PreviousId
        if ($match) { return $match }
        Assert (-not $Run.Process.HasExited) "CLI exited before a matching result. See $($Run.Directory)."
        Start-Sleep -Milliseconds 250
    } while ($clock.Elapsed.TotalSeconds -lt $Seconds)
    throw "No fresh result after ${Seconds}s. Check the graphical session and $($Run.Directory)."
}

function Assert-Capture($Match, [string] $Png) {
    $result = $Match.Result
    Assert ($Match.Request.sdk -eq 'match') 'Default SDK/API selection must match native runtime.'
    Assert ($Match.Request.pair.sdk -eq 'match' -and
        $Match.Request.pair.nativeWasdkVersion -eq $Match.Request.wasdkVersion) `
        'Request must carry the provisioned API/native pair, not only a runtime label.'
    Assert ($Match.Request.pair.runtimeIdentifier -eq ('win-' + $Platform.ToLowerInvariant())) `
        'Provisioned SDK/native RID must match the bundle architecture.'
    Assert ($Match.Request.pair.winUiSha256 -match '^[0-9a-f]{64}$') `
        'SDK/API metadata must identify actual managed WinUI content.'
    Assert $result.renderSucceeded "Render failed: $($result.renderError)"
    Assert (-not $result.captureError) "Capture failed: $($result.captureError)"
    Assert ($result.screenshotPath -eq $Png) "Wrong screenshot path in $($Match.Path)."
    Assert ($result.captureMethod -in @('Windows.Graphics.Capture', 'RenderTargetBitmap')) `
        "Unknown capture backend in $($Match.Path)."
    if ($RequireWgc) {
        Assert ($result.captureMethod -eq 'Windows.Graphics.Capture' -and
            [string]::IsNullOrEmpty($result.captureWarning)) `
            "WGC required, got $($result.captureMethod): $($result.captureWarning)"
    }
    if ($result.captureMethod -eq 'RenderTargetBitmap') {
        Assert (-not [string]::IsNullOrWhiteSpace($result.captureWarning)) `
            'RenderTargetBitmap must report why WGC was unavailable.'
    }
}

function Assert-Png([string] $Path, [string] $Marker = '') {
    Assert (Test-Path -LiteralPath $Path -PathType Leaf) "PNG missing: $Path"
    $bytes = [IO.File]::ReadAllBytes($Path)
    Assert ($bytes.Length -gt 24 -and
        [BitConverter]::ToString($bytes, 0, 8) -eq '89-50-4E-47-0D-0A-1A-0A') "Invalid PNG: $Path"
    $bitmap = New-Object Drawing.Bitmap $Path
    try {
        Assert ($bitmap.Width -ge 100 -and $bitmap.Height -ge 100) `
            "Unexpected PNG dimensions: $($bitmap.Width)x$($bitmap.Height)."
        $colors = @{}
        $hits = 0
        $samples = 0
        $expected = if ($Marker) { [Drawing.ColorTranslator]::FromHtml($Marker) } else { $null }
        # Sample the full image: capture may include chrome, and DPI varies.
        $step = [Math]::Max(1, [int] ([Math]::Min($bitmap.Width, $bitmap.Height) / 100))
        for ($y = 0; $y -lt $bitmap.Height; $y += $step) {
            for ($x = 0; $x -lt $bitmap.Width; $x += $step) {
                $color = $bitmap.GetPixel($x, $y)
                $colors[$color.ToArgb()] = $true
                $samples++
                if ($expected -and $color.A -ge 250 -and
                    [Math]::Abs([int] $color.R - $expected.R) -le 5 -and
                    [Math]::Abs([int] $color.G - $expected.G) -le 5 -and
                    [Math]::Abs([int] $color.B - $expected.B) -le 5) { $hits++ }
            }
        }
        Assert ($colors.Count -ge 8) "Blank/unrendered PNG: $Path. Check the graphical session."
        if ($Marker) {
            Assert ($hits -gt $samples * 0.03) `
                "Expected $Marker marker missing ($hits/$samples pixels): $Path. Capture may be stale or the session is not rendering."
        }
    }
    finally { $bitmap.Dispose() }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Write-Fixture([string] $Path, [string] $Color, [string] $Text, [switch] $Invalid) {
    $setup = if ($Invalid) { 'SmokeMissingType value = null;' } else { 'Log("Smoke fixture loaded.");' }
    $source = @"
// repro: Smoke fixture
// wasdk: $WasdkVersion
// packaged: no
class Repro
{
    const string Xaml = """
        <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
              Width="400" Height="260" Background="$Color">
            <TextBlock Text="$Text" Foreground="White" FontSize="28"
                       HorizontalAlignment="Center" VerticalAlignment="Center" />
        </Grid>
        """;
    static void Setup(FrameworkElement root) { $setup }
}
"@
    [IO.File]::WriteAllText($Path, $source)
}

function Assert-NoRunner {
    $left = @(Get-PrivateProcesses | Where-Object {
        [IO.Path]::GetFileName($_.ExecutablePath) -eq 'ReproStudio.Runner.exe'
    })
    Assert ($left.Count -eq 0) ("Headless one-shot leaked Runner PID(s): " +
        (($left | ForEach-Object { $_.ProcessId }) -join ', '))
}

function Run-OneShot([string] $Name, [string] $Exe, [string[]] $Extra = @(),
    [string] $Cache = (Join-Path $workspace 'cache'), [string] $Marker = '') {
    $png = Join-Path $workspace "$Name capture.png"
    Assert (-not (Test-Path -LiteralPath $png)) "PNG already exists before launch: $png"
    $run = Start-Logged $Name $Exe ($Extra + $stock + @('--no-watch', '--screenshot', $png)) $Cache
    Wait-Exit $run
    $match = Get-Result $run
    Assert ($null -ne $match) "No matching request/result pair in $($run.Temp)."
    Assert-Capture $match $png
    $null = Assert-Png $png $Marker
    Assert ((Read-Log $run).Contains($match.Result.captureMethod)) 'CLI did not report the capture backend.'
    Assert-NoRunner
    return $run
}

try {
    New-Item -ItemType Directory -Path $workspace -Force | Out-Null
    foreach ($name in $environmentNames) {
        $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    }
    $env:BundleRoot = $bundle
    $env:MSBUILDDISABLENODEREUSE = '1'
    $env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
    $env:UseSharedCompilation = 'false'
    Write-Host "Smoke workspace: $workspace"

    $built = Run-Case 'fresh root dotnet run' {
        Assert (Test-Path -LiteralPath $project) "Root entry project missing: $project"
        Add-Type -AssemblyName System.Drawing
        $sdk = Start-Logged 'sdk' 'dotnet' @('--version')
        Wait-Exit $sdk -Seconds 30
        Assert ((Get-Content -LiteralPath $sdk.Stdout -Raw).Trim() -match '^10\.') '.NET SDK 10 is required.'
        $build = Start-Logged 'build' 'dotnet' @('run', '--no-launch-profile',
            '--disable-build-servers', '-v:quiet', '-c', $Configuration,
            "-p:Platform=$Platform", "-p:BundleRoot=$bundle", '-p:UseSharedCompilation=false',
            '--', '--help')
        Wait-Exit $build -Seconds 900
        Assert ((Read-Log $build) -match 'REPROSTUDIO_CACHE') 'Fresh dotnet run did not launch the host.'
        $script:appDirectory = Get-Property $project 'OutDir' 'host-output'
        $runnerDirectory = Get-Property $runnerProject 'OutDir' 'runner-output'
        Assert ([IO.Path]::GetFullPath($appDirectory).StartsWith(
            $workspace + '\', [StringComparison]::OrdinalIgnoreCase)) 'Build output escaped the private workspace.'
        Assert ([IO.Path]::GetFullPath($runnerDirectory).TrimEnd('\') -eq
            (Join-Path $appDirectory 'runner-base')) 'Runner output is not beside the host.'
        Assert-Layout $appDirectory
        if (-not $WasdkVersion) {
            $script:WasdkVersion = Get-Property $runnerProject 'WasdkVersion' 'runtime-version'
        }
        Assert ($WasdkVersion -match '^\d+\.\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z.-]+)?$') `
            '-WasdkVersion must be an exact version, not a prefix such as 2.2.'
        $script:exe = Join-Path $appDirectory 'ReproStudio.exe'
        $script:stock = @('--wasdk', $WasdkVersion, '--payload', 'none', '--unpackaged', '--headless')
    }

    if ($built) {
        $null = Run-Case 'root argument forwarding' {
            $runArgs = @('run', '--no-build', '--no-launch-profile', '-c', $Configuration,
                "-p:Platform=$Platform", "-p:BundleRoot=$bundle", '--')
            $help = Start-Logged 'run-help' 'dotnet' ($runArgs + @('--help'))
            Wait-Exit $help -Seconds 60
            Assert ((Read-Log $help) -match 'REPROSTUDIO_CACHE') 'Root run did not forward --help to the CLI.'
            $doctor = Start-Logged 'run-doctor' 'dotnet' ($runArgs + @('--doctor'))
            Wait-Exit $doctor -Seconds 90
            Assert ((Read-Log $doctor).Contains($appDirectory.TrimEnd('\'))) 'Doctor selected another build output.'
            $bad = Start-Logged 'run-unknown-option' 'dotnet' ($runArgs + @('--smoke-unknown-option'))
            Wait-Exit $bad -Expected 2 -Seconds 60
            Assert ((Read-Log $bad) -match 'Unknown option: --smoke-unknown-option') `
                'Unknown host option was not forwarded.'
            $missing = Start-Logged 'run-missing-value' 'dotnet' ($runArgs + @('--wasdk', '--headless'))
            Wait-Exit $missing -Expected 2 -Seconds 60
            Assert ((Read-Log $missing) -match '--wasdk' -and (Read-Log $missing) -match '--help') `
                'Missing option value did not produce a diagnostic and help hint.'
        }

        $null = Run-Case 'default hello headless PNG' {
            $run = Run-OneShot 'default-hello' $exe
            Assert ((Read-Log $run).Contains((Join-Path $appDirectory 'samples\hello.cs'))) `
                'No-file launch did not select the bundled hello.cs.'
        }

        $null = Run-Case 'save and reload in the same Runner' {
            $fixture = Join-Path $workspace 'repro with spaces.cs'
            $png = Join-Path $workspace 'watch capture.png'
            Write-Fixture $fixture '#CC2244' 'Before save'
            $run = Start-Logged 'watch' $exe (@($fixture) + $stock + @('--screenshot', $png))
            $first = Wait-Result $run
            Assert-Capture $first $png
            $before = Assert-Png $png '#CC2244'
            Copy-Item -LiteralPath $png -Destination (Join-Path $run.Directory 'before.png')
            $runner = @(Get-PrivateProcesses | Where-Object {
                [IO.Path]::GetFileName($_.ExecutablePath) -eq 'ReproStudio.Runner.exe'
            })
            Assert ($runner.Count -eq 1) 'Expected exactly one owned Runner in watch mode.'
            $previous = $first
            $previousHash = $before
            for ($save = 1; $save -le $ReloadCount; $save++) {
                $color = if ($save % 2) { '#2266CC' } else { '#CC2244' }
                $text = if ($save % 2) { 'After save' } else { 'Before save' }
                Write-Fixture $fixture $color $text
                $next = Wait-Result $run $previous.Result.requestId -Seconds 90
                Assert ($next.Path -eq $first.Path) 'Saving replaced the Runner request channel.'
                Assert ($next.Request.pair.key -eq $first.Request.pair.key) 'Ordinary C# save changed the SDK/native pair.'
                Assert ($next.Request.xaml.Contains($color)) "Request does not contain save $save's expected color."
                Assert-Capture $next $png
                # Keep each actual frame even when its pixels fail the freshness check.
                Copy-Item -LiteralPath $png -Destination (Join-Path $run.Directory ("after-{0:D2}.png" -f $save))
                $after = Assert-Png $png $color
                Assert ($after -ne $previousHash) "PNG bytes did not change after save $save."
                $previous = $next
                $previousHash = $after
            }
            # Capture must not use pixel inequality as a freshness fence: a genuine
            # source save can recreate exactly the same visible scene.
            [IO.File]::AppendAllText($fixture, "`n// Same visible scene, new source revision.`n")
            $sameScene = Wait-Result $run $previous.Result.requestId -Seconds 90
            Assert ($sameScene.Path -eq $first.Path -and
                $sameScene.Request.pair.key -eq $first.Request.pair.key) 'Comment-only save changed the Runner channel or pair.'
            Assert-Capture $sameScene $png
            Copy-Item -LiteralPath $png -Destination (Join-Path $run.Directory 'same-scene.png')
            $null = Assert-Png $png $color
            $live = @(Get-PrivateProcesses | Where-Object {
                [IO.Path]::GetFileName($_.ExecutablePath) -eq 'ReproStudio.Runner.exe'
            })
            Assert ($live.Count -eq 1 -and $live[0].ProcessId -eq $runner[0].ProcessId -and
                $live[0].CreationDate -eq $runner[0].CreationDate -and -not $run.Process.HasExited) `
                'Saving relaunched the Runner or stopped the CLI.'
            Assert ((Read-Log $run) -match 'pushed') 'CLI did not report the saved request.'
        }

        $null = Run-Case 'invalid C# returns failure and stops Runner' {
            $fixture = Join-Path $workspace 'invalid snippet.cs'
            Write-Fixture $fixture '#CC2244' 'Invalid CSharp' -Invalid
            $run = Start-Logged 'invalid' $exe (@($fixture) + $stock +
                @('--no-watch', '--screenshot', (Join-Path $workspace 'invalid.png')))
            Wait-Exit $run -Expected 1
            $match = Get-Result $run
            Assert ($null -ne $match -and -not $match.Result.renderSucceeded) `
                'Invalid snippet has no matching failed render result.'
            Assert ((Read-Log $run) -match 'CS0246|SmokeMissingType') 'CLI did not surface the compiler diagnostic.'
            Assert-NoRunner
        }

        if (-not $SkipPackaging) {
            $null = Run-Case 'portable package and staged render' {
                $outputRoot = Join-Path $workspace 'portable'
                $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
                $pack = Start-Logged 'pack' $powershell @('-NoProfile', '-NonInteractive',
                    '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $repo 'pack.ps1'),
                    '-NoZip', '-OutputRoot', $outputRoot, '-Configuration', $Configuration, '-Platform', $Platform)
                Wait-Exit $pack -Seconds 900
                $stage = Join-Path $outputRoot "ReproStudio-$Platform"
                Assert-Layout $stage
                foreach ($relative in @('README.md', 'READ-ME-FIRST.txt', 'docs\guide.md',
                    'docs\how-it-works.md', 'docs\images\workflow-overview.png', 'samples\hello.cs')) {
                    Assert (Test-Path -LiteralPath (Join-Path $stage $relative) -PathType Leaf) `
                        "Portable content missing: $relative"
                }
                $payload = @(Get-ChildItem -LiteralPath (Join-Path $stage 'payload') -Recurse -File)
                Assert ($payload.Count -gt 0) 'Portable payload instructions are missing.'
                Assert (@($payload | Where-Object { $_.Extension -notin @('.txt', '.md') }).Count -eq 0) `
                    'Portable payload includes non-text files.'
                $fixture = Join-Path $workspace 'portable repro with spaces.cs'
                Write-Fixture $fixture '#2266CC' 'Portable smoke'
                $run = Run-OneShot 'staged' (Join-Path $stage 'ReproStudio.exe') @($fixture) `
                    (Join-Path $workspace 'portable-cache') '#2266CC'
                Assert ((Read-Log $run).Contains((Join-Path $stage 'runner-base'))) `
                    'Staged executable did not use its own base runner.'
            }
        }
    }
}
catch {
    $null = $failures.Add("Smoke setup: $($_.Exception.Message)")
    Write-Host "FAIL $($failures[$failures.Count - 1])" -ForegroundColor Red
}
finally {
    try { Stop-OwnedProcesses }
    catch { $null = $failures.Add("Final cleanup: $($_.Exception.Message)") }
    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
    }
}

if ($failures.Count -eq 0 -and -not $KeepArtifacts) {
    try { Remove-Item -LiteralPath $workspace -Recurse -Force }
    catch { $null = $failures.Add("Workspace cleanup: $($_.Exception.Message)") }
}
Write-Host "$passed passed; $($failures.Count) failed."
if ($failures.Count -gt 0) {
    foreach ($failure in $failures) { Write-Host "  $failure" }
    Write-Host "Artifacts and stdout/stderr logs: $workspace"
    exit 1
}
if ($KeepArtifacts) { Write-Host "Artifacts kept: $workspace" }
exit 0
