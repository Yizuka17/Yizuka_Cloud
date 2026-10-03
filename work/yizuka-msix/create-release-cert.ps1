$ErrorActionPreference = 'Stop'
$subject = 'CN=Yizuka Cloud Development'
$existing = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $subject -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddYears(9) } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if ($existing) {
    Write-Output "Existing signing certificate: $($existing.Thumbprint) (expires $($existing.NotAfter.ToString('yyyy-MM-dd')))"
    return
}
$certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject `
    -FriendlyName 'Yizuka Cloud MSIX signing' -CertStoreLocation 'Cert:\CurrentUser\My' `
    -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
    -NotAfter (Get-Date).AddYears(10) -KeyExportPolicy NonExportable
Write-Output "New signing certificate: $($certificate.Thumbprint) (expires $($certificate.NotAfter.ToString('yyyy-MM-dd')))"
