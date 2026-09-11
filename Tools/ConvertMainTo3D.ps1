param(
    [string]$ScenePath = "D:\DEV\RTSarcade\Scenes\main.tscn",
    [string]$LegacyPath = "D:\DEV\RTSarcade\Scenes\main_2d_legacy.tscn"
)

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

# Restore from legacy if current scene is corrupted
$current = [System.IO.File]::ReadAllText($ScenePath, [System.Text.Encoding]::UTF8)
if ($current.Length -lt 10000 -and (Test-Path -LiteralPath $LegacyPath)) {
    $current = [System.IO.File]::ReadAllText($LegacyPath, [System.Text.Encoding]::UTF8)
    Write-Output "RESTORED_FROM_LEGACY"
}

# Save 2D legacy once
if (-not (Test-Path -LiteralPath $LegacyPath)) {
    [System.IO.File]::WriteAllText($LegacyPath, $current, $utf8NoBom)
    Write-Output "LEGACY_SAVED=$LegacyPath"
}

$text = $current
$nl = "`n"
if ($text.Contains("`r`n")) { $nl = "`r`n" }

# 2. RTSCamera: Camera2D -> Camera3D
$oldCam = "[node name=`"RTSCamera`" type=`"Camera2D`" parent=`".`"]" + $nl +
          "position = Vector2(-5, 4)" + $nl +
          "zoom = Vector2(0.5, 0.5)" + $nl +
          "script = ExtResource(`"3_ynf5e`")"
$newCam = "[node name=`"RTSCamera`" type=`"Camera3D`" parent=`".`"]" + $nl +
          "current = true" + $nl +
          "position = Vector3(0, 30, 0)" + $nl +
          "script = ExtResource(`"3_ynf5e`")"
$text = $text.Replace($oldCam, $newCam)

# 3. Remove pre-placed 2D entity instances (Shrine/NanoCore/Tower)
foreach ($name in @('Shrine', 'NanoCore', 'Tower')) {
    $pattern = "[node name=`"$name"
    $start = 0
    while (($idx = $text.IndexOf($pattern, $start)) -ge 0) {
        $lineEnd = $text.IndexOf($nl, $idx)
        if ($lineEnd -lt 0) { break }
        $line = $text.Substring($idx, $lineEnd - $idx)
        if ($line -match 'parent="Entities"') {
            $text = $text.Remove($idx, $lineEnd - $idx + $nl.Length)
        }
        else {
            $start = $lineEnd + $nl.Length
        }
    }
}

# 4. Remove 2D effect node GameFX
$oldFx = "[node name=`"GameFX`" type=`"Node2D`" parent=`"User`"]" + $nl +
         "script = ExtResource(`"7_fdnlq`")" + $nl
$text = $text.Replace($oldFx, "")

# 5. Append MapGround3D script reference after last ext_resource line
$anchor = $nl + "[ext_resource"
$lastExt = $text.LastIndexOf($anchor)
if ($lastExt -ge 0) {
    $lineEnd = $text.IndexOf($nl, $lastExt + 1)
    $extLine = "[ext_resource type=`"Script`" path=`"res://Scripts/World/MapGround3D.cs`" id=`"30_ground`"]"
    $text = $text.Substring(0, $lineEnd + 1) + $extLine + $nl + $text.Substring($lineEnd + 1)
}

# 6. Insert 3D sun light and ground before Game node
$gameNode = "[node name=`"Game`""
$gameIdx = $text.IndexOf($gameNode)
if ($gameIdx -ge 0) {
    $insert = "[node name=`"Sun`" type=`"DirectionalLight3D`" parent=`".`"]" + $nl +
              "rotation_degrees = Vector3(-55, -30, 0)" + $nl +
              $nl +
              "[node name=`"Ground`" type=`"Node3D`" parent=`".`"]" + $nl +
              "script = ExtResource(`"30_ground`")" + $nl +
              $nl
    $text = $text.Substring(0, $gameIdx) + $insert + $text.Substring($gameIdx)
}

# 7. Hide 2D map layers so they never cover the 3D view
$navNode = "[node name=`"NavigationRegion2D`" type=`"NavigationRegion2D`" parent=`".`"]"
$text = $text.Replace($navNode, $navNode + $nl + "visible = false")

# 8. Remove 2D fog material attached to the FogOfWar instance root
$fogMaterial = "material = SubResource(`"ShaderMaterial_h1bgf`")" + $nl
$text = $text.Replace($fogMaterial, "")

if ($text.Length -lt 10000) {
    throw "CONVERT_FAILED: result too small"
}

[System.IO.File]::WriteAllText($ScenePath, $text, $utf8NoBom)
Write-Output "CONVERTED=$ScenePath"
