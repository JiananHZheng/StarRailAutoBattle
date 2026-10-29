# Runs the sequence analyzer on every sample in samples/ and prints per-action mismatches against expected.txt.
# Each sample is a folder samples/<name>/ with sample.json ({"video": ..., "team": ..., "start": ..., "end": ...})
# and expected.txt (the user-confirmed sequence). Exit code 1 when any sample has mismatches.
# Run from the main checkout: the analyzer needs axis-data/, which worktrees don't have.
#
#   pwsh scripts/check-samples.ps1            # all samples
#   pwsh scripts/check-samples.ps1 铁墓       # samples whose folder name contains 铁墓
param([string]$Filter = '', [switch]$NoBuild)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
[Console]::OutputEncoding = [Text.Encoding]::UTF8

if (-not $NoBuild) {
    dotnet build src/AxisTool -c Release --nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'AxisTool 编译失败。' }
}
$axis = Get-ChildItem src/AxisTool/bin/Release -Recurse -Filter axis.exe | Select-Object -First 1
if (-not $axis) { throw '找不到 axis.exe。' }

# One action per line: "序号 单位 [招式] [目标] [@ ...]" or a segment marker "== 换波 ==". Comments and targets are ignored.
function Read-Actions([string]$text) {
    $actions = [Collections.Generic.List[object]]::new()
    foreach ($raw in $text -split "`r?`n") {
        $line = ($raw -replace '\s+#.*$', '').Trim()
        if ($line -eq '' -or $line.StartsWith('#')) { continue }
        if ($line.StartsWith('==')) {
            $actions.Add([pscustomobject]@{ Kind = '分段'; Key = $line; Skill = ''; Line = $line })
            continue
        }
        $tokens = (($line -split '@')[0].Trim()) -split '\s+'
        $kind = if ($tokens[0] -match '-') { '插入' } else { '主' }
        $skill = if ($tokens.Count -gt 2 -and $tokens[2] -in '普攻', '战技', '终结技') { $tokens[2] } else { '' }
        $actions.Add([pscustomobject]@{ Kind = $kind; Key = "$kind $($tokens[1])"; Skill = $skill; Line = $line })
    }
    return , $actions
}

# Longest common subsequence on kind + unit, so one missed action shows as one mismatch instead of shifting the rest.
# Skill differences on matched actions are reported separately; targets are not compared.
function Compare-Actions($expected, $actual) {
    $n = $expected.Count; $m = $actual.Count
    $lcs = New-Object 'int[,]' ($n + 1), ($m + 1)
    for ($i = $n - 1; $i -ge 0; $i--) {
        for ($j = $m - 1; $j -ge 0; $j--) {
            $lcs[$i, $j] = if ($expected[$i].Key -eq $actual[$j].Key) { $lcs[($i + 1), ($j + 1)] + 1 }
                           else { [Math]::Max($lcs[($i + 1), $j], $lcs[$i, ($j + 1)]) }
        }
    }

    $rows = [Collections.Generic.List[object]]::new()
    $matches = [Collections.Generic.List[object]]::new()
    $missing = [Collections.Generic.List[object]]::new()
    $extra = [Collections.Generic.List[object]]::new()
    $flush = {
        $pairs = [Math]::Min($missing.Count, $extra.Count)
        for ($k = 0; $k -lt $pairs; $k++) { $rows.Add([pscustomobject]@{ 类型 = '不同'; 预期 = $missing[$k].Line; 实际 = $extra[$k].Line }) }
        for ($k = $pairs; $k -lt $missing.Count; $k++) { $rows.Add([pscustomobject]@{ 类型 = '缺少'; 预期 = $missing[$k].Line; 实际 = '' }) }
        for ($k = $pairs; $k -lt $extra.Count; $k++) { $rows.Add([pscustomobject]@{ 类型 = '多出'; 预期 = ''; 实际 = $extra[$k].Line }) }
        $missing.Clear(); $extra.Clear()
    }
    $i = 0; $j = 0
    while ($i -lt $n -or $j -lt $m) {
        if ($i -lt $n -and $j -lt $m -and $expected[$i].Key -eq $actual[$j].Key) {
            . $flush
            $matches.Add([pscustomobject]@{ Expected = $expected[$i]; Actual = $actual[$j] })
            if ($expected[$i].Skill -ne $actual[$j].Skill) {
                $rows.Add([pscustomobject]@{ 类型 = '招式不同'; 预期 = $expected[$i].Line; 实际 = $actual[$j].Line })
            }
            $i++; $j++
        }
        elseif ($j -lt $m -and ($i -ge $n -or $lcs[$i, ($j + 1)] -ge $lcs[($i + 1), $j])) { $extra.Add($actual[$j]); $j++ }
        else { $missing.Add($expected[$i]); $i++ }
    }
    . $flush
    return [pscustomobject]@{ Rows = $rows; Matches = $matches }
}

