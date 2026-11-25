using Nocturne.Core.Models.Authorization;

namespace Nocturne.Core.Contracts.Auth;

/// <summary>
/// Stores the devices a subject trusted to sign in without an authenticator code. A device belongs
/// to a subject, not a tenant, so every method runs as the scope's reader with no pin. Rows are
/// returned whether or not they have expired; <see cref="ITrustedDeviceService"/> decides liveness.
/// </summary>
public interface ITrustedDeviceRepository
{
    /// <summary>Stores a device and returns its id.</summary>
    Task<Guid> CreateAsync(NewTrustedDevice device, CancellationToken ct);

    /// <summary>The device whose cookie token hashes to <paramref name="tokenHash"/>, or null.</summary>
    Task<TrustedDeviceRecord?> FindByTokenHashAsync(string tokenHash, CancellationToken ct);

    /// <summary>Records that device <paramref name="deviceId"/> stood in for a code at <paramref name="now"/>.</summary>
    Task MarkUsedAsync(Guid deviceId, DateTime now, CancellationToken ct);

    /// <summary>The subject's devices, most recently trusted first.</summary>
    Task<IReadOnlyList<TrustedDeviceRecord>> ListAsync(Guid subjectId, CancellationToken ct);

    /// <summary>Removes device <paramref name="deviceId"/> if <paramref name="subjectId"/> holds it.</summary>
    Task<bool> RemoveAsync(Guid deviceId, Guid subjectId, CancellationToken ct);

    /// <summary>
    /// Removes every device the subject holds, except the one whose token hashes to
    /// <paramref name="keepTokenHash"/> when that is given, and returns how many were removed.
    /// </summary>
    Task<int> RemoveAllAsync(Guid subjectId, string? keepTokenHash, CancellationToken ct);
}
