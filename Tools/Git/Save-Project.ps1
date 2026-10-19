[CmdletBinding()]
param(
    [string]$ProjectPath,
    [ValidateSet('Interactive', 'Preview', 'Save', 'SaveAndPush')]
    [string]$Mode = 'Interactive',
    [string]$Message,
    [string]$Remote = 'origin',
    [switch]$Yes
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
if ([string]::IsNullOrWhiteSpace($ProjectPath)) { $ProjectPath = Join-Path $PSScriptRoot '../..' }
$script:RepoRoot = (Resolve-Path -LiteralPath $ProjectPath).Path
$script:GitExecutable = (Get-Command git -ErrorAction Stop).Source
$script:TrackedPaths = $null

# ProcessStartInfo keeps messages and paths as literal arguments, including quotes and Chinese.
function Quote-NativeArgument([string]$Value) {
    return '"' + ($Value -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"'
}
function Git([string[]]$GitArgs, [switch]$AllowFailure) {
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $script:GitExecutable
    $info.WorkingDirectory = $script:RepoRoot
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $info.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    $arguments = @('--no-optional-locks', '-c', 'core.quotepath=false') + $GitArgs
    $info.Arguments = ($arguments | ForEach-Object { Quote-NativeArgument $_ }) -join ' '
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    try {
        [void]$process.Start()
        $output = $process.StandardOutput.ReadToEndAsync()
        $errors = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $result = [pscustomobject]@{ Code = $process.ExitCode; Text = $output.Result; Error = $errors.Result }
        if (!$AllowFailure -and $result.Code -ne 0) { throw $result.Error.Trim() }
        return $result
    }
    finally { $process.Dispose() }
}
function Candidates {
    $raw = (Git -GitArgs @('ls-files', '--modified', '--deleted', '--others', '--exclude-standard', '-z')).Text
    return @($raw.Split([char]0) | Where-Object { $_ } | Sort-Object -Unique)
}
function StagedFiles {
    $raw = (Git -GitArgs @('diff', '--cached', '--no-renames', '--name-only', '-z')).Text
    return @($raw.Split([char]0) | Where-Object { $_ })
}
function Tracked([string]$Path) {
    if ($null -eq $script:TrackedPaths) {
        $script:TrackedPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($entry in (Git -GitArgs @('ls-files', '-z')).Text.Split([char]0)) {
            if ($entry) { [void]$script:TrackedPaths.Add($entry) }
        }
    }
    return $script:TrackedPaths.Contains($Path)
}
function Snapshot([string[]]$Paths) {
    $result = @{}
    foreach ($relative in $Paths) {
        $absolute = Join-Path $script:RepoRoot $relative
        $result[$relative] = if (Test-Path -LiteralPath $absolute -PathType Leaf) {
            $stream = [IO.File]::OpenRead($absolute)
            $hasher = [Security.Cryptography.SHA256]::Create()
            try { [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-', '') }
            finally { $hasher.Dispose(); $stream.Dispose() }
        } else { '<deleted>' }
    }
    return $result
}
function Assert-Unchanged([hashtable]$Before, [switch]$AfterStaging) {
    $pending = @(Candidates)
    if ($AfterStaging -and $pending.Count) { throw '暂存后工作区又发生变化，未提交，暂存内容已保留。' }
    $current = if ($AfterStaging) { @($Before.Keys) } else { $pending }
    if (@(Compare-Object @($Before.Keys | Sort-Object) @($current | Sort-Object)).Count -gt 0) {
        throw '待提交清单发生变化，请先在 Unity 保存并重新运行。脚本不会继续提交。'
    }
    $after = Snapshot $current
    foreach ($relative in $current) {
        if ($Before[$relative] -ne $after[$relative]) { throw "检查后文件发生变化：$relative。请重新运行。" }
    }
}
function Check-Files([string[]]$Paths) {
    $selected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($relative in $Paths) { [void]$selected.Add($relative) }
    function Need-Meta([string]$Relative) {
        $absolute = Join-Path $script:RepoRoot $Relative
        if (!(Test-Path -LiteralPath $absolute) -or (!$selected.Contains($Relative) -and !(Tracked $Relative))) {
            throw "Unity .meta 未配套入库：$Relative。请让 Unity 导入资源并保存后重试。"
        }
    }
    foreach ($relative in $Paths) {
        if ($relative -match '^(Library|Temp|Obj|Logs|UserSettings|Build|Builds|\.baseline-backups)/' -or
            $relative -match '^(OptimizationReview|Tools)/.*\b(bin|obj|node_modules|__pycache__)/') {
            throw "待提交清单含生成物或备份：$relative。请检查忽略规则。"
        }
        if ($relative -match '(^|/)(\.env($|\.)|id_rsa($|\.)|id_ed25519($|\.)|credentials\.json$|service[-_]account[^/]*\.json$)' -or
            $relative -match '\.(pem|key|pfx|p12)$') {
            throw "发现可能含密钥的文件：$relative。请人工检查，脚本不会自动提交。"
        }
        $absolute = Join-Path $script:RepoRoot $relative
        $exists = Test-Path -LiteralPath $absolute -PathType Leaf
        if ($exists) {
            $file = Get-Item -LiteralPath $absolute
            if ($file.Length -ge 100MB) { throw "单文件达到脚本上限 100 MiB：$relative。请另行配置大文件管理。" }
            if ($file.Length -ge 20MB) { Write-Warning "大文件 $relative ($([math]::Round($file.Length / 1MB, 1)) MiB)" }
            if ($file.Length -le 2MB) {
                $text = [IO.File]::ReadAllText($absolute)
                if ($text -match '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----' -or
                    $text -match '\bgh[pousr]_[A-Za-z0-9]{36,}\b|\bgithub_pat_[A-Za-z0-9_]{50,}\b') {
                    throw "发现疑似私钥或访问令牌：$relative。请人工检查，脚本不会自动提交。"
                }
            }
        }
        if ($relative.StartsWith('Assets/', [StringComparison]::OrdinalIgnoreCase)) {
            if ($relative.EndsWith('.meta', [StringComparison]::OrdinalIgnoreCase)) {
                $owner = Join-Path $script:RepoRoot $relative.Substring(0, $relative.Length - 5)
                if ($exists -and !(Test-Path -LiteralPath $owner)) { throw "孤立的 .meta：$relative" }
                if (!$exists -and (Test-Path -LiteralPath $owner)) { throw "资源仍在，但 .meta 被删除：$relative" }
            }
            elseif (!$exists) {
                if (Test-Path -LiteralPath ($absolute + '.meta')) { throw "删除资源时遗漏了同名 .meta：$relative" }
            }
            elseif (!(Split-Path -Leaf $relative).StartsWith('.')) {
                Need-Meta ($relative + '.meta')
            }
            $parent = $relative.Substring(0, $relative.LastIndexOf('/'))
            while ($parent -ne 'Assets' -and $parent.Contains('/')) {
                if (Test-Path -LiteralPath (Join-Path $script:RepoRoot $parent)) { Need-Meta ($parent + '.meta') }
                $parent = $parent.Substring(0, $parent.LastIndexOf('/'))
            }
        }
    }
}
function Check-PushTarget([string]$Branch) {
    $url = Git -GitArgs @('remote', 'get-url', '--push', $Remote) -AllowFailure
    if ($url.Code -ne 0) { throw "没有远程仓库 $Remote。请先按 Tools/Git/README.md 配置；本地保存可单独使用。" }
    if ($url.Text -match '^https?://[^/]+@') { throw '远程地址包含登录信息，请移除地址中的密码或令牌后再推送。' }
    Write-Host "上传目标：$($url.Text.Trim()) / $Branch"
}

function New-SaveNumber([switch]$IncludeRemote) {
    $zone = [TimeZoneInfo]::FindSystemTimeZoneById('China Standard Time')
    $day = [TimeZoneInfo]::ConvertTimeFromUtc([DateTime]::UtcNow, $zone).ToString('yyyy.MM.dd', [Globalization.CultureInfo]::InvariantCulture)
    $pattern = '^' + [regex]::Escape($day) + '\.(\d{5})$'
    $highest = 0
    foreach ($tag in ((Git -GitArgs @('tag', '--list', "$day.*")).Text -split '\r?\n')) {
        if ($tag -match $pattern) { $highest = [math]::Max($highest, [int]$Matches[1]) }
    }
    # Commit titles also retain the number, so deleting a tag does not silently reuse it.
    $subjectPattern = '^\[(' + [regex]::Escape($day) + '\.(\d{5}))\] '
    foreach ($subject in ((Git -GitArgs @('log', '--all', '--format=%s')).Text -split '\r?\n')) {
        if ($subject -match $subjectPattern) { $highest = [math]::Max($highest, [int]$Matches[2]) }
    }
    if ($IncludeRemote) {
        $remotePattern = '^refs/tags/' + [regex]::Escape($day) + '\.(\d{5})$'
        foreach ($line in ((Git -GitArgs @('ls-remote', '--tags', '--refs', $Remote, "refs/tags/$day.*")).Text -split '\r?\n')) {
            $reference = ($line -split '\s+')[-1]
            if ($reference -match $remotePattern) { $highest = [math]::Max($highest, [int]$Matches[1]) }
        }
    }
    if ($highest -ge 99999) { throw "今天的保存编号已用满（$day.99999），未开始暂存。" }
    return $day + '.' + ($highest + 1).ToString('D5', [Globalization.CultureInfo]::InvariantCulture)
}

function SaveTagRefs {
    # Only publish numbered saves reachable from the branch being uploaded.
    $tags = (Git -GitArgs @('tag', '--list', '--merged', 'HEAD')).Text -split '\r?\n'
    return @($tags | Where-Object { $_ -match '^\d{4}\.\d{2}\.\d{2}\.\d{5}$' } |
        ForEach-Object { 'refs/tags/' + $_ + ':refs/tags/' + $_ })
}

try {
    $actualRoot = (Git -GitArgs @('rev-parse', '--show-toplevel')).Text.Trim()
    if ([IO.Path]::GetFullPath($actualRoot).TrimEnd('\', '/') -ne $script:RepoRoot.TrimEnd('\', '/')) {
        throw '指定目录不是 Git 工程根目录，已停止。'
    }
    foreach ($directory in @('Assets', 'Packages', 'ProjectSettings')) {
        if (!(Test-Path -LiteralPath (Join-Path $script:RepoRoot $directory) -PathType Container)) { throw "缺少 Unity 工程目录 $directory。" }
    }
    $branchResult = Git -GitArgs @('symbolic-ref', '--quiet', '--short', 'HEAD') -AllowFailure
    if ($branchResult.Code -ne 0) { throw '当前处于分离 HEAD 状态，请先在 Git 客户端切换到正常分支。' }
    $branch = $branchResult.Text.Trim()
    if ((Git -GitArgs @('ls-files', '--unmerged')).Text) { throw '存在未解决的合并冲突，请先处理。' }
    foreach ($state in @('MERGE_HEAD', 'CHERRY_PICK_HEAD', 'REVERT_HEAD', 'rebase-merge', 'rebase-apply')) {
        $statePath = (Git -GitArgs @('rev-parse', '--git-path', $state)).Text.Trim()
        if (![IO.Path]::IsPathRooted($statePath)) { $statePath = Join-Path $script:RepoRoot $statePath }
        if (Test-Path -LiteralPath $statePath) { throw "Git 正在合并或整理历史（$state），请先完成该操作。" }
    }
    Write-Host "工程：$script:RepoRoot"
    Write-Host "分支：$branch"
    Write-Host '请先在 Unity 保存场景和资源。本脚本只处理已写入磁盘的内容。'
    Write-Host (Git -GitArgs @('status', '--short', '--untracked-files=all')).Text
    $paths = @(Candidates)
    Write-Host "待提交文件：$($paths.Count) 个"
    $snapshot = Snapshot $paths
    Check-Files $paths
    $staged = @(StagedFiles)
    if ($Mode -eq 'Preview') {
        if ($staged.Count) { Write-Warning '已有手动暂存内容；保存模式会停止，不会覆盖你的暂存选择。' }
        Write-Host '预览完成，未暂存、提交或推送。'
        exit 0
    }
    if ($staged.Count) { throw '已有手动暂存内容，请先用 Git 客户端完成或取消该暂存。脚本不会覆盖。' }
    if ($Yes -and $Mode -eq 'Interactive') { throw '-Yes 必须同时指定 -Mode Save 或 SaveAndPush。' }
    if ($Mode -eq 'Interactive') {
        Write-Host '1 = 保存到本地 Git；2 = 保存并上传；0 = 取消'
        switch (Read-Host '请选择') {
            '1' { $Mode = 'Save' }
            '2' { $Mode = 'SaveAndPush' }
            default { Write-Host '已取消，未暂存或提交。'; exit 0 }
        }
    }
    if ($Mode -eq 'SaveAndPush') { Check-PushTarget $branch }
    if ($paths.Count -gt 0) {
        if ([string]::IsNullOrWhiteSpace($Message)) {
            if ($Yes) { throw '自动模式必须提供非空 -Message。' }
            $Message = Read-Host '本次改了什么（例如：调整房屋布局和敌人路点；留空取消）'
            if ([string]::IsNullOrWhiteSpace($Message)) { Write-Host '已取消，未暂存或提交。'; exit 0 }
        }
        if (!$Yes -and (Read-Host "确认将上述全部文件提交到 $branch？输入 Y 确认") -notmatch '^(y|yes)$') {
            Write-Host '已取消，未暂存或提交。'; exit 0
        }
        if (@(StagedFiles).Count) { throw '检查后暂存区发生变化，已停止。' }
        if ((Git -GitArgs @('symbolic-ref', '--quiet', '--short', 'HEAD')).Text.Trim() -ne $branch) { throw '检查后分支发生变化，已停止。' }
        $saveNumber = New-SaveNumber -IncludeRemote:($Mode -eq 'SaveAndPush')
        Assert-Unchanged $snapshot
        [void](Git -GitArgs @('add', '--all', '--', '.'))
        $actualStaged = @(StagedFiles)
        if (@(Compare-Object @($paths | Sort-Object) @($actualStaged | Sort-Object)).Count) {
            throw '暂存清单与已确认清单不一致，未提交。暂存内容已保留，请用 Git 客户端检查。'
        }
        Assert-Unchanged $snapshot -AfterStaging
        if ((Git -GitArgs @('symbolic-ref', '--quiet', '--short', 'HEAD')).Text.Trim() -ne $branch) { throw '暂存后分支发生变化，未提交，暂存内容已保留。' }
        $commit = Git -GitArgs @('commit', '-m', "[$saveNumber] $Message")
        Write-Host $commit.Text
        $savedHash = (Git -GitArgs @('rev-parse', 'HEAD')).Text.Trim()
        $tagResult = Git -GitArgs @('tag', $saveNumber, $savedHash) -AllowFailure
        if ($tagResult.Code -ne 0) { throw "提交已保存在本机（$savedHash），但编号标签 $saveNumber 创建失败，请人工检查。`n$($tagResult.Error)" }
        Write-Host "本地保存完成，保存编号：$saveNumber" -ForegroundColor Green
    }
    else { Write-Host '没有新的文件变化，本地提交保持不变。' }
    if ($Mode -eq 'SaveAndPush') {
        if (!$paths.Count -and !$Yes -and (Read-Host "确认上传当前分支 $branch 的已有提交？输入 Y 确认") -notmatch '^(y|yes)$') { exit 0 }
        if ((Git -GitArgs @('symbolic-ref', '--quiet', '--short', 'HEAD')).Text.Trim() -ne $branch) { throw '上传前分支发生变化，已停止。' }
        $tagRefs = @(SaveTagRefs)
        $pushArgs = @('push', '--atomic', '--set-upstream', $Remote, "HEAD:refs/heads/$branch") + $tagRefs
        $push = Git -GitArgs $pushArgs -AllowFailure
        if ($push.Code -ne 0) { throw "上传失败，本地提交和编号仍然保留。请检查远程分支或同名编号冲突，脚本不会覆盖远程。`n$($push.Error)" }
        Write-Host $push.Text
        Write-Host $push.Error
        Write-Host '上传完成。'
    }
    else { Write-Host '本次仅保存到本机；需要异地备份时请选择“保存并上传”。' }
    exit 0
}
catch {
    Write-Host "已停止：$($_.Exception.Message)" -ForegroundColor Red
    Write-Host '未执行重置、清理、自动合并或强制推送。若错误出现在暂存之后，暂存内容会保留。'
    exit 1
}
