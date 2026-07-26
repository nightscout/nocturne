namespace Nocturne.Core.Contracts.Identity;

/// <summary>
/// Resolves the subject a tenant's background work should be attributed to.
/// </summary>
/// <remarks>
/// In-app notifications are keyed by subject id, not tenant id, so anything raised outside a request
/// — a connector sync, a detection pass — has no user of its own and must borrow one. A placeholder
/// subject does not work: the UI lists notifications for the signed-in subject, so one filed under
/// any other value is still created and still counted against the per-source rate limit.
/// </remarks>
public interface ITenantOwnerResolver
{
    /// <summary>
    /// Returns the subject id of the tenant's owner, or <c>null</c> when the tenant has none.
    /// </summary>
    /// <param name="tenantId">The tenant whose owner is wanted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<string?> GetOwnerSubjectIdAsync(Guid tenantId, CancellationToken cancellationToken = default);
}
