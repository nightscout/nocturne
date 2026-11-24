using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// The first credential store claims the setup: the store of the setup owner's first credential
/// (a passkey, a provider identity, or an adoption in its place) succeeds only while the tenant's
/// setup owner record (<c>tenants.setup_owner_subject_id</c>) still names the subject being
/// completed, and ends that record in the same transaction.
/// </summary>
/// <remarks>
/// The tenant's setup lock (<see cref="AdvisoryLockExtensions"/>, class <see cref="Class"/> keyed by
/// the tenant id) is taken before the record is read and held to the commit, so of two completions
/// for one tenant the second reads the record after the first has committed, finds it ended, and
/// stores nothing. The lock is held only for the store's own transaction, after the ceremony or the
/// provider has proved the credential, so it never waits on a network call. A store that fails or
/// is abandoned rolls back with its claim, and the record still names the subject.
/// <para>
/// <c>tenants</c>, <c>passkey_credentials</c> and <c>subject_oidc_identities</c> carry no tenant and
/// no row level security, so a context that reads as the setup request's scope (no tenant) reads
/// and writes them all.
/// </para>
/// </remarks>
internal static class SetupOwnerClaim
{
    internal const int Class = 0x4E53_4F43;

    /// <summary>
    /// Takes the tenant's setup lock and, when the record names <paramref name="subjectId"/>, ends
    /// it on the context (saved by the caller's commit). False, with nothing changed, when it does not.
    /// </summary>
    /// <exception cref="InvalidOperationException">No transaction is open on the context.</exception>
    internal static async Task<bool> ClaimSetupAsync(
        this NocturneDbContext db, Guid tenantId, Guid subjectId, CancellationToken ct)
    {
        if (db.Database.IsRelational() && db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("The setup is claimed inside a transaction");

        await db.LockForTransactionAsync(Class, tenantId, ct);
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant?.SetupOwnerSubjectId is not { } recorded || recorded != subjectId)
            return false;

        tenant.SetupOwnerSubjectId = null;
        return true;
    }

    /// <summary>
    /// Claims the setup for <paramref name="subjectId"/> and stores what <paramref name="stage"/>
    /// adds, on one context in one transaction, and returns the id it gives; null, with nothing
    /// stored, when the claim is lost.
    /// </summary>
    /// <param name="contexts">Opens the context, as the scope's reader.</param>
    /// <param name="tenantId">The tenant being set up.</param>
    /// <param name="subjectId">The subject the store completes setup as.</param>
    /// <param name="stage">Adds the credential to the context and returns its id.</param>
    /// <param name="stored">Whether the credential with that id exists, for a commit that reported failure.</param>
    /// <param name="ct">The cancellation token.</param>
    internal static async Task<Guid?> StoreClaimingSetupAsync(
        INocturneDbContextFactory contexts,
        Guid tenantId,
        Guid subjectId,
        Func<NocturneDbContext, Guid> stage,
        Func<NocturneDbContext, Guid, CancellationToken, Task<bool>> stored,
        CancellationToken ct)
    {
        await using var db = await contexts.CreateAsync(ct);
        return await db.ExecuteInTransactionAsync<Guid?>(
            async token =>
            {
                if (!await db.ClaimSetupAsync(tenantId, subjectId, token))
                    return null;

                var id = stage(db);
                await db.SaveChangesAsync(token);
                return id;
            },
            verifySucceeded: (id, token) => id is { } stagedId ? stored(db, stagedId, token) : Task.FromResult(true),
            ct: ct);
    }
}
