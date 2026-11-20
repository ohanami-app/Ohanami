<#
.SYNOPSIS
  Ohanami が PC に残したデータを消す。

.DESCRIPTION
  データの場所(根)の下にある、Ohanami が書いた物だけを消す。消す物の一覧と大きさを見せてから、「モデルのファイルだけ」か「すべて」かを聞く。
  - 消すのは、名前か型を知っている物だけ。知らない物は残して、最後に報告する。
  - 根の外(アプリのフォルダ・根の外に置いたモデル・スクリーンショットのフォルダ)には触れない。
  - 再帰の削除は使わない。ファイルは 1 つずつ、フォルダは空になったときだけ消す。
  - アプリが動いている間は、何も消さずに止まる。
  - この道具は、ログを書かない。ネットワークにも出ない。

  終了コード: 0 = 消した(消す物が無かった場合・知らない物を残しただけの場合・-ListOnly も 0)/ 1 = PowerShell 自身の失敗(引数の誤りなど)/
  2 = 始める前に止めた(アプリが動いている・場所を決められない・-Yes だけが付いている)/ 3 = 利用者がやめた/ 4 = 消せなかった物がある/ 5 = 想定外のエラー

.PARAMETER Mode
  Models = モデルのファイルだけ。All = モデルのファイル・インデックス・手動タグ・サムネイル・ログのすべて。省くと、一覧を見せて聞く。

.PARAMETER Yes
  最後の確かめを省く。-Mode と一緒のときだけ使える。

.PARAMETER ListOnly
  一覧を出して終わる(何も消さない)。

.PARAMETER DataRoot
  データの場所を指す絶対パス。省くと、環境変数 OHANAMI_DATA_DIR、それも無ければ %LOCALAPPDATA%\Ohanami(アプリと同じ決め方)。
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [ValidateSet('Models', 'All')]
    [string]$Mode,
    [switch]$Yes,
    [switch]$ListOnly,
    [string]$DataRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 出力が取り込まれているときだけ UTF-8 にする(ふつうの窓では触らない)
if ([Console]::IsOutputRedirected) { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) }

# ---- アプリの書き手に合わせた、名前と型の表(アプリが書く物を変えたら、ここも直す。tests/Ohanami.RemoveData.Tests が見張る)
$DataDirectoryVariable = 'OHANAMI_DATA_DIR'
$ModelsDirectoryVariable = 'OHANAMI_MODELS_DIR'
$EmbeddingDirectoryName = 'ViT-SO400M-16-SigLIP2-384__ohanami'
$EmbeddingFiles = @('visual/model.onnx', 'textual/model.onnx', 'textual/model.onnx_data', 'textual/tokenizer.json')
$VlmDirectoryName = 'Qwen3-VL-8B-Instruct-GGUF'
$VlmFiles = @('Qwen3VL-8B-Instruct-Q4_K_M.gguf', 'mmproj-Qwen3VL-8B-Instruct-F16.gguf')

# 集めた物の置き場: Models = モデルのファイル(取りかけの .part を含む)、Db = データベースの組、Index = そのほかのアプリの書いた物
function New-Spec([string]$scope) {
    return @{
        Scope = $scope
        Files = New-Object System.Collections.Generic.List[object]
        Dirs = New-Object System.Collections.Generic.List[object]
    }
}

function Add-FileRule($spec, [string]$pattern, [string]$group) {
    $spec.Files.Add(@{ Pattern = $pattern; Group = $group })
}

function Add-DirRule($spec, [string]$pattern, $child) {
    $spec.Dirs.Add(@{ Pattern = $pattern; Spec = $child })
}

