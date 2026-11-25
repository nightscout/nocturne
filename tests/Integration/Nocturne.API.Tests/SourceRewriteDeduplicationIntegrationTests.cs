using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Infrastructure.Data.Services.Deduplication;
using Xunit;
using Xunit.Abstractions;
using V4Models = Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// Two connectors reported one dose and deduplication linked their copies, and then one connector
/// withdrew its copy, so the other's was promoted. A copy that connector writes again under the same
/// legacy id or sync identifier joins the group the other copy kept, so the dose is counted once. A record it writes under a new id is
/// a different dose and stays apart, however close and however alike, unless both copies are stamped with
/// the same upstream fingerprint, which a connector publish writes and names the dose's time, amount and type.
/// </summary>
[Collection(ApiIntegrationShards.Four)]
[Trait("Category", "Integration")]
public class SourceRewriteDeduplicationIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : ApiIntegrationTestBase(fixture, output)
{
    private const string Withdrawing = "glooko-connector";

    private readonly DateTime _at = DateTime.UtcNow.AddMinutes(-10);

    private AsyncServiceScope OpenTenantScope() => TenantJobScope.Open(
        Fixture.Services,
        new TenantContext(Fixture.TenantId, ApiIntegrationTestFixture.TenantSlug, "Integration", true, false),
        "test:source-rewrite");

    private V4Models.Bolus Copy(string source, string legacyId, int offsetSeconds, string? syncIdentifier = null) => new()
    {
        Timestamp = _at.AddSeconds(offsetSeconds),
        LegacyId = legacyId,
        SyncIdentifier = syncIdentifier,
        Device = source,
        DataSource = source,
        Insulin = 2.0,
        BolusType = V4Models.BolusType.Normal,
    };

    private Treatment Published(string legacyId, int offsetSeconds, double insulin = 2.0) => new()
    {
        Id = legacyId,
        EventType = "Correction Bolus",
        Insulin = insulin,
        CreatedAt = _at.AddSeconds(offsetSeconds).ToString("O"),
        DataSource = Withdrawing,
    };

    private async Task PublishAsync(Treatment treatment)
    {
        await using var scope = OpenTenantScope();
        (await scope.ServiceProvider.GetRequiredService<IConnectorPublisher>().Treatments
            .PublishTreatmentsAsync([treatment], Withdrawing, WriteOrigin.Live)).Should().BeTrue();
    }

    private async Task<int> BolusesOnBoardAsync()
    {
        await using var scope = OpenTenantScope();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return (await scope.ServiceProvider.GetRequiredService<IIobCobReader>().ReadInputsAsync(now)).Boluses.Count;
    }

    private async Task<int> ServedTreatmentsAsync() =>
        (await AuthenticatedClient.GetFromJsonAsync<JsonElement[]>("/api/v1/treatments"))!.Length;

    /// <summary>
    /// Links a pump copy and a withdrawing copy under <paramref name="legacyId"/>, then withdraws that copy.
    /// The withdrawing copy carries an upstream fingerprint when <paramref name="stamped"/>, as a connector publish writes it.
    /// </summary>
    private async Task LinkAndWithdrawAsync(string legacyId, string? syncIdentifier = null, bool stamped = false)
    {
        await using (var scope = OpenTenantScope())
        {
            await scope.ServiceProvider.GetRequiredService<IBolusRepository>().CreateAsync(
                Copy("pump-connector", "pump_bolus_" + Guid.NewGuid().ToString("N"), 0), WriteOrigin.Live);
        }

        if (stamped)
        {
            await PublishAsync(Published(legacyId, 5));
        }
        else
        {
            await using var scope = OpenTenantScope();
            await scope.ServiceProvider.GetRequiredService<IBolusRepository>()
                .CreateAsync(Copy(Withdrawing, legacyId, 5, syncIdentifier), WriteOrigin.Live);
        }
        (await BolusesOnBoardAsync()).Should().Be(1, "deduplication counts one copy of the dose");

        await using (var scope = OpenTenantScope())
        {
            await scope.ServiceProvider.GetRequiredService<ITreatmentService>()
                .DeleteFromSourceAsync(Withdrawing, new HashSet<string> { legacyId });
        }
        (await BolusesOnBoardAsync()).Should().Be(1, "the other source's copy is promoted");
    }

