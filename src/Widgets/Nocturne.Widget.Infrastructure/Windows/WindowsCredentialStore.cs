using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nocturne.Widget.Contracts;

namespace Nocturne.Widget.Infrastructure.Windows;

/// <summary>
/// Windows Credential Manager implementation for secure credential storage
/// </summary>
public class WindowsCredentialStore : ICredentialStore
{
    private const string Default_credentialTargetName = "Nocturne.Widget.OAuth";
    private const string Default_deviceAuthTargetName = "Nocturne.Widget.DeviceAuth";

    private readonly string _credentialTargetName;
    private readonly string _deviceAuthTargetName;
    private readonly ILogger<WindowsCredentialStore> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>
    /// Internal storage format for credentials
    /// </summary>
    private record CredentialData
    {
        public string ApiUrl { get; init; } = string.Empty;
        public string AccessToken { get; init; } = string.Empty;
        public string RefreshToken { get; init; } = string.Empty;
        public long ExpiresAtUnix { get; init; }
        public List<string> Scopes { get; init; } = new();
    }

    /// <summary>
    /// Internal storage format for device auth state
    /// </summary>
    private record DeviceAuthData
    {
        public string ApiUrl { get; init; } = string.Empty;
        public string DeviceCode { get; init; } = string.Empty;
        public string UserCode { get; init; } = string.Empty;
        public string VerificationUri { get; init; } = string.Empty;
        public string? VerificationUriComplete { get; init; }
        public long ExpiresAtUnix { get; init; }
        public int Interval { get; init; }
    }

    /// <summary>
    /// Initializes a new instance of the WindowsCredentialStore with default target names
    /// </summary>
    /// <param name="logger">The logger instance</param>
    public WindowsCredentialStore(ILogger<WindowsCredentialStore> logger)
        : this(logger, Default_credentialTargetName, Default_deviceAuthTargetName)
    {
    }

