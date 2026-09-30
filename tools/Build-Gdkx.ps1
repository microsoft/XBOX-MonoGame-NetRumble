<#
.SYNOPSIS
    Builds the Xbox (GDKX) flavour of the game and stages a console package layout.

.DESCRIPTION
    Four steps, in order, because each feeds the next:

      1. NetRumble.Game (desktop) - only for its content. The .xnb are platform-neutral
         for this title, so console reuses what the desktop build produced rather than
         running the content pipeline twice. Skipped if content is already there.
      2. NetRumble.Game.GDKX - compiles the same game sources for net8.0 and then runs
         ILC over the result, producing artifacts\gdkx\compiled-IL-<Config>.obj.
      3. XGameRuntimeThunks.vcxproj - xgameruntime.thunks.dll for the console. The GDK
         ships this redistributable for desktop only, and GDK.Net binds the whole flat
         API to it, so the console layout has to supply its own build of the same stubs.
      4. NetRumble.Bootstrap.vcxproj - links the AOT object into NetRumbleConsole.exe and
         stages artifacts\gdkx\layout\<Platform>\<Config>\Image\Loose. That path is owned
         by the vcxproj's LayoutDir; this script only reads it, and fails loudly rather
         than reporting a directory MSBuild did not write.

    Run tools\Setup-Gdkx.ps1 once first; this script only builds MGRumble's own projects
    plus the toolchain libraries it links against.

    Both dotnet build calls pass /p:NoWarn=NU1603. GDK.Net pins Microsoft.NET.ILLink.Tasks
    to the 8.0 feature band and builds with TreatWarningsAsErrors; a machine with a newer
    SDK and no access to nuget.org resolves a later package and NuGet warns. The
    suppression has to be a global property, because NU1603 is raised while restore
    evaluates GDK.Net itself, which does not inherit a referencing project's
    AdditionalProperties. See docs/xbox-console-build.md.

    Nothing here can be verified without a devkit: the output is Xbox OS code that cannot
    run on the build machine. A successful run means it built and linked, not that it
    boots. See docs/xbox-console-build.md.

.PARAMETER Configuration
    Debug or Release.

.PARAMETER Platform
    Gaming.Xbox.Scarlett.x64 (Series X|S) or Gaming.Xbox.XboxOne.x64.

.PARAMETER PlatformToolset
    The MSVC toolset for the native projects. Defaults to v145 (VS 2026).

.PARAMETER Package
    Also run MakePkg over the staged layout to produce an .xvc.

    Store branding is always staged from the repository's storelogos folder by the
    bootstrap project. Missing required logos fail the build rather than generating
    placeholder package art.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('Gaming.Xbox.Scarlett.x64', 'Gaming.Xbox.XboxOne.x64')]
    [string]$Platform = 'Gaming.Xbox.Scarlett.x64',
    [string]$PlatformToolset = 'v145',
    [switch]$Package
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

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
if (-not $msbuild) { throw 'Visual Studio MSBuild was not found; the dotnet CLI cannot build C++ projects.' }