    [Fact]
    public async Task A_copy_its_source_withdrew_and_reports_again_is_counted_once()
    {
        var legacyId = "glooko_bolus_" + Guid.NewGuid().ToString("N");
        await LinkAndWithdrawAsync(legacyId);

        await using (var scope = OpenTenantScope())
        {
            await scope.ServiceProvider.GetRequiredService<IBolusRepository>()
                .CreateAsync(Copy(Withdrawing, legacyId, 5), WriteOrigin.Live);
        }

        (await BolusesOnBoardAsync()).Should().Be(1);
        (await ServedTreatmentsAsync()).Should().Be(1);
    }

    /// <summary>
    /// Each withdrawn copy stays in the group as a deleted member. None of them holds its source against
    /// the next copy written again, so the dose is still counted once, and a merge over the group finds
    /// nothing to change.
    /// </summary>
    [Fact]
    public async Task A_copy_its_source_withdraws_and_reports_again_twice_is_counted_once()
    {
        var legacyId = "glooko_bolus_" + Guid.NewGuid().ToString("N");
        await LinkAndWithdrawAsync(legacyId);

        for (var cycle = 0; cycle < 2; cycle++)
        {
            await using (var scope = OpenTenantScope())
            {
                await scope.ServiceProvider.GetRequiredService<IBolusRepository>()
                    .CreateAsync(Copy(Withdrawing, legacyId, 5), WriteOrigin.Live);
            }
            (await BolusesOnBoardAsync()).Should().Be(1);

            if (cycle == 0)
            {
                await using var scope = OpenTenantScope();
                await scope.ServiceProvider.GetRequiredService<ITreatmentService>()
                    .DeleteFromSourceAsync(Withdrawing, new HashSet<string> { legacyId });
                (await BolusesOnBoardAsync()).Should().Be(1, "the other source's copy is promoted again");
            }
        }

        (await ServedTreatmentsAsync()).Should().Be(1);

        var links = await BolusLinksAsync();
        links.Select(l => l.CanonicalId).Distinct().Should().ContainSingle();
        links.Should().ContainSingle(l => l.DataSource == "pump-connector").Which.Deleted.Should().BeFalse();
        var withdrawing = links.Where(l => l.DataSource == Withdrawing).ToList();
        withdrawing.Should().HaveCount(3);
        withdrawing.Count(l => l.Deleted).Should().Be(2);

        await using (var scope = OpenTenantScope())
        {
            (await scope.ServiceProvider.GetRequiredService<DuplicateReconciler>()
                .MergeAllAsync(RecordType.Bolus, CancellationToken.None)).Should().Be(0);
        }

        (await BolusesOnBoardAsync()).Should().Be(1);
        (await ServedTreatmentsAsync()).Should().Be(1);
    }

    /// <summary>The tenant's bolus links, each with whether its record is deleted.</summary>
    private async Task<List<(Guid CanonicalId, string DataSource, bool Deleted)>> BolusLinksAsync()
    {
        await using var scope = OpenTenantScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreateAsync();
        var key = RecordTypeKeys.Key(RecordType.Bolus);
        var links = await db.LinkedRecords.AsNoTracking().Where(l => l.RecordType == key).ToListAsync();
        var deleted = await db.Boluses.IncludingDeleted().AsNoTracking()
            .Where(b => b.DeletedAt != null).Select(b => b.Id).ToListAsync();
        return [.. links.Select(l => (l.CanonicalId, l.DataSource, deleted.Contains(l.RecordId)))];
    }

