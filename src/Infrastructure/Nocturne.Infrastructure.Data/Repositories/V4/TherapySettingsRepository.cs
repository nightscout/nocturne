using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing therapy settings in the database. A LegacyId-only type (no SyncId-upsert,
/// no deduplication), so it uses the shared
/// <see cref="V4RepositoryBase{TModel,TEntity}"/> behaviour unchanged plus the therapy-settings-specific
/// queries below.
/// </summary>
public class TherapySettingsRepository : ProfileScopedRepositoryBase<TherapySettings, TherapySettingsEntity>, ITherapySettingsRepository
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TherapySettingsRepository"/> class.
    /// </summary>
    // logger is unused for this LegacyId-only type but retained for DI + direct test construction.
    public TherapySettingsRepository(INocturneDbContextFactory contextFactory, IAuditContext auditContext, ILogger<TherapySettingsRepository> logger, IV4RecordBroadcaster<TherapySettings>? broadcaster = null, ILegacyReadCacheInvalidation? legacyReads = null)
        : base(contextFactory, auditContext, logger, broadcaster, legacyReads)
    {
    }

    /// <inheritdoc />
    protected override TherapySettingsEntity ToEntity(TherapySettings model) => TherapySettingsMapper.ToEntity(model);

    /// <inheritdoc />
    protected override TherapySettings ToDomain(TherapySettingsEntity entity) => TherapySettingsMapper.ToDomainModel(entity);

    /// <inheritdoc />
    protected override void ApplyUpdate(TherapySettingsEntity target, TherapySettings source) =>
        TherapySettingsMapper.UpdateEntity(target, source);

    /// <inheritdoc />
    public async Task<IReadOnlyList<TherapySettings>> GetDefaultsAsync(CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx
            .TherapySettings.AsNoTracking()
            .Where(e => e.IsDefault)
            .OrderByDescending(e => e.Timestamp)
            .ToListAsync(ct);
        return entities.Select(TherapySettingsMapper.ToDomainModel).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProfileStoreRow>> GetStoreIndexAsync(CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await DocumentRows(ctx)
            .Select(d => new ProfileStoreRow(
                d.DocumentId, d.Row.LegacyId != null, d.Row.ProfileName, d.Row.Timestamp, d.Row.IsDefault,
                d.Row.DocumentDefaultProfile))
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<TherapySettings?> GetNewestDocumentRowAsync(CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entity = await ctx
            .TherapySettings.AsNoTracking()
            .OrderByDescending(e => e.Timestamp)
            .ThenByDescending(e => e.Id)
            .FirstOrDefaultAsync(ct);
        return entity is null ? null : TherapySettingsMapper.ToDomainModel(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetDocumentIdsAsync(CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await RankedDocumentIds(ctx).ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<long> CountDocumentsAsync(CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await DocumentRows(ctx).Select(d => d.DocumentId).Distinct().LongCountAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IReadOnlyList<TherapySettings>>> GetDocumentsAsync(
        int limit, int offset, bool descending, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var page = await RankedDocumentIds(ctx, descending).Skip(offset).Take(limit).ToListAsync(ct);

        var rows = (await DocumentRows(ctx).Where(d => page.Contains(d.DocumentId)).ToListAsync(ct))
            .ToLookup(d => d.DocumentId, d => TherapySettingsMapper.ToDomainModel(d.Row), StringComparer.Ordinal);

        return page
            .Where(rows.Contains)
            .Select(id => (IReadOnlyList<TherapySettings>)rows[id].ToList())
            .ToList();
    }

    /// <summary>
    /// The rows decomposed from profile documents, each with the id of its document computed in SQL
    /// as <see cref="TherapySettings.DocumentIdOf"/> computes it.
    /// </summary>
    private static IQueryable<DocumentRow> DocumentRows(NocturneDbContext ctx) =>
        ctx.TherapySettings.AsNoTracking()
            .Select(e => new DocumentRow
            {
                DocumentId = e.LegacyId == null
                    ? e.Id.ToString()
                    : e.LegacyId.Contains(":")
                        ? e.LegacyId.Substring(0, e.LegacyId.IndexOf(":"))
                        : e.LegacyId,
                Row = e,
            });

    /// <summary>
    /// The document ids, newest first as Nightscout sorts them: by the document's newest row, then by
    /// id descending, compared bytewise as <see cref="StringComparer.Ordinal"/> compares them. When
    /// <paramref name="descending"/> is <c>false</c>, the reverse of that order.
    /// </summary>
    private static IQueryable<string> RankedDocumentIds(NocturneDbContext ctx, bool descending = true)
    {
        var documents = DocumentRows(ctx)
            .GroupBy(d => d.DocumentId)
            .Select(g => new { DocumentId = g.Key, Newest = g.Max(d => d.Row.Timestamp) });

        var ordered = descending
            ? documents
                .OrderByDescending(d => d.Newest)
                .ThenByDescending(d => EF.Functions.Collate(d.DocumentId, "C"))
            : documents
                .OrderBy(d => d.Newest)
                .ThenBy(d => EF.Functions.Collate(d.DocumentId, "C"));

        return ordered.Select(d => d.DocumentId);
    }

    private sealed class DocumentRow
    {
        public required string DocumentId { get; init; }

        public required TherapySettingsEntity Row { get; init; }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TherapySettings>> GetDocumentRowsAsync(
        string documentId,
        CancellationToken ct = default
    )
    {
        var prefix = documentId + ":";
        Guid? rowId = Guid.TryParse(documentId, out var parsed) ? parsed : null;

        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx
            .TherapySettings.AsNoTracking()
            .Where(e => e.LegacyId != null
                ? e.LegacyId.StartsWith(prefix) || e.LegacyId == documentId
                : e.Id == rowId)
            .ToListAsync(ct);
        return entities.Select(TherapySettingsMapper.ToDomainModel).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<TherapySettings>>> GetDocumentRowsAsync(
        IReadOnlyCollection<string> documentIds,
        CancellationToken ct = default
    )
    {
        var ids = documentIds.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0)
            return new Dictionary<string, IReadOnlyList<TherapySettings>>();

        await using var ctx = await ContextFactory.CreateAsync(ct);
        var rows = await DocumentRows(ctx).Where(d => ids.Contains(d.DocumentId)).ToListAsync(ct);
        return rows
            .GroupBy(d => d.DocumentId, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<TherapySettings>)g.Select(d => TherapySettingsMapper.ToDomainModel(d.Row)).ToList(),
                StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public async Task SetDefaultAsync(Guid? id, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var affected = await ctx
            .TherapySettings
            .Where(e => e.IsDefault || (id != null && e.Id == id))
            .ToListAsync(ct);
        foreach (var entity in affected)
            entity.IsDefault = entity.Id == id;
        await ctx.SaveChangesAsync(ct);
        await ReportWrittenAsync();
    }
}
