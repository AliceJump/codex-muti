#requires -Version 7.0
param([string]$DataDirectory = $(if ($env:CODEX_POOL_DATA_DIR) { $env:CODEX_POOL_DATA_DIR } else { Join-Path $env:LOCALAPPDATA 'CodexPool' }))
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw '此脚本使用 Windows 当前用户证书存储。' }
$taskDirectory = [IO.Path]::GetFullPath($DataDirectory)
$taskPointer = Join-Path $taskDirectory 'backend-https.thumbprint'
$taskCertificate = $null
if (Test-Path -LiteralPath $taskPointer) {
    $taskThumbprint = ([IO.File]::ReadAllText($taskPointer)).Trim()
    if ($taskThumbprint -match '^[0-9a-fA-F]{40}$') {
        $taskCertificate = Get-Item -LiteralPath ('Cert:\CurrentUser\My\' + $taskThumbprint) -ErrorAction SilentlyContinue
        if ($taskCertificate -and (-not $taskCertificate.HasPrivateKey -or $taskCertificate.NotBefore -gt (Get-Date) -or $taskCertificate.NotAfter -le (Get-Date).AddDays(14) -or
            -not $taskCertificate.MatchesHostname('127.0.0.1', $false, $false) -or
            -not ($taskCertificate.EnhancedKeyUsageList.ObjectId.Value -contains '1.3.6.1.5.5.7.3.1'))) { $taskCertificate = $null }
    }
}
if (-not $taskCertificate) {
    # The private key stays non-exportable in the current user's Windows key store.
    $taskCertificate = New-SelfSignedCertificate -Subject 'CN=Codex Pool Local Backend' -FriendlyName 'Codex Pool Local Backend' `
        -CertStoreLocation 'Cert:\CurrentUser\My' -Type Custom -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 `
        -KeyExportPolicy NonExportable -KeyUsage DigitalSignature,KeyEncipherment -NotAfter (Get-Date).AddYears(1) `
        -TextExtension @('2.5.29.17={text}IPAddress=127.0.0.1', '2.5.29.37={text}1.3.6.1.5.5.7.3.1', '2.5.29.19={critical}{text}ca=0')
}
# Trust only this server certificate for the current user; no machine-wide CA or PFX export.
$taskRootStore = [Security.Cryptography.X509Certificates.X509Store]::new('Root', 'CurrentUser')
try {
    $taskRootStore.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    $taskPublicCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($taskCertificate.RawData)
    try { $taskRootStore.Add($taskPublicCertificate) } finally { $taskPublicCertificate.Dispose() }
} finally { $taskRootStore.Dispose() }
[IO.Directory]::CreateDirectory($taskDirectory) | Out-Null
[IO.File]::WriteAllText($taskPointer + '.tmp', $taskCertificate.Thumbprint)
Move-Item -LiteralPath ($taskPointer + '.tmp') -Destination $taskPointer -Force
Write-Host '本地后端证书已安装到当前 Windows 用户；未修改 Codex 配置或重启服务。'
Write-Host '部署新版 Daemon 后，将 chatgpt_base_url 设置为 "https://127.0.0.1:3211/backend-api"，再重启 Codex。'
Write-Host '移除 backend-https.thumbprint 并重启 Daemon 可停用 HTTPS；证书可在 certmgr.msc 中按 Codex Pool Local Backend 名称移除。'
