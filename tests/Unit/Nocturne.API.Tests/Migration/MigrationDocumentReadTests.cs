using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Services.Migration;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.API.Tests.Migration;

/// <summary>
/// Nightscout serves a number as a string, a NaN as <c>null</c> and a boolean as a string,
/// depending on the uploader. One such value must not cost the whole document, whichever collection
/// it is in.
/// </summary>
[Trait("Category", "Unit")]
public class MigrationDocumentReadTests
{
    private const string Id = "65f0000000000000000000f1";
    private const string Created = "2026-02-02T02:40:00Z";

    private readonly Guid _tenantId = Guid.NewGuid();

    private static Dictionary<string, string> Source(string collection, string document) =>
        new() { [$"/api/v1/{collection}.json"] = $"[{document}]" };

    [Theory]
    [InlineData("""{"_id":"65f0000000000000000000f1","name":"Oats","type":"food","carbs":"12"}""", 12, 1, 0)]
    [InlineData("""{"_id":"65f0000000000000000000f1","name":"Oats","type":"food","carbs":12,"fat":null}""", 12, 1, 0)]
    [InlineData("""{"_id":"65f0000000000000000000f1","name":"Oats","type":"food","carbs":12,"isValid":"false"}""", 12, 0, 1)]
    public async Task An_api_migration_reads_a_food_with_a_string_number_a_null_number_or_a_string_isValid(
        string document, double carbs, int live, int deleted)
    {
        await using var provider = MigrationJobHarness.BuildProvider(
            MigrationJobHarness.Serving(Source("food", document)));

        var status = await MigrationJobHarness.RunAsync(provider, onCreated: null, ["food"], tenantId: _tenantId);

        status.CollectionProgress["food"].DocumentsFailed.Should().Be(0);
        using var scope = provider.CreateScope();
        await using var context = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>()
            .CreateForTenantAsync(_tenantId);
        var foods = await context.Foods.IncludingDeleted().ToListAsync();
        foods.Should().ContainSingle().Which.Carbs.Should().Be(carbs);
        foods.Count(f => f.DeletedAt is null).Should().Be(live);
        foods.Count(f => f.DeletedAt is not null).Should().Be(deleted);
    }

    [Fact]
    public async Task An_api_migration_reads_a_profile_with_a_string_isValid_and_string_and_null_numbers()
    {
        Profile? read = null;
        await using var provider = MigrationJobHarness.BuildProvider(
            MigrationJobHarness.Serving(Source("profile", """
                {"_id":"65f0000000000000000000a1","defaultProfile":"Default","startDate":"2026-06-01T00:00:00.000Z",
                 "mills":"1780272000000","isValid":"false","units":"mg/dl",
                 "store":{"Default":{"dia":"3","carbratio":[{"time":"00:00","value":null,"timeAsSeconds":"0"}]}}}
                """)),
            profileOutcome: profile =>
            {
                read = profile;
                return new InvalidatingDecomposition<Profile>();
            });

        var status = await MigrationJobHarness.RunAsync(provider, "profile");

        status.CollectionProgress["profile"].DocumentsFailed.Should().Be(0);
        read.Should().NotBeNull();
        read!.IsValid.Should().BeFalse();
        read.Mills.Should().Be(1780272000000);
        read.Store["Default"].Dia.Should().Be(3);
        read.Store["Default"].CarbRatio.Should().ContainSingle().Which.Value.Should().Be(0);
    }

    [Fact]
    public async Task An_api_migration_reads_a_treatment_with_a_string_number_and_a_null_number()
    {
        IReadOnlyList<Treatment>? read = null;
        await using var provider = MigrationJobHarness.BuildProvider(
            MigrationJobHarness.Serving(Source("treatments", $$"""
                {"_id":"{{Id}}","eventType":"Meal Bolus","created_at":"{{Created}}","date":1770000000000,"carbs":"12","insulin":null}
                """)),
            treatmentOutcome: page =>
            {
                read = page;
                return new DecompositionResult();
            });

        var status = await MigrationJobHarness.RunAsync(provider, "treatments");

        status.CollectionProgress["treatments"].DocumentsFailed.Should().Be(0);
        read.Should().ContainSingle().Which.Should().Match<Treatment>(t =>
            t.Id == Id && t.Carbs == 12 && t.Insulin == null);
    }

    [Fact]
    public async Task An_api_migration_reads_an_entry_with_a_string_number_and_a_null_number()
    {
        IReadOnlyList<Entry>? read = null;
        await using var provider = MigrationJobHarness.BuildProvider(
            MigrationJobHarness.Serving(Source("entries", $$"""
                {"_id":"{{Id}}","type":"sgv","created_at":"{{Created}}","date":1770000000000,"sgv":"120","mgdl":null}
                """)),
            entryOutcome: page =>
            {
                read = page;
                return new DecompositionResult();
            });

        var status = await MigrationJobHarness.RunAsync(provider, "entries");

        status.CollectionProgress["entries"].DocumentsFailed.Should().Be(0);
        read.Should().ContainSingle().Which.Should().Match<Entry>(e =>
            e.Id == Id && e.Sgv == 120 && e.Mgdl == 0);
    }

    [Fact]
    public async Task An_api_migration_reads_an_activity_with_a_string_number_and_a_null_number()
    {
        await using var provider = MigrationJobHarness.BuildProvider(
            MigrationJobHarness.Serving(Source("activity", $$"""
                {"_id":"{{Id}}","created_at":"{{Created}}","date":1770000000000,"energy":"1050","fat":null}
                """)));

        var status = await MigrationJobHarness.RunAsync(provider, "activity");

        status.CollectionProgress["activity"].DocumentsFailed.Should().Be(0);
        status.CollectionProgress["activity"].DocumentsMigrated.Should().Be(1);
    }
}
