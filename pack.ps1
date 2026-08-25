<#
.SYNOPSIS
    Builds ReproStudio into a portable folder you can xcopy to another machine.

.DESCRIPTION
    Produces artifacts\ReproStudio-<platform>\ containing the console host at the root,
    the base runner beside it, and the sample repros:

        ReproStudio-x64\
            ReproStudio.exe               <- run this
            runner-base\                  <- the prebuilt runner, copied per WASDK version
            samples\                      <- example repro files
            probes\                       <- one-question platform checks, re-runnable
            investigations\               <- per-bug harnesses
            ...

    The console host is what ships, not the WinUI host. It has no Windows App SDK
    dependency at all, which keeps the bundle small and means the tool still runs (and
    can still tell you why) when the WASDK build under test will not start.

    Both executables are self-contained for .NET, and the runner is additionally
    self-contained for the Windows App SDK, so the target machine needs no SDK, no .NET
    runtime, and no Windows App SDK runtime installed. The floor is Windows 10 1809
    (17763), the minimum for both .NET 10 and every Windows App SDK version this tool
    provisions.

    Nothing in the bundle is written to at run time. Downloaded packages and provisioned
    per-version runners go to %LOCALAPPDATA%\winui-repro-app on the machine it runs on,
    so the folder can live on read-only media.

.PARAMETER Platform
    x64 (default), x86, or ARM64. This is the architecture of the *target* machine.

.PARAMETER Configuration
    Release (default) or Debug.

.PARAMETER OutputRoot
    Where to put the staged folder and zip. Defaults to artifacts\ next to this script.

.PARAMETER LocalWinUi
    Path to a .nupkg from tools\pack-local-winui.ps1, wrapping a private WinUI build.
    Stages it into local-winui\ and writes run-fixed.cmd / run-stock.cmd so the bundle
    can be run either way. Implies -Preprovision.

.PARAMETER Preprovision
    Windows App SDK versions to bake into a cache\ folder inside the bundle, so the
    target machine needs no internet. Slow and large, but the only way to run on a
    machine that is offline or firewalled off from NuGet.

.PARAMETER NoZip
    Stage the folder but skip the zip.

.EXAMPLE
    .\pack.ps1
    .\pack.ps1 -Platform ARM64

.EXAMPLE
    # Offline bundle for testing a private WinUI build on a machine with no network.
    $pkg = .\tools\pack-local-winui.ps1 -Source D:\winui\BuildOutput\bin\amd64chk\Product
    .\pack.ps1 -LocalWinUi $pkg -Preprovision 2.3.1
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'x86', 'ARM64')]
    [string] $Platform = 'x64',

    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',

    [string] $OutputRoot,

    [string] $LocalWinUi,

    [string[]] $Preprovision,

    [switch] $NoZip
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = $PSScriptRoot
if (-not $OutputRoot) { $OutputRoot = Join-Path $repoRoot 'artifacts' }

if ($LocalWinUi) {
    if (-not (Test-Path -LiteralPath $LocalWinUi -PathType Leaf)) {
        throw "No such WinUI package: $LocalWinUi. Build one with tools\pack-local-winui.ps1."
    }
    $LocalWinUi = (Resolve-Path -LiteralPath $LocalWinUi).Path

    # A local WinUI build is only worth shipping if it is ready to run. Provisioning it
    # on the target machine would need the network the offline bundle exists to avoid.
    if (-not $Preprovision) {
        throw '-LocalWinUi needs -Preprovision <version>, naming the Windows App SDK version to overlay it onto.'
    }
}

$rid = 'win-' + $Platform.ToLowerInvariant()
$cliProject = Join-Path $repoRoot 'src\ReproStudio.Cli\ReproStudio.Cli.csproj'
$runnerProject = Join-Path $repoRoot 'src\ReproStudio.Runner\ReproStudio.Runner.csproj'

# Both projects default their RuntimeIdentifier from the *build* machine's architecture,
# so it has to be passed explicitly or cross-architecture packing would silently produce
# the wrong runtime.
$buildArgs = @(
    '-c', $Configuration
    '-p:Platform=' + $Platform
    '-p:RuntimeIdentifier=' + $rid
)

function Get-OutputDirectory([string] $project) {
    # Ask MSBuild rather than reconstructing the path: it varies with TFM and RID, and a
    # stale guess would silently package the wrong bits.
    $relative = & dotnet msbuild $project @(
        '-p:Configuration=' + $Configuration
        '-p:Platform=' + $Platform
        '-p:RuntimeIdentifier=' + $rid
        '-getProperty:OutDir'
    ) | Select-Object -Last 1

    if (-not $relative) { throw "Could not read OutDir for $project." }
    return Join-Path (Split-Path -Parent $project) $relative.Trim()
}

