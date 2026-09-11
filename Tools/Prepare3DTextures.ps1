param(
    [string]$SourceDir = "D:\DEV\RTSarcade\ArtRes\imgs",
    [string]$OutputDir = "D:\DEV\RTSarcade\ArtRes\imgs3d"
)

Add-Type -AssemblyName System.Drawing

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$transparent = @()
$files = Get-ChildItem -LiteralPath $SourceDir -Filter *.png

foreach ($png in $files) {
    $bmp = New-Object System.Drawing.Bitmap($png.FullName)
    $hasAlpha = $false

    for ($x = 0; $x -lt $bmp.Width -and -not $hasAlpha; $x++) {
        for ($y = 0; $y -lt $bmp.Height -and -not $hasAlpha; $y++) {
            if ($bmp.GetPixel($x, $y).A -lt 255) {
                $hasAlpha = $true
            }
        }
    }

    if ($hasAlpha) {
        $out = New-Object System.Drawing.Bitmap($bmp.Width, $bmp.Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($out)
        $g.Clear([System.Drawing.Color]::White)
        $g.DrawImage($bmp, 0, 0, $bmp.Width, $bmp.Height)
        $g.Dispose()
        $out.Save((Join-Path $OutputDir $png.Name), [System.Drawing.Imaging.ImageFormat]::Png)
        $out.Dispose()
        $transparent += $png.Name
    }
    else {
        Copy-Item -LiteralPath $png.FullName -Destination (Join-Path $OutputDir $png.Name) -Force
    }

    $bmp.Dispose()
}

Write-Output ("TOTAL=" + $files.Count)
Write-Output ("TRANSPARENT_COUNT=" + $transparent.Count)
$transparent | ForEach-Object { Write-Output ("TRANSPARENT: " + $_) }

# 图集瓦片：原图是 128x128（2x2 个 64x64 瓦片），裁剪为单瓦片，
# 保证 3D 里“一格一个贴图”（草地/墙壁与网格一致）
foreach ($name in @('Grass.png', 'Wall.png', 'NanoCreep.png')) {
    $src = Join-Path $SourceDir $name
    $dst = Join-Path $OutputDir $name
    if (-not (Test-Path -LiteralPath $src)) { continue }

    $bmp = New-Object System.Drawing.Bitmap($src)
    $crop = New-Object System.Drawing.Bitmap(64, 64, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($crop)
    $g.Clear([System.Drawing.Color]::White)
    $g.DrawImage($bmp, (New-Object System.Drawing.Rectangle(0, 0, 64, 64)), (New-Object System.Drawing.Rectangle(0, 0, 64, 64)), [System.Drawing.GraphicsUnit]::Pixel)
    $g.Dispose()
    $crop.Save($dst, [System.Drawing.Imaging.ImageFormat]::Png)
    $crop.Dispose()
    $bmp.Dispose()
    Write-Output ("ATLAS_CROP: " + $name)
}
