using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.Infrastructure.Data.Tests.V4Goldens;

/// <summary>
/// The batch lookups a page of legacy profile documents reads its schedules and therapy settings
/// through answer what the single-key lookups would, key by key.
/// </summary>
[Trait("Category", "Integration")]
[Collection("V4 goldens")]
public class ScheduleRepositoryBatchReadTests
{
    private readonly V4GoldenFixture _fx;

    public ScheduleRepositoryBatchReadTests(V4GoldenFixture fx) => _fx = fx;

    private static readonly DateTime T0 = new(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task NewestByProfileNames_IsWhatEachNameLookupReturnsFirst()
    {
        using var scope = await _fx.BeginTenantScopeAsync(Guid.NewGuid());
        var repo = scope.ServiceProvider.GetRequiredService<IBasalScheduleRepository>();
        await repo.BulkCreateAsync(
        [
            Schedule("Day", T0, "day-old"),
            Schedule("Day", T0.AddHours(2), "day-new"),
            Schedule("Day", T0.AddHours(1), "day-middle"),
            Schedule("Night", T0, "night"),
            Schedule("Other", T0.AddHours(5), "other"),
        ], WriteOrigin.Live, CancellationToken.None);

        var newest = (await repo.GetNewestByProfileNamesAsync(["Day", "Night", "Missing"])).ToList();

        newest.Select(s => s.LegacyId).Should().BeEquivalentTo(["day-new", "night"]);
        foreach (var name in new[] { "Day", "Night" })
        {
            var first = (await repo.GetByProfileNameAsync(name)).First();
            newest.Single(s => s.ProfileName == name).Id.Should().Be(first.Id);
        }
    }

    [Fact]
    public async Task NewestByProfileNames_BreaksATimestampTieByTheGreaterId_AsTheNameLookupDoes()
    {
        using var scope = await _fx.BeginTenantScopeAsync(Guid.NewGuid());
        var repo = scope.ServiceProvider.GetRequiredService<IBasalScheduleRepository>();
        await repo.BulkCreateAsync(
        [
            Schedule("Day", T0, "tie-a"),
            Schedule("Day", T0, "tie-b"),
            Schedule("Day", T0, "tie-c"),
        ], WriteOrigin.Live, CancellationToken.None);

        var rows = (await repo.GetByProfileNameAsync("Day")).ToList();
        var expected = rows.Max(r => r.Id);

        rows.First().Id.Should().Be(expected);
        (await repo.GetNewestByProfileNamesAsync(["Day"])).Single().Id.Should().Be(expected);
    }

    [Fact]
    public async Task ByCorrelationIds_ServesEveryRowOfEachGroup_AndNoOther()
    {
        using var scope = await _fx.BeginTenantScopeAsync(Guid.NewGuid());
        var repo = scope.ServiceProvider.GetRequiredService<ICarbRatioScheduleRepository>();
        var (first, second, other) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await repo.BulkCreateAsync(
        [
            new CarbRatioSchedule { Timestamp = T0, ProfileName = "Day", CorrelationId = first, LegacyId = "a" },
            new CarbRatioSchedule { Timestamp = T0, ProfileName = "Night", CorrelationId = first, LegacyId = "b" },
            new CarbRatioSchedule { Timestamp = T0, ProfileName = "Day", CorrelationId = second, LegacyId = "c" },
            new CarbRatioSchedule { Timestamp = T0, ProfileName = "Day", CorrelationId = other, LegacyId = "d" },
            new CarbRatioSchedule { Timestamp = T0, ProfileName = "Day", LegacyId = "e" },
        ], WriteOrigin.Live, CancellationToken.None);

        var rows = await repo.GetByCorrelationIdsAsync([first, second]);

        rows.Select(r => r.LegacyId).Should().BeEquivalentTo(["a", "b", "c"]);
    }

    [Fact]
    public async Task ByLegacyIds_ServesTheRowUnderEachId_AndNoDeletedOne()
    {
        using var scope = await _fx.BeginTenantScopeAsync(Guid.NewGuid());
        var repo = scope.ServiceProvider.GetRequiredService<ITargetRangeScheduleRepository>();
        await repo.BulkCreateAsync(
        [
            new TargetRangeSchedule { Timestamp = T0, ProfileName = "Day", LegacyId = "p:Day" },
            new TargetRangeSchedule { Timestamp = T0, ProfileName = "Night", LegacyId = "p:Night" },
            new TargetRangeSchedule { Timestamp = T0, ProfileName = "Gone", LegacyId = "p:Gone" },
            new TargetRangeSchedule { Timestamp = T0, ProfileName = "Else", LegacyId = "q:Else" },
        ], WriteOrigin.Live, CancellationToken.None);
        await repo.DeleteByLegacyIdAsync("p:Gone", WriteOrigin.Live, CancellationToken.None);

        var rows = await repo.GetByLegacyIdsAsync(["p:Day", "p:Night", "p:Gone", "p:Missing"]);

        rows.Select(r => r.ProfileName).Should().BeEquivalentTo(["Day", "Night"]);
    }

    [Fact]
    public async Task ByProfileNames_ServesEveryRowUnderEachName_AndNoDeletedOne()
    {
        using var scope = await _fx.BeginTenantScopeAsync(Guid.NewGuid());
        var repo = scope.ServiceProvider.GetRequiredService<ITherapySettingsRepository>();
        await repo.BulkCreateAsync(
        [
            new TherapySettings { Timestamp = T0, ProfileName = "Day", LegacyId = "d-old" },
            new TherapySettings { Timestamp = T0.AddHours(1), ProfileName = "Day", LegacyId = "d-new" },
            new TherapySettings { Timestamp = T0, ProfileName = "Night", LegacyId = "n" },
            new TherapySettings { Timestamp = T0, ProfileName = "Gone", LegacyId = "g" },
            new TherapySettings { Timestamp = T0, ProfileName = "Else", LegacyId = "e" },
        ], WriteOrigin.Live, CancellationToken.None);
        await repo.DeleteByLegacyIdAsync("g", WriteOrigin.Live, CancellationToken.None);

        var rows = await repo.GetByProfileNamesAsync(["Day", "Night", "Gone", "Missing"]);

        rows.Select(r => r.LegacyId).Should().BeEquivalentTo(["d-old", "d-new", "n"]);
    }

    [Fact]
    public async Task DocumentRowsByIds_ServeWhatTheSingleDocumentReadServes_ForEachDocument()
    {
        using var scope = await _fx.BeginTenantScopeAsync(Guid.NewGuid());
        var repo = scope.ServiceProvider.GetRequiredService<ITherapySettingsRepository>();
        var own = new TherapySettings { Timestamp = T0, ProfileName = "Own" };
        await repo.BulkCreateAsync(
        [
            new TherapySettings { Timestamp = T0, ProfileName = "Day", LegacyId = "doc1:Day" },
            new TherapySettings { Timestamp = T0, ProfileName = "Night", LegacyId = "doc1:Night" },
            new TherapySettings { Timestamp = T0, ProfileName = "Solo", LegacyId = "doc2" },
            new TherapySettings { Timestamp = T0, ProfileName = "Gone", LegacyId = "doc3:Gone" },
            new TherapySettings { Timestamp = T0, ProfileName = "Else", LegacyId = "doc4:Else" },
            own,
        ], WriteOrigin.Live, CancellationToken.None);
        await repo.DeleteByLegacyIdAsync("doc3:Gone", WriteOrigin.Live, CancellationToken.None);
        var ownKey = TherapySettings.DocumentIdOf((await repo.GetByProfileNameAsync("Own")).Single());
        string[] keys = ["doc1", "doc2", "doc3", ownKey, "missing"];

        var batch = await repo.GetDocumentRowsAsync(keys);

        batch.Keys.Should().BeEquivalentTo(["doc1", "doc2", ownKey]);
        foreach (var key in keys)
        {
            var single = await repo.GetDocumentRowsAsync(key);
            (batch.TryGetValue(key, out var rows) ? rows : []).Select(r => r.Id)
                .Should().BeEquivalentTo(single.Select(r => r.Id), key);
        }
        batch["doc1"].Select(r => r.ProfileName).Should().BeEquivalentTo(["Day", "Night"]);
    }

    private static BasalSchedule Schedule(string profileName, DateTime timestamp, string legacyId) =>
        new() { Timestamp = timestamp, ProfileName = profileName, LegacyId = legacyId };
}
