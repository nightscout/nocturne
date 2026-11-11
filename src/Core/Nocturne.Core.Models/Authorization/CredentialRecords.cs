namespace Nocturne.Core.Models.Authorization;

/// <summary>A passkey to store, as the authenticator returned it.</summary>
public sealed record NewPasskeyCredential(
    Guid SubjectId,
    byte[] CredentialId,
    byte[] PublicKey,
    uint SignCount,
    IReadOnlyList<string> Transports,
    string? Label,
    Guid? AaGuid,
    DateTime CreatedAt);

/// <summary>A stored passkey. The public key is the authenticator's public half, not a secret.</summary>
public sealed record PasskeyCredentialRecord(
    Guid Id,
    Guid SubjectId,
    byte[] CredentialId,
    byte[] PublicKey,
    uint SignCount,
    IReadOnlyList<string> Transports,
    string? Label,
    DateTime CreatedAt,
    DateTime? LastUsedAt,
    Guid? AaGuid);

/// <summary>A stored passkey with the subject that holds it.</summary>
public sealed record StoredPasskey(PasskeyCredentialRecord Credential, SubjectRecord Holder);

/// <summary>A subject with every passkey it holds.</summary>
public sealed record PasskeyHolder(SubjectRecord Subject, IReadOnlyList<PasskeyCredentialRecord> Credentials);

/// <summary>
/// A TOTP credential to store. <see cref="SecretKey"/> is the shared secret in clear; the store
/// encrypts it at rest and nothing logs it.
/// </summary>
/// <param name="LastUsedStep">The time step the code that proved setup consumed, so it cannot also sign in.</param>
public sealed record NewTotpCredential(
    Guid SubjectId,
    byte[] SecretKey,
    string? Label,
    DateTime CreatedAt,
    long? LastUsedStep,
    DateTime? LastUsedAt);

/// <summary>What verifying a code needs of a TOTP credential: the secret in clear and the last step consumed.</summary>
public sealed record TotpCredentialSecret(Guid Id, byte[] SecretKey, long? LastUsedStep);

/// <summary>A TOTP credential as listed to its holder; the secret stays in the store.</summary>
public sealed record TotpCredentialSummary(Guid Id, string? Label, DateTime CreatedAt, DateTime? LastUsedAt);

/// <summary>A live recovery code's row and the self-describing hash it is verified against.</summary>
public sealed record RecoveryCodeHash(Guid Id, string CodeHash);
