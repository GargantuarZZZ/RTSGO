param(
	[Parameter(Mandatory = $true)][string]$FilePath,
	[Parameter(Mandatory = $true)][string[]]$CutSignatures,
	[Parameter(Mandatory = $true)][string[]]$OutputNames
)

# 机械拆分 partial class：把从每个 CutSignature 开始、到下一个 CutSignature（或类结尾）的连续成员块
# 搬到独立 partial 文件，原文件删除这些块。不改任何逻辑，只改文件布局。

$ErrorActionPreference = 'Stop'
$lines = Get-Content -LiteralPath $FilePath -Encoding UTF8

$cuts = @()
foreach ($sig in $CutSignatures) {
	$found = -1
	for ($i = 0; $i -lt $lines.Count; $i++) {
		if ($lines[$i].TrimStart().StartsWith($sig, [System.StringComparison]::Ordinal)) {
			$found = $i
			break
		}
	}
	if ($found -lt 0) {
		throw "Signature not found: $sig"
	}
	$cuts += $found
}

$clsLine = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
	if ($lines[$i].Trim().StartsWith('public partial class ')) {
		$clsLine = $i
		break
	}
}
if ($clsLine -lt 0) {
	throw 'public partial class not found'
}

$nsLine = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
	if ($lines[$i].Trim().StartsWith('namespace ')) {
		$nsLine = $i
		break
	}
}
if ($nsLine -lt 0) {
	throw 'namespace not found'
}
$nsName = ($lines[$nsLine].Trim() -replace '^namespace\s+', '' -replace '\s*\{\s*$', '').Trim()

$last = $lines.Count - 1
while ($last -gt 0 -and $lines[$last].Trim() -ne '}') {
	$last--
}
$classClose = $last - 1
while ($classClose -gt 0 -and $lines[$classClose].Trim() -ne '}') {
	$classClose--
}

$usingBlock = $lines[0..($nsLine - 1)]
$classDecl = $lines[$clsLine]

for ($i = 0; $i -lt $cuts.Count; $i++) {
	$start = $cuts[$i]
	$end = if ($i + 1 -lt $cuts.Count) { $cuts[$i + 1] - 1 } else { $classClose - 1 }
	$group = $lines[$start..$end]

	$outPath = Join-Path (Split-Path -Parent $FilePath) $OutputNames[$i]
	$content = @()
	$content += $usingBlock
	$content += ''
	$content += "namespace $nsName"
	$content += '{'
	$content += "	$classDecl"
	$content += '	{'
	$content += $group
	$content += '	}'
	$content += '}'
	Set-Content -LiteralPath $outPath -Value $content -Encoding UTF8
	Write-Output "split -> $OutputNames[$i] (lines $($start + 1)..$($end + 1))"
}

# 从原文件删除被搬走的块（倒序删除，保持行号有效）
$keep = @($lines)
for ($i = $cuts.Count - 1; $i -ge 0; $i--) {
	$start = $cuts[$i]
	$end = if ($i + 1 -lt $cuts.Count) { $cuts[$i + 1] - 1 } else { $classClose - 1 }
	$keep = $keep[0..($start - 1)] + $keep[($end + 1)..($keep.Count - 1)]
}
Set-Content -LiteralPath $FilePath -Value $keep -Encoding UTF8
Write-Output 'original rewritten'
