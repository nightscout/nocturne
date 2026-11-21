using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// The one predicate for the subjects counted as the instance's platform admins: active, holding
/// platform admin, and not a system subject.
/// </summary>
/// <remarks>
/// A counted admin always satisfies <see cref="SubjectSignInState.IsActivePlatformAdmin"/>, which
/// decides what a credential carries. The count also leaves out system subjects, which have no
/// person behind them to administer the instance.
/// </remarks>
public static class PlatformAdminFilter
{
    internal const int LockClass = 0x504C_4144;

    /// <summary>The active, non-system subjects that hold platform admin.</summary>
    public static IQueryable<SubjectEntity> ActivePlatformAdmins(this IQueryable<SubjectEntity> subjects) =>
        subjects.Where(s => s.IsPlatformAdmin && s.IsActive && !s.IsSystemSubject);

    /// <summary>
    /// Takes the instance's platform-admin lock, then answers whether <paramref name="subjectId"/> is
    /// the only counted admin. The lock holds until the transaction ends, so two changes that would
    /// each leave one admin cannot both pass.
    /// </summary>
    /// <exception cref="InvalidOperationException">No transaction is open on the context.</exception>
    internal static async Task<bool> IsLastActivePlatformAdminAsync(
        this NocturneDbContext db, Guid subjectId, CancellationToken ct)
    {
        await db.LockForTransactionAsync(LockClass, Guid.Empty, ct);
        var admins = await db.Subjects.ActivePlatformAdmins().Select(s => s.Id).Take(2).ToListAsync(ct);
        return admins is [var only] && only == subjectId;
    }
}