function Copy-Tree([string] $source, [string] $destination) {
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    # /MIR so a rerun cannot leave stale files behind. Robocopy uses exit codes 0-7 for
    # success, so anything higher is a real failure.
    $null = robocopy $source $destination /MIR /NFL /NDL /NJH /NJS /NP /R:2 /W:1
    if ($LASTEXITCODE -ge 8) { throw "Copy failed ($source -> $destination), robocopy exit $LASTEXITCODE." }
}

function Measure-Tree([string] $path) {
    $files = Get-ChildItem -Path $path -Recurse -File
    $bytes = ($files | Measure-Object -Property Length -Sum).Sum
    return '{0:N0} files, {1:N0} MB' -f $files.Count, ($bytes / 1MB)
}

function Write-CmdLauncher([string] $path, [string] $version, [string] $extra, [string] $what) {
    $name = Split-Path -Leaf $path

    # %~dp0 keeps every path relative to the bundle, so it runs from any folder, and
    # REPROSTUDIO_CACHE points at the cache staged beside it - no download, no network.
    $body = @"
@echo off
rem Runs a repro against $what.
rem   $name samples\hello.cs
setlocal
set "REPROSTUDIO_CACHE=%~dp0cache"
if "%~1"=="" (
    echo usage: $name ^<file.cs^> [options]
    exit /b 1
)
"%~dp0ReproStudio.exe" %* --wasdk $version$extra
"@
    Set-Content -Path $path -Value $body -Encoding ascii
}

# A running runner holds a lock on its own exe, which makes the build fail with a
# confusing file-in-use error.
Get-Process -Name 'ReproStudio', 'ReproStudio.Runner', 'ReproStudio.Host' -ErrorAction SilentlyContinue |
    ForEach-Object { Stop-Process -Id $_.Id -Force }

Write-Host "Building $Configuration $Platform ($rid)..." -ForegroundColor Cyan

# dotnet build, not dotnet publish: publish drops ReproStudio.Runner.pri, and without it the
# runner cannot resolve ms-appx:///Microsoft.UI.Xaml/Themes/themeresources.xaml and crashes.
& dotnet build $runnerProject @buildArgs
if ($LASTEXITCODE -ne 0) { throw 'Runner build failed.' }

& dotnet build $cliProject @buildArgs
if ($LASTEXITCODE -ne 0) { throw 'Console host build failed.' }

$cliOut = Get-OutputDirectory $cliProject
$runnerOut = Get-OutputDirectory $runnerProject

foreach ($required in @(
        (Join-Path $cliOut 'ReproStudio.exe')
        (Join-Path $runnerOut 'ReproStudio.Runner.exe')
        # Proof both halves really are self-contained. Without these the bundle would
        # need a matching .NET runtime installed on the target machine.
        (Join-Path $cliOut 'System.Private.CoreLib.dll')
        (Join-Path $runnerOut 'System.Private.CoreLib.dll')
        # See the dotnet build comment above.
        (Join-Path $runnerOut 'ReproStudio.Runner.pri')
        # Needed for "// packaged: yes"; easy to lose, and it only fails at launch time.
        (Join-Path $cliOut 'RunnerIdentity\Package.appxmanifest')
    )) {
    if (-not (Test-Path $required)) { throw "Build output is missing $required." }
}

# A resources.pri in the runner output shadows ReproStudio.Runner.pri and makes every
# launch die on themeresources.xaml. The build deletes it, but bin is not cleaned between
# builds and MSIX tooling has written one there before, so refuse to ship one.
$strayPri = Join-Path $runnerOut 'resources.pri'
if (Test-Path $strayPri) {
    throw "Stale $strayPri would break the runner at startup. Delete it and rebuild."
}

$stage = Join-Path $OutputRoot ('ReproStudio-' + $Platform)
Write-Host "Staging $stage..." -ForegroundColor Cyan

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
Copy-Tree $cliOut $stage
Copy-Tree $runnerOut (Join-Path $stage 'runner-base')
Copy-Tree (Join-Path $repoRoot 'samples') (Join-Path $stage 'samples')

# probes\ and investigations\ ship too. They exist to be run on a test machine -
# an old build, a VM, a box with a candidate WASDK on it - and that machine has
# the bundle, not a clone. Leaving them out meant the one place you most want to
# re-take a measurement was the one place the harness wasn't.
foreach ($extra in 'probes', 'investigations')
{
    $src = Join-Path $repoRoot $extra
    if (Test-Path $src)
    {
        Copy-Tree $src (Join-Path $stage $extra)
    }
}

