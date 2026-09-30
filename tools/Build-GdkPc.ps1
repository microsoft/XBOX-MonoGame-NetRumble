<#
.SYNOPSIS
    Builds the PC (Gaming.Desktop) flavour of the game and stages a GDK package layout.

.DESCRIPTION
    The PC counterpart to tools\Build-Gdkx.ps1, and the replacement for the old
    packaging\Make-Package.ps1. Three steps:

      1. dotnet publish of src\NetRumble.Game - self-contained win-x64. Self-contained is
         not a preference: a packaged GDK title runs in a sandbox that does not provide
         the shared .NET runtime, so a framework-dependent publish installs and then
         fails to start.
      2. Stage the layout under artifacts\gdkpc\layout\<Configuration>: the publish
         output with the apphost renamed to the name the config declares, the content
         tree, MicrosoftGame.config, the store logos and the GDK redistributables the
         title loads at runtime.
      3. Optionally run MakePkg over it to produce an .msixvc.

    Unlike the console pipeline, everything here can be run on the build machine: the
    layout is ordinary win-x64 code, and `wdapp` / `xbapp install` of the package targets
    the same PC it was built on.

    What the layout carries beyond the publish output, and why:

      * MicrosoftGame.PC.config, staged as MicrosoftGame.config. It declares only
        NetRumblePC.exe; the repo's MicrosoftGame.config declares the console executable
        too, and MakePkg rejects a layout that is missing an executable its config names.
        See packaging\MicrosoftGame.PC.config.
      * Party.dll, PlayFabCore.dll, PlayFabServices.dll, libHttpClient.dll,
        Microsoft.Xbox.Services.C.Thunks.dll and XCurl.dll. These are redistributables,
        not system components: nothing installs them machine-wide, and inside the package
        sandbox only what the layout carries is loadable. Without them the title installs
        and runs but PartyRuntimeProbe and XblRuntimeProbe report unavailable and the
        game degrades to offline - a packaged build that silently loses multiplayer.
      * xgameruntime.thunks.dll already arrives in the publish output;
        NetRumble.Platform.GameCore copies it out of the installed GDK. It is verified
        here rather than copied again, because a layout missing it has no Gaming Runtime
        at all.

    Redistributables are taken from the installed GDK (windows\bin\x64) when there is
    one. The vendored third_party\party copies are the fallback for Party, PlayFab Core
    and libHttpClient only: they are Gaming.Desktop builds, so they are the right
    architecture for this target - which is exactly why the console build refuses them -
    but the set is incomplete, and a package built from the fallback is missing Xbox
    Services and PlayFab Services.

.PARAMETER Configuration
    Release by default. Debug layouts are fine for loose deployment and local installs
    but must never be packaged for submission.

.PARAMETER OutputRoot
    Where the publish output, layout and package are written. Defaults to
    artifacts\gdkpc, mirroring artifacts\gdkx for the console.

.PARAMETER StoreLogos
    Folder holding StoreLogo.png and friends. Defaults to the repository's storelogos
    folder, which is also what the console bootstrap project stages.

.PARAMETER Package
    Also run MakePkg over the staged layout to produce an .msixvc.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$OutputRoot,

    [string]$StoreLogos,

    [switch]$Package
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $OutputRoot) { $OutputRoot = Join-Path $repoRoot 'artifacts\gdkpc' }

$publishDir = Join-Path $OutputRoot "publish\$Configuration"
$layout = Join-Path $OutputRoot "layout\$Configuration"
$projectPath = Join-Path $repoRoot 'src\NetRumble.Game\NetRumble.Game.csproj'

# --- 1. Publish ---------------------------------------------------------------

Write-Host "Publishing $Configuration for PC..." -ForegroundColor Cyan

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

# NoWarn=NU1603 for the same reason tools\Build-Gdkx.ps1 passes it: GDK.Net pins
# Microsoft.NET.ILLink.Tasks to the 8.0 feature band and builds with
# TreatWarningsAsErrors, so a machine with a newer SDK and no nuget.org resolves a later
# package and the warning becomes an error. It has to be a global property - restore
# raises it while evaluating GDK.Net, which does not inherit a referencing project's
# AdditionalProperties. See docs/xbox-console-build.md.
dotnet publish $projectPath `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    --output $publishDir `
    --nologo -v minimal /p:NoWarn=NU1603

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

