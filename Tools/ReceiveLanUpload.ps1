param(
	[int]$Port = 8766,
	[string]$OutDir = "D:\RTSarcade\received"
)

# 局域网接收上传服务：第二台电脑浏览器打开 http://<本机IP>:8766/ 选择文件上传，
# 文件保存到 $OutDir，方便 Codex 直接读取分析。

$ErrorActionPreference = "Stop"
$MaxBodyBytes = 200MB

if (-not (Test-Path -LiteralPath $OutDir)) {
	New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
}

function Read-Request {
	param($Stream)

	$buffer = New-Object byte[] 8192
	$ms = New-Object System.IO.MemoryStream
	$deadline = [DateTime]::UtcNow.AddSeconds(15)

	while ($true) {
		if (-not $Stream.CanRead) {
			break
		}

		if ($Stream.DataAvailable) {
			$read = $Stream.Read($buffer, 0, $buffer.Length)
			if ($read -le 0) {
				break
			}

			$ms.Write($buffer, 0, $read)

			$all = $ms.ToArray()
			$text = [System.Text.Encoding]::ASCII.GetString($all)
			$idx = $text.IndexOf("`r`n`r`n")

			if ($idx -ge 0) {
				$headerLen = $idx + 4
				$leftover = New-Object byte[] ($all.Length - $headerLen)
				[Array]::Copy($all, $headerLen, $leftover, 0, $leftover.Length)
				return @{ Header = $text.Substring(0, $headerLen); Leftover = $leftover }
			}
		}

		if ([DateTime]::UtcNow -gt $deadline) {
			break
		}

		Start-Sleep -Milliseconds 20
	}

	$all = $ms.ToArray()
	return @{ Header = [System.Text.Encoding]::ASCII.GetString($all); Leftover = New-Object byte[] 0 }
}

function Read-BodyBytes {
	param($Stream, [int]$ContentLength)

	$body = New-Object byte[] $ContentLength
	$read = 0

	while ($read -lt $ContentLength) {
		$n = $Stream.Read($body, $read, $ContentLength - $read)
		if ($n -le 0) {
			break
		}
		$read += $n
	}

	return $body
}

function IndexOf-Bytes {
	param([byte[]]$Haystack, [byte[]]$Needle, [int]$Start)

	if ($Needle.Length -eq 0 -or $Haystack.Length -lt $Needle.Length) {
		return -1
	}

	for ($i = $Start; $i -le $Haystack.Length - $Needle.Length; $i++) {
		$match = $true
		for ($j = 0; $j -lt $Needle.Length; $j++) {
			if ($Haystack[$i + $j] -ne $Needle[$j]) {
				$match = $false
				break
			}
		}
		if ($match) {
			return $i
		}
	}

	return -1
}

function Save-Multipart {
	param([byte[]]$Body, [string]$ContentType, [string]$Folder)

	$saved = New-Object System.Collections.Generic.List[string]
	$boundaryMatch = [regex]::Match($ContentType, "boundary=(.+)")

	if (-not $boundaryMatch.Success) {
		return $saved
	}

	$boundary = "--" + $boundaryMatch.Groups[1].Value.Trim('"')
	$boundaryBytes = [System.Text.Encoding]::ASCII.GetBytes($boundary)
	$crlf = [System.Text.Encoding]::ASCII.GetBytes("`r`n")
	$headerSep = [System.Text.Encoding]::ASCII.GetBytes("`r`n`r`n")

	$pos = 0
	$start = IndexOf-Bytes $Body $boundaryBytes $pos

	while ($start -ge 0) {
		$dataStart = $start + $boundaryBytes.Length

		if ($dataStart + 1 -lt $Body.Length -and
			$Body[$dataStart] -eq 13 -and $Body[$dataStart + 1] -eq 10) {
			$dataStart += 2
		}

		# 结束边界 --boundary--
		if ($dataStart + 1 -lt $Body.Length -and
			$Body[$dataStart] -eq 45 -and $Body[$dataStart + 1] -eq 45) {
			break
		}

		$next = IndexOf-Bytes $Body $boundaryBytes $dataStart
		if ($next -lt 0) {
			break
		}

		$partEnd = $next
		if ($partEnd -ge 2 -and $Body[$partEnd - 2] -eq 13 -and $Body[$partEnd - 1] -eq 10) {
			$partEnd -= 2
		}

		$partLen = $partEnd - $dataStart
		if ($partLen -gt 0) {
			$part = New-Object byte[] $partLen
			[Array]::Copy($Body, $dataStart, $part, 0, $partLen)

			$headerEnd = IndexOf-Bytes $part $headerSep 0

			if ($headerEnd -ge 0) {
				$headerText = [System.Text.Encoding]::ASCII.GetString($part, 0, $headerEnd)
				$fileMatch = [regex]::Match($headerText, 'filename="([^"]*)"')

				if ($fileMatch.Success) {
					$fileName = [System.IO.Path]::GetFileName($fileMatch.Groups[1].Value)

					if (-not [string]::IsNullOrEmpty($fileName)) {
						$contentStart = $headerEnd + 4
						$contentLen = $part.Length - $contentStart
						$content = New-Object byte[] $contentLen
						[Array]::Copy($part, $contentStart, $content, 0, $contentLen)

						$outPath = Join-Path $Folder $fileName
						[System.IO.File]::WriteAllBytes($outPath, $content)
						$saved.Add($fileName)
					}
				}
			}
		}

		$pos = $next + $boundaryBytes.Length
		$start = IndexOf-Bytes $Body $boundaryBytes $pos
	}

	return $saved
}