    [Fact]
    public async Task A_copy_its_source_withdrew_and_reports_again_under_its_sync_identifier_is_counted_once()
    {
        var syncIdentifier = Guid.NewGuid().ToString();
        await LinkAndWithdrawAsync("glooko_bolus_" + Guid.NewGuid().ToString("N"), syncIdentifier);

        await using (var scope = OpenTenantScope())
        {
            await scope.ServiceProvider.GetRequiredService<IBolusRepository>().CreateAsync(
                Copy(Withdrawing, "glooko_bolus_" + Guid.NewGuid().ToString("N"), 5, syncIdentifier), WriteOrigin.Live);
        }

        (await BolusesOnBoardAsync()).Should().Be(1);
        (await ServedTreatmentsAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(180)]
    public async Task A_dose_its_source_reports_under_a_new_id_after_withdrawing_its_copy_is_counted_apart(
        int offsetSeconds)
    {
        await LinkAndWithdrawAsync("glooko_bolus_" + Guid.NewGuid().ToString("N"));

        await using (var scope = OpenTenantScope())
        {
            await scope.ServiceProvider.GetRequiredService<IBolusRepository>()
                .CreateAsync(Copy(Withdrawing, "glooko_bolus_" + Guid.NewGuid().ToString("N"), offsetSeconds), WriteOrigin.Live);
        }

        (await BolusesOnBoardAsync()).Should().Be(2, "the withdrawn copy's source still holds its place in the group");
        (await ServedTreatmentsAsync()).Should().Be(2);
    }

    [Fact]
    public async Task A_copy_its_source_withdrew_and_publishes_again_under_a_new_id_is_counted_once()
    {
        await LinkAndWithdrawAsync("glooko_bolus_" + Guid.NewGuid().ToString("N"), stamped: true);

        await PublishAsync(Published("glooko_bolus_" + Guid.NewGuid().ToString("N"), 5));

        (await BolusesOnBoardAsync()).Should().Be(1);
        (await ServedTreatmentsAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData(5, 2.04)]
    [InlineData(20, 2.0)]
    public async Task A_dose_its_source_publishes_under_a_new_id_that_differs_from_its_withdrawn_copy_is_counted_apart(
        int offsetSeconds, double insulin)
    {
        await LinkAndWithdrawAsync("glooko_bolus_" + Guid.NewGuid().ToString("N"), stamped: true);

        await PublishAsync(Published("glooko_bolus_" + Guid.NewGuid().ToString("N"), offsetSeconds, insulin));

        (await BolusesOnBoardAsync()).Should().Be(2);
        (await ServedTreatmentsAsync()).Should().Be(2);
    }

    [Fact]
    public async Task A_copy_written_again_under_a_new_id_with_no_fingerprint_is_counted_apart()
    {
        await LinkAndWithdrawAsync("glooko_bolus_" + Guid.NewGuid().ToString("N"), stamped: true);

        await using (var scope = OpenTenantScope())
        {
            await scope.ServiceProvider.GetRequiredService<IBolusRepository>()
                .CreateAsync(Copy(Withdrawing, "glooko_bolus_" + Guid.NewGuid().ToString("N"), 5), WriteOrigin.Live);
        }

        (await BolusesOnBoardAsync()).Should().Be(2);
        (await ServedTreatmentsAsync()).Should().Be(2);
    }

    [Fact]
    public async Task A_copy_published_under_a_new_id_after_an_unfingerprinted_one_was_withdrawn_is_counted_apart()
    {
        await LinkAndWithdrawAsync("glooko_bolus_" + Guid.NewGuid().ToString("N"));

        await PublishAsync(Published("glooko_bolus_" + Guid.NewGuid().ToString("N"), 5));

        (await BolusesOnBoardAsync()).Should().Be(2);
        (await ServedTreatmentsAsync()).Should().Be(2);
    }
}
