using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Moq;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Infrastructure.Data;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]

namespace Nocturne.API.Tests.TestDoubles;

internal static class TreatmentDecomposerExtensions
{
    /// <summary>
    /// Decomposes <paramref name="treatment"/> as a batch of one, rethrowing its refusal as an edit of
    /// one treatment does.
    /// </summary>
    public static async Task<TreatmentDecomposition> DecomposeOneAsync(
        this ITreatmentDecomposer decomposer, Treatment treatment, WriteOrigin origin, CancellationToken ct = default)
    {
        var decomposition = await decomposer.DecomposeBatchAsync([treatment], origin, ct);
        decomposition.ThrowIfRejected(0);
        return decomposition;
    }

    /// <summary>
    /// Answers the batch reads and the bulk upsert through the per-record forms, so a test sets up and
    /// verifies one temp basal at a time: each record is created, or updated when its legacy id reads back.
    /// </summary>
    public static Mock<ITempBasalRepository> AnswersBatchFormsPerRecord(this Mock<ITempBasalRepository> mock)
    {
        mock.Setup(r => r.GetByLegacyIdsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns(async (IEnumerable<string> ids, CancellationToken ct) =>
            {
                var stored = new List<TempBasal>();
                foreach (var id in ids)
                {
                    if (await mock.Object.GetByLegacyIdAsync(id, ct) is { } tempBasal)
                        stored.Add(tempBasal);
                }
                return stored.AsEnumerable();
            });
        mock.Setup(r => r.BulkUpsertAsync(It.IsAny<IEnumerable<TempBasal>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Returns(async (IEnumerable<TempBasal> records, WriteOrigin origin, CancellationToken ct) =>
            {
                List<TempBasal> written = [], updated = [];
                var skippedDeleted = 0;
                foreach (var record in records)
                {
                    if (record.LegacyId is { } id && await mock.Object.GetByLegacyIdAsync(id, ct) is { } existing)
                    {
                        record.Id = existing.Id;
                        var update = await mock.Object.UpdateAsync(existing.Id, record, origin, ct) ?? record;
                        written.Add(update);
                        updated.Add(update);
                    }
                    else
                    {
                        try
                        {
                            written.Add(await mock.Object.CreateAsync(record, origin, ct) ?? record);
                        }
                        catch (RecreationBlockedException)
                        {
                            skippedDeleted++;
                        }
                    }
                }
                return new BulkWrite<TempBasal>(written, skippedDeleted) { Updated = updated };
            });
        return mock;
    }

    /// <summary>Answers each time's insulin context through <paramref name="at"/>.</summary>
    public static Mock<IActiveProfileResolver> AnswersInsulinContexts(
        this Mock<IActiveProfileResolver> mock, Func<long, TreatmentInsulinContext?> at)
    {
        mock.Setup(r => r.GetActiveInsulinContextsAsync(It.IsAny<IReadOnlyList<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<long> times, CancellationToken _) => [.. times.Select(at)]);
        return mock;
    }

    /// <summary>Answers each time's scheduled basal rate through <paramref name="at"/>.</summary>
    public static Mock<IBasalRateResolver> AnswersBasalRates(this Mock<IBasalRateResolver> mock, Func<long, double?> at)
    {
        mock.Setup(r => r.FindBasalRatesAsync(It.IsAny<IReadOnlyList<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<long> times, CancellationToken _) => [.. times.Select(at)]);
        return mock;
    }
}

/// <summary>
/// A treatment decomposer whose batch answers each treatment through <see cref="DecomposeAsync"/>
/// (<see cref="PerTreatmentDecomposerExtensions.AnswersBatchPerTreatment"/>), so a test of a caller
/// sets up and verifies one treatment at a time.
/// </summary>
internal interface IPerTreatmentDecomposer : ITreatmentDecomposer
{
    Task<DecompositionResult> DecomposeAsync(Treatment treatment, WriteOrigin origin, CancellationToken ct);
}

internal static class PerTreatmentDecomposerExtensions
{
    /// <summary>
    /// Answers <see cref="ITreatmentDecomposer.DecomposeBatchAsync"/> treatment by treatment: a refusal
    /// rejects only its treatment, any other failure ends the batch with what the treatments before it
    /// committed, and each treatment's records join its own group.
    /// </summary>
    public static Mock<IPerTreatmentDecomposer> AnswersBatchPerTreatment(this Mock<IPerTreatmentDecomposer> mock)
    {
        mock.Setup(d => d.DecomposeBatchAsync(
                It.IsAny<IReadOnlyList<Treatment>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>(),
                It.IsAny<TreatmentDecomposition?>()))
            .Returns(async (IReadOnlyList<Treatment> treatments, WriteOrigin origin, CancellationToken ct, TreatmentDecomposition? into) =>
            {
                var result = into ?? new TreatmentDecomposition();
                var groups = new List<Guid>();
                result.Begin(groups);
                for (var i = 0; i < treatments.Count; i++)
                {
                    groups.Add(Guid.CreateVersion7());
                    DecompositionResult one;
                    try
                    {
                        one = await mock.Object.DecomposeAsync(treatments[i], origin, ct) ?? new DecompositionResult();
                    }
                    catch (TreatmentMappingException ex)
                    {
                        result.Reject(i, ex);
                        continue;
                    }
                    catch (DbUpdateException ex) when (StoreRefusal.ReasonFor(ex) is not null)
                    {
                        result.Reject(i, ex);
                        continue;
                    }

                    if (one.StoredRecords.Select(r => (r as IV4Record)?.CorrelationId)
                            .FirstOrDefault(g => g is not null) is { } group)
                        groups[i] = group;
                    Join(one.CreatedRecords, result.CreatedRecords, groups[i], result);
                    Join(one.UpdatedRecords, result.UpdatedRecords, groups[i], result);
                    Join(one.UnchangedRecords, result.UnchangedRecords, groups[i], result);
                    result.SkippedDeleted += one.SkippedDeleted;
                    result.SkippedUnsupported += one.SkippedUnsupported;
                }

                return result;
            });
        return mock;
    }

    private static void Join(List<object> records, List<object> into, Guid group, TreatmentDecomposition result)
    {
        foreach (var record in records)
        {
            if (record is StateSpan span)
                result.SpanOwners[span] = group;
            else if (record is V4RecordBase { CorrelationId: null } v4)
                v4.CorrelationId = group;
            else if (record is TempBasal { CorrelationId: null } tempBasal)
                tempBasal.CorrelationId = group;
            into.Add(record);
        }
    }
}
