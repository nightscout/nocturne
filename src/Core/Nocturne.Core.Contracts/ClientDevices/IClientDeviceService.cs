using Nocturne.Core.Models.ClientDevices;

namespace Nocturne.Core.Contracts.ClientDevices;

/// <summary>
/// Manages the registry of client app installs (Prelude, Companion) that can be alert-engine
/// actuation targets. Tenant scoping is applied by the data context; callers pass the subject.
/// </summary>
/// <seealso cref="RegisterDeviceRequest"/>
/// <seealso cref="ClientDeviceDto"/>
public interface IClientDeviceService
{
    /// <summary>
    /// Idempotently register or refresh a device for the given subject, keyed on the request's
    /// install id. Advertised capabilities are filtered to those known, allowed for the kind, and
    /// covered by <paramref name="grantedScopes"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The kind is unknown or the install id is missing.</exception>
    Task<ClientDeviceDto> RegisterAsync(
        Guid subjectId,
        RegisterDeviceRequest request,
        IReadOnlySet<string> grantedScopes,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the devices registered by the given subject, most-recently-seen first.</summary>
    Task<IReadOnlyList<ClientDeviceDto>> GetForSubjectAsync(
        Guid subjectId,
        CancellationToken cancellationToken = default);
}
