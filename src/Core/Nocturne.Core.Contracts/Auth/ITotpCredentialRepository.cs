using Nocturne.Core.Models.Authorization;

namespace Nocturne.Core.Contracts.Auth;

/// <summary>
/// Stores TOTP credentials: the authenticator-app secrets a subject proves a second factor with.
/// The secret is encrypted at rest by the store and read back in clear only by
/// <see cref="ListSecretsAsync"/>. A credential belongs to a subject, not a tenant, so every
/// method runs as the scope's reader with no pin.
/// </summary>
public interface ITotpCredentialRepository
{
    /// <summary>Stores a credential and returns its id.</summary>
    Task<Guid> CreateAsync(NewTotpCredential credential, CancellationToken ct);

    /// <summary>The subject's credentials as listed to them, newest first; the secret is not read.</summary>
    Task<IReadOnlyList<TotpCredentialSummary>> ListAsync(Guid subjectId, CancellationToken ct);

    /// <summary>
    /// The subject's credentials with their secrets in clear, for verifying a code.
    /// </summary>
    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// A stored secret could not be decrypted. The whole list fails, since the secrets are
    /// decrypted as the rows are read; <see cref="RemoveAsync"/> does not read them, so an
    /// unreadable credential can still be removed.
    /// </exception>
    Task<IReadOnlyList<TotpCredentialSecret>> ListSecretsAsync(Guid subjectId, CancellationToken ct);

    /// <summary>
    /// Consumes time step <paramref name="step"/> on credential <paramref name="credentialId"/> at
    /// <paramref name="now"/>, in one statement conditioned on the step not having been taken, so
    /// two requests presenting the same code cannot both succeed. False when another request took it.
    /// </summary>
    Task<bool> TryConsumeStepAsync(Guid credentialId, long step, DateTime now, CancellationToken ct);

    /// <summary>Removes credential <paramref name="credentialId"/> if <paramref name="subjectId"/> holds it, without reading its secret.</summary>
    Task<bool> RemoveAsync(Guid credentialId, Guid subjectId, CancellationToken ct);

    /// <summary>How many credentials the subject holds.</summary>
    Task<int> CountAsync(Guid subjectId, CancellationToken ct);
}
