<#
.SYNOPSIS
  スリープ(sleep-report の sleep-load)用: 新しい llama-server のプロセスが現れたら(= モデルの読み込みの最中)、すぐ PC をスリープ(SetSuspendState の Suspend)させる。1 回だけ。

.DESCRIPTION
  llama-server の読み込みは約 3 秒しかなく(モデルがキャッシュにあると、起動に約 2.6 秒)、手で「読み込み中」にスリープさせるのは難しい。
  このスクリプトを先に起動して待たせ、「VLM を始める」を押して llama-server が現れたら、100ms 間隔の監視で、すぐ SetSuspendState を呼ぶ。

  実行する前から動いていた llama-server は無視する(新しく現れたものだけに反応する)。**どの llama-server でも反応する**ので、このスクリプトを待たせている間は、App 以外の llama-server を起動しない。
  スリープから復帰すると、SetSuspendState が戻ってスクリプトが終わる。-WhatIf を付けると、スリープさせず「何をするか」だけを出す(PC は止まらない)。GPU は使わない。この確認にだけ使う。

  SetSuspendState(Suspend, $false, $false) が実際に S3 のスリープになるか、休止状態になるかは、この版では未確認(`sleep-report` が出す EffectiveState の番号と HiberPagesWritten で確かめる)。
  休止状態になった場合は、この確認としては無効(スタートメニューの「スリープ」を使う)。

.PARAMETER TimeoutMinutes
  llama-server が現れるのを待つ上限(分。既定 10)。超えたら何もしないで終わる(終了コード 3)。

.NOTES
  終了コード: 0 = スリープさせた(-WhatIf のときは、した場合)、3 = 時間切れ。
  実機での動作(本物のスリープ)は、この版では未確認。引数の解釈・新しいプロセスの検出・-WhatIf は、スクラッチの偽のプロセスで確かめた(スリープは実行していない)。

.EXAMPLE
  pwsh tools/sleep-on-llama-start.ps1 -WhatIf
  pwsh tools/sleep-on-llama-start.ps1
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [double]$TimeoutMinutes = 10
)

$ErrorActionPreference = 'Stop'

function Write-Log([string]$message) {
    Write-Host ('{0} {1}' -f (Get-Date).ToString('HH:mm:ss.fff'), $message)
}

$before = @(Get-Process -Name 'llama-server' -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
Write-Log "待つ(すでに動いている llama-server は無視する: $($before -join ',')。新しく現れたら、スリープする)"
$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
while ((Get-Date) -lt $deadline) {
    $new = @(Get-Process -Name 'llama-server' -ErrorAction SilentlyContinue | Where-Object { $before -notcontains $_.Id })
    if ($new.Count -gt 0) {
        Write-Log "llama-server(pid $($new[0].Id)、開始 $($new[0].StartTime.ToString('HH:mm:ss.fff')))を見つけた。スリープする"
        if ($PSCmdlet.ShouldProcess('この PC', 'スリープ(SetSuspendState の Suspend)')) {
            Add-Type -AssemblyName System.Windows.Forms
            $ok = [System.Windows.Forms.Application]::SetSuspendState([System.Windows.Forms.PowerState]::Suspend, $false, $false)
            Write-Log "SetSuspendState の戻り値: $ok(復帰後にここに来る)。次に sleep-report --scenario sleep-load"
        }
        exit 0
    }
    Start-Sleep -Milliseconds 100
}
Write-Log '時間切れ(新しい llama-server は現れなかった)。何もしないで終わる'
exit 3
