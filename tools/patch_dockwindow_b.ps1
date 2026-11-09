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

function OfType-Line([string]$needle) {
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i].Contains($needle) -and $lines[$i].TrimStart().StartsWith('"')) { return $i }
    }
    throw "map line not found: $needle"
}

# 1) 右键菜单里的「显示 Dock 设置图标」整项
$i = Find-Line 'System.Windows.Controls.MenuItem settingsIcon = new System.Windows.Controls.MenuItem' 0
$end = Find-Line 'menu.Items.Add(settingsIcon);' $i
Write-Output ("---- remove settingsIcon menu " + ($i + 1) + ".." + ($end + 1))
$lines.RemoveRange($i, ($end - $i + 1))

# 2) 拖拽到回收站的 Dock 命中区（PreviewDrop）
$i = Find-Line 'System.Windows.Controls.Button recycle = ((files == null || files.Length == 0) ? null :' 0
$end = Find-Line 'DockDropTarget target = ExternalDockTarget(e.GetPosition(Items).X);' $i
Write-Output ("---- remove recycle hit test (drop) " + ($i + 1) + ".." + ($end - 1))
$lines.RemoveRange($i, ($end - $i))

# 3) 拖拽到回收站的悬停提示（DockExternalOver）
$i = Find-Line 'System.Windows.Controls.Button recycle = ((!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) ? null :' 0
$end = Find-Line 'e.Effects = System.Windows.DragDropEffects.Copy;' $i
Write-Output ("---- remove recycle hit test (over) " + ($i + 1) + ".." + ($end - 1))
$lines.RemoveRange($i, ($end - $i))

# 4) 自检里「隐藏设置图标」段落
$i = Find-Line 'string text2 = string.Join("|", LayoutKeys);' 0
$end = Find-Line 'throw new Exception("重新显示设置图标后丢失位置");' $i
Write-Output ("---- remove settings self-test " + ($i + 1) + ".." + (($end + 2)))
# 结束位置还包含其后的 } 与空行
$lines.RemoveRange($i, ($end - $i + 4))

# 5) 图标种类映射里的四个入口 + 回收站
foreach ($key in @('computer', 'recycle', 'settings', 'power', 'network')) {
    $i = OfType-Line ('"iconbox://dock/' + $key + '" => "')
    Write-Output ("---- drop kind map " + ($i + 1) + ": " + $lines[$i].Trim())
    $lines.RemoveAt($i)
}

$out = [string]::Join("`r`n", $lines) + "`r`n"
[System.IO.File]::WriteAllText($path, $out, (New-Object System.Text.UTF8Encoding($false)))
Write-Output "written $path"
