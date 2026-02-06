# Nocturne Windows 11 Widget

A Windows 11 Widgets Board provider that displays real-time glucose data from your Nocturne server. Available in three sizes:

| Size | Widget | Description |
|------|--------|-------------|
| Small | Nocturne (Small) | Glucose value and trend direction |
| Medium | Nocturne | Glucose, trend, IOB/COB |
| Large | Nocturne Dashboard | Full dashboard with predictions |

## Requirements

- Windows 11 (Build 22000 or later)
- .NET 10 SDK
- PowerShell

## Install (Development)

### 1. Install the signing certificate

The widget must be signed to sideload on Windows 11. A self-signed development certificate is included in the repository.

Import the certificate to your trusted stores:

```powershell
# From the repository root:

# Install to TrustedPeople (required for sideloading, no admin needed)
$password = ConvertTo-SecureString -String 'NocturneDev123' -Force -AsPlainText
Import-PfxCertificate -FilePath src\Widgets\NocturneWidget.pfx -CertStoreLocation 'Cert:\CurrentUser\TrustedPeople' -Password $password

# Install to Trusted Root (requires elevated PowerShell)
.\src\Widgets\install-cert-root.ps1
```

If you need to regenerate the certificate from scratch, run `.\src\Widgets\create-cert.ps1` from the repository root. This creates a new PFX and installs it. Note that regenerating the certificate will change the thumbprint, which must then be updated in the `.csproj`.

### 2. Build the MSIX package

```powershell
dotnet publish src\Widgets\Nocturne.Widget.Windows11\Nocturne.Widget.Windows11.csproj `
    -p:Platform=x64 `
    -c Release `
    -p:GenerateAppxPackageOnBuild=true
```

For ARM64 devices, replace `-p:Platform=x64` with `-p:Platform=ARM64`.

The signed MSIX will be output to:

```
src\Widgets\Nocturne.Widget.Windows11\bin\x64\Release\net10.0-windows10.0.22000.0\AppPackages\
```

### 3. Install the widget

```powershell
Add-AppxPackage -Path src\Widgets\Nocturne.Widget.Windows11\bin\x64\Release\net10.0-windows10.0.22000.0\AppPackages\Nocturne.Widget.Windows11_1.0.0.0_x64_Test\Nocturne.Widget.Windows11_1.0.0.0_x64.msix
```

Verify the installation:

```powershell
Get-AppxPackage -Name '*Nocturne*'
```

After installing, restart the Widget infrastructure so it discovers the new provider:

```powershell
Stop-Process -Name WidgetBoard -Force -ErrorAction SilentlyContinue
Stop-Process -Name WidgetService -Force -ErrorAction SilentlyContinue
```

Then reopen the Widgets Board (Win+W).

### Updating after a rebuild

After reinstalling an updated package, the widget provider COM server process must be stopped **twice** before the new version will load. Windows automatically restarts the process on the first stop, so you need to kill it a second time for the update to take effect.

```powershell
Stop-Process -Name Nocturne.Widget.Windows11 -Force -ErrorAction SilentlyContinue
Stop-Process -Name Nocturne.Widget.Windows11 -Force -ErrorAction SilentlyContinue
```

### 4. Add the widget

Open the Windows 11 Widgets Board (Win+W), click the **+** button, and search for "Nocturne". Select the size you want to add.

On first use, right-click the widget and select **Customize** to enter your Nocturne server URL and API token.

## Uninstall

```powershell
Get-AppxPackage -Name '*Nocturne*' | Remove-AppxPackage
```

## Project Structure

```
src/Widgets/
├── Nocturne.Widget.Contracts/        # Service interfaces
├── Nocturne.Widget.Infrastructure/   # API client, credential store, notifications
├── Nocturne.Widget.Windows11/        # Widget provider (main project)
│   ├── Templates/                    # Adaptive Card templates (Small/Medium/Large)
│   ├── Program.cs                    # COM server entry point
│   ├── WidgetProvider.cs             # Widget lifecycle and data binding
│   └── Package.appxmanifest          # Package identity and widget definitions
├── Nocturne.Widget.Windows11.Package/ # WAP project (alternative packaging)
├── create-cert.ps1                   # Generate a new self-signed certificate
├── install-cert-root.ps1             # Install certificate to root store (admin)
├── NocturneWidget.pfx                # Dev signing certificate
└── NocturneWidget.cer                # Public certificate
```
