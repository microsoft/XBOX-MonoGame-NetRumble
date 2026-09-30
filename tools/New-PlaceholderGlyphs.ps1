<#
.SYNOPSIS
    Generates PLACEHOLDER controller button glyphs for the UI prompts.

.DESCRIPTION
    Xbox cert requires button prompts to show button icons rather than letters in
    brackets, and the icons a shipping title must use are Microsoft's own: the official
    controller art from the Xbox Design Guide / GDK art pack, which is licensed to
    developers and cannot be redistributed in a repository like this one.

    This script draws stand-ins so the prompt renderer has something to show until that
    art is dropped in. They are deliberately plain - a white silhouette with the letter
    knocked out, matching the Controller_BumperLeft/Right art already in Content - and
    they are NOT cert-compliant.

    To ship: replace the PNGs under Content/Textures/UI/Controller with the official
    files of the same names and rebuild. Nothing in code changes; the names, the square
    280x280 footprint and the "white silhouette, tinted at draw time" convention are the
    entire contract.

.NOTES
    Windows-only (System.Drawing). Re-runnable; overwrites what it generated.
#>

[CmdletBinding()]
param(
    [string]$OutputDirectory
)

Add-Type -AssemblyName System.Drawing

if (-not $OutputDirectory) {
    $scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
    $OutputDirectory = Join-Path $scriptRoot '..\src\NetRumble.Game\Content\Textures\UI\Controller'
}

$size = 280
$resolved = (Resolve-Path $OutputDirectory).Path

function New-Glyph {
    param(
        [string]$Name,
        [scriptblock]$Paint
    )

    $bitmap = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    & $Paint $g

    $path = Join-Path $resolved "$Name.png"
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    $bitmap.Dispose()

    Write-Host "wrote $path"
}

# The letter is punched out of the disc rather than drawn on top of it, so the glyph is a
# single silhouette that tints to any colour the UI asks for - the same way the bumper art
# behaves.
function New-FaceButton {
    param([string]$Name, [string]$Letter)

    New-Glyph -Name $Name -Paint {
        param($g)

        $white = [System.Drawing.Brushes]::White
        $g.FillEllipse($white, 8, 8, $size - 16, $size - 16)

        $font = New-Object System.Drawing.Font('Segoe UI', 150, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $format = New-Object System.Drawing.StringFormat
        $format.Alignment = [System.Drawing.StringAlignment]::Center
        $format.LineAlignment = [System.Drawing.StringAlignment]::Center

        # CompositingMode.SourceCopy writes the glyph's transparent pixels straight into
        # the bitmap instead of blending them over the disc, which is what makes this a
        # knockout rather than a dark letter.
        $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $transparent = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0, 255, 255, 255))
        $g.DrawString($Letter, $font, $transparent, (New-Object System.Drawing.RectangleF(0, 0, $size, $size)), $format)

        $transparent.Dispose()
        $format.Dispose()
        $font.Dispose()
    }
}

function New-BarButton {
    param([string]$Name, [int]$BarCount)

    New-Glyph -Name $Name -Paint {
        param($g)

        $white = [System.Drawing.Brushes]::White
        $g.FillEllipse($white, 8, 8, $size - 16, $size - 16)

        $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $transparent = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0, 255, 255, 255))

        $barWidth = 120
        $barHeight = 20
        $gap = 26
        $left = ($size - $barWidth) / 2
        $totalHeight = ($BarCount * $barHeight) + (($BarCount - 1) * ($gap - $barHeight))
        $top = ($size - $totalHeight) / 2

        for ($i = 0; $i -lt $BarCount; $i++) {
            $g.FillRectangle($transparent, $left, $top + ($i * $gap), $barWidth, $barHeight)
        }

        $transparent.Dispose()
    }
}

New-FaceButton -Name 'Controller_A' -Letter 'A'
New-FaceButton -Name 'Controller_B' -Letter 'B'
New-FaceButton -Name 'Controller_X' -Letter 'X'
New-FaceButton -Name 'Controller_Y' -Letter 'Y'
New-BarButton -Name 'Controller_Menu' -BarCount 3
New-BarButton -Name 'Controller_View' -BarCount 2
