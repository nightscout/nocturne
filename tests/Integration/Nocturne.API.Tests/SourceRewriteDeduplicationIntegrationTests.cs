using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Xunit;
using Xunit.Abstractions;
using V4Models = Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// Two connectors reported one dose and deduplication linked their copies, and then one connector
/// withdrew its copy, so the other's was promoted. A copy that connector writes again under the same
/// legacy id or sync identifier joins the group the other copy kept, so the dose is counted once. A record it writes under a new id is
/// a different dose and stays apart, however close and however alike.
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

    private async Task<int> BolusesOnBoardAsync()
    {
        await using var scope = OpenTenantScope();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return (await scope.ServiceProvider.GetRequiredService<IIobCobReader>().ReadInputsAsync(now)).Boluses.Count;
    }

    private async Task<int> ServedTreatmentsAsync() =>
        (await AuthenticatedClient.GetFromJsonAsync<JsonElement[]>("/api/v1/treatments"))!.Length;

    /// <summary>Links a pump copy and a withdrawing copy under <paramref name="legacyId"/>, then withdraws that copy.</summary>
    private async Task LinkAndWithdrawAsync(string legacyId, string? syncIdentifier = null)
    {
        await using (var scope = OpenTenantScope())
        {
            var boluses = scope.ServiceProvider.GetRequiredService<IBolusRepository>();
            await boluses.CreateAsync(Copy("pump-connector", "pump_bolus_" + Guid.NewGuid().ToString("N"), 0), WriteOrigin.Live);
            await boluses.CreateAsync(Copy(Withdrawing, legacyId, 5, syncIdentifier), WriteOrigin.Live);
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
}
