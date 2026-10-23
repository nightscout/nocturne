using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Models.Authorization;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Analytics;

/// <summary>
/// <c>GET /api/v4/loop-analysis/envelope</c> end to end on PostgreSQL: what every Loop Analysis
/// page shares, with no page's figures.
/// </summary>
[Trait("Category", "Integration")]
public class LoopAnalysisEnvelopeIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : LoopAnalysisIntegrationTestBase(fixture, output)
{
    private const string Path = "/api/v4/loop-analysis/envelope";

    [Fact]
    public async Task Resolves_the_ranges_and_filters_and_lists_the_profiles_without_any_page_figures()
    {
        await SeedProfileAsync("Australia/Sydney");

        var body = await GetOkAsync(AuthenticatedClient, $"{Path}?{Week}&compare=previous&excludeExercise=true");

        body.GetProperty("primaryRange").GetProperty("startDate").GetString().Should().Be("2024-06-10");
        body.GetProperty("comparisonRange").GetProperty("endDate").GetString().Should().Be("2024-06-09");
        body.GetProperty("appliedFilters").GetProperty("excludeExercise").GetBoolean().Should().BeTrue();
        body.GetProperty("profileNames").EnumerateArray().Select(n => n.GetString()).Should().Equal("Default");
        Absent(body, "primary").Should().BeTrue();
        Absent(body, "comparison").Should().BeTrue();
    }

    [Fact]
    public async Task Withholds_the_profiles_and_names_the_filters_a_caller_without_therapy_cannot_use()
    {
        await SeedProfileAsync("Australia/Sydney");
        using var client = await CreateTokenClientAsync(Scope.ReportsRead, Scope.GlucoseRead, Scope.TreatmentsRead);

        var body = await GetOkAsync(client, $"{Path}?{Week}&profileName=Default");

        body.GetProperty("profileNames").EnumerateArray().Should().BeEmpty();
        body.GetProperty("withheldSeries").EnumerateArray().Select(s => s.GetString()).Should().Equal("ProfileNames");
        body.GetProperty("ignoredFilters").EnumerateArray().Select(s => s.GetString()).Should().Contain("profileName");
        body.GetProperty("unavailableFilters").EnumerateArray().Select(f => f.GetProperty("filter").GetString())
            .Should().Contain(["profileName", "excludeOpenLoop"]);
    }

    [Fact]
    public async Task Reports_the_clamp_to_a_history_clamped_member()
    {
        await SeedProfileAsync("Australia/Sydney");
        using var clamped = await CreateHistoryClampedClientAsync();

        var body = await GetOkAsync(clamped, $"{Path}?{Week}&compare=previous");

        body.GetProperty("historyClamped").GetBoolean().Should().BeTrue();
        Absent(body, "comparisonRange").Should().BeTrue();
    }

    [Fact]
    public async Task A_share_gets_its_reason_no_ranges_and_no_profiles()
    {
        await SeedProfileAsync("Australia/Sydney");
        using var share = await CreateShareClientAsync();

        var body = await GetOkAsync(share, $"{Path}?{Week}&compare=previous");

        body.GetProperty("timeZoneUnavailableReason").GetString().Should().Be("Share");
        Absent(body, "primaryRange").Should().BeTrue();
        Absent(body, "comparisonRange").Should().BeTrue();
        body.GetProperty("profileNames").EnumerateArray().Should().BeEmpty();
        body.GetProperty("withheldSeries").EnumerateArray().Select(s => s.GetString()).Should().Contain("ProfileNames");
    }

    [Fact]
    public async Task Returns_no_ranges_without_a_tenant_timezone()
    {
        var body = await GetOkAsync(AuthenticatedClient, $"{Path}?{Week}");

        body.GetProperty("timeZoneUnavailableReason").GetString().Should().Be("NotConfigured");
        Absent(body, "primaryRange").Should().BeTrue();
    }
}
