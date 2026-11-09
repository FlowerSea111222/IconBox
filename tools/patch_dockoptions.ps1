$ErrorActionPreference = 'Stop'
$path = 'C:\Users\13793\Documents\deepseek-harness\default-workspace\iconbox\source\src\IconBox\DockOptions.cs'
$lines = [System.Collections.Generic.List[string]]::new()
[System.IO.File]::ReadAllLines($path, [System.Text.Encoding]::UTF8) | ForEach-Object { $lines.Add($_) }

function Find-Line([string]$anchor, [int]$from) {
    for ($i = $from; $i -lt $lines.Count; $i++) {
        if ($lines[$i].Contains($anchor)) { return $i }
    }
    throw "anchor not found: $anchor"
}

function Remove-Block([string]$anchor, [int]$before, [int]$after) {
    $i = Find-Line $anchor 0
    $start = $i - $before
    $end = $i + $after
    Write-Output ("---- removing [{0}..{1}] for '{2}'" -f ($start + 1), ($end + 1), $anchor)
    for ($k = $start; $k -le $end; $k++) {
        $t = $lines[$k].Trim()
        if ($t.Length -gt 48) { $t = $t.Substring(0, 48) }
        Write-Output ("  - " + $t)
    }
    $lines.RemoveRange($start, ($end - $start + 1))
}

# 1) DockOptions: 删除四个独立开关字段 + 收纳整合 + ComputerStyle
Remove-Block 'public int ComputerStyle;' 1 1
Remove-Block 'public bool ShowComputer = false;' 1 6
Remove-Block 'public bool ShowPower;' 1 1
Remove-Block 'public bool ShowNetwork;' 1 1
Remove-Block 'public bool ShowSettings = false;' 1 1

# 2) Defaults(): 清理对应初始化
Remove-Block 'ComputerStyle = 0;' 0 0
Remove-Block 'ShowComputer = false;' 0 0
Remove-Block 'Consolidate = true;' 0 0
Remove-Block 'ShowSettings = false;' 0 0

# 3) Normalize(): 清理 ComputerStyle 夹取与收纳整合同步块
Remove-Block 'ComputerStyle = Math.Max(0, Math.Min(1, ComputerStyle));' 0 0
Remove-Block 'if (Consolidate)' 0 9

# 4) SystemCount(): 只统计应用面板 + 控制中心 + 运行列表
$i = Find-Line 'public int SystemCount()' 0
$end = $i
while ($lines[$end] -ne $null -and $lines[$end].Trim() -ne '}') { $end++ }
Write-Output ("---- rebuilding SystemCount lines {0}..{1}" -f ($i + 1), ($end + 1))
$body = @(
    "`tpublic int SystemCount()",
    "`t{",
    "`t`t// Dock 上的系统入口固定为三类：应用面板 + 控制中心 + 运行列表",
    "`t`treturn 1 + (ShowControlCenter ? 1 : 0) + (ShowRunningList ? 1 : 0);",
    "`t}"
)
$lines.RemoveRange($i, ($end - $i + 1))
$lines.InsertRange($i, [string[]]$body)

$out = [string]::Join("`r`n", $lines) + "`r`n"
[System.IO.File]::WriteAllText($path, $out, (New-Object System.Text.UTF8Encoding($false)))
Write-Output "written $path"
