<#
.SYNOPSIS
    One-time setup of the external GDKX toolchain.

.DESCRIPTION
    The Xbox build needs two things that are not on NuGet and are not vendored here:

      MonoGame with the GDKX overlay
          MonoGame.XB1's develop branch is not a fork of MonoGame - it is an overlay that
          expects to sit inside a MonoGame checkout as GDKX/. It supplies
          native/monogame, the Xbox backend that produces mgruntime.dll, which is the
          console counterpart of the WindowsDX12 runtime the desktop build gets from the
          MonoGame NuGet package.

      NativeAOT-GDKX
          The Xbox port of the CoreCLR NativeAOT runtime: the PAL, the native BCL helpers
          and the direct-P/Invoke list. The Xbox OS cannot host CoreCLR, so this is what
          makes running .NET on a console possible at all.

    This script checks both are present, creates the GDKX overlay as a git worktree if
    MonoGame.XB1 is available, and builds mgruntime.dll. It is safe to re-run.

    Everything lands beside the repo rather than inside it, so nothing here ends up in
    MGRumble's history. See docs/xbox-console-build.md for the layout and for what to do if your
    checkouts live somewhere else.

.PARAMETER Root
    Where the external repositories live. Defaults to MGRumble's parent directory.

.PARAMETER Platform
    Gaming.Xbox.Scarlett.x64 (Series X|S) or Gaming.Xbox.XboxOne.x64.

.PARAMETER Configuration
    Debug or Release.

.PARAMETER PlatformToolset
    The MSVC toolset to build the native halves with. Defaults to v145 (VS 2026); pass
    v143 for VS 2022.
#>
[CmdletBinding()]
param(
    [string]$Root,
    [ValidateSet('Gaming.Xbox.Scarlett.x64', 'Gaming.Xbox.XboxOne.x64')]
    [string]$Platform = 'Gaming.Xbox.Scarlett.x64',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$PlatformToolset = 'v145'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Root) { $Root = Split-Path -Parent $repoRoot }

$monoGame = Join-Path $Root 'MonoGame'
$monoGameXb1 = Join-Path $Root 'MonoGame.XB1'
$nativeAot = Join-Path $Root 'NativeAOT-GDKX'
$overlay = Join-Path $monoGame 'GDKX'

Write-Host "Toolchain root: $Root" -ForegroundColor Cyan

# --- Prerequisites ------------------------------------------------------------

if (-not (Test-Path $monoGame)) {
    throw "MonoGame was not found at $monoGame. Clone https://github.com/MonoGame/MonoGame there."
}
if (-not (Test-Path $nativeAot)) {
    throw "NativeAOT-GDKX was not found at $nativeAot. Clone it there; it must stay a sibling of the dotnet/runtime checkout it compiles the PAL from."
}
if (-not (Test-Path (Join-Path $Root 'runtime'))) {
    throw "A dotnet/runtime checkout (release/8.0) was not found at $(Join-Path $Root 'runtime'). NativeAOT-GDKX's Runtime project compiles CoreCLR sources straight out of it."
}

$gdk = Get-ChildItem 'C:\Program Files (x86)\Microsoft GDK' -Directory -ErrorAction SilentlyContinue |
    Where-Object { Test-Path (Join-Path $_.FullName 'xbox\include\XGameRuntime.h') } |
    Sort-Object Name -Descending | Select-Object -First 1
if (-not $gdk) {
    throw 'No Microsoft GDK with the Xbox extensions was found. Install the GDK *and* the Xbox extensions (the "xbox" folder) - the console build needs the GXDK headers and libraries, not just the desktop GRDK.'
}
Write-Host "GDK with Xbox extensions: $($gdk.Name)" -ForegroundColor DarkGray

function Find-VsMSBuild {
    # vswhere is the supported way to locate an install; guessing at paths breaks on
    # Build Tools SKUs and on side-by-side VS versions.
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) { return $null }
    $vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vsPath) { return $null }
    $msbuild = Join-Path $vsPath 'MSBuild\Current\Bin\MSBuild.exe'
    if (Test-Path $msbuild) { return $msbuild }
    return $null
}
$msbuild = Find-VsMSBuild
if (-not $msbuild) {
    throw 'Visual Studio MSBuild was not found. The console projects are C++ projects; the dotnet CLI cannot build them (MSB4278).'
}

$platformDir = Join-Path (Split-Path -Parent (Split-Path -Parent $msbuild)) "..\Microsoft\VC\v180\Platforms\$Platform"
if (-not (Test-Path $platformDir)) {
    Write-Warning "The $Platform platform was not found under this Visual Studio install. Install the GDK's Visual Studio integration for the Xbox platforms."
}

# --- The GDKX overlay ---------------------------------------------------------

if (Test-Path (Join-Path $overlay 'native\monogame\monogame.xbox.vcxproj')) {
    Write-Host "GDKX overlay already present at $overlay" -ForegroundColor DarkGray
}
else {
    if (-not (Test-Path $monoGameXb1)) {
        throw "The GDKX overlay is missing and MonoGame.XB1 was not found at $monoGameXb1 to create it from. Clone MonoGame.XB1 (develop branch) there, or copy its contents to $overlay yourself."
    }
    # A worktree rather than a copy: the overlay stays a live checkout that can be pulled
    # and diffed, while the MonoGame repo it sits inside stays clean.
    Write-Host "Creating the GDKX overlay at $overlay..." -ForegroundColor Cyan
    git -C $monoGameXb1 worktree add $overlay develop
    if ($LASTEXITCODE -ne 0) { throw 'git worktree add failed.' }

    # MonoGame must not see the overlay as untracked content of its own.
    $exclude = Join-Path $monoGame '.git\info\exclude'
    if ((Test-Path $exclude) -and -not (Select-String -Path $exclude -Pattern '^/GDKX/$' -Quiet)) {
        Add-Content $exclude "`n# MonoGame.XB1's GDKX overlay, created by MGRumble's tools/Setup-Gdkx.ps1.`n/GDKX/"
    }
}

# --- MonoGame's native Xbox backend -------------------------------------------

Write-Host "Building mgruntime.dll for $Platform|$Configuration..." -ForegroundColor Cyan
& $msbuild (Join-Path $overlay 'native\monogame\monogame.xbox.vcxproj') `
    /p:Configuration=$Configuration /p:Platform=$Platform /p:PlatformToolset=$PlatformToolset `
    /m /nologo /v:minimal
if ($LASTEXITCODE -ne 0) { throw 'monogame.xbox.vcxproj failed to build.' }

$mgruntime = Join-Path $monoGame "Artifacts\native\mgruntime\$Platform\$Configuration\mgruntime.dll"
if (-not (Test-Path $mgruntime)) { throw "mgruntime.dll was not produced at $mgruntime." }

Write-Host ''
Write-Host "Toolchain ready. mgruntime.dll -> $mgruntime" -ForegroundColor Green
Write-Host 'Now run tools\Build-Gdkx.ps1 to build the game.' -ForegroundColor Green
