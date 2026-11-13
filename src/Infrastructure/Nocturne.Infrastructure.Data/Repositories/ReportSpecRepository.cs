using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Reports;
using Nocturne.Core.Models.Reports;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>EF Core store for <see cref="ReportSpecEntity"/>, one context per operation, under the scope's reader.</summary>
public sealed class ReportSpecRepository(INocturneDbContextFactory contexts) : IReportSpecRepository
{
    public async Task<IReadOnlyList<SavedReportSpec>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateAsync(ct);
        var rows = await db.ReportSpecs.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ct);
        return [.. rows.Select(ToModel)];
    }

    public async Task<SavedReportSpec?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateAsync(ct);
        var row = await db.ReportSpecs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        return row is null ? null : ToModel(row);
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateAsync(ct);
        return await db.ReportSpecs.CountAsync(ct);
    }

    public async Task<SavedReportSpecWrite> CreateAsync(
        string name, string specJson, int version, Guid? createdBySubjectId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateAsync(ct);
        if (await db.ReportSpecs.AnyAsync(r => r.Name == name, ct))
            return new SavedReportSpecWrite(SavedReportSpecWriteStatus.NameTaken);

        var row = new ReportSpecEntity
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            Spec = specJson,
            Version = version,
            CreatedBySubjectId = createdBySubjectId,
        };
        db.ReportSpecs.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return new SavedReportSpecWrite(SavedReportSpecWriteStatus.NameTaken);
        }
        return new SavedReportSpecWrite(SavedReportSpecWriteStatus.Saved, ToModel(row));
    }

    public async Task<SavedReportSpecWrite> UpdateAsync(
        Guid id, string? name, string? specJson, int? version, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateAsync(ct);
        var row = await db.ReportSpecs.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is null)
            return new SavedReportSpecWrite(SavedReportSpecWriteStatus.NotFound);

        if (name is not null && name != row.Name)
        {
            if (await db.ReportSpecs.AnyAsync(r => r.Name == name && r.Id != id, ct))
                return new SavedReportSpecWrite(SavedReportSpecWriteStatus.NameTaken);
            row.Name = name;
        }
        if (specJson is not null)
        {
            row.Spec = specJson;
            row.Version = version ?? row.Version;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return new SavedReportSpecWrite(SavedReportSpecWriteStatus.NameTaken);
        }
        return new SavedReportSpecWrite(SavedReportSpecWriteStatus.Saved, ToModel(row));
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateAsync(ct);
        var row = await db.ReportSpecs.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is null)
            return false;

        row.DeletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static SavedReportSpec ToModel(ReportSpecEntity row) =>
        new(row.Id, row.Name, row.Spec, row.Version, row.CreatedBySubjectId, row.CreatedAt, row.UpdatedAt);
}