# --- 2. Layout ----------------------------------------------------------------

Write-Host "Staging layout at $layout..." -ForegroundColor Cyan

if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
New-Item -ItemType Directory -Path $layout -Force | Out-Null
Copy-Item (Join-Path $publishDir '*') $layout -Recurse -Force

# Debug symbols are publish output, not shipping content. The submission validator fails
# every .pdb it finds as a foreign file, so strip them here rather than leaving a package
# that builds cleanly and is rejected at submission.
Get-ChildItem $layout -Recurse -Filter '*.pdb' -File | Remove-Item -Force

$publishedExe = Join-Path $layout 'NetRumble.Game.exe'
if (-not (Test-Path $publishedExe)) {
    throw "Expected $publishedExe in the publish output; the assembly name may have changed. The config and this script both name the executable NetRumblePC.exe."
}
Rename-Item $publishedExe 'NetRumblePC.exe'

Copy-Item (Join-Path $repoRoot 'packaging\MicrosoftGame.PC.config') `
          (Join-Path $layout 'MicrosoftGame.config') -Force

# XR-055. The SCID XSAPI is initialized with. Without this the title falls back to
# deriving one from the title id, which is right only for a title on the default service
# configuration and fails at the first achievement write for any other.
Copy-Item (Join-Path $repoRoot 'packaging\xboxservices.config') `
          (Join-Path $layout 'xboxservices.config') -Force

if (-not (Test-Path (Join-Path $layout 'Content'))) {
    throw "The publish output staged no Content folder at '$layout'. The MGCB task did not run, and the title cannot load a single asset."
}

# The Gaming Runtime thunks come from the build, not from here: the flat GDK API is
# linked in from a static library and is not exported by any DLL, so this redistributable
# is the only way managed code reaches it. NetRumble.Platform.GameCore copies it and
# warns when the GDK is absent; that warning is a hard failure once a package is the
# goal, because the packaged title has no other Gaming Runtime to fall back on.
if (-not (Test-Path (Join-Path $layout 'xgameruntime.thunks.dll'))) {
    throw 'xgameruntime.thunks.dll is not in the publish output, so this build has no Gaming Runtime and would package a permanently offline title. Install the Microsoft GDK, or set GameRuntimeThunks to a copy of it, and rebuild.'
}

# Store logos. These are shipping package assets, so silently replacing missing art with
# generated placeholders would produce a valid-looking package with the wrong branding.
$logoSource = if ($StoreLogos) { $StoreLogos } else { Join-Path $repoRoot 'storelogos' }
$layoutLogos = Join-Path $layout 'storelogos'
$requiredLogos = @('StoreLogo.png', 'Logo.png', 'SmallLogo.png', 'LargeLogo.png', 'SplashScreen.png')
$missingLogos = @($requiredLogos | Where-Object { -not (Test-Path (Join-Path $logoSource $_)) })

if ($missingLogos.Count -gt 0) {
    throw "Store logos are incomplete at '$logoSource'. Missing: $($missingLogos -join ', ')."
}

Copy-Item $logoSource $layoutLogos -Recurse -Force
Write-Host "  store logos from $logoSource" -ForegroundColor DarkGray

# Redistributables. The GDK's own copies first; the vendored Gaming.Desktop binaries are
# the fallback for the three they cover.
$gdkRedistDir = $null
if ($env:GameDKCoreLatest) {
    $candidate = Join-Path $env:GameDKCoreLatest 'windows\bin\x64'
    if (Test-Path $candidate) { $gdkRedistDir = $candidate }
}

$vendoredDir = Join-Path $repoRoot 'third_party\party'

