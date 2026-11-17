<#
.SYNOPSIS
  ビルドとテストに要る、リポジトリに入れていないファイルを取って置く。手で動かす。ネットワークに出るのは、このスクリプトだけ(製品ではない)。

.DESCRIPTION
  取るもの(取得元は一次の配布元だけ):
    1. llama.cpp b12110 の 2 つの zip(GitHub の ggml-org/llama.cpp のリリース)と、cuDNN 9.28.0.42(CUDA 13)の zip(NVIDIA の配布元)。
       製品が使うファイルだけを取り出して、<runtime>\llama.cpp\b12110\ と <runtime>\cudnn\9.28.0.42_cuda13\ に置く(CUDA の 3 つの DLL は llama.cpp の側)。
    2. 通常のテストに要る tokenizer.json(timm/ViT-SO400M-16-SigLIP2-384 のコミット fec784da… の同名のファイル)。
       <models>\ViT-SO400M-16-SigLIP2-384__ohanami\textual\ に置く。
    3. Visual C++ ランタイムの 5 つの DLL は、取らない(取得元は Visual Studio の再配布のフォルダ)。
       Visual Studio 2022 / Build Tools のインストール先から、許可の表と SHA-256 が同じ物を探して、<runtime>\vcruntime\14.44.35211_x64\ に写す。
       見つからなければ、取り方を案内して、終了コード 1 で止まる(-SkipVc で省く)。

  確かめること(1 つでも違えば、その場で止まる。置いたファイルは、確かめたあとにだけ置く):
    - zip の SHA-256(このスクリプトの定数)。zip の中の、置くファイルごとの SHA-256(tools/release/allowed-files.tsv の値)。
    - tokenizer.json の SHA-256(定数。models/MANIFEST.tsv の値と同じことも確かめる)。
  置き場に、同じ名前で中身の違うファイルがあれば、止まる(-Force で上書きする)。全部そろっていれば、何も取らない。
  zip は、%TEMP%\ohanami-fetch に取り、成功したら消す(-KeepDownloads で残す。途中で止まったときは残るが、再開はしない。次は取り直す)。

  取る量: zip で、llama.cpp 約 0.6GB、cuDNN 約 1.3GB。置く量は、runtime 約 1.2GB。作業の場所に、zip の分の空きが要る。

.PARAMETER RuntimeDirectory
  runtime/ に当たるフォルダ。既定は、環境変数 OHANAMI_RUNTIME_DIR、なければリポジトリの runtime/。

.PARAMETER ModelsDirectory
  models/ に当たるフォルダ。既定は、環境変数 OHANAMI_MODELS_DIR、なければリポジトリの models/。

.PARAMETER WorkDirectory
  zip を取る場所。既定は %TEMP%\ohanami-fetch。

.PARAMETER SkipRuntime
  llama.cpp と cuDNN を取らない。

.PARAMETER SkipTokenizer
  tokenizer.json を取らない。

.PARAMETER SkipVc
  VC++ ランタイムを探さない。

.PARAMETER KeepDownloads
  取った zip を消さない。

.PARAMETER Force
  置き場にある、中身の違うファイルを上書きする。

.EXAMPLE
  pwsh tools/runtime/Get-Runtime.ps1
