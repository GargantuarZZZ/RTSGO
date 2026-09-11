param(
    [Parameter(Mandatory = $true)][string]$Path1,
    [Parameter(Mandatory = $true)][string]$Path2,
    [int]$MaxDiffLines = 25
)

function Get-Sections {
    param([string]$Path)

    $sections = [ordered]@{}
    $current = 'HEADER'
    $sections[$current] = [System.Collections.Generic.List[string]]::new()

    foreach ($line in [System.IO.File]::ReadLines($Path)) {
        if ($line -match '^\[([A-Z_]+)\]$') {
            $current = $Matches[1]
            if (-not $sections.Contains($current)) {
                $sections[$current] = [System.Collections.Generic.List[string]]::new()
            }
        }
        else {
            $sections[$current].Add($line)
        }
    }

    return $sections
}

$s1 = Get-Sections -Path $Path1
$s2 = Get-Sections -Path $Path2
$totalDiff = 0

foreach ($name in ($s1.Keys + $s2.Keys | Sort-Object -Unique)) {
    $l1 = @($s1[$name])
    $l2 = @($s2[$name])

    $same = ($l1.Count -eq $l2.Count)
    if ($same) {
        for ($i = 0; $i -lt $l1.Count; $i++) {
            if ($l1[$i] -ne $l2[$i]) {
                $same = $false
                break
            }
        }
    }

    if ($same) {
        continue
    }

    $totalDiff++
    Write-Host ""
    Write-Host "==== 区段 [$name] 不一致 ====" -ForegroundColor Yellow

    if ($name -eq 'SECTION_HASHES') {
        $d1 = @{}
        $d2 = @{}

        foreach ($l in $l1) {
            $p = $l -split '='
            if ($p.Count -ge 2) { $d1[$p[0]] = $p[1] }
        }
        foreach ($l in $l2) {
            $p = $l -split '='
            if ($p.Count -ge 2) { $d2[$p[0]] = $p[1] }
        }

        foreach ($k in ($d1.Keys + $d2.Keys | Sort-Object -Unique)) {
            if ($d1[$k] -ne $d2[$k]) {
                Write-Host ("  {0}: A={1} B={2}" -f $k, $d1[$k], $d2[$k]) -ForegroundColor Red
            }
        }
    }
    else {
        $shown = 0
        $maxLen = [Math]::Max($l1.Count, $l2.Count)

        for ($i = 0; $i -lt $maxLen -and $shown -lt $MaxDiffLines; $i++) {
            if ($i -lt $l1.Count) {
                $a = $l1[$i]
            }
            else {
                $a = '(missing)'
            }

            if ($i -lt $l2.Count) {
                $b = $l2[$i]
            }
            else {
                $b = '(missing)'
            }

            if ($a -ne $b) {
                Write-Host "  A: $a" -ForegroundColor Red
                Write-Host "  B: $b" -ForegroundColor Red
                $shown++
            }
        }

        if ($shown -ge $MaxDiffLines) {
            Write-Host "  ... (仅显示前 $MaxDiffLines 条)"
        }
    }
}

Write-Host ""
if ($totalDiff -eq 0) {
    Write-Host "两个报告完全一致" -ForegroundColor Green
    exit 0
}

Write-Host "共 $totalDiff 个区段不一致" -ForegroundColor Red
exit 1
