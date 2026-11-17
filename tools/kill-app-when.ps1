<#
.SYNOPSIS
  スリープ・強制終了の確認(sleep-report の kill-app-after-kill・kill-app-final)用: フィクスチャのフォルダの caption(または指定の段階)の done が N 枚になったら、Ohanami.App を強制終了する(Stop-Process -Force = TerminateProcess)。1 回だけ。

.DESCRIPTION
  手でタイミングを合わせる代わりに、done の数を 1 秒おきに数えて、N 枚に達した瞬間に App を強制終了する。強制終了は「タスクの終了」(ウィンドウに閉じるよう頼む = 正常な終了になる)ではなく、
  プロセスを即座に終わらせる(ADR 0026 の、アプリの強制終了のあとの後始末の確認)。llama-server を道連れにする(ジョブオブジェクト)かを、このあと sleep-report で確かめる。

  数え方: ベンチの `stage-count`(sleep-report と同じ DB の読み取り専用の読み方。sqlite3 の CLI に頼らない)。ベンチの exe が要る(-BenchExe。既定はリポジトリの tools\Ohanami.Bench\bin\Debug または Release)。

  安全のため、終わらせるのは **プロセス名が Ohanami.App で、-AppPid(省略時は、動いている Ohanami.App がちょうど 1 つのときのその pid)に一致するプロセスだけ**。
  他のプロセス・他の llama-server には触らない。-WhatIf を付けると、強制終了は実行せず「何をするか」だけを出す(数える部分は動く)。GPU は使わない。スリープ・強制終了の確認にだけ使う。

.PARAMETER Folder
  フィクスチャの場面のフォルダ(sleep-fixture が作った kill-app のフォルダ。App に追加したもの)。sleep-report の --folder と同じ。

.PARAMETER Threshold
  done の枚数がこの値以上になったら強制終了する(sleep-fixture が作る kill-app のフォルダは 30 枚)。

.PARAMETER AppPid
  強制終了する Ohanami.App の pid。省略すると、動いている Ohanami.App がちょうど 1 つのときだけ、それを使う。

.PARAMETER Stage
  数える段階(embed / ocr / caption。既定 caption)。

.PARAMETER DataDir
  App を OHANAMI_DATA_DIR で起動した場所(既定 %LOCALAPPDATA%\Ohanami\sleep-check)。

.PARAMETER BenchExe
  Ohanami.Bench.exe のパス(既定は、リポジトリの tools\Ohanami.Bench\bin\Debug\net10.0\ または Release)。

.PARAMETER TimeoutMinutes
  待つ上限(分。既定 15)。超えたら何もしないで終わる(終了コード 3)。

.PARAMETER PollSeconds
  数える間隔(秒。既定 1)。

.NOTES
  終了コード: 0 = 強制終了した(-WhatIf のときは、した場合)、2 = App が居ない(何もしなかった)、3 = 時間切れ。数えられない(ベンチの失敗)ときは例外で止まる。
  実機での動作(本物の App と done の増加を相手にした確認)は、この版では未確認。引数の解釈・数え方・-WhatIf・偽のプロセスの強制終了は、スクラッチの偽物で確かめた。

.EXAMPLE
  pwsh tools/kill-app-when.ps1 -Folder $env:LOCALAPPDATA\Ohanami\sleep-check-fixture\kill-app -Threshold 8 -WhatIf
  pwsh tools/kill-app-when.ps1 -Folder $env:LOCALAPPDATA\Ohanami\sleep-check-fixture\kill-app -Threshold 8
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)][string]$Folder,
    [Parameter(Mandatory = $true)][ValidateRange(1, 100000)][int]$Threshold,
    [int]$AppPid = 0,
    [ValidateSet('embed', 'ocr', 'caption')][string]$Stage = 'caption',
    [string]$DataDir = (Join-Path $env:LOCALAPPDATA 'Ohanami\sleep-check'),
    [string]$BenchExe = '',
    [double]$TimeoutMinutes = 15,
    [double]$PollSeconds = 1
)

$ErrorActionPreference = 'Stop'

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

$script:Bench = Resolve-BenchExe $BenchExe
if (-not (Test-Path -LiteralPath (Join-Path $DataDir 'ohanami.db'))) { throw "DB が無い: $(Join-Path $DataDir 'ohanami.db')(-DataDir は、App を OHANAMI_DATA_DIR で起動した場所)" }

if ($AppPid -eq 0) {
    $apps = @(Get-Process -Name 'Ohanami.App' -ErrorAction SilentlyContinue)
    if ($apps.Count -ne 1) { throw "動いている Ohanami.App が $($apps.Count) 個。-AppPid で強制終了する pid を指定する" }
    $AppPid = $apps[0].Id
}

Write-Log "監視を始める。Ohanami.App pid $AppPid、$Stage の done が $Threshold 枚以上で強制終了。フォルダ: $Folder"
$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
$last = -1
while ((Get-Date) -lt $deadline) {
    $process = Get-Process -Id $AppPid -ErrorAction SilentlyContinue
    if ($null -eq $process -or $process.ProcessName -ne 'Ohanami.App') {
        Write-Log "Ohanami.App(pid $AppPid)が居ない。何もしないで終わる"
        exit 2
    }
    $count = Get-DoneCount
    if ($count -ne $last) { Write-Log "$Stage の done = $count"; $last = $count }
    if ($count -ge $Threshold) {
        $llama = @(Get-Process -Name 'llama-server' -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
        Write-Log "done = $count >= $Threshold。強制終了する(いま動いている llama-server の pid: $($llama -join ','))"
        if ($PSCmdlet.ShouldProcess("Ohanami.App pid $AppPid", '強制終了(Stop-Process -Force)')) {
            Stop-Process -Id $AppPid -Force
            Write-Log '強制終了した。次に sleep-report --scenario kill-app-after-kill'
        }
        exit 0
    }
    Start-Sleep -Milliseconds ([int]($PollSeconds * 1000))
}
Write-Log "時間切れ(done = $last)。何もしないで終わる"
exit 3
