<#
.SYNOPSIS
    Compiles this title's custom effects for an Xbox shader profile.

.DESCRIPTION
    Everything else in Content - textures, SpriteFonts, PCM and MP3 - is built once by
    the desktop project and loaded unchanged on console. Compiled shaders are the
    exception. An .mgfxo carries a profile byte, MonoGame's Effect.ReadHeader compares it
    against the running backend's own profile, and the console backend reports 22 (Xbox
    Series) or 21 (Xbox One) where desktop DirectX 12 reports 2. Handing a desktop-built
    effect to the console therefore throws

        This MGFX effect was built for a different platform!

    ...which is what crashed the title on entering a match. This script builds the same
    .fx a second time with the GDK shader profile so the console layout can carry its own
    copy.

    It needs two things that the desktop content build does not:

      * The MonoGame fork with the GDKX overlay (GDKX\Tools\MonoGame.Effect.Compiler\
        ShaderProfile.XS.cs and ShaderProfile.XB.cs). The overlay is imported
        automatically by MonoGame.Effect.Compiler.csproj when the folder is present, so
        building MGCB out of that tree is all that is required. Pass -MonoGameRoot or set
        MONOGAME_GDKX_ROOT; the default is a MonoGame checkout beside this repository.

      * The GDK's Xbox shader compiler. The overlay looks for it at
        %GameDKLatest%\GXDK\bin\<Scarlett|XboxOne>\DXC.exe, which is where the June 2024
        GDK put it. Recent GDKs ship it as <version>\xbox\bin\<gen9|gen8>\dxc.exe instead,
        so when the expected path is missing this script points GameDKLatest at a small
        shim directory of junctions in artifacts\ that presents the old shape. Nothing is
        copied and the GDK is not modified.

    The output is the console counterpart of the desktop content folder:

        src\NetRumble.Game\Content\bin\<profile>\Content\Effects\*.xnb

    NetRumble.Game.GDKX.csproj stages that over the desktop effects when it is present,
    and warns (rather than fails) when it is not - MatchRenderer draws the lit pass unlit
    if the effect will not load, so a machine without the fork can still produce a
    playable console build.

.PARAMETER ShaderProfile
    XboxSeries (profile 22, Scarlett) or XboxOne (profile 21). Must match the console the
    package targets; the profile byte is checked per backend, not per family.

.PARAMETER MonoGameRoot
    The MonoGame checkout carrying the GDKX overlay.

.PARAMETER SkipToolBuild
    Reuse a previously built MGCB instead of rebuilding it.

.EXAMPLE
    tools\Build-ConsoleShaders.ps1
    tools\Build-ConsoleShaders.ps1 -ShaderProfile XboxOne
#>
[CmdletBinding()]
param(
    [ValidateSet('XboxSeries', 'XboxOne')]
    [string]$ShaderProfile = 'XboxSeries',

    [string]$MonoGameRoot = $env:MONOGAME_GDKX_ROOT,

    [switch]$SkipToolBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$contentDir = Join-Path $repoRoot 'src\NetRumble.Game\Content'
$effectsDir = Join-Path $contentDir 'Effects'

function Resolve-MonoGameRoot {
    param([string]$Requested)

    $candidates = @()
    if ($Requested) { $candidates += $Requested }
    $candidates += (Join-Path (Split-Path -Parent $repoRoot) 'MonoGame')

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path (Join-Path $candidate 'GDKX\MonoGame.Effect.Compiler.targets'))) {
            return (Resolve-Path $candidate).Path
        }
    }

    throw @"
No MonoGame checkout with the GDKX overlay was found$(if ($Requested) { " (tried '$Requested')" }).
The overlay supplies the Xbox shader profiles and lives at <root>\GDKX\.
Pass -MonoGameRoot <path>, or set MONOGAME_GDKX_ROOT. See docs/xbox-console-build.md.
"@
}

# The overlay's hard-coded DXC location is from the June 2024 GDK. Present that shape
# from wherever this machine's GDK actually keeps the compiler, using junctions so the
# files stay in place and stay patchable by the GDK installer.
function Resolve-GameDkLatest {
    param([string]$ShaderProfile)

    $leaf = if ($ShaderProfile -eq 'XboxSeries') { 'Scarlett' } else { 'XboxOne' }

    $existing = $env:GameDKLatest
    if ($existing -and (Test-Path (Join-Path $existing "GXDK\bin\$leaf\DXC.exe"))) {
        return $existing
    }

    $gdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Microsoft GDK'
    if (-not (Test-Path $gdkRoot)) {
        throw "The Microsoft GDK was not found at '$gdkRoot'. The Xbox shader compiler ships with it."
    }

    # Version folders sort newest-last; anything else in there (bin, redist) is not one.
    $version = Get-ChildItem $gdkRoot -Directory |
        Where-Object { $_.Name -match '^\d+$' } |
        Sort-Object Name |
        Select-Object -Last 1

    if (-not $version) {
        throw "No versioned GDK was found under '$gdkRoot'."
    }

    $gen = if ($ShaderProfile -eq 'XboxSeries') { 'gen9' } else { 'gen8' }
    $source = Join-Path $version.FullName "xbox\bin\$gen"

    if (-not (Test-Path (Join-Path $source 'dxc.exe'))) {
        throw "The Xbox shader compiler was not found at '$source\dxc.exe'. Install the GDK's console tools."
    }

    $shim = Join-Path $repoRoot 'artifacts\gdkshim'
    $link = Join-Path $shim "GXDK\bin\$leaf"

    if (-not (Test-Path $link)) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $link) | Out-Null
        cmd /c mklink /J "`"$link`"" "`"$source`"" | Out-Null
        if (-not (Test-Path (Join-Path $link 'dxc.exe'))) {
            throw "Could not link '$link' to '$source'."
        }
    }

    Write-Host "  DXC: $source (via shim)"
    return $shim
}

