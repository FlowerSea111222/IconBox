$ErrorActionPreference = 'Stop'
$path = 'C:\Users\13793\Documents\deepseek-harness\default-workspace\iconbox\source\src\IconBox\DockLayoutRules.cs'
$lines = [System.Collections.Generic.List[string]]::new()
[System.IO.File]::ReadAllLines($path, [System.Text.Encoding]::UTF8) | ForEach-Object { $lines.Add($_) }

function Find-Line([string]$needle, [int]$from) {
    for ($i = $from; $i -lt $lines.Count; $i++) {
        if ($lines[$i].Contains($needle)) { return $i }
    }
    throw "not found: $needle"
}

# 1) 四个系统入口常量（连同紧随的空行）
foreach ($name in @('Computer', 'Power', 'Network', 'Settings')) {
    $i = Find-Line ("public const string " + $name + " = ") 0
    Write-Output ("---- const " + $name + " at line " + ($i + 1))
    $lines.RemoveAt($i)
    if ($i -lt $lines.Count -and $lines[$i].Trim() -eq '') { $lines.RemoveAt($i) }
}

# 2) SystemKeys
$i = Find-Line 'SystemKeys = new string[7]' 0
$lines[$i] = '	public static readonly string[] SystemKeys = new string[3] { "iconbox://dock/apps", "iconbox://dock/control", "iconbox://dock/running" };'
Write-Output ("---- SystemKeys at line " + ($i + 1))

# 3) Visible()：整块重建
$sig = Find-Line 'public static bool Visible(DockOptions o, string key)' 0
$end = -1
for ($k = $sig; $k -lt $lines.Count; $k++) {
    if ($lines[$k].Trim() -eq '}') { $end = $k; break }
}
Write-Output ("---- Visible block lines " + ($sig + 1) + ".." + ($end + 1))
$body = @(
    "`tpublic static bool Visible(DockOptions o, string key)",
    "`t{",
    "`t`t// 此电脑 / 设置 / 电源 / 网络已并入控制中心，Dock 上只剩应用面板、控制中心与运行列表",
    "`t`tkey = Current(key);",
    "`t`tswitch (key)",
    "`t`t{",
    "`t`tcase `"iconbox://dock/control`":",
    "`t`t`treturn o.ShowControlCenter;",
    "`t`tcase `"iconbox://dock/running`":",
    "`t`t`treturn o.ShowRunningList;",
    "`t`tdefault:",
    "`t`t`treturn true;",
    "`t`t}",
    "`t}"
)
$lines.RemoveRange($sig, ($end - $sig + 1))
$lines.InsertRange($sig, [string[]]$body)

# 4) Merge 默认顺序
$i = Find-Line 'return new string[2] { "iconbox://dock/apps"' 0
$lines[$i] = '			return new string[1] { "iconbox://dock/apps" }.Concat(apps).Concat(new string[2] { "iconbox://dock/control", "iconbox://dock/running" }).ToList();'
if ($i -gt 0 -and $lines[$i - 1].Contains('设置图标排在控制中心')) {
    $lines[$i - 1] = '			// 控制中心排在运行列表之前，避免 Dock 溢出时被收进"更多"'
}
Write-Output ("---- Merge default at line " + ($i + 1))

# 5) 应用插入锚点
$i = Find-Line 'IndexOf("iconbox://dock/computer")' 0
$lines[$i] = $lines[$i].Replace('iconbox://dock/computer', 'iconbox://dock/control')
Write-Output ("---- merge anchor at line " + ($i + 1))

$out = [string]::Join("`n", $lines) + "`n"
[System.IO.File]::WriteAllText($path, $out, (New-Object System.Text.UTF8Encoding($false)))
Write-Output "written $path"
