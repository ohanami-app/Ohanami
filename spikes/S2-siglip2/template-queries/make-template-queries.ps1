# known-items.tsv の query_ja を、固定の3つのテンプレートで包んだ known-items-T*.tsv を作る(段階0の切り分け用)。
# テンプレートは、結果を見る前に固定したもの(ADR 0014)。結果を見てから変えない。
#   T1「{q}の画像」  T2「{q}が描かれたゲームのスクリーンショット」  T3「これは{q}のイラストです」
# query_en 列は空にする(rank の「en」を混ぜない)。query_ja の空白区切りの語はそのまま {q} に入れる。
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $here '..\known-items.tsv'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$lines = [System.IO.File]::ReadAllLines($src, $utf8)
$header = $lines[0].TrimStart([char]0xFEFF).Split("`t")
$iFile = [Array]::IndexOf($header, 'filename'); $iJa = [Array]::IndexOf($header, 'query_ja')
if ($iFile -lt 0 -or $iJa -lt 0) { throw "列名が想定と違う: $($lines[0])" }
$templates = [ordered]@{
    'T1' = '{q}の画像'
    'T2' = '{q}が描かれたゲームのスクリーンショット'
    'T3' = 'これは{q}のイラストです'
}
foreach ($name in $templates.Keys) {
    $t = $templates[$name]
    $out = New-Object System.Collections.Generic.List[string]
    $out.Add("filename`tquery_ja`tquery_en")
    for ($i = 1; $i -lt $lines.Length; $i++) {
        if ($lines[$i].Length -eq 0) { continue }
        $c = $lines[$i].Split("`t")
        $q = $c[$iJa].Trim()
        if ($q.Length -eq 0) { throw "${src} の $($i + 1) 行目: query_ja が空" }
        $out.Add("$($c[$iFile].Trim())`t$($t.Replace('{q}', $q))`t")
    }
    $dest = Join-Path $here "known-items-$name.tsv"
    [System.IO.File]::WriteAllText($dest, ($out -join "`n") + "`n", $utf8)
    Write-Host "$name : $($out.Count - 1) 行 -> $dest"
}