# An empty drop folder, always. ReproStudio.exe looks for "payload" next to itself,
# so shipping the folder pre-made turns "test a private build on this machine" into
# copying a DLL in - nothing to read, nothing to configure. The README is what the
# person who unzips this actually finds, so it has to stand on its own. .txt is
# ignored when the payload is read, so this file never counts as content.
$payloadDir = Join-Path $stage 'payload'
New-Item -ItemType Directory -Path $payloadDir -Force | Out-Null
Set-Content -Path (Join-Path $payloadDir 'README.txt') -Encoding ascii -Value @'
Drop files here to test a private build.

Anything in this folder is copied over the Windows App SDK runtime, after the
version is laid down, so a file here beats the stock file of the same name.
The usual case is one DLL:

    payload\Microsoft.ui.xaml.dll

Then run a repro as normal:

    ReproStudio.exe samples\hello.cs

The console prints which files it picked up. Subfolders work too and keep their
relative paths, so payload\Microsoft.UI.Xaml\ overlays the themes directory.

To run stock while files are sitting here, pass --payload none:

    ReproStudio.exe samples\hello.cs --payload none

Always take a stock reading before you trust a payload reading. If the harness
was broken, both readings are worthless, and only the stock one tells you that.

Nothing here is validated. A binary that does not load makes the runner fail to
start, and it will say so. .txt and .md files (like this one) are ignored.
'@


# --- optional: a local WinUI build, and a cache that makes the bundle run offline ---

$stagedWinUi = $null
$stagedWinUiFull = $null
if ($LocalWinUi) {
    $winuiDir = Join-Path $stage 'local-winui'
    New-Item -ItemType Directory -Path $winuiDir -Force | Out-Null
    Copy-Item -LiteralPath $LocalWinUi -Destination $winuiDir -Force
    # Relative, because the bundle gets copied somewhere else and an absolute path
    # baked into run-fixed.cmd would point at this machine.
    $stagedWinUi = 'local-winui\' + (Split-Path -Leaf $LocalWinUi)
    $stagedWinUiFull = Join-Path $winuiDir (Split-Path -Leaf $LocalWinUi)
    Write-Host "  local WinUI: $stagedWinUi" -ForegroundColor DarkGray
}

if ($Preprovision) {
    $cacheDir = Join-Path $stage 'cache'
    $stagedExe = Join-Path $stage 'ReproStudio.exe'
    # Any repro will do: provisioning only depends on the version, not the content.
    $seed = Join-Path $stage 'samples\hello.cs'
    if (-not (Test-Path $seed)) { throw "Cannot pre-provision: $seed is missing." }

    foreach ($version in $Preprovision) {
        # Both flavours, so the bundle can show the stock behaviour next to the local
        # build's. A fix you cannot compare against the bug it fixes proves nothing.
        # Absolute path here: a relative --winui resolves against the repro file, not
        # the working directory, and the seed repro lives one folder down in samples\.
        $variants = New-Object System.Collections.ArrayList
        $null = $variants.Add(@())
        if ($stagedWinUiFull) { $null = $variants.Add(@('--winui', $stagedWinUiFull)) }

        foreach ($variant in $variants) {
            $label = if ($variant.Count -gt 0) { "$version + local WinUI" } else { $version }
            Write-Host "  Pre-provisioning $label..." -ForegroundColor DarkGray

            $runArgs = @($seed, '--wasdk', $version, '--provision-only') + $variant
            # REPROSTUDIO_CACHE only for this child process, so packing never disturbs
            # the packer's own cache.
            $previous = $env:REPROSTUDIO_CACHE
            $env:REPROSTUDIO_CACHE = $cacheDir
            try { & $stagedExe @runArgs | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray } }
            finally { $env:REPROSTUDIO_CACHE = $previous }

            if ($LASTEXITCODE -ne 0) { throw "Pre-provisioning $label failed." }
        }
    }

    # nupkgs\ is the download staging area and local-winui\ holds unpacked overrides.
    # Both are inputs that have already been consumed; versions\ is the payload.
    foreach ($intermediate in @('nupkgs', 'local-winui')) {
        $path = Join-Path $cacheDir $intermediate
        if (Test-Path $path) { Remove-Item $path -Recurse -Force }
    }

    Write-Host ("  cache: " + (Measure-Tree $cacheDir)) -ForegroundColor DarkGray

    # --payload none matters here. Without it, a DLL dropped into payload\ would be
    # picked up by the stock launcher too, and the "stock" half of the comparison
    # would quietly be the private build. A stock reading that is not stock is worse
    # than no reading, because nothing about it looks wrong.
    Write-CmdLauncher (Join-Path $stage 'run-stock.cmd') $Preprovision[0] `
        ' --payload none' ('stock Windows App SDK ' + $Preprovision[0])

    if ($stagedWinUi) {
        Write-CmdLauncher (Join-Path $stage 'run-fixed.cmd') $Preprovision[0] `
            " --winui `"%~dp0$stagedWinUi`" --payload none" 'the local WinUI build'
    }

    # Runs whatever is in payload\, on top of the same pinned version, so a dropped
    # DLL is directly comparable with run-stock.cmd.
    Write-CmdLauncher (Join-Path $stage 'run-payload.cmd') $Preprovision[0] `
        " --payload `"%~dp0payload`"" 'the files in payload\'
}

# The bundle root is ~200 loose DLLs. Leave a note so whoever unzips it on a test
# machine isn't left guessing which file to run. An offline bundle is a different
# thing to explain than a plain one, so the variable parts are built up first.
if ($Preprovision) {
    $runLines = @(
        '    run-stock.cmd   samples\hello.cs   <- stock Windows App SDK ' + $Preprovision[0]
        '    run-payload.cmd samples\hello.cs   <- the same, plus whatever is in payload\'
    )
    if ($stagedWinUi) {
        $runLines = @(
            '    run-stock.cmd   samples\hello.cs   <- stock Windows App SDK ' + $Preprovision[0]
            '    run-fixed.cmd   samples\hello.cs   <- the same, with the local WinUI build'
            '    run-payload.cmd samples\hello.cs   <- the same, plus whatever is in payload\'
        )
    }

    $howToRun = ($runLines -join "`n") + @"


