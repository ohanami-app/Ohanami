<#
.SYNOPSIS
  サーバの強制終了(sleep-report の kill-server)用: フィクスチャのフォルダの caption(または指定の段階)の done が N 枚になったら、App の llama-server を 1 回だけ強制終了する(Stop-Process -Force)。

.DESCRIPTION
  手でタイミングを合わせる代わりに、done の数を 1 秒おきに数えて、N 枚に達したあとに最初に見つかった「App の llama-server」を 1 回だけ kill する。
  App は「サーバの異常」として kill → 起動し直し → 同じ画像をやり直す(ADR 0026)。

  数え方: ベンチの `stage-count`(sleep-report と同じ DB の読み取り専用の読み方。sqlite3 の CLI に頼らない)。ベンチの exe が要る(-BenchExe。既定はリポジトリの tools\Ohanami.Bench\bin\Debug または Release)。

  安全のため、kill するのは **App の pid ファイル(<DataDir>\llama-server.pid)に記録された llama-server だけ**(pid・exe のパス・プロセスの開始時刻が記録と一致するときだけ。
  tools/kill-llama-after-health.ps1 と同じ確認)。他の llama-server・他のプロセスには触らない。記録と一致するものが無ければ、kill しないで待つ。
  -WhatIf を付けると、kill は実行せず「何をするか」だけを出す。GPU は使わない(llama-server を起動するのは App)。この確認にだけ使う。

.PARAMETER Folder
  フィクスチャの場面のフォルダ(sleep-fixture が作った kill-server のフォルダ。App に追加したもの)。sleep-report の --folder と同じ。

.PARAMETER Threshold
  done の枚数がこの値以上になったら kill する(sleep-fixture が作る kill-server のフォルダは 24 枚)。

.PARAMETER Stage
  数える段階(embed / ocr / caption。既定 caption)。

.PARAMETER DataDir
  App を OHANAMI_DATA_DIR で起動した場所(既定 %LOCALAPPDATA%\Ohanami\sleep-check)。

.PARAMETER BenchExe
  Ohanami.Bench.exe のパス(既定は、リポジトリの tools\Ohanami.Bench\bin\Debug\net10.0\ または Release)。

.PARAMETER TimeoutMinutes
  待つ上限(分。既定 20)。超えたら何もしないで終わる(終了コード 3)。

.PARAMETER PollSeconds
  数える間隔(秒。既定 1)。

.NOTES
  終了コード: 0 = kill した(-WhatIf のときは、した場合)、3 = 時間切れ。数えられない(ベンチの失敗)ときは例外で止まる。
  実機での動作(本物の App・llama-server を相手にした確認)は、この版では未確認。引数の解釈・数え方・-WhatIf・pid ファイルとの照合・偽のプロセスの kill は、スクラッチの偽物で確かめた。

.EXAMPLE
  pwsh tools/kill-llama-when.ps1 -Folder $env:LOCALAPPDATA\Ohanami\sleep-check-fixture\kill-server -Threshold 12 -WhatIf
  pwsh tools/kill-llama-when.ps1 -Folder $env:LOCALAPPDATA\Ohanami\sleep-check-fixture\kill-server -Threshold 12
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)][string]$Folder,
    [Parameter(Mandatory = $true)][ValidateRange(1, 100000)][int]$Threshold,
    [ValidateSet('embed', 'ocr', 'caption')][string]$Stage = 'caption',
    [string]$DataDir = (Join-Path $env:LOCALAPPDATA 'Ohanami\sleep-check'),
    [string]$BenchExe = '',
    [double]$TimeoutMinutes = 20,
    [double]$PollSeconds = 1
)

$ErrorActionPreference = 'Stop'
$pidFile = Join-Path $DataDir 'llama-server.pid'

function Write-Log([string]$message) {
    Write-Host ('{0} {1}' -f (Get-Date).ToString('HH:mm:ss.fff'), $message)
}

function Resolve-BenchExe([string]$explicit) {
    if ($explicit) {
        if (-not (Test-Path -LiteralPath $explicit)) { throw "BenchExe が無い: $explicit" }
        return (Resolve-Path -LiteralPath $explicit).Path
    }
    $repo = Split-Path -Parent $PSScriptRoot
    foreach ($config in 'Debug', 'Release') {
        $candidate = Join-Path $repo "tools\Ohanami.Bench\bin\$config\net10.0\Ohanami.Bench.exe"
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    throw 'Ohanami.Bench.exe が見つからない。-BenchExe で指定するか、先に dotnet build tools/Ohanami.Bench を実行する'
}

# ベンチの stage-count で done の数を数える。数えられなければ止まる(黙って 0 にしない)。一時的な失敗は 3 回まで読み直す
function Get-DoneCount {
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        $output = & $script:Bench stage-count --folder $Folder --stage $Stage --data $DataDir 2>&1
        $text = ("$output").Trim()
        $number = 0
        if ($LASTEXITCODE -eq 0 -and [int]::TryParse($text, [ref]$number)) { return $number }
        if ($attempt -eq 3) { throw "stage-count が数えられない(終了コード $LASTEXITCODE): $text" }
        Start-Sleep -Milliseconds 500
    }
}

# pid ファイルの記録と、実際のプロセスが一致するときだけ、そのプロセスを返す(違えば $null)。tools/kill-llama-after-health.ps1 と同じ確認
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
    return [pscustomobject]@{ Pid = [int]$record.pid; Process = $process }
}

$script:Bench = Resolve-BenchExe $BenchExe
if (-not (Test-Path -LiteralPath (Join-Path $DataDir 'ohanami.db'))) { throw "DB が無い: $(Join-Path $DataDir 'ohanami.db')(-DataDir は、App を OHANAMI_DATA_DIR で起動した場所)" }

Write-Log "監視を始める。$Stage の done が $Threshold 枚以上になったら、pid ファイル($pidFile)の llama-server を 1 回 kill。フォルダ: $Folder"
$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
$last = -1
$noticed = $false
while ((Get-Date) -lt $deadline) {
    $count = Get-DoneCount
    if ($count -ne $last) { Write-Log "$Stage の done = $count"; $last = $count }
    if ($count -ge $Threshold) {
        $server = Get-RecordedServer
        if ($null -eq $server) {
            if (-not $noticed) { Write-Log 'done は足りたが、pid ファイルの記録と一致する llama-server が居ない(処理の合間か、起動の途中)。kill しないで待つ'; $noticed = $true }
        } else {
            Write-Log "done = $count >= $Threshold。llama-server(pid $($server.Pid)。pid ファイルの記録と一致)を強制終了する"
            if ($PSCmdlet.ShouldProcess("llama-server pid $($server.Pid)", '強制終了(Stop-Process -Force)')) {
                Stop-Process -Id $server.Pid -Force
                Write-Log 'kill した。App が起動し直すのを待ってから、全部流れ終わったら sleep-report --scenario kill-server'
            }
            exit 0
        }
    }
    Start-Sleep -Milliseconds ([int]($PollSeconds * 1000))
}
Write-Log "時間切れ(done = $last)。何もしないで終わる"
exit 3