# Precision/recall per action type (主 / 插入 / 分段: unit and type must match) and per skill (matched action with the
# same skill; only lines that name a skill count). Totals accumulate over all samples.
$stats = [ordered]@{}
function Add-Stats($expected, $actual, $matches) {
    $count = { param($label, $list, $pick) @($list | Where-Object { (& $pick $_) -eq $label }).Count }
    $labels = @(
        @{ Name = '主'; Pick = { $args[0].Kind } }, @{ Name = '插入'; Pick = { $args[0].Kind } }, @{ Name = '分段'; Pick = { $args[0].Kind } },
        @{ Name = '普攻'; Pick = { $args[0].Skill } }, @{ Name = '战技'; Pick = { $args[0].Skill } }, @{ Name = '终结技'; Pick = { $args[0].Skill } })
    foreach ($label in $labels) {
        $pick = $label.Pick
        if (-not $stats.Contains($label.Name)) { $stats[$label.Name] = [pscustomobject]@{ 预期 = 0; 实际 = 0; 对上 = 0 } }
        $s = $stats[$label.Name]
        $s.预期 += & $count $label.Name $expected $pick
        $s.实际 += & $count $label.Name $actual $pick
        $s.对上 += @($matches | Where-Object { (& $pick $_.Expected) -eq $label.Name -and (& $pick $_.Actual) -eq $label.Name }).Count
    }
}

function Write-Stats {
    $percent = { param($a, $b) if ($b -eq 0) { '—' } else { '{0:0.0}%' -f (100.0 * $a / $b) } }
    $table = foreach ($name in $stats.Keys) {
        $s = $stats[$name]
        if ($s.预期 -eq 0 -and $s.实际 -eq 0) { continue }
        [pscustomobject]@{ 类型 = $name; 预期 = $s.预期; 实际 = $s.实际; 对上 = $s.对上; 精确率 = & $percent $s.对上 $s.实际; 召回率 = & $percent $s.对上 $s.预期 }
    }
    Write-Host '== 按类型统计（全部样本）'
    $table | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
}

$failed = 0
foreach ($sample in Get-ChildItem samples -Directory | Where-Object { $_.Name -like "*$Filter*" }) {
    $info = Get-Content (Join-Path $sample.FullName 'sample.json') -Raw -Encoding utf8 | ConvertFrom-Json
    # The blind-test video must never be viewed.
    if ($info.video -match 'BV18nVd6VEiB') { Write-Host "== $($sample.Name)：跳过（盲测视频）"; continue }
    $expectedFile = Join-Path $sample.FullName 'expected.txt'
    if (-not (Test-Path $expectedFile)) { Write-Host "== $($sample.Name)：跳过（还没有 expected.txt）"; continue }
    if (-not (Test-Path $info.video)) { Write-Host "== $($sample.Name)：找不到视频 $($info.video)"; $failed++; continue }

    # actual.txt is the analyzer's latest output, kept for inspection; it is gitignored.
    $actualFile = Join-Path $sample.FullName 'actual.txt'
    $axisArgs = @('sequence', $info.video, '--out', $actualFile)
    if ($info.team) { $axisArgs += '--team', $info.team }
    if ($null -ne $info.start) { $axisArgs += '--start', "$($info.start)" }
    if ($null -ne $info.end) { $axisArgs += '--end', "$($info.end)" }
    & $axis.FullName @axisArgs | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Host "== $($sample.Name)：分析器出错（退出码 $LASTEXITCODE）"; $failed++; continue }

    $expected = Read-Actions (Get-Content $expectedFile -Raw -Encoding utf8)
    $actual = Read-Actions (Get-Content $actualFile -Raw -Encoding utf8)
    $result = Compare-Actions $expected $actual
    $rows = $result.Rows
    Add-Stats $expected $actual $result.Matches
    $matched = $expected.Count - @($rows | Where-Object { $_.类型 -in '缺少', '不同' }).Count
    Write-Host "== $($sample.Name)：预期 $($expected.Count) 条，实际 $($actual.Count) 条，对上 $matched 条，不一致 $($rows.Count) 处"
    if ($rows.Count -gt 0) {
        $failed++
        $rows | Format-Table -AutoSize -Wrap | Out-String -Width 200 | Write-Host
    }
}

if ($stats.Count -gt 0) { Write-Stats }
if ($failed -gt 0) { Write-Host "有 $failed 个样本不一致。"; exit 1 }
Write-Host '全部样本一致。'