# モデルのフォルダの型: 相対パス(区切りは /。深さは 2 まで)のファイルと、それぞれ + .part
function New-ModelSpec([string[]]$relativePaths) {
    $spec = New-Spec 'Models'
    $subs = @{}
    foreach ($rel in $relativePaths) {
        $parts = $rel.Split('/')
        if ($parts.Count -eq 1) {
            Add-FileRule $spec ('^' + [regex]::Escape($parts[0]) + '(\.part)?$') 'Models'
        }
        elseif ($parts.Count -eq 2) {
            if (-not $subs.ContainsKey($parts[0])) {
                $subs[$parts[0]] = New-Spec 'Models'
                Add-DirRule $spec ('^' + [regex]::Escape($parts[0]) + '$') $subs[$parts[0]]
            }
            Add-FileRule $subs[$parts[0]] ('^' + [regex]::Escape($parts[1]) + '(\.part)?$') 'Models'
        }
        else {
            throw "モデルの相対パスが深すぎる: $rel"
        }
    }
    return $spec
}

$specModels = New-Spec 'Models'
Add-DirRule $specModels ('^' + [regex]::Escape($EmbeddingDirectoryName) + '$') (New-ModelSpec $EmbeddingFiles)
Add-DirRule $specModels ('^' + [regex]::Escape($VlmDirectoryName) + '$') (New-ModelSpec $VlmFiles)

$specLogs = New-Spec 'Index'
Add-FileRule $specLogs '^ohanami-[0-9]{8}\.log$' 'Index'
Add-FileRule $specLogs '^llama-server-[0-9]{8}-[0-9]{6}-[0-9]{3}\.log$' 'Index'

$specThumbLeaf = New-Spec 'Index'
Add-FileRule $specThumbLeaf '^[0-9a-f]{64}_[0-9]+x[0-9]+\.jpg(\.[0-9a-f]{32}\.tmp)?$' 'Index'
$specThumbVersion = New-Spec 'Index'
Add-DirRule $specThumbVersion '^[0-9a-f]{2}$' $specThumbLeaf
$specThumbs = New-Spec 'Index'
Add-DirRule $specThumbs '^v[0-9]+$' $specThumbVersion

$specRoot = New-Spec 'Root'
Add-FileRule $specRoot '^ohanami\.db(-wal|-shm)?$' 'Db'
Add-FileRule $specRoot '^ohanami\.db\.bak-v[0-9]+(-[0-9]+)?$' 'Index'
Add-FileRule $specRoot '^llama-server\.pid$' 'Index'
Add-FileRule $specRoot '^llama-server\.pid\.[0-9a-f]{32}\.tmp$' 'Index'
Add-DirRule $specRoot '^logs$' $specLogs
Add-DirRule $specRoot '^thumbs$' $specThumbs
Add-DirRule $specRoot '^models$' $specModels

# ---- 画面の文
function Format-Size([long]$bytes) {
    $inv = [System.Globalization.CultureInfo]::InvariantCulture
    if ($bytes -ge 1000000000) { return ($bytes / 1e9).ToString('0.00', $inv) + ' GB' }
    if ($bytes -ge 1000000) { return ($bytes / 1e6).ToString('0.0', $inv) + ' MB' }
    if ($bytes -ge 1000) { return ($bytes / 1e3).ToString('0', $inv) + ' KB' }
    return [string]$bytes + ' バイト'
}

function Get-Total($items) {
    $count = 0
    [long]$bytes = 0
    foreach ($i in $items) { $count++; $bytes += $i.Length }
    return [pscustomobject]@{ Count = $count; Bytes = $bytes }
}

function Show-Limited($lines, [int]$limit) {
    $all = @($lines)
    $shown = 0
    foreach ($l in $all) {
        if ($shown -ge $limit) { break }
        Write-Host ('  ' + $l)
        $shown++
    }
    if ($all.Count -gt $limit) { Write-Host ('  ほか {0} 個' -f ($all.Count - $limit)) }
}

# ---- 根の決め方(-DataRoot、環境変数、既定の順)
function Test-AbsolutePath([string]$path) {
    return ($path -match '^[A-Za-z]:\\') -or ($path -match '^\\\\')
}

