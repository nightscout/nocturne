using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.EntityConfigurations;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary><see cref="IPeopleViewRepository"/>, one context per call, each pinned to the caller's subject.</summary>
public sealed class PeopleViewRepository(INocturneDbContextFactory contexts) : IPeopleViewRepository
{
    /// <summary>
    /// The advisory lock class every write holds per subject from its first read to its commit
    /// (<see cref="AdvisoryLockExtensions"/>), so the count, the name and the order it read still hold when it commits.
    /// </summary>
    internal const int SubjectLockClass = 0x4E50_5657;

    private static readonly JsonSerializerOptions QueryJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<IReadOnlyList<PeopleView>> GetOfSubjectAsync(Guid subjectId, CancellationToken ct)
    {
        await using var db = await contexts.CreateForSubjectAsync(subjectId, ct);
        var rows = await InOrder(db.PeopleViews.AsNoTracking().Where(v => v.SubjectId == subjectId)).ToListAsync(ct);
        return [.. rows.Select(ToModel)];
    }

    public async Task<PeopleViewWrite> CreateAsync(Guid subjectId, PeopleViewDraft draft, CancellationToken ct)
    {
        await using var db = await contexts.CreateForSubjectAsync(subjectId, ct);
        return await NameTakenOnClashAsync(db.ExecuteInTransactionAsync(async token =>
        {
            await db.LockForTransactionAsync(SubjectLockClass, subjectId, token);
            var positions = await db.PeopleViews.Where(v => v.SubjectId == subjectId).Select(v => v.Position).ToListAsync(token);
            if (positions.Count >= PeopleViewLimits.PerViewer)
                return new PeopleViewWrite(PeopleViewChange.LimitReached, null);
            if (await NameTakenAsync(db, subjectId, null, draft.Name, token))
                return new PeopleViewWrite(PeopleViewChange.NameTaken, null);

            var row = new PeopleViewEntity
            {
                Id = Guid.CreateVersion7(),
                SubjectId = subjectId,
                Position = positions.Count == 0 ? 0 : positions.Max() + 1,
            };
            Apply(row, draft);
            db.PeopleViews.Add(row);
            await db.SaveChangesAsync(token);
            return new PeopleViewWrite(PeopleViewChange.Changed, ToModel(row));
        }, ct: ct));
    }

    public async Task<PeopleViewWrite> UpdateAsync(Guid subjectId, Guid viewId, PeopleViewDraft draft, CancellationToken ct)
    {
        await using var db = await contexts.CreateForSubjectAsync(subjectId, ct);
        return await NameTakenOnClashAsync(db.ExecuteInTransactionAsync(async token =>
        {
            await db.LockForTransactionAsync(SubjectLockClass, subjectId, token);
            var row = await db.PeopleViews.FirstOrDefaultAsync(v => v.Id == viewId && v.SubjectId == subjectId, token);
            if (row is null)
                return new PeopleViewWrite(PeopleViewChange.NotFound, null);
            if (await NameTakenAsync(db, subjectId, viewId, draft.Name, token))
                return new PeopleViewWrite(PeopleViewChange.NameTaken, null);

            Apply(row, draft);
            await db.SaveChangesAsync(token);
            return new PeopleViewWrite(PeopleViewChange.Changed, ToModel(row));
        }, ct: ct));
    }

    public async Task<bool> DeleteAsync(Guid subjectId, Guid viewId, CancellationToken ct)
    {
        await using var db = await contexts.CreateForSubjectAsync(subjectId, ct);
        return await db.ExecuteInTransactionAsync(async token =>
        {
            await db.LockForTransactionAsync(SubjectLockClass, subjectId, token);
            var row = await db.PeopleViews.FirstOrDefaultAsync(v => v.Id == viewId && v.SubjectId == subjectId, token);
            if (row is null)
                return false;

            db.PeopleViews.Remove(row);
            await db.SaveChangesAsync(token);
            return true;
        }, ct: ct);
    }

    public async Task<IReadOnlyList<PeopleView>?> ReorderAsync(Guid subjectId, IReadOnlyList<Guid> order, CancellationToken ct)
    {
        await using var db = await contexts.CreateForSubjectAsync(subjectId, ct);
        return await db.ExecuteInTransactionAsync<IReadOnlyList<PeopleView>?>(async token =>
        {
            await db.LockForTransactionAsync(SubjectLockClass, subjectId, token);
            var rows = await db.PeopleViews.Where(v => v.SubjectId == subjectId).ToListAsync(token);
            var positions = order.Select((id, position) => (id, position)).DistinctBy(p => p.id).ToDictionary(p => p.id, p => p.position);
            if (order.Count != rows.Count || positions.Count != order.Count || !rows.All(r => positions.ContainsKey(r.Id)))
                return null;

            foreach (var row in rows)
                row.Position = positions[row.Id];
            await db.SaveChangesAsync(token);
            return [.. InOrder(rows.AsQueryable()).Select(ToModel)];
        }, ct: ct);
    }

    /// <summary>
    /// Whether another view of the subject holds <paramref name="name"/> ignoring case, by the database's own
    /// <c>lower()</c>, the one the stored <c>name_key</c> and its unique index use, so this check and the index agree.
    /// </summary>
    private static Task<bool> NameTakenAsync(NocturneDbContext db, Guid subjectId, Guid? viewId, string name, CancellationToken ct)
    {
        var other = viewId ?? Guid.Empty;
        return db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (
                SELECT 1 FROM people_views WHERE subject_id = {subjectId} AND id <> {other} AND name_key = lower({name})
            ) AS "Value"
            """).SingleAsync(ct);
    }

    /// <summary>
    /// The write, or <see cref="PeopleViewChange.NameTaken"/> when the unique index refuses its name: a clash the
    /// check under the lock did not see, as a view another writer committed without the lock. Caught outside the
    /// transaction, which a failed statement has aborted.
    /// </summary>
    private static async Task<PeopleViewWrite> NameTakenOnClashAsync(Task<PeopleViewWrite> write)
    {
        try
        {
            return await write;
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return new PeopleViewWrite(PeopleViewChange.NameTaken, null);
        }
    }

    private static IQueryable<PeopleViewEntity> InOrder(IQueryable<PeopleViewEntity> rows) =>
        rows.OrderBy(v => v.Position).ThenBy(v => v.CreatedAt).ThenBy(v => v.Id);

    private static void Apply(PeopleViewEntity row, PeopleViewDraft draft)
    {
        row.Name = draft.Name;
        row.Query = JsonSerializer.Serialize(draft.Query, QueryJson);
        row.Lens = PeopleViewColumns.Of(draft.Lens);
        row.ReviewDays = draft.ReviewDays;
    }

    private static PeopleView ToModel(PeopleViewEntity row) => new(
        row.Id,
        row.Name,
        row.Position,
        JsonSerializer.Deserialize<PeopleQuery>(row.Query, QueryJson) ?? PeopleQuery.Empty,
        PeopleViewColumns.LensOf(row.Lens),
        row.ReviewDays,
        row.CreatedAt,
        row.UpdatedAt);
}