function Send-Text {
	param($Stream, [string]$Text, [int]$StatusCode = 200)

	$body = [System.Text.Encoding]::UTF8.GetBytes($Text)
	$reason = if ($StatusCode -eq 200) { "OK" } else { "Error" }
	$header = "HTTP/1.1 $StatusCode $reason`r`nContent-Type: text/html; charset=utf-8`r`nContent-Length: $($body.Length)`r`nConnection: close`r`n`r`n"
	$headerBytes = [System.Text.Encoding]::ASCII.GetBytes($header)
	$Stream.Write($headerBytes, 0, $headerBytes.Length)
	$Stream.Write($body, 0, $body.Length)
	$Stream.Flush()
}

$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Any, $Port)
$listener.Start()

Write-Host "[UploadServer] 上传服务已启动: http://0.0.0.0:$Port/  (保存到: $OutDir)"

while ($true) {
	try {
		$client = $listener.AcceptTcpClient()
	}
	catch {
		break
	}

	try {
		$stream = $client.GetStream()
		$req = Read-Request $stream
		$request = $req.Header
		$lines = $request -split "`r`n"
		$requestLine = $lines[0]
		$parts = $requestLine -split " "
		$method = if ($parts.Count -ge 1) { $parts[0] } else { "" }
		$path = if ($parts.Count -ge 2) { $parts[1].Trim('/') } else { "" }

		$contentLength = 0
		$contentType = ""

		foreach ($line in $lines) {
			if ($line -match "^Content-Length:\s*(\d+)") {
				$contentLength = [int]$Matches[1]
			}
			if ($line -match "^Content-Type:\s*(.+)") {
				$contentType = $Matches[1].Trim()
			}
		}

		if ($method -eq "GET" -and ($path -eq "" -or $path -eq "index.html")) {
			$html = @"
<html><body style='font-family:sans-serif'>
<h2>向 Codex 传文件</h2>
<form method='post' enctype='multipart/form-data'>
  <input type='file' name='file' multiple>
  <button type='submit'>上传</button>
</form>
<p>上传后文件会出现在 D:\RTSarcade\received\ 下。</p>
</body></html>
"@
			Send-Text $stream $html
		}
		elseif ($method -eq "GET" -and $path -eq "list") {
			$files = Get-ChildItem -LiteralPath $OutDir -File -ErrorAction SilentlyContinue |
				Sort-Object LastWriteTime -Descending |
				Select-Object -First 50

			$html = "<html><body style='font-family:sans-serif'><h2>已收到文件</h2><ul>"
			foreach ($f in $files) {
				$html += "<li>$($f.Name) | $([math]::Round($f.Length/1KB,1)) KB | $($f.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))</li>"
			}
			$html += "</ul></body></html>"
			Send-Text $stream $html
		}
		elseif ($method -eq "POST") {
			if ($contentLength -le 0 -or $contentLength -gt $MaxBodyBytes) {
				Send-Text $stream "文件太大或为空" 413
			}
			else {
				$body = New-Object byte[] $contentLength
				$copied = [Math]::Min($req.Leftover.Length, $contentLength)
				[Array]::Copy($req.Leftover, 0, $body, 0, $copied)

				$readTotal = $copied
				while ($readTotal -lt $contentLength) {
					$n = $stream.Read($body, $readTotal, $contentLength - $readTotal)
					if ($n -le 0) {
						break
					}
					$readTotal += $n
				}

				$saved = Save-Multipart $body $contentType $OutDir

				if ($saved.Count -gt 0) {
					Send-Text $stream ("上传成功: " + ($saved -join ", "))
				}
				else {
					Send-Text $stream "没有解析到文件（请用表单上传）" 400
				}
			}
		}
		else {
			Send-Text $stream "404" 404
		}
	}
	catch {
		# 忽略客户端中断
	}
	finally {
		try { $client.Close() } catch { }
	}
}