$monoGameRoot = Resolve-MonoGameRoot -Requested $MonoGameRoot
$mgcbProject = Join-Path $monoGameRoot 'Tools\MonoGame.Content.Builder\MonoGame.Content.Builder.csproj'
$mgcb = Join-Path $monoGameRoot 'Artifacts\MonoGame.Content.Builder\Release\mgcb.dll'

Write-Host "MonoGame (GDKX overlay): $monoGameRoot"

if (-not $SkipToolBuild -or -not (Test-Path $mgcb)) {
    Write-Host 'Building MGCB with the GDKX shader profiles...'

    # DisableNativeBuild skips the premake5-driven native pipeline build, which is only
    # needed for texture and audio processing - this script builds effects only.
    & dotnet build $mgcbProject -c Release --nologo -v quiet -p:DisableNativeBuild=True
    if ($LASTEXITCODE -ne 0) {
        throw "Building MGCB failed. See the output above."
    }
}

if (-not (Test-Path $mgcb)) {
    throw "MGCB was not produced at '$mgcb'."
}

$env:GameDKLatest = Resolve-GameDkLatest -ShaderProfile $ShaderProfile

$outputDir = Join-Path $contentDir "bin\$ShaderProfile\Content"
$intermediateDir = Join-Path $contentDir "obj\$ShaderProfile"

$effects = Get-ChildItem $effectsDir -Filter *.fx -Recurse -File
if (-not $effects) {
    Write-Host "No effects under '$effectsDir'; nothing to build."
    return
}

$expectedProfileId = if ($ShaderProfile -eq 'XboxSeries') { 22 } else { 21 }

foreach ($effect in $effects) {
    $relative = $effect.FullName.Substring($contentDir.Length).TrimStart('\').Replace('\', '/')
    Write-Host "Compiling $relative for $ShaderProfile..."

    Push-Location $contentDir
    try {
        & dotnet $mgcb `
            "/platform:$ShaderProfile" `
            "/outputDir:$outputDir" `
            "/intermediateDir:$intermediateDir" `
            '/importer:EffectImporter' `
            '/processor:EffectProcessor' `
            '/processorParam:DebugMode=Auto' `
            "/build:$relative"
        if ($LASTEXITCODE -ne 0) {
            throw "MGCB failed for '$relative'."
        }
    }
    finally {
        Pop-Location
    }

    $xnb = Join-Path $outputDir ([IO.Path]::ChangeExtension($relative.Replace('/', '\'), '.xnb'))
    if (-not (Test-Path $xnb)) {
        throw "MGCB reported success but produced no '$xnb'."
    }

    # MGCB reports success even when the Xbox DXC declines to emit bytecode - it did
    # exactly that until the /rootsig-define argument was fixed, leaving a well-formed
    # effect with empty shaders that draws nothing. Check the header, and check that
    # there is something in the file to draw with.
    $bytes = [IO.File]::ReadAllBytes($xnb)
    $magic = -1
    for ($i = 0; $i -lt [Math]::Min(512, $bytes.Length - 6); $i++) {
        if ($bytes[$i] -eq 0x4D -and $bytes[$i + 1] -eq 0x47 -and $bytes[$i + 2] -eq 0x46 -and $bytes[$i + 3] -eq 0x58) {
            $magic = $i
            break
        }
    }

    if ($magic -lt 0) {
        throw "'$xnb' carries no MGFX header."
    }

    $actualProfileId = $bytes[$magic + 5]
    if ($actualProfileId -ne $expectedProfileId) {
        throw "'$xnb' was built for shader profile $actualProfileId, not $expectedProfileId ($ShaderProfile)."
    }

    if ($bytes.Length -lt 2048) {
        throw "'$xnb' is only $($bytes.Length) bytes - the shader compiler emitted no bytecode."
    }

    Write-Host "  -> $xnb (profile $actualProfileId, $($bytes.Length) bytes)"
}

Write-Host ''
Write-Host "Console effects are in $outputDir."
Write-Host 'Build NetRumble.Game.GDKX (or run tools\Build-Gdkx.ps1) to stage them.'