function Invoke-Native([string]$project, [string[]]$extra) {
    Write-Host "  $([System.IO.Path]::GetFileName($project))" -ForegroundColor DarkGray
    & $msbuild $project /p:Configuration=$Configuration /p:Platform=$Platform `
        /p:PlatformToolset=$PlatformToolset /m /nologo /v:minimal @extra
    if ($LASTEXITCODE -ne 0) { throw "$project failed to build." }
}

# --- 1. Content ---------------------------------------------------------------

$contentDir = Join-Path $repoRoot 'src\NetRumble.Game\Content\bin\WindowsDX12'
if (-not (Test-Path $contentDir)) {
    Write-Host 'Building desktop content...' -ForegroundColor Cyan
    dotnet build (Join-Path $repoRoot 'src\NetRumble.Game\NetRumble.Game.csproj') -c $Configuration --nologo -v minimal /p:NoWarn=NU1603
    if ($LASTEXITCODE -ne 0) { throw 'The desktop build failed; console content comes from it.' }
}

# --- 2. Managed + AOT ---------------------------------------------------------

Write-Host 'Compiling the game ahead of time...' -ForegroundColor Cyan
dotnet build (Join-Path $repoRoot 'src\NetRumble.Game.GDKX\NetRumble.Game.GDKX.csproj') `
    -c $Configuration --nologo -v minimal /p:GdkxConsolePlatform=$Platform /p:NoWarn=NU1603
if ($LASTEXITCODE -ne 0) { throw 'NetRumble.Game.GDKX failed to build.' }

# --- 3 and 4. Native ----------------------------------------------------------

Write-Host 'Building native projects...' -ForegroundColor Cyan
Invoke-Native (Join-Path $repoRoot 'console\XGameRuntimeThunks\XGameRuntimeThunks.vcxproj')
Invoke-Native (Join-Path $repoRoot 'console\NetRumble.Bootstrap\NetRumble.Bootstrap.vcxproj')

$layout = Join-Path $repoRoot "artifacts\gdkx\layout\$Platform\$Configuration\Image\Loose"
if (-not (Test-Path $layout)) {
    throw "The native build reported success but staged no layout at $layout."
}

Write-Host ''
Write-Host "Console layout: $layout" -ForegroundColor Green

if (-not $Package) {
    Write-Host 'Skipping MakePkg (pass -Package to build one).' -ForegroundColor DarkGray
    return
}

# --- Packaging ----------------------------------------------------------------

$makePkg = Get-Command 'makepkg.exe' -ErrorAction SilentlyContinue
if (-not $makePkg) {
    $gdkBin = Join-Path ${env:ProgramFiles(x86)} 'Microsoft GDK\bin\makepkg.exe'
    if (Test-Path $gdkBin) {
        $makePkg = Get-Command $gdkBin
    }
    else {
        throw 'makepkg.exe was not found on PATH or in the GDK bin folder. Open an Xbox Gaming Command Prompt from the GDK and retry.'
    }
}

$packageDir = Join-Path $repoRoot "artifacts\gdkx\$Platform\$Configuration\Package"
New-Item -ItemType Directory -Force $packageDir | Out-Null
$mapFile = Join-Path $packageDir 'layout.xml'

# genmap enumerates *everything* under the layout, so anything that has found its way in
# there gets packed. A MakePkg run whose /pd resolved inside the layout leaves a Package
# folder behind, and the next package then carries the previous one's symbol bundle and
# metadata - the observed cost was an .xvc of 1.16 GB against the 0.75 GB this produces.
# The layout is a build output and only the bootstrap project is allowed to write it.
$strays = Get-ChildItem $layout -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -in @('Package', 'pkg') }
foreach ($stray in $strays) {
    Write-Warning "Removing stale packaging output from the layout: $($stray.FullName)"
    Remove-Item $stray.FullName -Recurse -Force
}

# genmap first: MakePkg packs from a manifest enumerating every file, not from a folder.
& $makePkg.Source genmap /f $mapFile /d $layout
if ($LASTEXITCODE -ne 0) { throw "makepkg genmap failed with exit code $LASTEXITCODE." }

# /lt marks the package loose-tools/local-test signed, which is what a devkit will accept.
& $makePkg.Source pack /f $mapFile /lt /d $layout /pd $packageDir
if ($LASTEXITCODE -ne 0) { throw "makepkg pack failed with exit code $LASTEXITCODE." }

# Most of an Xbox package is the Game OS image MakePkg embeds (gameos.xvd, ~330 MB here)
# plus XVC container overhead; the title's own files are a rounding error beside it. The
# size is reported so an unexpected jump is noticed at build time rather than on a devkit.
$xvc = Get-ChildItem (Join-Path $packageDir '*.xvc') | Sort-Object -Descending LastWriteTime | Select-Object -First 1
if ($xvc) {
    Write-Host ("Package: {0} ({1:N0} MB)" -f $xvc.Name, ($xvc.Length / 1MB)) -ForegroundColor Green
}

Write-Host "Package written to $packageDir" -ForegroundColor Green
