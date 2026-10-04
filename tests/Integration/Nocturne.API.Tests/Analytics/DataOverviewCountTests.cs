using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.API.Services.Analytics;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Services;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Services;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Analytics;

[Trait("Category", "Integration")]
public class DataOverviewCountTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : ApiIntegrationTestBase(fixture, output)
{
    [Theory]
    [InlineData("UTC", 2024)]
    [InlineData("America/New_York", 2024)]
    [InlineData("Australia/Sydney", 2024)]
    [InlineData("Asia/Kathmandu", 2024)]
    [InlineData("Europe/Amsterdam", 1937)]
    [InlineData("Pacific/Apia", 2011)]
    [InlineData("America/Havana", 2024)]
    public async Task Every_day_count_matches_dotnet_timezone_grouping(string timezone, int year)
    {
        var zone = TimeZoneHelper.GetTimeZoneInfoFromId(timezone);
        var start = TimeZoneInfo.ConvertTimeToUtc(new DateTime(year, 1, 1), zone);
        var end = TimeZoneInfo.ConvertTimeToUtc(new DateTime(year + 1, 1, 1), zone);
        var timestamps = new List<DateTime>();
        for (var timestamp = start; timestamp < end; timestamp = timestamp.AddHours(6))
            timestamps.Add(timestamp);
        for (var day = new DateTime(year, 1, 1); day <= new DateTime(year + 1, 1, 1); day = day.AddDays(1))
        {
            if (zone.IsInvalidTime(day) || zone.IsAmbiguousTime(day))
                continue;

            var boundary = TimeZoneInfo.ConvertTimeToUtc(day, zone);
            timestamps.AddRange([boundary.AddMicroseconds(-1), boundary, boundary.AddMicroseconds(1)]);
        }

        await SeedAsync(Fixture.TenantId, timestamps);
        var expected = timestamps.Where(timestamp => timestamp >= start && timestamp < end).GroupBy(timestamp =>
            TimeZoneInfo.ConvertTimeFromUtc(timestamp, zone).ToString("yyyy-MM-dd"))
            .ToDictionary(group => group.Key, group => group.Count());

        var result = await ReadAsync(year, timezone);
        result.Days.Select(day => day.Date).Should().Equal(expected.Keys.Order());
        foreach (var day in result.Days)
        {
            day.Counts.Should().BeEquivalentTo(new Dictionary<string, int>
            {
                ["Notes"] = expected[day.Date], ["DeviceStatus"] = expected[day.Date],
            });
            day.TotalCount.Should().Be(2 * expected[day.Date]);
            day.TotalCarbs.Should().BeNull();
            day.TotalDailyDose.Should().BeNull();
        }
        if (year == 2024)
            result.Days.Should().HaveCount(366);
    }

    [Fact]
    public async Task Source_filters_soft_deletes_deduplication_and_tenant_isolation_are_preserved()
    {
        var timestamp = new DateTime(2024, 2, 29, 12, 0, 0, DateTimeKind.Utc);
        await SeedAsync(Fixture.TenantId, [timestamp], "vendor-a");
        await SeedAsync(Fixture.TenantId, [timestamp], "vendor-b");
        var otherTenant = await AuthTestHelpers.SeedTenantAsync(Fixture, "count-other", "Other");
        await SeedAsync(otherTenant, [timestamp], "vendor-a");
        await WithTenantScopeAsync(async services =>
        {
            await using var context = await services.GetRequiredService<ITenantDbContextFactory>().CreateAsync();
            var duplicateId = Guid.NewGuid();
            context.Notes.AddRange(
                new NoteEntity { Id = duplicateId, Timestamp = timestamp, DataSource = "vendor-a" },
                new NoteEntity { Id = Guid.NewGuid(), Timestamp = timestamp, DataSource = "vendor-a", DeletedAt = timestamp });
            context.LinkedRecords.Add(new LinkedRecordEntity
            {
                Id = Guid.NewGuid(), CanonicalId = Guid.NewGuid(), RecordId = duplicateId,
                RecordType = "note", IsPrimary = false, DataSource = "vendor-a",
            });
            context.ApsSnapshots.Add(new ApsSnapshotEntity
            {
                Id = Guid.NewGuid(), Timestamp = timestamp, AidAlgorithm = "Loop", DeletedAt = timestamp,
            });
            await context.SaveChangesAsync();
            return true;
        });

        var all = (await ReadAsync(2024)).Days.Should().ContainSingle().Which;
        all.Counts.Should().BeEquivalentTo(new Dictionary<string, int> { ["Notes"] = 2, ["DeviceStatus"] = 2 });
        var filtered = (await ReadAsync(2024, sources: ["vendor-a"])).Days.Should().ContainSingle().Which;
        filtered.Counts.Should().BeEquivalentTo(new Dictionary<string, int> { ["Notes"] = 1 });
        (await ReadAsync(2024, sources: ["missing"])).Days.Should().BeEmpty();
    }

