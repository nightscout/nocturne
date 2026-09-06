using Nocturne.Connectors.Core.Utilities;

namespace Nocturne.API.Authorization;

/// <summary>
/// Turns a credential's stored digest into a stable identifier for the audit trail.
/// </summary>
/// <remarks>
/// A digest is comparison material: for some credentials it is exactly what the caller presents on
/// the wire, and for the rest it is what a lookup matches on. Recording it, or any prefix of it,
/// hands a reader of the audit trail something they can present or match. The identifier here is a
/// second SHA-256 over that digest under a per-credential-family domain constant, truncated: it
/// cannot be replayed as the credential, and recovering the digest from it means inverting SHA-256.
/// Two different credentials yield two different values, so the trail can still tell them apart.
/// </remarks>
public static class AuditFingerprint
{
    /// <summary>Domain constant for the instance key. See <see cref="InstanceKeyDigest"/>.</summary>
    public const string InstanceKeyDomain = "nocturne/instance-key-audit-fingerprint/";

    /// <summary>Domain constant for an API secret's grant hash.</summary>
    public const string ApiSecretDomain = "nocturne/api-secret-audit-fingerprint/";

    /// <summary>Characters of the second digest that are kept.</summary>
    public const int Length = 16;

    /// <summary>
    /// Returns the fingerprint of <paramref name="digest"/> under <paramref name="domain"/>, or
    /// null when there is no digest to fingerprint.
    /// </summary>
    public static string? Of(string domain, string? digest) =>
        string.IsNullOrEmpty(digest) ? null : HashUtils.Sha256Hex(domain + digest)[..Length];
}
