<#
.SYNOPSIS
    Wraps a locally built WinUI into a .nupkg that ReproStudio can use with --winui.

.DESCRIPTION
    ReproStudio can swap just the WinUI component of a Windows App SDK runner, either
    for another published version or for a local .nupkg (see "--winui" and the
    "// winui:" header key). This script builds that .nupkg out of a private WinUI
    build, so you can run a repro against bits you just compiled.

    The provisioner only ever reads two paths out of a WinUI package:

        runtimes-framework\win-x64\native\
        runtimes\win-x64\native\

    so that is all this produces. It is not a publishable package - no real nuspec
    metadata, no dependencies - just the shape ReproStudio reads.

    Only files the WinUI component actually owns are picked up (see $WinUiFiles). A
    private build's output folder usually mixes in binaries from other Windows App SDK
    components - InteractiveExperiences ships dcompi.dll, dwmcorei.dll,
    Microsoft.UI.Input.dll and friends - and overlaying those would silently mix two
    different component versions. Whatever the source folder does not have is simply
    left alone, so the stock version's copy keeps being used.

.PARAMETER Source
    Folder holding the built WinUI binaries. For the microsoft-ui-xaml repo this is
    BuildOutput\bin\<flavour>\Product, or BuildOutput\packaging\<config>\runtimes\<rid>\native.

.PARAMETER OutputPath
    Where to write the .nupkg. Defaults to artifacts\local-winui\ next to this repo.

.PARAMETER Version
    Version string for the file name. Defaults to the source Microsoft.ui.xaml.dll's
    file version plus its build date, which keeps successive builds distinguishable.

.PARAMETER IncludeLanguageResources
    Also copy the localized resource folders (af-ZA, de-DE, ...). Off by default: a
    private build rarely rebuilds them, and the stock version already has a matching set.

.EXAMPLE
    .\tools\pack-local-winui.ps1 -Source D:\winui\BuildOutput\bin\amd64chk\Product

.EXAMPLE
    # Then run a repro against it:
    ReproStudio.exe bug.cs --wasdk 2.3.1 --winui artifacts\local-winui\<name>.nupkg
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Source,

    [string] $OutputPath,

    [string] $Version,

    [switch] $IncludeLanguageResources
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# The native payload of the real Microsoft.WindowsAppSDK.WinUI package. Anything not
# on this list belongs to another component and must not be overlaid.
$WinUiFiles = @(
    'Microsoft.ui.xaml.dll'
    'Microsoft.UI.Xaml.Controls.dll'
    'Microsoft.UI.Xaml.Controls.pri'
    'Microsoft.UI.Xaml.Internal.dll'
    'Microsoft.UI.Xaml.Phone.dll'
    'Microsoft.ui.xaml.resources.19h1.dll'
    'Microsoft.ui.xaml.resources.common.dll'
    'WinUIEdit.dll'
)

# The themes folder (generic.xaml and friends), shipped as a directory.
$WinUiFolders = @('Microsoft.UI.Xaml')

if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
    throw "Source folder not found: $Source"
}
$Source = (Resolve-Path -LiteralPath $Source).Path

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputPath) { $OutputPath = Join-Path $repoRoot 'artifacts\local-winui' }

$core = Join-Path $Source 'Microsoft.ui.xaml.dll'
if (-not (Test-Path -LiteralPath $core)) {
    throw "No Microsoft.ui.xaml.dll in $Source. Point -Source at the folder holding the built WinUI binaries."
}

if (-not $Version) {
    $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($core)
    # FileVersion carries a machine/commit suffix in a private build; keep only the digits.
    $numeric = ($info.FileVersion -split ' ')[0]
    $stamp = (Get-Item -LiteralPath $core).LastWriteTime.ToString('yyMMdd-HHmm')
    $Version = "$numeric-local-$stamp"
}

Write-Host "Packing local WinUI $Version" -ForegroundColor Cyan
Write-Host "  from $Source" -ForegroundColor DarkGray

$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("winui-local-" + [Guid]::NewGuid().ToString('N'))
# runtimes-framework is what the modern (1.8+) component package uses, and the
# provisioner looks there first.
$native = Join-Path $staging 'runtimes-framework\win-x64\native'
New-Item -ItemType Directory -Path $native -Force | Out-Null

try {
    $copied = @()
    foreach ($name in $WinUiFiles) {
        $path = Join-Path $Source $name
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            Copy-Item -LiteralPath $path -Destination (Join-Path $native $name) -Force
            $copied += $name
        }
    }

    foreach ($name in $WinUiFolders) {
        $path = Join-Path $Source $name
        if (Test-Path -LiteralPath $path -PathType Container) {
            Copy-Item -LiteralPath $path -Destination $native -Recurse -Force
            $copied += "$name\"
        }
    }

    if ($IncludeLanguageResources) {
        # Locale folders are the only other thing the component ships. Matched by shape
        # (xx-YY) rather than a list, so a new locale does not need a script change.
        $locales = @(Get-ChildItem -LiteralPath $Source -Directory |
            Where-Object { $_.Name -match '^[a-z]{2,3}(-[A-Za-z]+)+$' })
        foreach ($locale in $locales) {
            Copy-Item -LiteralPath $locale.FullName -Destination $native -Recurse -Force
        }
        if ($locales.Count -gt 0) { $copied += "$($locales.Count) locale folders" }
    }

    if ($copied.Count -eq 0) { throw "Nothing to pack: no WinUI binaries found in $Source." }

    foreach ($name in $copied) { Write-Host "    $name" -ForegroundColor DarkGray }

    New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
    $nupkg = Join-Path $OutputPath "Microsoft.WindowsAppSDK.WinUI.$Version.nupkg"
    if (Test-Path -LiteralPath $nupkg) { Remove-Item -LiteralPath $nupkg -Force }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $staging, $nupkg, [System.IO.Compression.CompressionLevel]::Optimal, $false)
}
finally {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}

$size = (Get-Item -LiteralPath $nupkg).Length / 1MB
Write-Host ('  {0:N0} MB -> {1}' -f $size, $nupkg) -ForegroundColor Green
Write-Output $nupkg
