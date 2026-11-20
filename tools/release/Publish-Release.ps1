<#
.SYNOPSIS
  配布物(self-contained の zip)を作る。手で動かす。zip を GitHub などに上げることはしない。

.DESCRIPTION
  次の順に進む。1 つでも食い違えば、その時点で止まる(zip は作らない)。
    1. dotnet publish(Release・win-x64・self-contained)を、出力先の Ohanami フォルダに作る。
    2. 出力のファイルを、許可の表(tools/release/allowed-files.tsv)と突き合わせる。
       - 表に無いファイルが出力にあれば失敗(pdb・lib・開発機の物が紛れ込むのを止める)。
       - 表にあるファイルが出力に無ければ失敗。
       - 表に SHA-256 が書いてあるファイル(第三者のバイナリ・.NET のランタイム)は、SHA-256 が違えば失敗。
         同梱する第三者のバイナリの SHA-256 は、実行時ではなく、ここで確かめる(llama-server.exe だけは、実行時にも確かめる)。
       - LICENSE・THIRD-PARTY-NOTICES.md・licenses/ の文書(表の出どころは repository)は、リポジトリの同じパスのファイルと、バイト単位で同一でなければ失敗。
       - データ削除の道具(直下の Ohanami-RemoveData.cmd・Ohanami-RemoveData.ps1。表の出どころは repository)は、リポジトリの src/Ohanami.RemoveData/ の同名のファイルと、バイト単位で同一でなければ失敗。
         加えて文字コードと改行を確かめる: .ps1 は BOM 付きの UTF-8(先頭が EF BB BF)、.cmd は ASCII だけで、LF の前に必ず CR がある(CRLF)。
         Windows PowerShell 5.1 は BOM の無い .ps1 を ANSI として読むので、日本語が化ける。LF だけの .cmd は、cmd.exe の読み方が保証されない。違えば zip は作らない。
    3. 出力のすべてのファイルの中に、ビルドした PC のパスが残っていないことを確かめる(下の「パスの検査」)。
    4. zip を作り、2GiB 未満であることを確かめ、zip の SHA-256 を出す(<zip>.sha256 にも書く)。

  パスの検査: 次の文字列を、出力のすべてのファイル(第三者の DLL・exe も含む)のバイト列から探す。ASCII(UTF-8)と UTF-16 の両方で、
  英字の大文字・小文字は区別しない。1 つでも見つかれば、ファイルと位置を出して止まる(zip は作らない)。
    - ユーザーのフォルダ($env:USERPROFILE。例: C:\Users\<名前>)と、リポジトリのルートの絶対パス。区切りは \ と / の両方。
    - \<ユーザー名>\ と /<ユーザー名>/(区切りで挟んだ形)。ユーザー名だけでは、短い名前が無関係なバイト列に当たりすぎるので、区切りで挟む。
  配布する DLL・exe には、build/OhanamiPathMap.props(Release の PathMap)が、pdb の場所とソースのパスを置き換えて、パスを残さない。
  第三者のファイルに当たったときは、止めたままにして、そのファイルを調べる(許可の仕組みは、必要になってから作る)。

  CUDA・cuDNN・VC++ ランタイム・llama-server の一式は、リポジトリの外の runtime/ から、ビルドが exe の隣にコピーする(build/*.targets)。
  git の worktree など、runtime/ が無い場所からは、-RuntimeDirectory(または環境変数 OHANAMI_RUNTIME_DIR)で、本体の runtime/ を指す。

  許可の表を更新するとき(.NET や NuGet の版を上げた、同梱物を替えた): -ProposeTable を付けて動かすと、出力の実際の中身から作った表を
  <出力先>\allowed-files.proposed.tsv に書く(表そのものは書き換えない)。差分を目で確かめてから、tools/release/allowed-files.tsv に手で置き換える。

.PARAMETER OutputDirectory
  出力先。リポジトリの外に置く。既定は %TEMP%\ohanami-release。空でなければ、-Force が要る(中身を消して作り直す)。

.PARAMETER Version
  zip の名前に入れる版(Ohanami-<版>-win-x64.zip)。既定は dev。

.PARAMETER RuntimeDirectory
  runtime/ に当たるディレクトリ(中に llama.cpp\b12110・cudnn\<版>・vcruntime\<版> がある)。

.PARAMETER TablePath
  許可の表のファイル。既定は tools/release/allowed-files.tsv(試すときだけ、別の表を指す)。

.PARAMETER ProposeTable
  許可の表の案を書く(上の説明)。突き合わせの失敗では止まらず、案を書いてから終わる。

.PARAMETER NoZip
  突き合わせまでで終わる(zip を作らない)。

.EXAMPLE
  pwsh tools/release/Publish-Release.ps1 -Version 0.1.0 -RuntimeDirectory C:\Users\<名前>\RiderProjects\Ohanami\runtime
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $env:TEMP 'ohanami-release'),
    [string]$Version = 'dev',
    [string]$RuntimeDirectory,
    [string]$TablePath,
    [switch]$ProposeTable,
    [switch]$NoZip,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$tablePath = if ($TablePath) { $TablePath } else { Join-Path $PSScriptRoot 'allowed-files.tsv' }
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$limitBytes = [int64]2GB - 1   # 1 ファイル 2GiB 未満(GitHub Releases)

# 出力先は、リポジトリの外(pdb などが木に入らないように)
$out = [System.IO.Path]::GetFullPath($OutputDirectory)
if ($out.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "出力先がリポジトリの中: $out (リポジトリの外を指定する)"
}
if ($RuntimeDirectory) { $env:OHANAMI_RUNTIME_DIR = [System.IO.Path]::GetFullPath($RuntimeDirectory) }

$appDir = Join-Path $out 'Ohanami'
$zipPath = Join-Path $out "Ohanami-$Version-win-x64.zip"

if (Test-Path -LiteralPath $out) {
    if ((Get-ChildItem -LiteralPath $out -Force | Measure-Object).Count -gt 0) {
        if (-not $Force) { throw "出力先が空でない: $out (-Force で、中身を消して作り直す)" }
        Remove-Item -LiteralPath $out -Recurse -Force
    }
}
New-Item -ItemType Directory -Path $out -Force | Out-Null

# ---- 1. publish
Write-Host '== 1. dotnet publish (Release / win-x64 / self-contained)'
& dotnet publish (Join-Path $repoRoot 'src\Ohanami.App') -c Release -r win-x64 --self-contained true -o $appDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish が失敗した(終了コード $LASTEXITCODE)" }

# ---- 2. 許可の表との突き合わせ
Write-Host '== 2. 出力を許可の表と突き合わせる'
$appFull = (Resolve-Path -LiteralPath $appDir).Path
$actual = @{}
foreach ($f in Get-ChildItem -LiteralPath $appFull -Recurse -File) {
    $rel = $f.FullName.Substring($appFull.Length).TrimStart('\', '/').Replace('\', '/')
    $actual[$rel] = $f
}
Write-Host ("   出力: {0} ファイル、{1:N1} MiB" -f $actual.Count, (($actual.Values | Measure-Object Length -Sum).Sum / 1MB))

# 配布物の直下に置くデータ削除の道具は、リポジトリでは src/Ohanami.RemoveData/ にある(配布物のパス → リポジトリのパス)。ほかの文書は、同じパス
$repositoryPathOf = @{
    'Ohanami-RemoveData.cmd' = 'src/Ohanami.RemoveData/Ohanami-RemoveData.cmd'
    'Ohanami-RemoveData.ps1' = 'src/Ohanami.RemoveData/Ohanami-RemoveData.ps1'
}

function Get-Label([string]$rel) {
    $name = [System.IO.Path]::GetFileName($rel)
    if ($rel -eq 'LICENSE' -or $rel -eq 'THIRD-PARTY-NOTICES.md' -or $rel -match '^licenses/' -or $repositoryPathOf.ContainsKey($rel)) { return 'repository' }
    if ($rel -notmatch '/' -and $name -match '^Ohanami\.(App\.(exe|dll|deps\.json|runtimeconfig\.json)|Core\.dll)$') { return 'ohanami' }
    if ($name -match '^(cudart|cublas|cublasLt)64_13\.dll$') { return 'cuda (llama.cpp の同梱物)' }
    if ($name -match '^cudnn.*64_9\.dll$') { return 'cudnn' }
    if ($name -match '^(msvcp140|msvcp140_1|msvcp140_atomic_wait|vcruntime140|vcruntime140_1)\.dll$') { return 'vc++ ランタイム (Visual Studio 2022 の再配布ファイル)' }
    if ($name -match '^(llama-server\.exe|llama.*\.dll|mtmd\.dll|ggml.*\.dll|libomp\.dll)$') { return 'llama.cpp' }
    if ($name -match '^onnxruntime.*\.dll$') { return 'onnxruntime (NuGet)' }
    if ($name -match '^(SkiaSharp\.dll|libSkiaSharp\.dll)$') { return 'skiasharp (NuGet)' }
    if ($name -match '^(SQLitePCLRaw\..*\.dll|e_sqlite3\.dll)$') { return 'sqlite (NuGet)' }
    return '.NET ランタイム'
}

if ($ProposeTable) {
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("path`tsha256`tsource")
    $sortedKeys = [string[]]$actual.Keys
    [System.Array]::Sort($sortedKeys, [System.StringComparer]::Ordinal)
    foreach ($rel in $sortedKeys) {
        $label = Get-Label $rel
        $hash = if ($label -eq 'ohanami' -or $label -eq 'repository') { '' } else { (Get-FileHash -LiteralPath $actual[$rel].FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
        $lines.Add("$rel`t$hash`t$label")
    }
    $proposed = Join-Path $out 'allowed-files.proposed.tsv'
    [System.IO.File]::WriteAllText($proposed, (($lines -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "   許可の表の案を書いた: $proposed ($($lines.Count - 1) 行)"
    return
}

if (-not (Test-Path -LiteralPath $tablePath)) { throw "許可の表が無い: $tablePath" }
$rows = @{}
$tableLines = [System.IO.File]::ReadAllLines($tablePath, [System.Text.Encoding]::UTF8)
if ($tableLines.Count -lt 2 -or $tableLines[0] -ne "path`tsha256`tsource") { throw '許可の表の 1 行目が「path<TAB>sha256<TAB>source」でない' }
foreach ($line in $tableLines | Select-Object -Skip 1) {
    if ($line.Length -eq 0) { continue }
    $c = $line.Split("`t")
    if ($c.Count -ne 3) { throw "許可の表の列数が 3 でない: $line" }
    if ($rows.ContainsKey($c[0])) { throw "許可の表に同じパスが 2 回ある: $($c[0])" }
    if ($c[2] -eq 'ohanami' -or $c[2] -eq 'repository') { if ($c[1].Length -ne 0) { throw "リポジトリの物・ビルドで作る物($($c[2]))に SHA-256 は書かない: $($c[0])" } }
    elseif ($c[1] -notmatch '^[0-9a-f]{64}$') { throw "SHA-256 が小文字 16 進 64 桁でない: $($c[0])" }
    $rows[$c[0]] = $c
}

$problems = [System.Collections.Generic.List[string]]::new()
foreach ($rel in ($actual.Keys | Sort-Object)) {
    if (-not $rows.ContainsKey($rel)) { $problems.Add("表に無いファイルが出力にある: $rel"); continue }
    $row = $rows[$rel]
    if ($row[2] -eq 'repository') {
        # 利用者向けの文書は、リポジトリの同じパスのファイルと、バイト単位で同一であること(表に SHA-256 は書かない。文書を直すたびに表を直さなくてよいように)
        $repoFile = Join-Path $repoRoot $(if ($repositoryPathOf.ContainsKey($rel)) { $repositoryPathOf[$rel] } else { $rel })
        if (-not (Test-Path -LiteralPath $repoFile -PathType Leaf)) { $problems.Add("リポジトリに無いファイルが出力にある: $rel") }
        elseif ((Get-FileHash -LiteralPath $actual[$rel].FullName -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $repoFile -Algorithm SHA256).Hash) { $problems.Add("出力の文書が、リポジトリのファイルと違う: $rel") }
    }
    elseif ($row[2] -ne 'ohanami') {
        $got = (Get-FileHash -LiteralPath $actual[$rel].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($got -ne $row[1]) { $problems.Add("SHA-256 が表と違う: $rel (表 $($row[1])、実際 $got)") }
    }
    if ($actual[$rel].Length -ge $limitBytes) { $problems.Add("1 ファイルが 2GiB 以上: $rel") }
}
foreach ($rel in ($rows.Keys | Sort-Object)) {
    if (-not $actual.ContainsKey($rel)) { $problems.Add("表にあるファイルが出力に無い: $rel") }
}

# データ削除の道具の文字コードと改行を、出力のファイルのバイト列で確かめる。
# .ps1 は BOM 付きの UTF-8(Windows PowerShell 5.1 は BOM が無いと ANSI として読み、日本語が化ける)。
# .cmd は ASCII だけで、改行は CRLF だけ(LF の前に必ず CR、CR の後に必ず LF)。
if ($actual.ContainsKey('Ohanami-RemoveData.ps1')) {
    $b = [System.IO.File]::ReadAllBytes($actual['Ohanami-RemoveData.ps1'].FullName)
    if ($b.Length -lt 3 -or $b[0] -ne 0xEF -or $b[1] -ne 0xBB -or $b[2] -ne 0xBF) { $problems.Add('Ohanami-RemoveData.ps1 の先頭が BOM(EF BB BF)でない') }
}
if ($actual.ContainsKey('Ohanami-RemoveData.cmd')) {
    $b = [System.IO.File]::ReadAllBytes($actual['Ohanami-RemoveData.cmd'].FullName)
    for ($i = 0; $i -lt $b.Length; $i++) {
        if ($b[$i] -gt 0x7F) { $problems.Add("Ohanami-RemoveData.cmd に ASCII 以外のバイトがある(位置 $i)"); break }
        if ($b[$i] -eq 0x0A -and ($i -eq 0 -or $b[$i - 1] -ne 0x0D)) { $problems.Add("Ohanami-RemoveData.cmd に、CR の無い LF がある(位置 $i)"); break }
        if ($b[$i] -eq 0x0D -and ($i + 1 -ge $b.Length -or $b[$i + 1] -ne 0x0A)) { $problems.Add("Ohanami-RemoveData.cmd に、LF の無い CR がある(位置 $i)"); break }
    }
}
if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Host "   NG: $_" -ForegroundColor Red }
    throw "許可の表との突き合わせに失敗した($($problems.Count) 件)。zip は作らない。同梱物を替えたときは、-ProposeTable で表の案を作って確かめる。"
}
Write-Host "   OK: $($actual.Count) ファイルすべてが表と一致(第三者のバイナリ・ランタイムの SHA-256 を含む)"

# ---- 3. ビルドした PC のパスが、どのファイルにも残っていないこと
Write-Host '== 3. 出力のファイルに、ビルドした PC のパスが残っていないか調べる'
if (-not ('OhanamiPathScanner' -as [type])) {
    Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.IO;

public static class OhanamiPathScanner
{
    // ファイルのバイト列から、パターン(バイト列。ASCII の英字は大文字・小文字を区別しない)を探す。見つかった位置(最初の 1 つ)を返す。無ければ -1。
    public static long Find(string path, byte[] pattern)
    {
        if (pattern.Length == 0) throw new ArgumentException("空のパターン");
        byte[] lower = new byte[pattern.Length];
        for (int i = 0; i < pattern.Length; i++) lower[i] = Lower(pattern[i]);
        byte first = lower[0];
        byte firstUpper = Upper(first);
        const int chunk = 4 * 1024 * 1024;
        byte[] buffer = new byte[chunk + pattern.Length];
        long baseOffset = 0;
        int carry = 0;
        using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
        {
            while (true)
            {
                int n = s.Read(buffer, carry, chunk);
                if (n <= 0) return -1;
                int len = carry + n;
                int limit = len - pattern.Length;
                int pos = 0;
                while (pos <= limit)
                {
                    var span = new ReadOnlySpan<byte>(buffer, pos, limit - pos + 1);
                    int idx = first == firstUpper ? span.IndexOf(first) : span.IndexOfAny(first, firstUpper);
                    if (idx < 0) break;
                    pos += idx;
                    bool ok = true;
                    for (int k = 1; k < lower.Length; k++)
                    {
                        if (Lower(buffer[pos + k]) != lower[k]) { ok = false; break; }
                    }
                    if (ok) return baseOffset + pos;
                    pos++;
                }
                // 次の塊の頭に、末尾の (パターン長 - 1) バイトを持ち越す(塊の境目をまたぐ一致を逃さない)
                carry = Math.Min(pattern.Length - 1, len);
                Buffer.BlockCopy(buffer, len - carry, buffer, 0, carry);
                baseOffset += len - carry;
            }
        }
    }

    private static byte Lower(byte b) { return (b >= (byte)'A' && b <= (byte)'Z') ? (byte)(b + 32) : b; }
    private static byte Upper(byte b) { return (b >= (byte)'a' && b <= (byte)'z') ? (byte)(b - 32) : b; }
}
'@
}

$scanStrings = [System.Collections.Generic.List[string]]::new()
foreach ($root in @($env:USERPROFILE, $repoRoot)) {
    if ([string]::IsNullOrWhiteSpace($root)) { continue }
    $trimmed = $root.TrimEnd('\', '/')
    $scanStrings.Add($trimmed)
    $scanStrings.Add($trimmed.Replace('\', '/'))
}
if (-not [string]::IsNullOrWhiteSpace($env:USERNAME)) {
    $scanStrings.Add('\' + $env:USERNAME + '\')
    $scanStrings.Add('/' + $env:USERNAME + '/')
}
$scanStrings = [string[]]($scanStrings | Sort-Object -Unique)
if ($scanStrings.Count -eq 0) { throw 'ユーザーのフォルダ・リポジトリのルート・ユーザー名のどれも取れず、パスの検査ができない' }
$scanPatterns = foreach ($str in $scanStrings) {
    [pscustomobject]@{ Text = $str; Encoding = 'UTF-8'; Bytes = [System.Text.Encoding]::UTF8.GetBytes($str) }
    [pscustomobject]@{ Text = $str; Encoding = 'UTF-16'; Bytes = [System.Text.Encoding]::Unicode.GetBytes($str) }
}
$scanProblems = [System.Collections.Generic.List[string]]::new()
$scanned = 0
foreach ($rel in ($actual.Keys | Sort-Object)) {
    foreach ($pat in $scanPatterns) {
        $at = [OhanamiPathScanner]::Find($actual[$rel].FullName, $pat.Bytes)
        if ($at -ge 0) { $scanProblems.Add("ビルドした PC のパスが残っている: $rel (位置 $at、$($pat.Encoding)、文字列 $($pat.Text))") }
    }
    $scanned++
}
if ($scanProblems.Count -gt 0) {
    $scanProblems | ForEach-Object { Write-Host "   NG: $_" -ForegroundColor Red }
    throw "配布物に、ビルドした PC のパスが残っている($($scanProblems.Count) 件)。zip は作らない。自分のファイルなら build/OhanamiPathMap.props が効いているかを、第三者のファイルなら、そのファイルを調べる。"
}
Write-Host "   OK: $scanned ファイルに、ユーザーのフォルダ・リポジトリのルート・ユーザー名のパスは無い($($scanStrings.Count) 文字列 x UTF-8 / UTF-16)"

if ($NoZip) { Write-Host '== -NoZip: ここまで'; return }

# ---- 4. zip
Write-Host '== 4. zip を作る'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($appFull, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $true)
$zipLength = (Get-Item -LiteralPath $zipPath).Length
if ($zipLength -ge $limitBytes) { throw ("zip が 2GiB 以上: {0:N0} バイト" -f $zipLength) }
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText("$zipPath.sha256", "$zipHash  $([System.IO.Path]::GetFileName($zipPath))`n", (New-Object System.Text.UTF8Encoding($false)))
Write-Host ("   zip: {0}" -f $zipPath)
Write-Host ("   大きさ: {0:N0} バイト ({1:N1} MiB)" -f $zipLength, ($zipLength / 1MB))
Write-Host ("   SHA-256: {0}" -f $zipHash)
