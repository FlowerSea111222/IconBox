# IconBox 一键构建脚本
#   源码 (source\)  ->  可运行/可打包的便携版 (dist\portable\)
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -SkipBuild   # 只重新收集文件
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -RunDir D:\gbdev
#       （把产物同步到工作区之外的调试目录，避免 DSH 沙箱把进程降到低完整性级别）
param(
    [string]$Configuration = 'Release',
    [string]$Dotnet = 'D:\dev\dotnet\dotnet.exe',
    [switch]$SkipBuild,
    [string]$RunDir = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcRoot = Join-Path $root 'source'
$proj = Join-Path $srcRoot 'src\IconBox.csproj'
$out = Join-Path $srcRoot ("src\bin\{0}\net48" -f $Configuration)
$dist = Join-Path $root 'dist\portable'

if (-not (Test-Path $proj)) { throw "找不到工程文件：$proj" }

if (-not $SkipBuild) {
    if (-not (Test-Path $Dotnet)) { throw "找不到 dotnet：$Dotnet（可用 -Dotnet 指定）" }
    Write-Host "[1/4] 编译 $proj ..."
    & $Dotnet build $proj -c $Configuration -v minimal
    if ($LASTEXITCODE -ne 0) { throw '编译失败' }
}

Write-Host "[2/4] 清理 $dist ..."
if (Test-Path $dist) { Remove-Item -LiteralPath $dist -Recurse -Force }
New-Item -ItemType Directory -Force -Path $dist, (Join-Path $dist 'lib'), (Join-Path $dist 'licenses') | Out-Null

Write-Host '[3/4] 收集运行时文件 ...'
$files = @(
    @{ From = (Join-Path $out 'IconBox.exe');                    To = $dist },
    @{ From = (Join-Path $out 'IconBox.exe.config');             To = $dist },
    @{ From = (Join-Path $out 'Interop.UIAutomationClient.dll');    To = $dist },
    @{ From = (Join-Path $srcRoot 'lib\NPSMLib.dll');               To = (Join-Path $dist 'lib') },
    @{ From = (Join-Path $srcRoot 'DefaultConfig.json');            To = $dist },
    @{ From = (Join-Path $srcRoot 'README.md');                     To = $dist },
    @{ From = (Join-Path $srcRoot 'THIRD_PARTY_NOTICES.md');        To = $dist }
)
foreach ($f in $files) {
    if (-not (Test-Path $f.From)) { throw "缺少文件：$($f.From)" }
    Copy-Item -LiteralPath $f.From -Destination $f.To -Force
}
Copy-Item -Path (Join-Path $srcRoot 'licenses\*.txt') -Destination (Join-Path $dist 'licenses') -Force

$exe = Join-Path $dist 'IconBox.exe'
$signScript = Join-Path $root 'tools\sign.ps1'
if ((Test-Path -LiteralPath $signScript) -and (Test-Path -LiteralPath $exe)) {
    Write-Host '[4/4] 代码签名（消除 SmartScreen「未知发布者」） ...'
    try {
        & $signScript -Path $exe | Out-Null
        $sig = Get-AuthenticodeSignature -FilePath $exe
        if ($sig.Status -eq 'Valid') { Write-Host ('      签名有效：' + $sig.SignerCertificate.Subject) }
        else { Write-Warning ('      签名状态：' + $sig.Status) }
    }
    catch {
        Write-Warning ('      签名失败（不阻断构建）：' + $_.Exception.Message)
    }
}
$ver = (Get-Item $exe).VersionInfo.FileVersion
$total = (Get-ChildItem $dist -Recurse -File | Measure-Object Length -Sum).Sum

Write-Host ''
Write-Host ("完成：IconBox {0}" -f $ver)
Write-Host ("产物：{0}" -f $dist)
Write-Host ("文件：{0} 个，共 {1:N0} 字节" -f (Get-ChildItem $dist -Recurse -File).Count, $total)

if ($RunDir) {
    Write-Host ("同步到调试目录：{0}" -f $RunDir)
    New-Item -ItemType Directory -Force -Path $RunDir | Out-Null
    Copy-Item -Path (Join-Path $dist '*') -Destination $RunDir -Recurse -Force
}