# XCurl is the HTTP stack libHttpClient uses on PC; Party and PlayFab both sit on it.
# PlayFabServices and the Xbox Services thunks have no vendored copy at all, which is
# what makes a GDK-less package a degraded one rather than merely an unsigned one.
$redists = @(
    @{ Name = 'Party.dll';                          Vendored = $true;  Required = $true },
    @{ Name = 'PlayFabCore.dll';                    Vendored = $true;  Required = $true },
    @{ Name = 'libHttpClient.dll';                  Vendored = $true;  Required = $true },
    @{ Name = 'PlayFabServices.dll';                Vendored = $false; Required = $true },
    @{ Name = 'Microsoft.Xbox.Services.C.Thunks.dll'; Vendored = $false; Required = $true },
    @{ Name = 'XCurl.dll';                          Vendored = $false; Required = $false }
)

$missing = @()
foreach ($redist in $redists) {
    $source = $null
    if ($gdkRedistDir) {
        $fromGdk = Join-Path $gdkRedistDir $redist.Name
        if (Test-Path $fromGdk) { $source = $fromGdk }
    }
    if (-not $source -and $redist.Vendored) {
        $fromVendor = Join-Path $vendoredDir $redist.Name
        if (Test-Path $fromVendor) { $source = $fromVendor }
    }

    if ($source) {
        Copy-Item $source $layout -Force
    }
    elseif ($redist.Required) {
        $missing += $redist.Name
    }
}

if ($missing.Count -gt 0) {
    # A warning rather than an error, matching how the rest of the port treats an absent
    # GDK: the offline provider is complete, so the package still installs and plays a
    # practice match. It just cannot sign in or host a match, which is not something to
    # discover after installing it.
    Write-Warning "These redistributables were not found and are not in the layout: $($missing -join ', '). The packaged title will degrade to offline. Install the Microsoft GDK and rerun."
}

Write-Host ''
Write-Host "PC layout: $layout" -ForegroundColor Green

if (-not $Package) {
    Write-Host 'Skipping MakePkg (pass -Package to build one).' -ForegroundColor DarkGray
    return
}

# --- 3. Packaging -------------------------------------------------------------

$makePkg = Get-Command 'makepkg.exe' -ErrorAction SilentlyContinue
if (-not $makePkg) {
    $gdkBin = Join-Path ${env:ProgramFiles(x86)} 'Microsoft GDK\bin\makepkg.exe'
    if (Test-Path $gdkBin) {
        $makePkg = Get-Command $gdkBin
    }
    else {
        throw 'makepkg.exe was not found on PATH or in the GDK bin folder. Open a Microsoft GDK command prompt and retry.'
    }
}

$packageDir = Join-Path $OutputRoot "$Configuration\Package"
New-Item -ItemType Directory -Force $packageDir | Out-Null
$mapFile = Join-Path $packageDir 'layout.xml'

# genmap enumerates everything under the layout, so a previous packaging run whose output
# landed inside it would be packed into the next package. The layout is a build output
# and only this script writes it; anything else in there is stale.
$strays = Get-ChildItem $layout -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -in @('Package', 'pkg') }
foreach ($stray in $strays) {
    Write-Warning "Removing stale packaging output from the layout: $($stray.FullName)"
    Remove-Item $stray.FullName -Recurse -Force
}

# genmap first: MakePkg packs from a manifest enumerating every file, not from a folder.
& $makePkg.Source genmap /f $mapFile /d $layout
if ($LASTEXITCODE -ne 0) { throw "makepkg genmap failed with exit code $LASTEXITCODE." }

# /pc is what makes this a PC package - without it MakePkg builds a console XVC, embeds a
# Game OS image and produces something no PC will install. /lt marks it loose-tools/local
# test signed, which is what a development machine will accept.
& $makePkg.Source pack /pc /f $mapFile /lt /d $layout /pd $packageDir
if ($LASTEXITCODE -ne 0) { throw "makepkg pack failed with exit code $LASTEXITCODE." }

$packageFile = Get-ChildItem (Join-Path $packageDir '*.msixvc') -ErrorAction SilentlyContinue |
    Sort-Object -Descending LastWriteTime | Select-Object -First 1
if ($packageFile) {
    Write-Host ("Package: {0} ({1:N0} MB)" -f $packageFile.Name, ($packageFile.Length / 1MB)) -ForegroundColor Green
}

Write-Host "Package written to $packageDir" -ForegroundColor Green
Write-Host 'Install it locally with: wdapp install <package>' -ForegroundColor DarkGray
