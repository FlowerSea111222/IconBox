$ErrorActionPreference = 'Stop'
$path = 'C:\Users\13793\Documents\deepseek-harness\default-workspace\iconbox\source\src\IconBox\DockWindow.cs'
$lines = [System.Collections.Generic.List[string]]::new()
[System.IO.File]::ReadAllLines($path, [System.Text.Encoding]::UTF8) | ForEach-Object { $lines.Add($_) }

function Find-Line([string]$needle, [int]$from) {
    for ($i = $from; $i -lt $lines.Count; $i++) {
        if ($lines[$i].Contains($needle)) { return $i }
    }
    throw "not found: $needle"
}

function Block-End([int]$start) {
    $depth = 0
    $seen = $false
    for ($i = $start; $i -lt $lines.Count; $i++) {
        foreach ($ch in $lines[$i].ToCharArray()) {
            if ($ch -eq '{') { $depth++; $seen = $true }
            elseif ($ch -eq '}') { $depth-- }
        }
        if ($seen -and $depth -eq 0) { return $i }
    }
    throw "unbalanced block from $start"
}

function Remove-Block([string]$needle, [int]$afterEnd, [string]$label) {
    $i = Find-Line $needle 0
    $end = Block-End $i
    Write-Output ("---- remove $label : lines " + ($i + 1) + ".." + ($end + 1))
    Write-Output ("     head: " + $lines[$i].Trim())
    $lines.RemoveRange($i, ($end - $i + 1 + $afterEnd))
}

# 1) 电源菜单（并入控制中心后 Dock 侧不再需要）
Remove-Block 'private void Power(int action)' 0 'Power(int)'
# 2) 快捷菜单按钮（电源 / Wi-Fi·蓝牙）
Remove-Block 'private void AddQuickButton(string key, string glyph, string title, string[] names, Action<int> action)' 0 'AddQuickButton'
Remove-Block 'private void AddQuickButtons()' 0 'AddQuickButtons'

# 3) 调用点
$i = Find-Line '		AddQuickButtons();' 0
$lines.RemoveAt($i)
Write-Output ("---- remove call at line " + ($i + 1))

# 4) 此电脑 / 回收站块、设置块
Remove-Block 'if (dock.ShowComputer && !dock.Consolidate)' 0 'ShowComputer block'
Remove-Block 'if (dock.ShowSettings && !dock.Consolidate)' 0 'ShowSettings block'

# 5) 自检辅助方法里的旧字段
$i = Find-Line 'C.Prefs.Dock.ShowPower = (C.Prefs.Dock.ShowNetwork =' 0
$lines[$i] = '		C.Prefs.Dock.ShowControlCenter = true;'
Write-Output ("---- fix CheckMixedPointerReorder at line " + ($i + 1))

# 6) 名称 / 图标映射中删除四个入口
foreach ($key in @('computer', 'settings', 'power', 'network', 'recycle')) {
    $i = Find-Line ('"iconbox://dock/' + $key + '" =>') 0
    Write-Output ("---- drop map line " + ($i + 1) + ": " + $lines[$i].Trim())
    $lines.RemoveAt($i)
}

$out = [string]::Join("`r`n", $lines) + "`r`n"
[System.IO.File]::WriteAllText($path, $out, (New-Object System.Text.UTF8Encoding($false)))
Write-Output "written $path"
