# Add the certificate to Trusted Root store
# This script should be run elevated (as Administrator)

$pfxPath = "src\Widgets\NocturneWidget.pfx"
$password = ConvertTo-SecureString -String 'NocturneDev123' -Force -AsPlainText

# Import the certificate
$cert = Import-PfxCertificate -FilePath $pfxPath -CertStoreLocation 'Cert:\LocalMachine\Root' -Password $password

Write-Host "Certificate added to Trusted Root store: $($cert.Thumbprint)"
