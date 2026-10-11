# Draws the MSIX/Store logo images (same design as the app icon) into src/Restyle.Store/Package/Assets.
# Run with Windows PowerShell:  powershell -ExecutionPolicy Bypass -File store\make-assets.ps1
param([string]$OutDir = (Join-Path $PSScriptRoot "..\src\Restyle.Store\Package\Assets"))
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); return $p
}

# Draws the logo as a square of side $s with its top-left at ($ox, $oy).
function DrawLogo($g, [float]$ox, [float]$oy, [float]$s) {
    $bg = RoundRect $ox $oy $s $s ($s * 0.22)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF $ox, $oy), (New-Object System.Drawing.PointF ($ox + $s), ($oy + $s)), ([System.Drawing.Color]::FromArgb(255, 0x35, 0x84, 0xE4)), ([System.Drawing.Color]::FromArgb(255, 0x91, 0x41, 0xAC))
    $g.FillPath($brush, $bg)
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(240, 255, 255, 255))
    $soft = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(110, 255, 255, 255))
    $g.FillPath($white, (RoundRect ($ox + $s * 0.2) ($oy + $s * 0.2) ($s * 0.6) ($s * 0.42) ($s * 0.06)))
    $g.FillRectangle($soft, $ox + $s * 0.2, $oy + $s * 0.2, $s * 0.6, [Math]::Max(1, $s * 0.08))
    $g.FillPath($soft, (RoundRect ($ox + $s * 0.14) ($oy + $s * 0.7) ($s * 0.72) ($s * 0.13) ($s * 0.05)))
    $dot = [Math]::Max(1.5, $s * 0.07)
    foreach ($i in -1, 0, 1) {
        $cx = $ox + $s * 0.5 + $i * $s * 0.12
        $g.FillEllipse($white, $cx - $dot / 2, $oy + $s * 0.765 - $dot / 2, $dot, $dot)
    }
}

# name, width, height, logo size as a fraction of the height
$images = @(
    @("Square44x44Logo.png", 44, 44, 1.0),
    @("StoreLogo.png", 50, 50, 1.0),
    @("SmallTile.png", 71, 71, 0.66),
    @("Square150x150Logo.png", 150, 150, 0.66),
    @("Wide310x150Logo.png", 310, 150, 0.66),
    @("LargeTile.png", 310, 310, 0.66)
)

foreach ($img in $images) {
    $name, $w, $h, $frac = $img
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = [Math]::Floor($h * $frac)
    DrawLogo $g (($w - $s) / 2) (($h - $s) / 2) $s
    $g.Dispose()
    $bmp.Save((Join-Path $OutDir $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    "  $name ($w x $h)"
}