    /// <summary>
    /// Initializes a new instance of the WindowsCredentialStore with custom target names.
    /// Use this to isolate credentials between different native clients (e.g. widget vs tray).
    /// </summary>
    public WindowsCredentialStore(ILogger<WindowsCredentialStore> logger, string credentialTargetName, string deviceAuthTargetName)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _credentialTargetName = credentialTargetName;
        _deviceAuthTargetName = deviceAuthTargetName;
    }

    /// <inheritdoc />
    public Task<NocturneCredentials?> GetCredentialsAsync()
    {
        try
        {
            var json = ReadCredential(_credentialTargetName);
            if (json is null)
            {
                return Task.FromResult<NocturneCredentials?>(null);
            }

            var data = JsonSerializer.Deserialize<CredentialData>(json, JsonOptions);
            if (data is null)
            {
                _logger.LogWarning("Failed to deserialize credentials");
                return Task.FromResult<NocturneCredentials?>(null);
            }

            var credentials = new NocturneCredentials
            {
                ApiUrl = data.ApiUrl,
                AccessToken = data.AccessToken,
                RefreshToken = data.RefreshToken,
                ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(data.ExpiresAtUnix),
                Scopes = data.Scopes.AsReadOnly(),
            };

            return Task.FromResult<NocturneCredentials?>(credentials);
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
            var data = new CredentialData
            {
                ApiUrl = credentials.ApiUrl,
                AccessToken = credentials.AccessToken,
                RefreshToken = credentials.RefreshToken,
                ExpiresAtUnix = credentials.ExpiresAt.ToUnixTimeSeconds(),
                Scopes = credentials.Scopes.ToList(),
            };

            var json = JsonSerializer.Serialize(data, JsonOptions);
            WriteCredential(_credentialTargetName, json);
            _logger.LogInformation("OAuth credentials saved successfully");

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving credentials to Windows Credential Manager");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task UpdateTokensAsync(string accessToken, string? refreshToken, int expiresIn)
    {
        var existing = await GetCredentialsAsync();
        if (existing is null)
        {
            throw new InvalidOperationException("No existing credentials to update");
        }

        var updated = new NocturneCredentials
        {
            ApiUrl = existing.ApiUrl,
            AccessToken = accessToken,
            RefreshToken = refreshToken ?? existing.RefreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
            Scopes = existing.Scopes,
        };

        await SaveCredentialsAsync(updated);
        _logger.LogInformation("OAuth tokens refreshed successfully");
    }

    /// <inheritdoc />
    public Task DeleteCredentialsAsync()
    {
        try
        {
            DeleteCredential(_credentialTargetName);
            _logger.LogInformation("Credentials deleted successfully");
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

    /// <inheritdoc />
    public Task SaveDeviceAuthStateAsync(DeviceAuthorizationState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        try
        {
            var data = new DeviceAuthData
            {
                ApiUrl = state.ApiUrl,
                DeviceCode = state.DeviceCode,
                UserCode = state.UserCode,
                VerificationUri = state.VerificationUri,
                VerificationUriComplete = state.VerificationUriComplete,
                ExpiresAtUnix = state.ExpiresAt.ToUnixTimeSeconds(),
                Interval = state.Interval,
            };

            var json = JsonSerializer.Serialize(data, JsonOptions);
            WriteCredential(_deviceAuthTargetName, json);
            _logger.LogDebug("Device auth state saved");

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving device auth state");
            throw;
        }
    }

    /// <inheritdoc />
    public Task<DeviceAuthorizationState?> GetDeviceAuthStateAsync()
    {
        try
        {
            var json = ReadCredential(_deviceAuthTargetName);
            if (json is null)
            {
                return Task.FromResult<DeviceAuthorizationState?>(null);
            }

            var data = JsonSerializer.Deserialize<DeviceAuthData>(json, JsonOptions);
            if (data is null)
            {
                return Task.FromResult<DeviceAuthorizationState?>(null);
            }

            var state = new DeviceAuthorizationState
            {
                ApiUrl = data.ApiUrl,
                DeviceCode = data.DeviceCode,
                UserCode = data.UserCode,
                VerificationUri = data.VerificationUri,
                VerificationUriComplete = data.VerificationUriComplete,
                ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(data.ExpiresAtUnix),
                Interval = data.Interval,
            };

            return Task.FromResult<DeviceAuthorizationState?>(state);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading device auth state");
            return Task.FromResult<DeviceAuthorizationState?>(null);
        }
    }

    /// <inheritdoc />
    public Task ClearDeviceAuthStateAsync()
    {
        try
        {
            DeleteCredential(_deviceAuthTargetName);
            _logger.LogDebug("Device auth state cleared");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing device auth state");
            throw;
        }
    }

    #region Windows Credential Manager Helpers

    private string? ReadCredential(string targetName)
    {
        if (!CredRead(targetName, CredentialType.Generic, 0, out var credentialPtr))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                _logger.LogDebug("No credential found for {TargetName}", targetName);
                return null;
            }

            _logger.LogWarning("Failed to read credential {TargetName}, error: {Error}", targetName, error);
            return null;
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
            return Encoding.Unicode.GetString(passwordBytes);
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    private void WriteCredential(string targetName, string data)
    {
        var dataBytes = Encoding.Unicode.GetBytes(data);

        var credential = new CREDENTIAL
        {
            Type = CredentialType.Generic,
            TargetName = targetName,
            CredentialBlobSize = (uint)dataBytes.Length,
            CredentialBlob = Marshal.AllocHGlobal(dataBytes.Length),
            Persist = CredentialPersistence.LocalMachine,
            UserName = "NocturneWidget",
        };

        try
        {
            Marshal.Copy(dataBytes, 0, credential.CredentialBlob, dataBytes.Length);

            if (!CredWrite(ref credential, 0))
            {
                var error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException(
                    $"Failed to write credential {targetName}. Error: {error}"
                );
            }
        }
        finally
        {
            Marshal.FreeHGlobal(credential.CredentialBlob);
        }
    }

    private void DeleteCredential(string targetName)
    {
        if (!CredDelete(targetName, CredentialType.Generic, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                _logger.LogWarning("Failed to delete credential {TargetName}, error: {Error}", targetName, error);
            }
        }
    }

    #endregion

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