#>
[CmdletBinding()]
param(
    [string]$RuntimeDirectory,
    [string]$ModelsDirectory,
    [string]$WorkDirectory = (Join-Path $env:TEMP 'ohanami-fetch'),
    [switch]$SkipRuntime,
    [switch]$SkipTokenizer,
    [switch]$SkipVc,
    [switch]$KeepDownloads,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$runtimeDir = if ($RuntimeDirectory) { $RuntimeDirectory } elseif ($env:OHANAMI_RUNTIME_DIR) { $env:OHANAMI_RUNTIME_DIR } else { Join-Path $repoRoot 'runtime' }
$modelsDir = if ($ModelsDirectory) { $ModelsDirectory } elseif ($env:OHANAMI_MODELS_DIR) { $env:OHANAMI_MODELS_DIR } else { Join-Path $repoRoot 'models' }
$runtimeDir = [System.IO.Path]::GetFullPath($runtimeDir)
$modelsDir = [System.IO.Path]::GetFullPath($modelsDir)
$workDir = [System.IO.Path]::GetFullPath($WorkDirectory)
$stageDir = Join-Path $workDir 'stage'

# ---- 取得元(一次の配布元だけ)。置き場の名前は build/*.targets と同じ(DistributionLayoutTests が突き合わせる)
$llamaBase = 'https://github.com/ggml-org/llama.cpp/releases/download/b12110'
$sets = @(
    @{
        Name = 'llama.cpp b12110 (本体)'
        Url = "$llamaBase/llama-b12110-bin-win-cuda-13.4-x64.zip"
        Sha256 = 'dc910a969190b5d42e189afd73117f6bbdd8dd8ed6586342c5e0b82cab62c673'
        Dest = 'llama.cpp\b12110'
        Label = 'llama.cpp'
        EntryDir = ''
    },
    @{
        Name = 'llama.cpp b12110 (CUDA ランタイム)'
        Url = "$llamaBase/cudart-llama-bin-win-cuda-13.4-x64.zip"
        Sha256 = '738f8c251ac22b70c3ae6f83a10cf222725df0395246a2cf58f32bdb85fbe668'
        Dest = 'llama.cpp\b12110'
        Label = 'cuda (llama.cpp の同梱物)'
        EntryDir = ''
    },
    @{
        Name = 'cuDNN 9.28.0.42 (CUDA 13)'
        Url = 'https://developer.download.nvidia.com/compute/cudnn/redist/cudnn/windows-x86_64/cudnn-windows-x86_64-9.28.0.42_cuda13-archive.zip'
        Sha256 = 'b1da81fc9b1e6196205a13ca59cba7b3a321d907c03e90a19d9665867d762137'
        Dest = 'cudnn\9.28.0.42_cuda13'
        Label = 'cudnn'
        EntryDir = 'cudnn-windows-x86_64-9.28.0.42_cuda13-archive/bin/x64/'
    }
)
$tokenizer = @{
    Url = 'https://huggingface.co/timm/ViT-SO400M-16-SigLIP2-384/resolve/fec784dabb3081a5f101fc74eefaf9d1ed08237b/tokenizer.json'
    Sha256 = '220c63d496e0c14e63eb656c91e0215e926202e4c74b1f089e09f1920d779b04'
    Dest = 'ViT-SO400M-16-SigLIP2-384__ohanami\textual\tokenizer.json'
}
$vcDest = 'vcruntime\14.44.35211_x64'
$vcLabelPrefix = 'vc++'

# ---- 許可の表(tools/release/allowed-files.tsv)。置いたファイルの SHA-256 の正解
$tablePath = Join-Path $PSScriptRoot '..\release\allowed-files.tsv'
$tableLines = [System.IO.File]::ReadAllLines($tablePath, [System.Text.Encoding]::UTF8)
if ($tableLines.Count -lt 2 -or $tableLines[0] -ne "path`tsha256`tsource") { throw "許可の表の 1 行目が「path<TAB>sha256<TAB>source」でない: $tablePath" }
$table = [System.Collections.Generic.List[object]]::new()
foreach ($line in $tableLines | Select-Object -Skip 1) {
    if ($line.Length -eq 0) { continue }
    $c = $line.Split("`t")
    if ($c.Count -ne 3) { throw "許可の表の列数が 3 でない: $line" }
    $table.Add([pscustomobject]@{ Name = $c[0]; Sha256 = $c[1]; Label = $c[2] })
}

function Get-Expected([string]$label, [switch]$Prefix) {
    $rows = @($table | Where-Object { if ($Prefix) { $_.Label.StartsWith($label, [System.StringComparison]::Ordinal) } else { $_.Label -ceq $label } })
    if ($rows.Count -eq 0) { throw "許可の表に、出どころ「$label」の行が無い" }
    foreach ($r in $rows) {
        if ($r.Name -notmatch '^[A-Za-z0-9._-]+$') { throw "許可の表のファイル名が、平らな名前でない: $($r.Name)" }
        if ($r.Sha256 -notmatch '^[0-9a-f]{64}$') { throw "許可の表の SHA-256 が、小文字 16 進 64 桁でない: $($r.Name)" }
    }
    return $rows
}

function Get-Sha256([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }

function Save-Download([string]$url, [string]$sha256, [string]$path) {
    # 取り済みで、SHA-256 が合えば使う。違えば、消して取り直す(途中からの再開はしない)
    if (Test-Path -LiteralPath $path) {
        if ((Get-Sha256 $path) -eq $sha256) { Write-Host "   取り済み: $path"; return }
        Remove-Item -LiteralPath $path -Force
    }
    if (-not (Get-Command curl.exe -ErrorAction SilentlyContinue)) { throw 'curl.exe が無い(Windows 10 以降に入っている)。' }
    Write-Host "   取る: $url"
    New-Item -ItemType Directory -Path (Split-Path $path) -Force | Out-Null
    & curl.exe --fail --location --silent --show-error --retry 3 --output $path $url
    if ($LASTEXITCODE -ne 0) { throw "取得に失敗した(curl の終了コード $LASTEXITCODE): $url" }
    $got = Get-Sha256 $path
    if ($got -ne $sha256) {
        Remove-Item -LiteralPath $path -Force
        throw "取ったファイルの SHA-256 が定数と違う: $url`n  定数 $sha256`n  実際 $got`n(取得元が差し替わった、または回線で壊れた。取ったファイルは消した)"
    }
    Write-Host "   SHA-256 一致: $sha256"
}

function Get-DestState([string]$dir, $files) {
    # 置き場の状態: ok(全部あって一致)/ missing(足りない)/ differs(同じ名前で中身が違う物がある)
    $missing = $false
    foreach ($f in $files) {
        $p = Join-Path $dir $f.Name
        if (-not (Test-Path -LiteralPath $p)) { $missing = $true; continue }
        if ((Get-Sha256 $p) -ne $f.Sha256) { return @{ State = 'differs'; File = $f.Name } }
    }
    return @{ State = $(if ($missing) { 'missing' } else { 'ok' }); File = $null }
}

function Install-Files([string]$stage, [string]$dir, $files) {
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    foreach ($f in $files) {
        $dst = Join-Path $dir $f.Name
        Copy-Item -LiteralPath (Join-Path $stage $f.Name) -Destination "$dst.part" -Force
        Move-Item -LiteralPath "$dst.part" -Destination $dst -Force
    }
}

$placed = [System.Collections.Generic.List[string]]::new()
$sourceNotes = @{}   # 置き場ごとの SOURCE.txt の行

if (Test-Path -LiteralPath $stageDir) { Remove-Item -LiteralPath $stageDir -Recurse -Force }
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

# ---- 1. llama.cpp と cuDNN
if (-not $SkipRuntime) {
    Write-Host "== 1. llama.cpp と cuDNN を $runtimeDir に置く"
    foreach ($set in $sets) {
        $files = Get-Expected $set.Label
        $dir = Join-Path $runtimeDir $set.Dest
        $state = Get-DestState $dir $files
        if ($state.State -eq 'differs' -and -not $Force) {
            throw "置き場に、中身の違うファイルがある: $(Join-Path $dir $state.File)(上書きするなら -Force)"
        }
        $note = "$($set.Name)`n  取得元: $($set.Url)`n  zip の SHA-256: $($set.Sha256)`n  置いたファイル: $($files.Count) 個(SHA-256 は tools/release/allowed-files.tsv と同じ)"
        if (-not $sourceNotes.ContainsKey($set.Dest)) { $sourceNotes[$set.Dest] = [System.Collections.Generic.List[string]]::new() }
        $sourceNotes[$set.Dest].Add($note)
        if ($state.State -eq 'ok') { Write-Host "   $($set.Name): 置き済み($($files.Count) 個。取らない)"; continue }

        Write-Host "   $($set.Name)"
        $zipPath = Join-Path $workDir ([System.IO.Path]::GetFileName($set.Url))
        Save-Download $set.Url $set.Sha256 $zipPath

        $stage = Join-Path $stageDir ($set.Dest -replace '[\\/]', '_')
        New-Item -ItemType Directory -Path $stage -Force | Out-Null
        $zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
        try {
            foreach ($f in $files) {
                # 取り出す名前は、表のファイル名で決める(zip の中の名前では、置き場所を決めない)
                $entry = $zip.GetEntry($set.EntryDir + $f.Name)
                if ($null -eq $entry) { throw "zip に、置くファイルが無い: $($set.EntryDir)$($f.Name)($($set.Url))" }
                $target = Join-Path $stage $f.Name
                [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
                $got = Get-Sha256 $target
                if ($got -ne $f.Sha256) { throw "取り出したファイルの SHA-256 が、許可の表と違う: $($f.Name)`n  表 $($f.Sha256)`n  実際 $got" }
            }
        }
        finally { $zip.Dispose() }
        Install-Files $stage $dir $files
        foreach ($f in $files) { $placed.Add((Join-Path $dir $f.Name)) }
        Write-Host "   置いた: $dir($($files.Count) 個。SHA-256 は表と一致)"
        if (-not $KeepDownloads) { Remove-Item -LiteralPath $zipPath -Force }
    }
    foreach ($dest in $sourceNotes.Keys) {
        $text = "このフォルダのファイルは、tools/runtime/Get-Runtime.ps1 が取って置いた。製品が使うファイルだけを、zip から取り出してある。`n`n" + ($sourceNotes[$dest] -join "`n`n") + "`n"
        [System.IO.File]::WriteAllText((Join-Path (Join-Path $runtimeDir $dest) 'SOURCE.txt'), $text, (New-Object System.Text.UTF8Encoding($false)))
    }
}

# ---- 2. tokenizer.json
if (-not $SkipTokenizer) {
    Write-Host "== 2. tokenizer.json を $modelsDir に置く"
    # 定数が、Git に入っている models/MANIFEST.tsv の値と同じことを確かめる(食い違えば、テストが落ちるので、ここで止める)
    $manifestPath = Join-Path $repoRoot 'models\MANIFEST.tsv'
    $manifestRow = [System.IO.File]::ReadAllLines($manifestPath, [System.Text.Encoding]::UTF8) |
        Where-Object { $_.Split("`t").Count -ge 5 -and $_.Split("`t")[0] -like '*ViT-SO400M-16-SigLIP2-384__ohanami' -and $_.Split("`t")[2] -eq 'textual/tokenizer.json' } | Select-Object -First 1
    if (-not $manifestRow) { throw "models/MANIFEST.tsv に、textual/tokenizer.json の行が無い" }
    if ($manifestRow.Split("`t")[4] -ne $tokenizer.Sha256) { throw "tokenizer.json の SHA-256(定数)が、models/MANIFEST.tsv と違う: $($manifestRow.Split("`t")[4])" }

    $dest = Join-Path $modelsDir $tokenizer.Dest
    $isOk = $false
    if (Test-Path -LiteralPath $dest) {
        if ((Get-Sha256 $dest) -eq $tokenizer.Sha256) { $isOk = $true }
        elseif (-not $Force) { throw "置き場に、中身の違うファイルがある: $dest(上書きするなら -Force)" }
    }
    if ($isOk) { Write-Host "   置き済み: $dest(取らない)" }
    else {
        $path = Join-Path $workDir 'tokenizer.json'
        Save-Download $tokenizer.Url $tokenizer.Sha256 $path
        New-Item -ItemType Directory -Path (Split-Path $dest) -Force | Out-Null
        Copy-Item -LiteralPath $path -Destination "$dest.part" -Force
        Move-Item -LiteralPath "$dest.part" -Destination $dest -Force
        $placed.Add($dest)
        Write-Host "   置いた: $dest"
        if (-not $KeepDownloads) { Remove-Item -LiteralPath $path -Force }
    }
}

# ---- 3. VC++ ランタイム(取らない。Visual Studio の再配布のフォルダから、表と SHA-256 が同じ物を探して写す)
$vcProblem = $null
if (-not $SkipVc) {
    Write-Host "== 3. VC++ ランタイムを探す"
    $files = Get-Expected $vcLabelPrefix -Prefix
    $dir = Join-Path $runtimeDir $vcDest
    $state = Get-DestState $dir $files
    if ($state.State -eq 'differs' -and -not $Force) { throw "置き場に、中身の違うファイルがある: $(Join-Path $dir $state.File)(上書きするなら -Force)" }
    if ($state.State -eq 'ok') { Write-Host "   置き済み: $dir($($files.Count) 個。探さない)" }
    else {
        $installs = @()
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (Test-Path -LiteralPath $vswhere) { $installs += @(& $vswhere -products '*' -all -property installationPath) }
        $vsRoot = Join-Path $env:ProgramFiles 'Microsoft Visual Studio\2022'
        if (Test-Path -LiteralPath $vsRoot) { $installs += @(Get-ChildItem -LiteralPath $vsRoot -Directory | ForEach-Object { $_.FullName }) }
        $candidates = @($installs | Where-Object { $_ } | Sort-Object -Unique |
            ForEach-Object { Get-ChildItem -Path (Join-Path $_ 'VC\Redist\MSVC\*\x64\Microsoft.VC143.CRT') -Directory -ErrorAction SilentlyContinue } |
            ForEach-Object { $_.FullName })
        $found = $null
        $seen = [System.Collections.Generic.List[string]]::new()
        foreach ($c in $candidates) {
            $same = $true
            foreach ($f in $files) {
                $p = Join-Path $c $f.Name
                if (-not (Test-Path -LiteralPath $p) -or (Get-Sha256 $p) -ne $f.Sha256) { $same = $false; break }
            }
            if ($same) { $found = $c; break }
            $v = Join-Path $c 'vcruntime140.dll'
            $seen.Add($(if (Test-Path -LiteralPath $v) { "$c(vcruntime140.dll の版 $((Get-Item -LiteralPath $v).VersionInfo.FileVersion))" } else { $c }))
        }
        if ($found) {
            $stage = Join-Path $stageDir 'vc'
            New-Item -ItemType Directory -Path $stage -Force | Out-Null
            foreach ($f in $files) { Copy-Item -LiteralPath (Join-Path $found $f.Name) -Destination (Join-Path $stage $f.Name) }
            Install-Files $stage $dir $files
            foreach ($f in $files) { $placed.Add((Join-Path $dir $f.Name)) }
            $text = "Visual C++ ランタイム(x64。Visual C++ 2015-2022 の再配布ファイル)のうち、ORT と llama-server が使う 5 つ。変更していない。`n写した元: $found`nSHA-256 は tools/release/allowed-files.tsv と同じ(tools/runtime/Get-Runtime.ps1 が確かめた)。再配布の根拠: docs/adr/0044-vc-runtime-bundled-app-local.md`n"
            [System.IO.File]::WriteAllText((Join-Path $dir 'SOURCE.txt'), $text, (New-Object System.Text.UTF8Encoding($false)))
            Write-Host "   写した: $found -> $dir($($files.Count) 個。SHA-256 は表と一致)"
        }
        else {
            $seenText = if ($seen.Count -gt 0) { "見つかった再配布のフォルダ(SHA-256 が表と違う):`n" + (($seen | ForEach-Object { "     $_" }) -join "`n") } else { "Visual Studio 2022 / Build Tools の、再配布のフォルダ(VC\Redist\MSVC\<版>\x64\Microsoft.VC143.CRT)が、見つからなかった。" }
            $vcProblem = @"
VC++ ランタイムの 5 つの DLL($(($files | ForEach-Object { $_.Name }) -join '、'))を、置けなかった。
$seenText
取り方(どちらか):
  - Visual Studio 2022 の 17.14 系(Community でも、Build Tools でもよい。「MSVC v143 - VS 2022 C++ x64/x86 ビルド ツール」を入れる)を入れて、このスクリプトを、もう一度動かす。
    表の SHA-256 は、再配布ファイルの版 14.44.35211.0(Visual Studio 2022 17.14 の VC\Redist\MSVC\14.44.35112)のもの。
  - 手で、その 5 つの DLL を $dir に置く。ビルドは、5 つがあれば通る。動かすには 14.44.35207 以上が要る(それより古いと ONNX Runtime が動かない。docs/adr/0044-vc-runtime-bundled-app-local.md)。
    配布物を作る(tools/release/Publish-Release.ps1)ときだけは、表と同じ SHA-256 でないと、突き合わせが失敗する。
"@
        }
    }
}

if (Test-Path -LiteralPath $stageDir) { Remove-Item -LiteralPath $stageDir -Recurse -Force }
if (-not $KeepDownloads -and (Test-Path -LiteralPath $workDir) -and -not (Get-ChildItem -LiteralPath $workDir -Force | Measure-Object).Count) { Remove-Item -LiteralPath $workDir -Force }

Write-Host "== 完了: 置いたファイル $($placed.Count) 個"
if ($vcProblem) {
    Write-Host $vcProblem -ForegroundColor Red
    exit 1
}
