using Microsoft.Extensions.Options;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Configuration;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Ensures at least one platform admin exists.
/// </summary>
/// <remarks>
/// <para>
/// Priority order:
/// <list type="number">
///   <item>If <c>Platform:AdminSubjectIds</c> is configured, those subjects are granted platform admin status.</item>
///   <item>Otherwise, if no platform admin exists, the oldest owner of the oldest tenant who can sign in is granted it.</item>
///   <item>Otherwise, if every platform admin is stranded (<see cref="ISubjectRepository.ReplaceStrandedPlatformAdminsAsync"/>) and such an owner exists, the flag moves from them to that owner.</item>
/// </list>
/// </para>
/// <para>
/// <see cref="BootstrapAsync"/> runs at startup. On a fresh install it sees an empty
/// database and grants nothing, so the setup flow calls
/// <see cref="EnsureFirstOwnerIsPlatformAdminAsync"/> once the first owner has bound a
/// credential — otherwise that owner could not reach the admin UI until the API restarted.
/// </para>
/// </remarks>
public class PlatformAdminBootstrapService
{
    private readonly ITenantRepository _tenants;
    private readonly ITenantMembershipRepository _members;
    private readonly ISubjectRepository _subjects;
    private readonly PlatformOptions _options;
    private readonly ILogger<PlatformAdminBootstrapService> _logger;

