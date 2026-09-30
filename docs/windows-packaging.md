# Windows packaging

How to turn the desktop build into a package you can install and launch on the same PC.
This is the Windows path only. For XBOX Series X|S see
[Building for XBOX Series X|S](xbox-console-build.md).

## Build and stage

```powershell
tools\Build-GdkPc.ps1                # Release: publish and stage artifacts\gdkpc\layout\Release
tools\Build-GdkPc.ps1 -Package       # ...and run MakePkg to produce an .msixvc
tools\Build-GdkPc.ps1 -Configuration Debug
```

The desktop flavour is an ordinary `dotnet build`. The script exists only to turn a
publish output into a layout that MakePkg accepts.

## What the layout contains

The publish is self-contained `win-x64`, because the packaged sandbox does not provide the
shared .NET runtime.

Alongside the publish output, the layout carries:

- `packaging\MicrosoftGame.PC.config`, staged as `MicrosoftGame.config`;
- the store logos from `storelogos\`;
- the redistributables the title loads at runtime: Party, PlayFab Core and Services,
  libHttpClient, XCurl and the XBOX services thunks.

Nothing installs those redistributables machine-wide, and inside a package only what the
layout carries can be loaded. A package built without them installs and launches but
degrades to offline. They come from the installed GDK. `third_party\party` is the fallback
for the three binaries it vendors, which are desktop builds and therefore correct for this
target.

The five logo files named by the packaging configs are required. A missing file fails the
build rather than quietly substituting generated branding.

## Why there are two game configs

`packaging\MicrosoftGame.PC.config` is separate from `packaging\MicrosoftGame.config`
because MakePkg validates that every executable a config declares is present in the layout,
and `MicrosoftGame.config` declares the XBOX Series X|S executable as well.

`packaging\MicrosoftGame.config` stays as it is. It is the identity file that the
unpackaged desktop build and the platform spike stage beside themselves, and it is where an
unpackaged GDK process reads its title identity from.

## Why `makepkg pack` is invoked with `/pc`

Without `/pc`, MakePkg produces an XBOX Series X|S package, which no PC will install. The
script always passes it.

## Install and launch

```powershell
wdapp install .\<package-name>.msixvc
wdapp list                                  # confirm the package is registered
wdapp launch <package-family-name>!Game
```

`wdapp install` does not install anything itself. It hands the `.msixvc` to the Microsoft
Store's install service and waits for an answer.

If the install times out with `0x800705b4`, see
[Install times out with 0x800705b4](troubleshooting.md#install-times-out-with-0x800705b4).

## Before you submit anything

The controller glyphs and store logos in this repository are placeholders. Replace both
with real assets before taking any part of this sample into a submission. See
[Known gaps](known-gaps.md#configuration-and-assets).
