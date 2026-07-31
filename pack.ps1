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

.PARAMETER NoZip
    Stage the folder but skip the zip.

.EXAMPLE
    .\pack.ps1
    .\pack.ps1 -Platform ARM64
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'x86', 'ARM64')]
    [string] $Platform = 'x64',

    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',

    [string] $OutputRoot,

    [switch] $NoZip
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = $PSScriptRoot
if (-not $OutputRoot) { $OutputRoot = Join-Path $repoRoot 'artifacts' }

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

# The bundle root is ~200 loose DLLs. Leave a note so whoever unzips it on a test
# machine isn't left guessing which file to run.
$readme = @"
ReproStudio - portable build

Run a repro:

    ReproStudio.exe samples\hello.cs

It watches the file, so every save refreshes the preview. Ctrl+C stops it.
Try samples\full-header.cs to see every option a repro file can set.

If something doesn't work, start here:

    ReproStudio.exe --doctor

All options:

    ReproStudio.exe --help

What you need on this machine:

  - Windows 10 1809 (build 17763) or newer.
  - Internet. Windows App SDK versions are downloaded from NuGet the first time
    you ask for one, then cached.
  - Developer Mode, but ONLY for repros using "// packaged: yes".
    Settings > Privacy & security > For developers.

You do NOT need a dev SDK, .NET, or the Windows App SDK runtime installed.

Downloads and provisioned runners go to %LOCALAPPDATA%\winui-repro-app.
Set REPROSTUDIO_CACHE to put them somewhere else.

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
Write-Host '  Unzip on the target machine, then: ReproStudio.exe samples\hello.cs' -ForegroundColor DarkGray
