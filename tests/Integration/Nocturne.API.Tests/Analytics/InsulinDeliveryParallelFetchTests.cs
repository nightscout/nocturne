using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Models;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Analytics;

/// <summary>
/// Correctness integration test for the parallel fetch path in
/// <c>GET /api/v4/statistics/insulin-delivery-stats</c>.
///
/// Fires 5 concurrent requests and asserts that every response is
/// identical — i.e. that the <c>Task.WhenAll</c> fan-out inside
/// <see cref="Nocturne.API.Controllers.V4.Analytics.StatisticsController.GetInsulinDeliveryStatistics"/>
/// never leaks state between parallel context branches and always
/// returns a consistent answer under concurrency.
/// </summary>
[Trait("Category", "Integration")]
public class InsulinDeliveryParallelFetchTests : ApiIntegrationTestBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public InsulinDeliveryParallelFetchTests(
        ApiIntegrationTestFixture fixture,
        ITestOutputHelper output
    )
        : base(fixture, output) { }

    /// <summary>
    /// Fires 5 concurrent GET requests to the insulin-delivery-stats endpoint and
    /// verifies that all responses are non-null and fully consistent with each other.
    ///
    /// The test uses an empty database (cleaned in InitializeAsync) so there is no
    /// dependency on pre-seeded data. An empty-data result (all zeros) is a valid
    /// <see cref="InsulinDeliveryStatistics"/> — what matters is that every
    /// concurrent call returns the *same* value.
    /// </summary>
    [Fact]
    public async Task ConcurrentRequests_ReturnConsistentResults()
    {
        // Arrange — a fixed 7-day window in the past so the clock doesn't interfere.
        var endDate = new DateTime(2024, 1, 8, 0, 0, 0, DateTimeKind.Utc);
        var startDate = endDate.AddDays(-7);
        var query = $"?startDate={startDate:O}&endDate={endDate:O}";
        var url = $"/api/v4/statistics/insulin-delivery-stats{query}";

        // Fire 5 concurrent requests using the shared ApiClient.
        // HttpClient is thread-safe for concurrent GETs, so a single authenticated
        // client is sufficient here.
        using var client = CreateAuthenticatedClient();

        var tasks = Enumerable
            .Range(0, 5)
            .Select(_ => client.GetAsync(url))
            .ToList();

        var responses = await Task.WhenAll(tasks);

        // Assert — every response must succeed.
        foreach (var response in responses)
        {
            response.StatusCode.Should().Be(
                HttpStatusCode.OK,
                because: "the endpoint should succeed even with an empty database"
            );
        }

        // Deserialise all payloads.
        var results = new List<InsulinDeliveryStatistics>();
        foreach (var response in responses)
        {
            var result = await response.Content.ReadFromJsonAsync<InsulinDeliveryStatistics>(JsonOptions);
            result.Should().NotBeNull(
                because: "the endpoint must return a valid InsulinDeliveryStatistics body"
            );
            results.Add(result!);
        }

        Log($"Received {results.Count} responses. TotalBolus={results[0].TotalBolus}, " +
            $"TotalBasal={results[0].TotalBasal}, TotalInsulin={results[0].TotalInsulin}");

        // All 5 responses must be identical — parallel execution must not produce
        // divergent results through tenant-context leakage or race conditions.
        var reference = results[0];
        foreach (var result in results.Skip(1))
        {
            result.TotalBolus.Should().Be(reference.TotalBolus,
                because: "concurrent fetches must return the same TotalBolus");
            result.TotalBasal.Should().Be(reference.TotalBasal,
                because: "concurrent fetches must return the same TotalBasal");
            result.TotalInsulin.Should().Be(reference.TotalInsulin,
                because: "concurrent fetches must return the same TotalInsulin");
            result.BolusCount.Should().Be(reference.BolusCount,
                because: "concurrent fetches must return the same BolusCount");
            result.BasalCount.Should().Be(reference.BasalCount,
                because: "concurrent fetches must return the same BasalCount");
        }
    }
}
