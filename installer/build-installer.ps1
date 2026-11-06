# 打包安装向导：编译 IconBox.iss -> dist\IconBox-Setup-<版本>.exe
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\build-installer.ps1
#   ... -SkipBuild        跳过源码编译，直接用 dist\portable 里已有的产物打包
#   ... -Iscc <路径>      指定 ISCC.exe
param(
    [string]$Iscc = 'D:\dev\InnoSetup6\ISCC.exe',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
$iss = Join-Path $here 'IconBox.iss'
$payload = Join-Path $root 'dist\portable'

# 1) 先保证便携版产物存在
if (-not $SkipBuild) {
    Write-Host '[1/3] 构建便携版 ...'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build.ps1')
    if ($LASTEXITCODE -ne 0) { throw '构建便携版失败' }
} else {
    Write-Host '[1/3] 跳过源码构建'
}
if (-not (Test-Path (Join-Path $payload 'IconBox.exe'))) {
    throw "缺少便携版产物：$payload（先运行 build.ps1）"
}

# 2) 补 UTF-8 BOM（Inno Setup 需要 BOM 才能正确识别 .iss 里的中文）
$bytes = [System.IO.File]::ReadAllBytes($iss)
if (-not ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)) {
    $text = [System.Text.Encoding]::UTF8.GetString($bytes)
    [System.IO.File]::WriteAllText($iss, $text, (New-Object System.Text.UTF8Encoding($true)))
    Write-Host '    已为 IconBox.iss 补写 UTF-8 BOM'
}

# 3) 编译
if (-not (Test-Path $Iscc)) { throw "找不到 ISCC.exe：$Iscc（装好 Inno Setup 6 后可用 -Iscc 指定）" }
Write-Host "[2/3] 编译 $iss ..."
& $Iscc $iss
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup 编译失败' }

# 4) 汇总
$setup = Get-ChildItem (Join-Path $root 'dist') -Filter 'IconBox-Setup-*.exe' |
         Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host '[3/3] 完成'
Write-Host ''
Write-Host ("安装程序：{0}" -f $setup.FullName)
Write-Host ("大小：{0:N2} MB" -f ($setup.Length / 1MB))
Write-Host ("SHA256：{0}" -f (Get-FileHash $setup.FullName -Algorithm SHA256).Hash)
