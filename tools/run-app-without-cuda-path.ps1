<#
.SYNOPSIS
  PATH から CUDA 系の DLL があるディレクトリを外して、Ohanami.App を起動する。

.DESCRIPTION
  開発機は PATH に CUDA Toolkit があり、アプリに同梱し忘れた CUDA / cuDNN の DLL が PATH から黙って読まれて、問題が隠れる
  (ADR 0015)。このスクリプトは、PATH から該当のディレクトリを外した環境で App を起動し、
  同梱した DLL だけで動くことを確かめる。手動で App を確かめるときは、このスクリプトで起動する。

  外す対象の判定は、ディレクトリ名でなく中身で行う: PATH の各ディレクトリに、CUDA 系の DLL
  (cudart64_*, cublas*, cudnn*, cufft*, curand*, cusolver*, cusparse*, nvrtc*, nvJitLink*)が 1 つでもあれば外す。
  ドライバの nvcuda.dll は対象にしない(System32 にある正規のもの)。
  外した PATH は、起動する子プロセスの環境変数だけに設定する(このシェルや、OS のユーザーの環境は変えない)。

.PARAMETER Configuration
  Debug か Release。既定は Debug。

.PARAMETER Exe
  起動する exe。省略時は src/Ohanami.App/bin/<Configuration>/net10.0-windows/win-x64/Ohanami.App.exe。

.PARAMETER ArgumentList
  App に渡す引数(動作確認で、Exe に別のプログラムを指定するときにも使う)。

.PARAMETER DryRun
  起動せず、外すディレクトリと、残る PATH の件数を表示して終わる。

.PARAMETER Wait
  App が終わるまで待ち、その終了コードを返す。

.EXAMPLE
  pwsh tools/run-app-without-cuda-path.ps1
  pwsh tools/run-app-without-cuda-path.ps1 -Configuration Release -Wait
  pwsh tools/run-app-without-cuda-path.ps1 -DryRun
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$Exe,
    [string[]]$ArgumentList = @(),
    [switch]$DryRun,
    [switch]$Wait
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Exe) {
    $Exe = Join-Path $repoRoot "src/Ohanami.App/bin/$Configuration/net10.0-windows/win-x64/Ohanami.App.exe"
}

# CUDA 系の DLL の名前(ORT / cuDNN / cuBLAS などが LoadLibrary で名前だけで探すもの)
$cudaPatterns = @(
    'cudart64_*.dll', 'cublas*.dll', 'cudnn*.dll', 'cufft*.dll', 'curand*.dll',
    'cusolver*.dll', 'cusparse*.dll', 'nvrtc*.dll', 'nvJitLink*.dll'
)

function Test-HasCudaDll([string]$dir) {
    if (-not (Test-Path -LiteralPath $dir -PathType Container)) { return $false }
    foreach ($pattern in $cudaPatterns) {
        if (Get-ChildItem -LiteralPath $dir -Filter $pattern -File -ErrorAction SilentlyContinue | Select-Object -First 1) { return $true }
    }
    return $false
}

$originalPath = [Environment]::GetEnvironmentVariable('PATH', 'Process')
if ([string]::IsNullOrEmpty($originalPath)) { throw 'PATH が空です。' }

$kept = New-Object System.Collections.Generic.List[string]
$removed = New-Object System.Collections.Generic.List[string]
foreach ($entry in $originalPath.Split([IO.Path]::PathSeparator)) {
    if ([string]::IsNullOrWhiteSpace($entry)) { continue }
    $expanded = [Environment]::ExpandEnvironmentVariables($entry.Trim().Trim('"'))
    if (Test-HasCudaDll $expanded) { $removed.Add($entry) } else { $kept.Add($entry) }
}
$newPath = [string]::Join([IO.Path]::PathSeparator, $kept)

Write-Host "PATH から外したディレクトリ: $($removed.Count) 件(残り $($kept.Count) 件)"
foreach ($r in $removed) { Write-Host "  - $r" }

if ($DryRun) {
    Write-Host '-DryRun のため起動しません。'
    return
}

if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) {
    throw "App の exe が見つかりません: $Exe (先に dotnet build Ohanami.sln を実行してください)"
}

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Exe
$psi.WorkingDirectory = Split-Path -Parent $Exe
$psi.UseShellExecute = $false
foreach ($a in $ArgumentList) { $psi.ArgumentList.Add($a) }
$psi.Environment['PATH'] = $newPath

Write-Host "起動: $Exe"
$process = [System.Diagnostics.Process]::Start($psi)
if ($Wait) {
    $process.WaitForExit()
    Write-Host "終了コード: $($process.ExitCode)"
    exit $process.ExitCode
}
