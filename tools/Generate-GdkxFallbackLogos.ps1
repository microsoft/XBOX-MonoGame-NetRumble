param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDir
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

New-Item -ItemType Directory -Force $OutputDir | Out-Null

$logos = @(
    @{ Name = 'StoreLogo.png'; Width = 100; Height = 100; TextSize = 18 },
    @{ Name = 'Logo.png'; Width = 150; Height = 150; TextSize = 26 },
    @{ Name = 'SmallLogo.png'; Width = 44; Height = 44; TextSize = 8 },
    @{ Name = 'LargeLogo.png'; Width = 480; Height = 480; TextSize = 74 },
    @{ Name = 'SplashScreen.png'; Width = 1920; Height = 1080; TextSize = 110 }
)

foreach ($logo in $logos) {
    $path = Join-Path $OutputDir $logo.Name
    $bitmap = [System.Drawing.Bitmap]::new($logo.Width, $logo.Height)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([System.Drawing.Color]::Black)

            $brush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 40, 140, 255))
            try {
                $size = [Math]::Min($logo.Width, $logo.Height)
                $padding = [Math]::Max(2, [int]($size * 0.18))
                $graphics.FillEllipse($brush, $padding, $padding, $size - ($padding * 2), $size - ($padding * 2))
            }
            finally {
                $brush.Dispose()
            }

            $font = [System.Drawing.Font]::new('Arial', $logo.TextSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
            try {
                $format = [System.Drawing.StringFormat]::new()
                try {
                    $format.Alignment = [System.Drawing.StringAlignment]::Center
                    $format.LineAlignment = [System.Drawing.StringAlignment]::Center
                    $textBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
                    try {
                        $graphics.DrawString('NR', $font, $textBrush, [System.Drawing.RectangleF]::new(0, 0, $logo.Width, $logo.Height), $format)
                    }
                    finally {
                        $textBrush.Dispose()
                    }
                }
                finally {
                    $format.Dispose()
                }
            }
            finally {
                $font.Dispose()
            }
        }
        finally {
            $graphics.Dispose()
        }

        $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $bitmap.Dispose()
    }
}
