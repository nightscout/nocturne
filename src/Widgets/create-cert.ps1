# Create self-signed certificate for widget signing
$cert = New-SelfSignedCertificate -Type Custom -Subject 'CN=Nocturne Dev' -KeyUsage DigitalSignature -FriendlyName 'Nocturne Widget Dev Certificate' -CertStoreLocation 'Cert:\CurrentUser\My' -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')

Write-Host "Created certificate: $($cert.Thumbprint)"

# Export to PFX
$password = ConvertTo-SecureString -String 'NocturneDev123' -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath 'src\Widgets\NocturneWidget.pfx' -Password $password

# Add to trusted people store (for sideloading)
$store = New-Object System.Security.Cryptography.X509Certificates.X509Store('TrustedPeople', 'CurrentUser')
$store.Open('ReadWrite')
$store.Add($cert)
$store.Close()

Write-Host 'Certificate installed to TrustedPeople store'
Write-Host "Thumbprint: $($cert.Thumbprint)"