# ---- アプリが動いているかの確かめ: アプリが根ごとに持つ名前付き Mutex を、取得せずに開いて見る
function Test-AppRunning([string]$root) {
    $normalized = [System.IO.Path]::GetFullPath($root).TrimEnd('\', '/').ToUpperInvariant()
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $hash = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($normalized)) } finally { $sha.Dispose() }
    $id = -join ($hash[0..7] | ForEach-Object { $_.ToString('x2') })
    $name = 'Local\Ohanami.Instance.' + $id
    $mutex = $null
    try {
        return [System.Threading.Mutex]::TryOpenExisting($name, [ref]$mutex)
    }
    catch [System.UnauthorizedAccessException] {
        return $true   # 開けなかったのは、別の所有者の物があるため。動いている物として扱う
    }
    finally {
        if ($null -ne $mutex) { $mutex.Dispose() }
    }
}

# ---- 数え上げ: 根の下の、名前か型を知っているフォルダだけに入る。リンクのフォルダには入らない
function Get-Inventory([string]$root) {
    $inv = [pscustomobject]@{
        Files = New-Object System.Collections.Generic.List[object]
        Dirs = New-Object System.Collections.Generic.List[object]
        Unknown = New-Object System.Collections.Generic.List[string]
        Links = New-Object System.Collections.Generic.List[string]
        Unreadable = New-Object System.Collections.Generic.List[object]
    }
    $queue = New-Object System.Collections.Generic.Queue[object]
    $queue.Enqueue(@{ Path = $root; Rel = ''; Spec = $specRoot })
    while ($queue.Count -gt 0) {
        $item = $queue.Dequeue()
        $spec = $item.Spec
        $inv.Dirs.Add([pscustomobject]@{ Path = $item.Path; Scope = $spec.Scope })
        try {
            $files = [System.IO.Directory]::GetFiles($item.Path)
            $dirs = [System.IO.Directory]::GetDirectories($item.Path)
        }
        catch {
            $inv.Unreadable.Add([pscustomobject]@{ Path = $item.Path; Scope = $spec.Scope; Reason = $_.Exception.Message })
            continue
        }
        foreach ($file in $files) {
            $name = [System.IO.Path]::GetFileName($file)
            $group = $null
            foreach ($rule in $spec.Files) {
                if ($name -match $rule.Pattern) { $group = $rule.Group; break }
            }
            if ($null -eq $group) { $inv.Unknown.Add($item.Rel + $name); continue }
            [long]$length = 0
            try { $length = (New-Object System.IO.FileInfo($file)).Length } catch { $length = 0 }
            $inv.Files.Add([pscustomobject]@{ Path = $file; Length = $length; Group = $group; IsPart = $name.EndsWith('.part', [System.StringComparison]::OrdinalIgnoreCase) })
        }
        foreach ($dir in $dirs) {
            $name = [System.IO.Path]::GetFileName($dir)
            $child = $null
            foreach ($rule in $spec.Dirs) {
                if ($name -match $rule.Pattern) { $child = $rule.Spec; break }
            }
            if ($null -eq $child) { $inv.Unknown.Add($item.Rel + $name + '\'); continue }
            $attributes = [System.IO.File]::GetAttributes($dir)
            if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { $inv.Links.Add($item.Rel + $name + '\'); continue }
            $queue.Enqueue(@{ Path = $dir; Rel = $item.Rel + $name + '\'; Spec = $child })
        }
    }
    return $inv
}

# ---- 消す: 根の外を消さないための確かめと、1 つずつの削除
function Assert-UnderRoot([string]$path) {
    $full = [System.IO.Path]::GetFullPath($path)
    if (-not $full.StartsWith($script:RootPath + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "データの場所の外を消そうとした: $full"
    }
}

function Remove-DataFile($item, $result) {
    Assert-UnderRoot $item.Path
    try {
        [System.IO.File]::Delete($item.Path)
        $result.Deleted.Add($item)
        return $true
    }
    catch {
        $result.Failed.Add([pscustomobject]@{ Path = $item.Path; Reason = $_.Exception.Message })
        return $false
    }
}

# 空になった既知のフォルダを、深い順に消す(空でなければ消えない)。入っている物が残るのは、報告の「残した物」に出ている
function Remove-EmptyDirs($inv, [string[]]$scopes) {
    $list = $inv.Dirs.ToArray()
    for ($i = $list.Count - 1; $i -ge 0; $i--) {
        $dir = $list[$i]
        if ($scopes -notcontains $dir.Scope) { continue }
        if ($dir.Scope -eq 'Root') { continue }
        Assert-UnderRoot $dir.Path
        try { [System.IO.Directory]::Delete($dir.Path) }
        catch [System.IO.IOException] { }
        catch [System.UnauthorizedAccessException] { }
    }
}

# データベースの組を、本体・-wal・-shm の順に返す(本体が消せなければ、組の残りに手を付けない)
function Get-DbFilesInOrder($inv) {
    foreach ($suffix in @('', '-wal', '-shm')) {
        $wanted = 'ohanami.db' + $suffix
        foreach ($f in $inv.Files) {
            if ($f.Group -eq 'Db' -and [string]::Equals([System.IO.Path]::GetFileName($f.Path), $wanted, [System.StringComparison]::OrdinalIgnoreCase)) { $f }
        }
    }
}

# ---- 本体
try {
    if ($Yes -and -not $PSBoundParameters.ContainsKey('Mode')) {
        Write-Host '-Yes は、-Mode Models または -Mode All と一緒に使います。何も消さずに終わります。'
        exit 2
    }

    if ($PSBoundParameters.ContainsKey('DataRoot')) {
        $rawRoot = $DataRoot
        $how = '引数 -DataRoot で指定'
    }
    elseif (-not [string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($DataDirectoryVariable))) {
        $rawRoot = [Environment]::GetEnvironmentVariable($DataDirectoryVariable)
        $how = '環境変数 ' + $DataDirectoryVariable + ' で指定'
    }
    else {
        $rawRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Ohanami'
        $how = '既定の場所'
    }
    if (-not (Test-AbsolutePath $rawRoot)) {
        Write-Host ('データの場所を決められません: 絶対パス(例: C:\...)で指定してください。指定された値: ' + $rawRoot)
        exit 2
    }
    $script:RootPath = [System.IO.Path]::GetFullPath($rawRoot).TrimEnd('\', '/')

    Write-Host ('データの場所: ' + $script:RootPath + '(' + $how + ')')
    if (-not [System.IO.Directory]::Exists($script:RootPath)) {
        Write-Host '消す物はありません(データの場所がありません)。'
        exit 0
    }

    $inv = Get-Inventory $script:RootPath
    $modelFiles = @($inv.Files | Where-Object { $_.Group -eq 'Models' -and -not $_.IsPart })
    $partFiles = @($inv.Files | Where-Object { $_.Group -eq 'Models' -and $_.IsPart })
    $indexFiles = @($inv.Files | Where-Object { $_.Group -ne 'Models' })
    $modelTotal = Get-Total ($modelFiles + $partFiles)
    $partTotal = Get-Total $partFiles
    $indexTotal = Get-Total $indexFiles
    $kept = @($inv.Unknown) + @($inv.Links | ForEach-Object { $_ + '(リンク)' })

    Write-Host ''
    Write-Host ('[1] モデルのファイル: {0} ファイル、{1}' -f $modelTotal.Count, (Format-Size $modelTotal.Bytes))
    if ($partTotal.Count -gt 0) { Write-Host ('    うち取りかけ (.part): {0} ファイル、{1}' -f $partTotal.Count, (Format-Size $partTotal.Bytes)) }
    Write-Host ('[2] インデックス・手動タグ・サムネイル・ログ: {0} ファイル、{1}' -f $indexTotal.Count, (Format-Size $indexTotal.Bytes))
    Write-Host ''
    Write-Host '消さない物:'
    Write-Host '  - 登録したスクリーンショットのフォルダ(中の画像)'
    Write-Host ('  - アプリのフォルダ: ' + $PSScriptRoot + '(不要なら、手で消してください)')
    Write-Host '  - アプリのフォルダの近くの models フォルダなど、データの場所の外に置いたモデル'
    $modelsOverride = [Environment]::GetEnvironmentVariable($ModelsDirectoryVariable)
    if (-not [string]::IsNullOrEmpty($modelsOverride)) {
        Write-Host ('  - 環境変数 ' + $ModelsDirectoryVariable + ' が付いています: ' + $modelsOverride + '。モデルはそこにあります。この道具は消しません')
    }
    if ($kept.Count -gt 0) {
        Write-Host ''
        Write-Host ('知らない物・型に合わない物・リンクのフォルダ(残します): {0} 個' -f $kept.Count)
    }
    if ($inv.Unreadable.Count -gt 0) {
        Write-Host ''
        Write-Host ('中を調べられなかったフォルダ(中の物は消しません): {0} 個' -f $inv.Unreadable.Count)
    }

    if ($ListOnly) {
        if ($kept.Count -gt 0) { Show-Limited $kept 20 }
        foreach ($u in $inv.Unreadable) { Write-Host ('  ' + $u.Path + ' - ' + $u.Reason) }
        exit 0
    }

    if (Test-AppRunning $script:RootPath) {
        Write-Host ''
        Write-Host 'Ohanami が動いています。閉じてから、もう一度実行してください。何も消さずに終わります。'
        exit 2
    }

    $chosenByAnswer = $false
    if (-not $PSBoundParameters.ContainsKey('Mode')) {
        Write-Host ''
        Write-Host '消す物を選んでください。'
        Write-Host ('  1  モデルのファイルだけ(約 ' + (Format-Size $modelTotal.Bytes) + ')')
        Write-Host '  2  すべて(モデルのファイル・インデックス・手動タグ・サムネイル・ログ)'
        Write-Host 'それ以外を入力すると、何も消さずに終わります。'
        $answer = Read-Host '番号'
        if ($null -eq $answer) { Write-Host '入力がないので、何も消さずに終わります。'; exit 3 }
        $answer = $answer.Trim().Normalize([System.Text.NormalizationForm]::FormKC).ToLowerInvariant()
        if ($answer -eq '1') { $Mode = 'Models' }
        elseif ($answer -eq '2') { $Mode = 'All' }
        else { Write-Host '何も消さずに終わります。'; exit 3 }
        $chosenByAnswer = $true
    }
    $targets = @(if ($Mode -eq 'Models') { $modelFiles + $partFiles } else { $inv.Files.ToArray() })
    if ($targets.Count -eq 0) {
        Write-Host ''
        Write-Host '消す物はありません。'
        exit 0
    }

    if (-not $Yes) {
        $askAgain = ($Mode -eq 'All') -or (-not $chosenByAnswer)
        if ($askAgain) {
            Write-Host ''
            if ($Mode -eq 'All') {
                Write-Host '「すべて」を選びました。次の物が失われます。'
                Write-Host '  - インデックス(作り直しに、1 万枚で約 36 時間かかります)'
                Write-Host '  - 手動タグ(作り直せません)'
            }
            else {
                Write-Host 'モデルのファイルを消します。'
            }
            Write-Host '元に戻せません。'
            $confirm = Read-Host '消してよければ y を入力してください'
            if ($null -eq $confirm) { Write-Host '入力がないので、何も消さずに終わります。'; exit 3 }
            $confirm = $confirm.Trim().Normalize([System.Text.NormalizationForm]::FormKC).ToLowerInvariant()
            if ($confirm -ne 'y') { Write-Host '何も消さずに終わります。'; exit 3 }
        }
    }

    # 聞いている間にアプリが起動しうるので、消す直前にもう一度確かめる
    if (Test-AppRunning $script:RootPath) {
        Write-Host 'Ohanami が動いています。閉じてから、もう一度実行してください。何も消さずに終わります。'
        exit 2
    }

    $result = [pscustomobject]@{
        Deleted = New-Object System.Collections.Generic.List[object]
        Failed = New-Object System.Collections.Generic.List[object]
    }

    $stopped = $false
    if ($Mode -eq 'All') {
        # データベースの組を最初に。1 つでも消せなければ、ほかの物に手を付けずに止まる(アプリが動いたままだったときの受け皿)
        foreach ($item in @(Get-DbFilesInOrder $inv)) {
            if (-not (Remove-DataFile $item $result)) { $stopped = $true; break }
        }
        if (-not $stopped) {
            foreach ($item in @($inv.Files | Where-Object { $_.Group -eq 'Index' })) { [void](Remove-DataFile $item $result) }
        }
    }
    if (-not $stopped) {
        foreach ($item in @($modelFiles + $partFiles)) { [void](Remove-DataFile $item $result) }
        if ($Mode -eq 'All') { Remove-EmptyDirs $inv @('Models', 'Index') }
        else { Remove-EmptyDirs $inv @('Models') }
    }

    $rootRemoved = $false
    if ($Mode -eq 'All' -and -not $stopped) {
        try { [System.IO.Directory]::Delete($script:RootPath); $rootRemoved = $true }
        catch [System.IO.IOException] { }
        catch [System.UnauthorizedAccessException] { }
    }

    # ---- 報告
    $deletedModels = Get-Total @($result.Deleted | Where-Object { $_.Group -eq 'Models' })
    $deletedIndex = Get-Total @($result.Deleted | Where-Object { $_.Group -ne 'Models' })
    Write-Host ''
    Write-Host ('消しました: モデルのファイル {0} ファイル({1})、そのほか {2} ファイル({3})' -f $deletedModels.Count, (Format-Size $deletedModels.Bytes), $deletedIndex.Count, (Format-Size $deletedIndex.Bytes))
    $failedLines = @($result.Failed | ForEach-Object { $_.Path + ' - ' + $_.Reason })
    if ($failedLines.Count -gt 0) {
        Write-Host ('消せなかった物: {0} 個' -f $failedLines.Count)
        Show-Limited $failedLines 20
    }
    if ($stopped) { Write-Host 'データベースを消せなかったので、ほかの物には手を付けずに止めました。Ohanami を閉じてから、もう一度実行してください。' }

    $scopes = if ($Mode -eq 'All') { @('Models', 'Index') } else { @('Models') }
    $unreadableInScope = @($inv.Unreadable | Where-Object { $scopes -contains $_.Scope })
    foreach ($u in $unreadableInScope) { Write-Host ('中を調べられなかったので、中の物は消していません: ' + $u.Path + ' - ' + $u.Reason) }
    if ($kept.Count -gt 0) {
        Write-Host ('残した物(知らない物・型に合わない物・リンクのフォルダ): {0} 個' -f $kept.Count)
        Show-Limited $kept 20
    }
    if ($rootRemoved) { Write-Host 'データのフォルダ: 消しました' }
    else { Write-Host ('データのフォルダ: 残しました(' + $script:RootPath + ')') }
    Write-Host ('アプリのフォルダは消していません: ' + $PSScriptRoot)

    if ($result.Failed.Count -gt 0 -or $unreadableInScope.Count -gt 0 -or $stopped) { exit 4 }
    exit 0
}
catch {
    Write-Host ('想定外のエラーで止まりました: ' + $_.Exception.Message)
    exit 5
}
