# Uygulama simgesini (app.ico) üretir: turkuaz yuvarlatılmış kare üzerinde beyaz barkod çizgileri.
# Çoklu boyut (16–256 px), her boyut PNG olarak ICO içine gömülür. Çıktı: src\KumasBarkod.App\Assets\app.ico
# Kullanım: pwsh tools\icon\generate-icon.ps1
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot "..\..\src\KumasBarkod.App\Assets\app.ico"
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

$sizes = 16, 24, 32, 48, 64, 128, 256
# Barkod deseni (1 = çubuk, genişlik birimi); simgede okunaklı, gerçek bir kod değil
$pattern = 2,1,1,1,3,1,1,2,1,1,2,1,3,1,1,1,2

$pngs = foreach ($size in $sizes) {
    $bmp = New-Object Drawing.Bitmap $size, $size
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([Drawing.Color]::Transparent)

    # Yuvarlatılmış kare
    $r = [Math]::Max(2, [int]($size * 0.2))
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $r * 2, $r * 2, 180, 90)
    $path.AddArc($size - $r * 2 - 1, 0, $r * 2, $r * 2, 270, 90)
    $path.AddArc($size - $r * 2 - 1, $size - $r * 2 - 1, $r * 2, $r * 2, 0, 90)
    $path.AddArc(0, $size - $r * 2 - 1, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(0x1B, 0x9A, 0xAA))), $path)

    # Barkod çubukları
    $g.SmoothingMode = 'None'
    $margin = [Math]::Max(3, [int]($size * 0.2))
    $top = [int]($size * 0.24); $height = [int]($size * 0.52)
    $units = ($pattern | Measure-Object -Sum).Sum
    $unit = ($size - 2 * $margin) / $units
    $x = $margin; $black = $true
    foreach ($w in $pattern) {
        $width = [Math]::Max(1, [int][Math]::Round($w * $unit))
        if ($black) { $g.FillRectangle([Drawing.Brushes]::White, [int]$x, $top, $width, $height) }
        $x += $w * $unit; $black = -not $black
    }
    $g.Dispose()

    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    , $ms.ToArray()
}

# ICO kabı: başlık + dizin + PNG verileri
$fs = [IO.File]::Create($out); $w = New-Object IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($png in $pngs) { $w.Write($png) }
$w.Dispose()
"Simge yazıldı: $out ($((Get-Item $out).Length) bayt)"