Use those rather than ReproStudio.exe directly. They point the cache at the copy
of it in this folder, which is what lets this run with no internet.
"@

    $needs = @"
  - Nothing else. Windows App SDK $($Preprovision -join ', ') is already unpacked in
    cache\, so there is nothing to download.
"@

    $cacheNote = @'
This bundle carries its own cache\ folder. The run-*.cmd files point at it, so
nothing is downloaded and nothing is written outside this folder.
'@
}
else {
    $howToRun = '    ReproStudio.exe samples\hello.cs'

    $needs = @'
  - Internet. Windows App SDK versions are downloaded from NuGet the first time
    you ask for one, then cached.
'@

    $cacheNote = @'
Downloads and provisioned runners go to %LOCALAPPDATA%\winui-repro-app.
Set REPROSTUDIO_CACHE to put them somewhere else.
'@
}

$readme = @"
ReproStudio - portable build

Run a repro:

$howToRun

It watches the file, so every save refreshes the preview. Ctrl+C stops it.
Try samples\full-header.cs to see every option a repro file can set.

Testing a private build:

    Copy the DLL into payload\ and run again. Anything in that folder is copied
    over the Windows App SDK runtime, so it beats the stock file of the same
    name. See payload\README.txt. Take a stock reading first, so you can tell
    a real difference from a broken harness.

If something doesn't work, start here:

    ReproStudio.exe --doctor

All options:

    ReproStudio.exe --help

What you need on this machine:

  - Windows 10 1809 (build 17763) or newer.
$needs
  - Developer Mode, but ONLY for repros using "// packaged: yes".
    Settings > Privacy & security > For developers.

You do NOT need a dev SDK, .NET, or the Windows App SDK runtime installed.

$cacheNote
Careful: a repro file's C# is compiled and run for real, with no sandbox. Only
run repros you trust.
"@
Set-Content -Path (Join-Path $stage 'READ-ME-FIRST.txt') -Value $readme -Encoding utf8

Write-Host ("  " + (Measure-Tree $stage)) -ForegroundColor DarkGray

if (-not $NoZip) {
    $zip = $stage + '.zip'
    Write-Host "Zipping $zip..." -ForegroundColor Cyan
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $true)
    Write-Host ('  {0:N0} MB' -f ((Get-Item $zip).Length / 1MB)) -ForegroundColor DarkGray
}

Write-Host 'Done.' -ForegroundColor Green
if ($stagedWinUi) {
    Write-Host '  Copy the folder to the target machine, then:' -ForegroundColor DarkGray
    Write-Host '    run-stock.cmd samples\hello.cs   (stock)' -ForegroundColor DarkGray
    Write-Host '    run-fixed.cmd samples\hello.cs   (local WinUI build)' -ForegroundColor DarkGray
}
elseif ($Preprovision) {
    Write-Host '  Copy the folder to the target machine, then: run-stock.cmd samples\hello.cs' -ForegroundColor DarkGray
}
else {
    Write-Host '  Unzip on the target machine, then: ReproStudio.exe samples\hello.cs' -ForegroundColor DarkGray
}
Write-Host '  To test a private build there, drop the DLL into payload\.' -ForegroundColor DarkGray