    /// <param name="tenants">The tenants walked oldest-first for an owner.</param>
    /// <param name="members">The memberships read under each tenant's own pin, so the bootstrap is
    /// independent of any tenant pinned on a request-scoped context.</param>
    /// <param name="subjects">The subjects the flag is read from and granted on, on a platform context.</param>
    /// <param name="options">Platform configuration options, including <c>AdminSubjectIds</c>.</param>
    /// <param name="logger">Logs each grant, and each grant declined or left unmade.</param>
    public PlatformAdminBootstrapService(
        ITenantRepository tenants,
        ITenantMembershipRepository members,
        ISubjectRepository subjects,
        IOptions<PlatformOptions> options,
        ILogger<PlatformAdminBootstrapService> logger)
    {
        _tenants = tenants;
        _members = members;
        _subjects = subjects;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Grants platform admin status according to the configured priority rules.
    /// </summary>
    public async Task BootstrapAsync(CancellationToken cancellationToken)
    {
        // Option 1: explicit config takes precedence
        if (_options.AdminSubjectIds.Count > 0)
        {
            var configGrants = await _subjects.GrantPlatformAdminAsync(_options.AdminSubjectIds, cancellationToken);

            // Config wins outright, so a set of IDs that matches nothing leaves the
            // instance with no platform admin and no way into the admin UI.
            if (configGrants == 0)
                _logger.LogWarning(
                    "Platform:AdminSubjectIds lists {ConfiguredCount} subject ID(s), none of which " +
                    "matched an existing subject. No platform admin was granted.",
                    _options.AdminSubjectIds.Count);

            return;
        }

        if (await _subjects.AnyPlatformAdminAsync(cancellationToken))
        {
            if (await _subjects.EveryPlatformAdminIsStrandedAsync(cancellationToken)
                && await FindOldestTenantOwnerWhoCanSignInAsync(cancellationToken) is { } successor)
                await ReplaceStrandedAdminsAsync(successor, "the owner of the oldest tenant who can sign in", cancellationToken);
            return;
        }

        var firstOwnerSubjectId = await FindOldestTenantOwnerWhoCanSignInAsync(cancellationToken);
        if (firstOwnerSubjectId is null)
        {
            _logger.LogInformation(
                "No platform admin exists and no tenant owner who can sign in was found, so none was granted");
            return;
        }

        await _subjects.GrantPlatformAdminAsync([firstOwnerSubjectId.Value], cancellationToken);

        _logger.LogInformation(
            "Granted platform admin to subject {SubjectId}, the owner of the oldest tenant who can sign in",
            firstOwnerSubjectId);
    }

    private async Task<bool> ReplaceStrandedAdminsAsync(
        Guid successorId, string successorRole, CancellationToken cancellationToken)
    {
        var cleared = await _subjects.ReplaceStrandedPlatformAdminsAsync(successorId, cancellationToken);
        if (cleared.Count == 0)
            return false;

        _logger.LogWarning(
            "Every platform admin ({ClearedSubjectIds}) could not sign in, so the flag was cleared from " +
            "them and granted to subject {SuccessorId}, {SuccessorRole}",
            cleared, successorId, successorRole);
        return true;
    }

    /// <summary>
    /// The oldest owner of the oldest tenant that has one who can sign in, or <see langword="null"/>
    /// when no tenant does. Resolved by walking tenants oldest-first and looking the owners up under
    /// each tenant's own pin, because a single query over every tenant's memberships has no tenant to
    /// be pinned to.
    /// </summary>
    private async Task<Guid?> FindOldestTenantOwnerWhoCanSignInAsync(CancellationToken cancellationToken)
    {
        foreach (var tenantId in await _tenants.ListIdsAsync(activeOnly: false, cancellationToken))
        {
            var owners = await _members.GetOwnerSubjectIdsWhoCanSignInAsync(tenantId, cancellationToken);
            if (owners.Count > 0) return owners[0];
        }

        return null;
    }

    /// <summary>
    /// Grants platform admin to the subject that has just completed first-owner setup,
    /// so a fresh install is administrable without restarting the API.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Declines when the operator pinned the choice through <c>Platform:AdminSubjectIds</c>.
    /// When the instance already has a platform admin, the flag moves to this owner only if every
    /// admin is stranded (<see cref="ISubjectRepository.ReplaceStrandedPlatformAdminsAsync"/>), as
    /// a setup step abandoned before an earlier restart leaves one: an established instance must
    /// not hand the flag to whoever runs setup.
    /// </para>
    /// <para>
    /// The caller names the subject, but this method re-derives whether that subject is
    /// eligible rather than taking its word: the instance must still be a single-tenant
    /// install and the subject must be an owner of that tenant. A privilege grant
    /// shouldn't depend on every present and future call site having checked setup state
    /// first, and the setup OIDC callback in particular is reachable anonymously.
    /// </para>
    /// <para>
    /// Single-tenant is counted the way setup counts it (<see cref="Nocturne.Infrastructure.Data.Extensions.DemoExclusionFilter"/>): an
    /// instance serving a demo alongside the operator's one tenant is still a fresh install.
    /// </para>
    /// </remarks>
    /// <param name="subjectId">The owner subject that just bound a credential.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if the flag was granted.</returns>
    public async Task<bool> EnsureFirstOwnerIsPlatformAdminAsync(
        Guid subjectId, CancellationToken cancellationToken)
    {
        if (_options.AdminSubjectIds.Count > 0)
            return false;

        // More than one tenant means this is not a fresh install, whatever the caller thinks.
        var tenants = await _tenants.ListNonDemoAsync(2, cancellationToken);
        if (tenants.Count != 1)
            return false;

        // The membership read runs under the sole tenant's pin: tenant_members is slated for
        // RLS policies keyed on the tenant GUC, and an unpinned read would then see no rows
        // and silently decline the grant — reintroducing the fresh-install lockout.
        var isTenantOwner = (await _members.GetOwnerSubjectIdsAsync(tenants[0].Id, cancellationToken)).Contains(subjectId);

        if (!isTenantOwner)
        {
            _logger.LogWarning(
                "Declined to grant platform admin to subject {SubjectId}: it does not hold the " +
                "owner role on the sole tenant", subjectId);
            return false;
        }

        if (await _subjects.AnyPlatformAdminAsync(cancellationToken))
            return await ReplaceStrandedAdminsAsync(subjectId, "the owner completing setup", cancellationToken);

        var granted = await _subjects.GrantPlatformAdminAsync([subjectId], cancellationToken) > 0;

        if (granted)
            _logger.LogInformation(
                "Granted platform admin to first owner {SubjectId} on setup completion", subjectId);

        return granted;
    }
}
