<#
.SYNOPSIS
  連続のサーバ異常の打ち切り(sleep-report の cutoff-stopped・cutoff-recovered)の手動確認用: App の llama-server が /health に答えたあと少し待って、リクエストの途中で kill することを繰り返す。

.DESCRIPTION
  VLM の 1 枚は約 4〜6 秒かかる。llama-server が /health に答えた(= App が最初のリクエストを送る)あと DelaySeconds 秒で kill すると、
  リクエストの途中でサーバが落ちる(ServerFailure)。App は kill → 起動し直し → 同じ画像をやり直すが、やり直しも同じように落とされるので、
  同じ画像が 2 回続けて異常になり、failed になる(1 枚目)。次の画像も同じ。3 枚が failed になったところで、App は「3 枚続けて異常」でジョブを止める
  (ADR 0026。Run = Failed、理由が画面に出る)。1 枚あたり kill 2 回なので、3 枚で 6 回。既定の Max は 7(止まる直前に起動し直した 1 つ分)。

  安全のため、kill するのは **App の pid ファイル(<DataDir>\llama-server.pid)に記録された llama-server だけ**
  (pid・exe のパス・プロセスの開始時刻が記録と一致するときだけ)。他の llama-server・他のプロセスには触らない。
  この確認にだけ使う。GPU は使わない(llama-server を起動するのは App)。

.PARAMETER DataDir
  App を OHANAMI_DATA_DIR で起動した場所(既定 %LOCALAPPDATA%\Ohanami\sleep-check)。

.PARAMETER DelaySeconds
  /health が 200 になってから kill するまでの秒数(既定 1.5。1 枚の処理の途中に入れば何秒でもよい)。

.PARAMETER Max
  kill する回数の上限(既定 7)。

.PARAMETER WaitForServerSeconds
  次の llama-server が現れるのを待つ上限(既定 120 秒)。現れなければ終わる(ジョブが止まった、または終わった)。

.EXAMPLE
  pwsh tools/kill-llama-after-health.ps1
  pwsh tools/kill-llama-after-health.ps1 -DataDir D:\ohanami-sleep-check -DelaySeconds 2
#>
[CmdletBinding()]
param(
    [string]$DataDir = (Join-Path $env:LOCALAPPDATA 'Ohanami\sleep-check'),
    [double]$DelaySeconds = 1.5,
    [int]$Max = 7,
    [int]$WaitForServerSeconds = 120
)

$ErrorActionPreference = 'Stop'
$pidFile = Join-Path $DataDir 'llama-server.pid'

function Write-Log([string]$message) {
    Write-Host ('{0} {1}' -f (Get-Date).ToString('HH:mm:ss.fff'), $message)
}

# pid ファイルの記録と、実際のプロセスが一致するときだけ、そのプロセスを返す(違えば $null)
function Get-RecordedServer {
    if (-not (Test-Path -LiteralPath $pidFile)) { return $null }
    try { $record = Get-Content -LiteralPath $pidFile -Raw | ConvertFrom-Json } catch { return $null }   # 書き込みの途中
    foreach ($name in 'pid', 'startTimeUtcTicks', 'exePath', 'port') {
        if ($null -eq $record.$name) { return $null }
    }
    $process = Get-Process -Id $record.pid -ErrorAction SilentlyContinue
    if ($null -eq $process) { return $null }
    try {
        if ($process.Path -ne $record.exePath) { return $null }
        if ($process.StartTime.ToUniversalTime().Ticks -ne [long]$record.startTimeUtcTicks) { return $null }
    } catch { return $null }   # 読めない(終了した)
    return [pscustomobject]@{ Pid = [int]$record.pid; Port = [int]$record.port; Process = $process }
}

function Test-Health([int]$port) {
    try {
        $response = Invoke-WebRequest -Uri "http://127.0.0.1:$port/health" -TimeoutSec 2 -UseBasicParsing
        return $response.StatusCode -eq 200
    } catch { return $false }   # 503(読み込み中)・接続できない
}

if (-not (Test-Path -LiteralPath $DataDir)) { throw "DataDir が無い: $DataDir" }
Write-Log "監視を始める。pid ファイル: $pidFile。/health に答えて $DelaySeconds 秒後に kill、最大 $Max 回"

$killed = 0
$lastPid = 0
while ($killed -lt $Max) {
    $deadline = (Get-Date).AddSeconds($WaitForServerSeconds)
    $server = $null
    while ((Get-Date) -lt $deadline) {
        $candidate = Get-RecordedServer
        if ($null -ne $candidate -and $candidate.Pid -ne $lastPid) { $server = $candidate; break }
        Start-Sleep -Milliseconds 300
    }
    if ($null -eq $server) { Write-Log "新しい llama-server が $WaitForServerSeconds 秒現れなかった。終わる(ジョブが止まった、または終わった)"; break }

    Write-Log "llama-server(pid $($server.Pid)、ポート $($server.Port))を見つけた。/health を待つ"
    $healthy = $false
    while ((Get-Date) -lt $deadline.AddSeconds($WaitForServerSeconds)) {
        if ($server.Process.HasExited) { break }
        if (Test-Health $server.Port) { $healthy = $true; break }
        Start-Sleep -Milliseconds 300
    }
    $lastPid = $server.Pid
    if (-not $healthy) { Write-Log "pid $($server.Pid) は /health に答える前に終わった、または答えなかった。次を待つ"; continue }

    Write-Log "pid $($server.Pid) が /health に答えた。$DelaySeconds 秒待って kill する"
    Start-Sleep -Milliseconds ([int]($DelaySeconds * 1000))
    $again = Get-RecordedServer
    if ($null -eq $again -or $again.Pid -ne $server.Pid) { Write-Log "待つあいだに終わった(記録と一致しない)。kill しない"; continue }
    Stop-Process -Id $server.Pid -Force
    $killed++
    Write-Log "kill した(pid $($server.Pid))。$killed / $Max 回"
}
Write-Log "終わり。kill した回数: $killed"
