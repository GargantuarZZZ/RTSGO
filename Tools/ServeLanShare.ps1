param(
	[string]$Folder = "D:\RTSAC1",
	[int]$Port = 8765,
	[string]$ZipName = "RTSAC1.zip"
)

# 局域网临时下载服务（TcpListener 版，不需要管理员权限）。
# 自动监控导出目录变化并重新打包 zip。
# 用法: powershell -ExecutionPolicy Bypass -File ServeLanShare.ps1 -Folder D:\RTSAC1 -Port 8765

$ErrorActionPreference = "Stop"
$zipPath = Join-Path $Folder $ZipName

function Rebuild-Zip {
	param([string]$FolderPath, [string]$ZipFilePath)

	if (Test-Path -LiteralPath $ZipFilePath) {
		Remove-Item -LiteralPath $ZipFilePath -Force
	}

	$items = Get-ChildItem -LiteralPath $FolderPath -Force |
		Where-Object { $_.Name -ne (Split-Path $ZipFilePath -Leaf) }

	Compress-Archive -Path $items.FullName -DestinationPath $ZipFilePath -CompressionLevel Optimal
}

function Get-NewestStamp {
	param([string]$FolderPath)

	$newest = Get-ChildItem -LiteralPath $FolderPath -Recurse -File -ErrorAction SilentlyContinue |
		Sort-Object LastWriteTimeUtc -Descending |
		Select-Object -First 1

	if ($newest -eq $null) {
		return [DateTime]::MinValue
	}

	return $newest.LastWriteTimeUtc
}

function Read-RequestHeaders {
	param($Stream)

	$buffer = New-Object byte[] 4096
	$sb = New-Object System.Text.StringBuilder
	$deadline = [DateTime]::UtcNow.AddSeconds(10)

	while ($true) {
		if (-not $Stream.CanRead) {
			break
		}

		if ($Stream.DataAvailable) {
			$read = $Stream.Read($buffer, 0, $buffer.Length)
			if ($read -le 0) {
				break
			}

			[void]$sb.Append([System.Text.Encoding]::ASCII.GetString($buffer, 0, $read))

			if ($sb.ToString().Contains("`r`n`r`n")) {
				break
			}
		}

		if ([DateTime]::UtcNow -gt $deadline) {
			break
		}

		Start-Sleep -Milliseconds 20
	}

	return $sb.ToString()
}

function Send-Response {
	param($Stream, [byte[]]$Body, [string]$ContentType)

	$header = "HTTP/1.1 200 OK`r`nContent-Type: $ContentType`r`nContent-Length: $($Body.Length)`r`nConnection: close`r`n`r`n"
	$headerBytes = [System.Text.Encoding]::ASCII.GetBytes($header)
	$Stream.Write($headerBytes, 0, $headerBytes.Length)
	$Stream.Write($Body, 0, $Body.Length)
	$Stream.Flush()
}

$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Any, $Port)
$listener.Start()

Write-Host "[LanShare] 服务已启动: http://0.0.0.0:$Port/  (目录: $Folder)"

$lastStamp = Get-NewestStamp $Folder
$zipReady = $false

if (-not (Test-Path -LiteralPath $zipPath)) {
	Write-Host "[LanShare] 首次打包中，请稍候..."
	Rebuild-Zip $Folder $zipPath
	$zipReady = $true
	$lastStamp = Get-NewestStamp $Folder
	Write-Host "[LanShare] 首次打包完成: $zipPath"
}
else {
	$zipReady = $true
}

while ($true) {
	# 监控导出目录：文件停止写入 5 秒后自动重新打包（避免导出中途打包损坏）
	$now = [DateTime]::UtcNow
	$newest = Get-NewestStamp $Folder

	if ($newest -gt $lastStamp.AddSeconds(1) -and ($now - $newest).TotalSeconds -ge 5) {
		Write-Host "[LanShare] 检测到导出更新，重新打包..."
		Rebuild-Zip $Folder $zipPath
		$zipReady = $true
		$lastStamp = $newest
		Write-Host "[LanShare] 重新打包完成"
	}

	try {
		$client = $listener.AcceptTcpClient()
	}
	catch {
		break
	}

	try {
		$stream = $client.GetStream()
		$request = Read-RequestHeaders $stream
		$requestLine = ($request -split "`r`n")[0]
		$parts = $requestLine -split " "
		$path = ""

		if ($parts.Count -ge 2) {
			$path = $parts[1].Trim('/')
		}

		if ($path -eq "" -or $path -eq "index.html") {
			$html = if ($zipReady -and (Test-Path -LiteralPath $zipPath)) {
				"<html><body style='font-family:sans-serif'><h2>RTSarcade 导出包</h2><p><a href='/$ZipName'>$ZipName</a></p><p>如果刚重新导出，等 10 秒再点。</p></body></html>"
			}
			else {
				"<html><body style='font-family:sans-serif'><h2>正在打包，请稍后刷新...</h2></body></html>"
			}
			Send-Response $stream ([System.Text.Encoding]::UTF8.GetBytes($html)) "text/html; charset=utf-8"
		}
		elseif ($path -eq $ZipName -and $zipReady -and (Test-Path -LiteralPath $zipPath)) {
			$bytes = [System.IO.File]::ReadAllBytes($zipPath)
			Send-Response $stream $bytes "application/zip"
		}
		else {
			$body = [System.Text.Encoding]::UTF8.GetBytes("404 Not Found")
			$header = "HTTP/1.1 404 Not Found`r`nContent-Type: text/plain`r`nContent-Length: $($body.Length)`r`nConnection: close`r`n`r`n"
			$headerBytes = [System.Text.Encoding]::ASCII.GetBytes($header)
			$stream.Write($headerBytes, 0, $headerBytes.Length)
			$stream.Write($body, 0, $body.Length)
			$stream.Flush()
		}
	}
	catch {
		# 客户端中断等，忽略
	}
	finally {
		try { $client.Close() } catch { }
	}
}
