using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Models.Authorization;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Analytics;

/// <summary>A profile write is seen by the next read of the profile versions, however soon it follows.</summary>
[Collection(ApiIntegrationShards.Two)]
[Trait("Category", "Integration")]
public class ProfileWriteVersionCacheIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : LoopAnalysisIntegrationTestBase(fixture, output)
{
    private const string Path = "/api/v4/loop-analysis/settings-evidence";

    [Fact]
    public async Task A_read_straight_after_a_profile_write_resolves_the_written_timezone()
    {
        var before = await GetOkAsync(AuthenticatedClient, $"{Path}?{Week}");
        before.GetProperty("timeZoneUnavailableReason").GetString().Should().Be("NotConfigured");

        await SeedProfileAsync("Australia/Sydney");
        using var member = await CreateTokenClientAsync(
            Scope.ReportsRead, Scope.GlucoseRead, Scope.TreatmentsRead, Scope.TherapyRead);
        var after = await GetOkAsync(member, $"{Path}?{Week}");

        Absent(after, "timeZoneUnavailableReason").Should().BeTrue("the write is in the store the read resolves the zone from");
        Absent(after, "primaryRange").Should().BeFalse();
    }
}
