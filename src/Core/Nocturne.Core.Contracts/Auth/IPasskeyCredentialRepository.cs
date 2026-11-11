using Nocturne.Core.Models.Authorization;

namespace Nocturne.Core.Contracts.Auth;

/// <summary>
/// Stores passkeys: the WebAuthn credentials a subject signs in with. A passkey belongs to a
/// subject, not a tenant, so every method runs as the scope's reader with no pin.
/// </summary>
public interface IPasskeyCredentialRepository
{
    /// <summary>Stores a passkey and returns its id.</summary>
    Task<Guid> CreateAsync(NewPasskeyCredential credential, CancellationToken ct);

    /// <summary>The passkey whose authenticator id is <paramref name="credentialId"/>, with its holder; null when there is none.</summary>
    Task<StoredPasskey?> FindByCredentialIdAsync(byte[] credentialId, CancellationToken ct);

    /// <summary>Whether any passkey has the authenticator id <paramref name="credentialId"/>.</summary>
    Task<bool> ExistsAsync(byte[] credentialId, CancellationToken ct);

    /// <summary>The authenticator ids of the subject's passkeys.</summary>
    Task<IReadOnlyList<byte[]>> GetCredentialIdsAsync(Guid subjectId, CancellationToken ct);

    /// <summary>How many passkeys the subject holds.</summary>
    Task<int> CountAsync(Guid subjectId, CancellationToken ct);

    /// <summary>The subject's passkeys, newest first.</summary>
    Task<IReadOnlyList<PasskeyCredentialRecord>> ListAsync(Guid subjectId, CancellationToken ct);

    /// <summary>Every active, non-system subject holding a passkey, with their passkeys.</summary>
    Task<IReadOnlyList<PasskeyHolder>> ListHoldersAsync(CancellationToken ct);

    /// <summary>
    /// Records an assertion with passkey <paramref name="credentialId"/> at <paramref name="now"/>:
    /// the authenticator's new signature counter and the time. False when there is no such passkey.
    /// </summary>
    Task<bool> RecordUseAsync(Guid credentialId, uint signCount, DateTime now, CancellationToken ct);

    /// <summary>Removes passkey <paramref name="credentialId"/> if <paramref name="subjectId"/> holds it.</summary>
    Task<bool> RemoveAsync(Guid credentialId, Guid subjectId, CancellationToken ct);
}
