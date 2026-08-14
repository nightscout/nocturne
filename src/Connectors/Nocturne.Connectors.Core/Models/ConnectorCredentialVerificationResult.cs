namespace Nocturne.Connectors.Core.Models;

/// <summary>
///     The outcome of a connector credential verification attempt. Carries whether the connector
///     supports verification at all, whether the provider accepted the credentials, and a
///     human-readable message that never contains the submitted values.
/// </summary>
public class ConnectorCredentialVerificationResult
{
    /// <summary>Whether the connector supports credential verification.</summary>
    public bool Supported { get; init; }

    /// <summary>Whether the provider accepted the credentials.</summary>
    public bool Success { get; init; }

    /// <summary>
    ///     A human-readable explanation of the outcome. Never contains the submitted credentials.
    /// </summary>
    public string? Message { get; init; }

    public static ConnectorCredentialVerificationResult NotSupported() => new()
    {
        Supported = false,
        Success = false,
        Message = "Credential verification is not supported for this connector",
    };

    public static ConnectorCredentialVerificationResult Verified() => new()
    {
        Supported = true,
        Success = true,
    };

    public static ConnectorCredentialVerificationResult Failed(string message) => new()
    {
        Supported = true,
        Success = false,
        Message = message,
    };
}
