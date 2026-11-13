using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Connectors;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// <see cref="IDemoDataPurge"/> over <see cref="NocturneDbContext"/>: one table list per group, the
/// one owner of "everything the demo service wrote" for every caller that clears demo data.
/// </summary>
/// <remarks>
/// <see cref="PurgeAsync"/> reads as the scope's tenant. The tenant overloads pin the tenant the caller
/// looked up and report as it in a job scope, since a platform admin's own scope resolves none and
/// the report would clear nothing.
/// </remarks>
public class DemoDataPurgeRepository(
    INocturneDbContextFactory contexts,
    TimeProvider time,
    IServiceScopeFactory scopes,
    ILegacyReadCacheInvalidation? legacyReads = null) : IDemoDataPurge
{
    private const string AuditEndpoint = "service:demo-purge";

    private static readonly IReadOnlyList<V4RecordTable> GlucoseTables = V4RecordTables.OfKind(V4RecordKind.Glucose);

    private static readonly IReadOnlyList<V4RecordTable> TreatmentTables =
        V4RecordTables.OfKind(V4RecordKind.Treatment, V4RecordKind.StateSpan);

    private static readonly IReadOnlyList<V4RecordTable> DeviceStatusTables = V4RecordTables.OfKind(V4RecordKind.DeviceStatus);

    private static readonly Type[] GlucoseModels = [.. GlucoseTables.Select(t => t.Model)];

    private static readonly Type[] TreatmentModels =
        [.. TreatmentTables.Select(t => t.Model), typeof(CompressionLowSuggestion), typeof(LabHbA1cResult)];

    private static readonly Type[] DeviceStatusModels = [.. DeviceStatusTables.Select(t => t.Model)];

    /// <inheritdoc/>
    public async Task<DemoPurgeCounts> PurgeAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateAsync(ct);
        var written = new HashSet<Type>();
        try
        {
            var treatments = await RunAsync(written, TreatmentModels, () => DeleteTreatmentsAsync(db, ct));
            var glucose = await RunAsync(written, GlucoseModels, () => DeleteGlucoseAsync(db, ct));
            var deviceStatus = await RunAsync(written, DeviceStatusModels, () => DeleteDeviceStatusAsync(db, ct));
            return new DemoPurgeCounts(glucose, treatments, deviceStatus);
        }
        finally
        {
            await ReportAsync(written, legacyReads);
        }
    }

    /// <inheritdoc/>
    public async Task<long> PurgeEntriesAsync(TenantContext tenant, CancellationToken ct)
    {
        await using var db = await contexts.CreateForTenantAsync(tenant.TenantId, ct);
        var written = new HashSet<Type>();
        try
        {
            return await RunAsync(written, GlucoseModels, () => DeleteGlucoseAsync(db, ct));
        }
        finally
        {
            await ReportInTenantAsync(written, tenant);
        }
    }

    /// <inheritdoc/>
    public async Task<long> PurgeTreatmentsAsync(TenantContext tenant, CancellationToken ct)
    {
        await using var db = await contexts.CreateForTenantAsync(tenant.TenantId, ct);
        var written = new HashSet<Type>();
        try
        {
            var treatments = await RunAsync(written, TreatmentModels, () => DeleteTreatmentsAsync(db, ct));
            var deviceStatus = await RunAsync(written, DeviceStatusModels, () => DeleteDeviceStatusAsync(db, ct));
            return treatments + deviceStatus;
        }
        finally
        {
            await ReportInTenantAsync(written, tenant);
        }
    }

    private static async Task<long> DeleteAsync(
        NocturneDbContext db, IReadOnlyList<V4RecordTable> tables, CancellationToken ct)
    {
        long deleted = 0;
        foreach (var table in tables)
            deleted += await table.PurgeAsync(db, DataSources.DemoService, ct);
        return deleted;
    }

    private static Task<long> DeleteGlucoseAsync(NocturneDbContext db, CancellationToken ct) =>
        DeleteAsync(db, GlucoseTables, ct);

    private async Task<long> DeleteTreatmentsAsync(NocturneDbContext db, CancellationToken ct)
    {
        var deleted = await DeleteAsync(db, TreatmentTables, ct);
        var demoAcceptedSpanIds = (await db.StateSpans
                .Where(s => s.Source == "compression-low-detection" && s.MetadataJson != null)
                .Select(s => new { s.Id, s.MetadataJson })
                .ToListAsync(ct))
            .Where(span => HasDemoSeedMarker(span.MetadataJson))
            .Select(span => span.Id)
            .ToArray();
        if (demoAcceptedSpanIds.Length > 0)
            deleted += await db.StateSpans.PurgeAsync(s => demoAcceptedSpanIds.Contains(s.Id), ct);
        deleted += await db.CompressionLowSuggestions.PurgeAsync(
            s => s.DataSource == DataSources.DemoService, ct);
        deleted += await db.LabHbA1cResults
            .Where(result => result.Note == "Demo lab result")
            .ExecuteUpdateAsync(update => update.SetProperty(
                result => result.DeletedAt, time.GetUtcNow().UtcDateTime), ct);
        return deleted;
    }

    private static Task<long> DeleteDeviceStatusAsync(NocturneDbContext db, CancellationToken ct) =>
        DeleteAsync(db, DeviceStatusTables, ct);

    private static bool HasDemoSeedMarker(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata))
            return false;

        try
        {
            using var document = JsonDocument.Parse(metadata);
            return document.RootElement.TryGetProperty("DemoSeed", out var marker)
                && marker.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<long> RunAsync(HashSet<Type> written, Type[] models, Func<Task<long>> purge)
    {
        var deleted = 0L;
        var completed = false;
        try
        {
            deleted = await purge();
            completed = true;
            return deleted;
        }
        finally
        {
            if (!completed || deleted > 0)
                written.UnionWith(models);
        }
    }

    private static Task ReportAsync(HashSet<Type> written, ILegacyReadCacheInvalidation? target) =>
        written.Count == 0 ? Task.CompletedTask : target?.WrittenAsync([.. written]) ?? Task.CompletedTask;

    private async Task ReportInTenantAsync(HashSet<Type> written, TenantContext tenant)
    {
        if (written.Count == 0)
            return;

        await using var job = TenantJobScope.Open(scopes, tenant, AuditEndpoint);
        await ReportAsync(written, job.ServiceProvider.GetRequiredService<ILegacyReadCacheInvalidation>());
    }
}