    [Theory]
    [InlineData("devices.read", 1)]
    [InlineData("glucose.read", 0)]
    [InlineData("treatments.read", 0)]
    [InlineData("", 0)]
    public async Task Share_category_visibility_is_applied_before_counts(string categories, int expected)
    {
        await SeedAsync(Fixture.TenantId, [new DateTime(2024, 2, 29, 12, 0, 0, DateTimeKind.Utc)]);
        var result = await ReadAsync(2024, categories: categories);
        result.Days.Sum(day => day.TotalCount).Should().Be(expected);
        if (expected > 0)
            result.Days[0].Counts.Should().BeEquivalentTo(new Dictionary<string, int> { ["DeviceStatus"] = 1 });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Member_and_share_history_clamps_are_applied_before_counts(bool share)
    {
        var now = DateTime.UtcNow;
        var recent = now.AddHours(-1);
        await SeedAsync(Fixture.TenantId, [now.AddDays(-3), recent]);
        var result = await ReadAsync(recent.Year, categories: share ? "devices.read" : null, clamped: true);
        result.Days.Sum(day => day.TotalCount).Should().Be(share ? 1 : 2);
        result.Days.Should().ContainSingle().Which.Date.Should().Be(recent.ToString("yyyy-MM-dd"));
    }

    private async Task SeedAsync(Guid tenant, IEnumerable<DateTime> timestamps, string source = "vendor-a")
    {
        using var scope = Fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantAccessor>().SetTenant(
            new TenantContext(tenant, "counts", "Counts", true, false));
        await using var context = await scope.ServiceProvider.GetRequiredService<ITenantDbContextFactory>().CreateAsync();
        foreach (var timestamp in timestamps)
        {
            context.Notes.Add(new NoteEntity { Id = Guid.NewGuid(), Timestamp = timestamp, DataSource = source });
            context.ApsSnapshots.Add(new ApsSnapshotEntity
            {
                Id = Guid.NewGuid(), Timestamp = timestamp, DataSource = source, AidAlgorithm = "Loop",
            });
        }
        await context.SaveChangesAsync();
    }

    private async Task<DailySummaryResponse> ReadAsync(
        int year, string timezone = "UTC", string[]? sources = null, string? categories = null, bool clamped = false)
    {
        return await WithTenantScopeAsync(async services =>
        {
            var readContext = services.GetRequiredService<ICategoryReadContext>();
            if (categories is not null)
            {
                readContext.MarkShare();
                readContext.SetVisibleCategories(categories);
                readContext.SetFullHistory(!clamped);
            }
            else if (clamped)
                readContext.ClampMemberHistory();

            var settings = new Mock<ITherapySettingsResolver>();
            settings.Setup(resolver => resolver.GetTimezoneAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(timezone);
            return await ActivatorUtilities.CreateInstance<DataOverviewService>(services, settings.Object)
                .GetDailySummaryAsync(year, sources);
        });
    }
}
