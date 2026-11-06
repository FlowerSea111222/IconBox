# 生成本机自签名代码签名证书（CN=IconBox），并加入「当前用户」的受信任根与受信任发布者存储。
#
# 为什么需要：
#   IconBox 默认没有签名，Windows SmartScreen 会把未签名且无信誉的程序拦成
#   「Windows 已保护你的电脑 / 未知发布者」。给 exe 与安装包签名后，Windows 能识别发布者，
#   本机不再弹拦截框。
#
# 局限（务必知道）：
#   自签名证书只在本机受信任。别人下载安装包时仍会看到「未知发布者」，
#   要彻底解决必须购买公共 CA 的代码签名证书（EV 证书可立即建立信誉）。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File tools\make-cert.ps1
#   然后再执行 tools\sign.ps1 对 exe / 安装包签名。

$ErrorActionPreference = "Stop"

$existing = Get-ChildItem Cert:\CurrentUser\My |
	Where-Object { $_.Subject -like "*CN=IconBox*" -and $_.HasPrivateKey } |
	Sort-Object NotAfter -Descending |
	Select-Object -First 1

if ($null -ne $existing) {
	$cert = $existing
	Write-Host ("复用已有证书：" + $cert.Thumbprint)
}
else {
	$cert = New-SelfSignedCertificate `
		-Type CodeSigningCert `
		-Subject "CN=IconBox, O=IconBox Project, C=CN" `
		-KeyUsage DigitalSignature `
		-KeyAlgorithm RSA `
		-KeyLength 2048 `
		-HashAlgorithm SHA256 `
		-CertStoreLocation "Cert:\CurrentUser\My" `
		-NotAfter (Get-Date).AddYears(10)
	Write-Host ("已生成证书：" + $cert.Thumbprint)
}

foreach ($name in @("Root", "TrustedPublisher")) {
	$store = New-Object System.Security.Cryptography.X509Certificates.X509Store($name, "CurrentUser")
	$store.Open("ReadWrite")
	try {
		if (-not ($store.Certificates | Where-Object { $_.Thumbprint -eq $cert.Thumbprint })) {
			$store.Add($cert)
			Write-Host ("已加入当前用户 " + $name + " 存储")
		}
	}
	finally {
		$store.Close()
	}
}

# 备份一份 pfx 到 signing\（该目录不进仓库），换机器时可用 -Pfx 指定。
$signing = Join-Path $PSScriptRoot "..\signing"
New-Item -ItemType Directory -Force -Path $signing | Out-Null
$pfxPath = Join-Path $signing "iconbox-codesign.pfx"
if (-not (Test-Path -LiteralPath $pfxPath)) {
	$secure = ConvertTo-SecureString -String "iconbox-local-only" -Force -AsPlainText
	Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $secure | Out-Null
	Write-Host ("证书已备份：" + $pfxPath + "（口令 iconbox-local-only，仅本机使用，请勿提交到仓库）")
}

Write-Host ""
Write-Host "完成。接下来执行："
Write-Host '  powershell -ExecutionPolicy Bypass -File tools\sign.ps1 -Path D:\gbdev\IconBox.exe'
