using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Nocturne.Widget.Contracts;

namespace Nocturne.Widget.Infrastructure.Windows;

/// <summary>
/// Windows Credential Manager implementation for secure credential storage
/// </summary>
public class WindowsCredentialStore : ICredentialStore
{
    private const string CredentialTargetName = "Nocturne.Widget.Credentials";
    private readonly ILogger<WindowsCredentialStore> _logger;

    /// <summary>
    /// Initializes a new instance of the WindowsCredentialStore
    /// </summary>
    /// <param name="logger">The logger instance</param>
    public WindowsCredentialStore(ILogger<WindowsCredentialStore> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<NocturneCredentials?> GetCredentialsAsync()
    {
        try
        {
            if (!CredRead(CredentialTargetName, CredentialType.Generic, 0, out var credentialPtr))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorNotFound)
                {
                    _logger.LogDebug("No credentials found in credential store");
                    return Task.FromResult<NocturneCredentials?>(null);
                }

                _logger.LogWarning("Failed to read credentials, error code: {ErrorCode}", error);
                return Task.FromResult<NocturneCredentials?>(null);
            }

            try
            {
                var credential = Marshal.PtrToStructure<CREDENTIAL>(credentialPtr);
                var passwordBytes = new byte[credential.CredentialBlobSize];
                Marshal.Copy(
                    credential.CredentialBlob,
                    passwordBytes,
                    0,
                    (int)credential.CredentialBlobSize
                );
                var credentialData = Encoding.Unicode.GetString(passwordBytes);

                // Parse stored data (format: "apiUrl|token")
                var parts = credentialData.Split('|', 2);
                if (parts.Length != 2)
                {
                    _logger.LogWarning("Invalid credential format in store");
                    return Task.FromResult<NocturneCredentials?>(null);
                }

                return Task.FromResult<NocturneCredentials?>(
                    new NocturneCredentials(parts[0], parts[1])
                );
            }
            finally
            {
                CredFree(credentialPtr);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading credentials from Windows Credential Manager");
            return Task.FromResult<NocturneCredentials?>(null);
        }
    }

    /// <inheritdoc />
    public Task SaveCredentialsAsync(NocturneCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        try
        {
            // Store as "apiUrl|token"
            var credentialData = $"{credentials.ApiUrl}|{credentials.Token}";
            var passwordBytes = Encoding.Unicode.GetBytes(credentialData);

            var credential = new CREDENTIAL
            {
                Type = CredentialType.Generic,
                TargetName = CredentialTargetName,
                CredentialBlobSize = (uint)passwordBytes.Length,
                CredentialBlob = Marshal.AllocHGlobal(passwordBytes.Length),
                Persist = CredentialPersistence.LocalMachine,
                UserName = "NocturneWidget",
            };

            try
            {
                Marshal.Copy(passwordBytes, 0, credential.CredentialBlob, passwordBytes.Length);

                if (!CredWrite(ref credential, 0))
                {
                    var error = Marshal.GetLastWin32Error();
                    _logger.LogError("Failed to save credentials, error code: {ErrorCode}", error);
                    throw new InvalidOperationException(
                        $"Failed to save credentials to Windows Credential Manager. Error: {error}"
                    );
                }

                _logger.LogInformation("Credentials saved successfully");
            }
            finally
            {
                Marshal.FreeHGlobal(credential.CredentialBlob);
            }

            return Task.CompletedTask;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            _logger.LogError(ex, "Error saving credentials to Windows Credential Manager");
            throw;
        }
    }

    /// <inheritdoc />
    public Task DeleteCredentialsAsync()
    {
        try
        {
            if (!CredDelete(CredentialTargetName, CredentialType.Generic, 0))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ErrorNotFound)
                {
                    _logger.LogWarning(
                        "Failed to delete credentials, error code: {ErrorCode}",
                        error
                    );
                }
            }
            else
            {
                _logger.LogInformation("Credentials deleted successfully");
            }

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting credentials from Windows Credential Manager");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<bool> HasCredentialsAsync()
    {
        var credentials = await GetCredentialsAsync();
        return credentials is not null;
    }

    #region Windows Credential Manager P/Invoke

    private const int ErrorNotFound = 1168;

    private enum CredentialType : uint
    {
        Generic = 1,
    }

    private enum CredentialPersistence : uint
    {
        Session = 1,
        LocalMachine = 2,
        Enterprise = 3,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public CredentialType Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public CredentialPersistence Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    [DllImport(
        "advapi32.dll",
        EntryPoint = "CredReadW",
        CharSet = CharSet.Unicode,
        SetLastError = true
    )]
    private static extern bool CredRead(
        string target,
        CredentialType type,
        uint reservedFlag,
        out IntPtr credentialPtr
    );

    [DllImport(
        "advapi32.dll",
        EntryPoint = "CredWriteW",
        CharSet = CharSet.Unicode,
        SetLastError = true
    )]
    private static extern bool CredWrite([In] ref CREDENTIAL credential, uint flags);

    [DllImport(
        "advapi32.dll",
        EntryPoint = "CredDeleteW",
        CharSet = CharSet.Unicode,
        SetLastError = true
    )]
    private static extern bool CredDelete(string target, CredentialType type, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredFree", SetLastError = true)]
    private static extern void CredFree(IntPtr cred);

    #endregion
}
