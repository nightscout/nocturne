using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Services;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Analytics;

/// <summary><c>GET /api/v4/statistics/aid-system-metrics</c> for a history-clamped member, on PostgreSQL.</summary>
[Collection(ApiIntegrationShards.Two)]
[Trait("Category", "Integration")]
public class AidSystemMetricsClampIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : LoopAnalysisIntegrationTestBase(fixture, output)
{
    private static string Path(DateTime start, DateTime end) =>
        $"/api/v4/statistics/aid-system-metrics?startDate={Uri.EscapeDataString(start.ToString("O"))}&endDate={Uri.EscapeDataString(end.ToString("O"))}";

    [Fact]
    public async Task A_clamped_member_asking_for_a_range_before_its_window_gets_nothing_and_the_flag()
    {
        await SeedSiteChangesAsync(DateTime.UtcNow.AddHours(-2));
        using var clamped = await CreateHistoryClampedClientAsync();

        var body = await GetOkAsync(clamped, Path(new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2024, 6, 8, 0, 0, 0, DateTimeKind.Utc)));

        body.GetProperty("rangeOutsideHistory").GetBoolean().Should().BeTrue();
        body.GetProperty("historyWindowHours").GetInt32().Should().Be(24);
        Absent(body, "siteChangeCount").Should().BeTrue();
    }

    [Fact]
    public async Task A_clamped_member_is_read_over_the_part_of_its_range_in_the_window()
    {
        var now = DateTime.UtcNow;
        await SeedSiteChangesAsync(now.AddHours(-18), now.AddHours(-2));
        using var clamped = await CreateHistoryClampedClientAsync();

        var body = await GetOkAsync(clamped, Path(now.AddDays(-3), now.AddHours(-12)));

        body.GetProperty("rangeOutsideHistory").GetBoolean().Should().BeFalse();
        body.GetProperty("siteChangeCount").GetInt32().Should().Be(1, "the change 2 hours ago is after the range asked for");
    }

    private async Task SeedSiteChangesAsync(params DateTime[] at)
    {
        using var scope = TenantScope();
        await using var context = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreateAsync();
        foreach (var t in at)
            context.DeviceEvents.Add(new DeviceEventEntity { Id = Guid.NewGuid(), Timestamp = t, EventType = "SiteChange", DataSource = "test" });
        await context.SaveChangesAsync();
    }
}
