using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Tests.V4Goldens;

/// <summary>
/// A re-upload of a temp basal the corroborator or a user already settled, written the way the
/// decomposer writes one: the stored row found by legacy id, then updated in place.
/// </summary>
[Trait("Category", "Integration")]
[Collection("V4 goldens")]
public class TempBasalReUploadOriginTests(V4GoldenFixture fx)
{
    private static readonly DateTime Start = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);

    private static TempBasal Upload(string legacyId, double rate, TempBasalOriginEvidence evidence, TempBasalOrigin origin, Guid? snapshot = null) =>
        new()
        {
            StartTimestamp = Start,
            EndTimestamp = Start.AddMinutes(30),
            Rate = rate,
            Origin = origin,
            OriginEvidence = evidence,
            DataSource = "trio",
            LegacyId = legacyId,
            ApsSnapshotId = snapshot,
        };

    [Fact]
    public async Task ReUploadKeepsAJudgementWhileTheTempIsUnchanged_ThenTakesTheDefaultOnceItsRateChanges_ThenYieldsToAStatement()
    {
        var tenant = Guid.NewGuid();
        const string legacyId = "tempBasal 1.2 2026-09-01T10:00:00Z";
        using var scope = await fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ITempBasalRepository>();
        var snapshot = await SeedSnapshotAsync(scope);

        var stored = await repo.CreateAsync(
            Upload(legacyId, 1.2, TempBasalOriginEvidence.Snapshot, TempBasalOrigin.Algorithm, snapshot),
            WriteOrigin.Live,
            CancellationToken.None);

        await ReUploadAsync(repo, stored.Id, Upload(legacyId, 1.2, TempBasalOriginEvidence.Uploader, TempBasalOrigin.Manual));
        var kept = await repo.GetByLegacyIdAsync(legacyId, CancellationToken.None);
        kept!.Origin.Should().Be(TempBasalOrigin.Algorithm);
        kept.OriginEvidence.Should().Be(TempBasalOriginEvidence.Snapshot);
        kept.ApsSnapshotId.Should().Be(snapshot);

        await repo.BulkUpsertAsync(
            [Upload(legacyId, 1.2, TempBasalOriginEvidence.Uploader, TempBasalOrigin.Manual)],
            WriteOrigin.Backfill,
            CancellationToken.None);
        var keptByBatch = await repo.GetByLegacyIdAsync(legacyId, CancellationToken.None);
        keptByBatch!.Origin.Should().Be(TempBasalOrigin.Algorithm);
        keptByBatch.OriginEvidence.Should().Be(TempBasalOriginEvidence.Snapshot);
        keptByBatch.ApsSnapshotId.Should().Be(snapshot);

        await ReUploadAsync(repo, stored.Id, Upload(legacyId, 0.4, TempBasalOriginEvidence.Uploader, TempBasalOrigin.Manual));
        var judgedAgain = await repo.GetByLegacyIdAsync(legacyId, CancellationToken.None);
        judgedAgain!.Origin.Should().Be(TempBasalOrigin.Manual);
        judgedAgain.OriginEvidence.Should().Be(TempBasalOriginEvidence.Uploader);
        judgedAgain.ApsSnapshotId.Should().BeNull();

        await ReUploadAsync(repo, stored.Id, Upload(legacyId, 0.4, TempBasalOriginEvidence.Reported, TempBasalOrigin.Algorithm));
        var stated = await repo.GetByLegacyIdAsync(legacyId, CancellationToken.None);
        stated!.Origin.Should().Be(TempBasalOrigin.Algorithm);
        stated.OriginEvidence.Should().Be(TempBasalOriginEvidence.Reported);
    }

    [Fact]
    public async Task ReUploadKeepsAStatedOriginWhateverTheRate()
    {
        var tenant = Guid.NewGuid();
        const string legacyId = "tempBasal 1.2 2026-09-01T11:00:00Z";
        using var scope = await fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ITempBasalRepository>();

        var stored = await repo.CreateAsync(
            Upload(legacyId, 1.2, TempBasalOriginEvidence.Reported, TempBasalOrigin.Manual),
            WriteOrigin.Live,
            CancellationToken.None);

        await ReUploadAsync(repo, stored.Id, Upload(legacyId, 0.4, TempBasalOriginEvidence.Uploader, TempBasalOrigin.Algorithm));

        var kept = await repo.GetByLegacyIdAsync(legacyId, CancellationToken.None);
        kept!.Origin.Should().Be(TempBasalOrigin.Manual);
        kept.OriginEvidence.Should().Be(TempBasalOriginEvidence.Reported);
    }

    private static async Task ReUploadAsync(ITempBasalRepository repo, Guid id, TempBasal upload)
    {
        upload.Id = id;
        await repo.UpdateAsync(id, upload, WriteOrigin.Live, CancellationToken.None);
    }

    private static async Task<Guid> SeedSnapshotAsync(IServiceScope scope)
    {
        await using var ctx = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreateAsync();
        var snapshot = new ApsSnapshotEntity
        {
            Id = Guid.CreateVersion7(), Timestamp = Start, AidAlgorithm = "Trio", Device = "Trio", Enacted = true, EnactedRate = 1.2,
        };
        ctx.ApsSnapshots.Add(snapshot);
        await ctx.SaveChangesAsync();
        return snapshot.Id;
    }
}
