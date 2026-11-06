# 给 IconBox 的可执行文件加代码签名，消除 Windows SmartScreen 的「未知发布者」拦截。
#
# 说明：
#   - 默认使用本机证书库里的自签名证书（CN=IconBox）。这类证书只在本机受信任，
#     对「自己这台电脑」足够消除拦截；要让别人的电脑也不弹窗，必须换成
#     公共 CA 签发的代码签名证书（EV 证书可立即获得信誉）。
#   - 证书私钥不进入仓库：签名目录 signing\ 已被 .gitignore 排除。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File tools\sign.ps1 -Path D:\gbdev\IconBox.exe
#   powershell -ExecutionPolicy Bypass -File tools\sign.ps1 -Path dist\IconBox-Setup-1.0.3.exe
#   powershell -ExecutionPolicy Bypass -File tools\sign.ps1 -Path x.exe -Pfx signing\iconbox-codesign.pfx -Password ***

param(
	[Parameter(Mandatory = $true)][string[]]$Path,
	[string]$Pfx = "",
	[string]$Password = "",
	[string]$TimestampServer = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"

function Get-SigningCertificate {
	if ($Pfx -ne "" -and (Test-Path -LiteralPath $Pfx)) {
		$secure = ConvertTo-SecureString -String $Password -Force -AsPlainText
		$imported = Import-PfxCertificate -FilePath $Pfx -CertStoreLocation Cert:\CurrentUser\My -Password $secure -Exportable
		Write-Host ("已导入证书：" + $imported.Thumbprint)
		return $imported
	}
	$cert = Get-ChildItem Cert:\CurrentUser\My |
		Where-Object { $_.Subject -like "*CN=IconBox*" -and $_.HasPrivateKey } |
		Sort-Object NotAfter -Descending |
		Select-Object -First 1
	if ($null -eq $cert) {
		throw "找不到代码签名证书。请先运行 tools\make-cert.ps1 生成自签名证书，或用 -Pfx 指定证书文件。"
	}
	return $cert
}

function Ensure-Trusted([System.Security.Cryptography.X509Certificates.X509Certificate2]$cert) {
	foreach ($name in @("Root", "TrustedPublisher")) {
		$store = New-Object System.Security.Cryptography.X509Certificates.X509Store($name, "CurrentUser")
		$store.Open("ReadWrite")
		try {
			if (-not ($store.Certificates | Where-Object { $_.Thumbprint -eq $cert.Thumbprint })) {
				$store.Add($cert)
				Write-Host ("证书已加入当前用户 " + $name + " 存储")
			}
		}
		finally {
			$store.Close()
		}
	}
}

$certificate = Get-SigningCertificate
Ensure-Trusted $certificate
Write-Host ("使用证书：" + $certificate.Subject + "（" + $certificate.Thumbprint + "）")

$failed = 0
foreach ($file in $Path) {
	$full = [System.IO.Path]::GetFullPath($file)
	if (-not (Test-Path -LiteralPath $full)) {
		Write-Warning ("文件不存在：" + $full)
		$failed++
		continue
	}
	$result = $null
	try {
		$result = Set-AuthenticodeSignature -FilePath $full -Certificate $certificate -HashAlgorithm SHA256 -TimestampServer $TimestampServer
	}
	catch {
		Write-Warning ("时间戳服务不可用，改为不带时间戳签名：" + $_.Exception.Message)
		$result = Set-AuthenticodeSignature -FilePath $full -Certificate $certificate -HashAlgorithm SHA256
	}
	$check = Get-AuthenticodeSignature -FilePath $full
	if ($check.Status -ne "Valid") {
		Write-Warning ($full + " 签名状态：" + $check.Status + " " + $check.StatusMessage)
		$failed++
	}
	else {
		$name = [System.IO.Path]::GetFileName($full)
		Write-Host ($name + " 已签名（" + $check.SignerCertificate.Subject + "）")
	}
}

if ($failed -gt 0) {
	throw ("有 " + $failed + " 个文件签名失败")
}
