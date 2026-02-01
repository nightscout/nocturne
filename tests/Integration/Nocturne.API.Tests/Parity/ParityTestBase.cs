using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Models;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Parity;

/// <summary>
/// Base class for API parity tests.
/// Provides helpers for seeding identical data to both systems and comparing responses.
///
/// Key behaviors:
/// - Seeds same data to both Nightscout and Nocturne
/// - Compares responses with strict null vs missing handling
/// - Ignores field order (JSON comparison is unordered)
/// - Handles dynamic fields (IDs, timestamps) appropriately
/// </summary>
[Collection("Parity")]
[Trait("Category", "Parity")]
[Parity]
public abstract class ParityTestBase : IAsyncLifetime
{
    protected readonly ParityTestFixture Fixture;
    protected readonly ITestOutputHelper Output;
    protected readonly ResponseComparer Comparer;

    /// <summary>
    /// Nightscout client for V1/V2 API (uses api-secret header)
    /// </summary>
    protected HttpClient NightscoutClient => Fixture.NightscoutClient;

    /// <summary>
    /// Nightscout client for V3 API (uses JWT Bearer token)
    /// </summary>
    protected HttpClient NightscoutV3Client => Fixture.NightscoutV3Client;

    protected HttpClient NocturneClient => Fixture.NocturneClient;

    protected ParityTestBase(ParityTestFixture fixture, ITestOutputHelper output)
    {
        Fixture = fixture;
        Output = output;
        Comparer = new ResponseComparer(GetComparisonOptions());
    }

    /// <summary>
    /// Override to customize comparison options per test class
    /// </summary>
    protected virtual ComparisonOptions GetComparisonOptions() => ComparisonOptions.Default;

    /// <summary>
    /// Gets the appropriate Nightscout client based on the API path.
    /// V3 paths use JWT Bearer authentication, V1/V2 use api-secret header.
    /// </summary>
    protected HttpClient GetNightscoutClientForPath(string path)
    {
        return path.StartsWith("/api/v3", StringComparison.OrdinalIgnoreCase)
            ? NightscoutV3Client
            : NightscoutClient;
    }

    public virtual async Task InitializeAsync()
    {
        // Clean up at the start of each test to ensure a clean slate
        // This handles leftover data from previous test runs or parallel test execution
        await Fixture.CleanupDataAsync();
    }

    public virtual async Task DisposeAsync()
    {
        await Fixture.CleanupDataAsync();
    }

    #region Data Seeding Helpers

    /// <summary>
    /// Seeds entries to both Nightscout and Nocturne.
    /// Uses TestDataFactory for consistent data generation.
    /// </summary>
    protected async Task SeedEntriesAsync(params Entry[] entries)
    {
        foreach (var entry in entries)
        {
            // Convert to dictionary for Nightscout - only include non-null values
            // This matches real-world behavior where clients only send fields they have
            var nsEntry = new Dictionary<string, object?>
            {
                ["type"] = entry.Type ?? "sgv",
                ["sgv"] = entry.Sgv,
                ["direction"] = entry.Direction,
                ["device"] = entry.Device,
                ["date"] = entry.Mills,
                ["dateString"] = entry.DateString,
            };

            // Only include optional fields if they have values
            if (entry.Noise.HasValue) nsEntry["noise"] = entry.Noise.Value;
            if (entry.Filtered.HasValue) nsEntry["filtered"] = entry.Filtered.Value;
            if (entry.Unfiltered.HasValue) nsEntry["unfiltered"] = entry.Unfiltered.Value;
            if (entry.Rssi.HasValue) nsEntry["rssi"] = entry.Rssi.Value;
            if (entry.Delta.HasValue) nsEntry["delta"] = entry.Delta.Value;

            var nsResponse = await NightscoutClient.PostAsJsonAsync("/api/v1/entries", new[] { nsEntry });
            nsResponse.EnsureSuccessStatusCode();

            var nocResponse = await NocturneClient.PostAsJsonAsync("/api/v1/entries", new[] { entry });
            nocResponse.EnsureSuccessStatusCode();
        }
    }

    /// <summary>
    /// Seeds treatments to both systems.
    /// Uses a dictionary to avoid sending default values that might affect behavior.
    /// </summary>
    protected async Task SeedTreatmentsAsync(params Treatment[] treatments)
    {
        foreach (var treatment in treatments)
        {
            // Convert to dictionary for both systems - only include non-null values
            // This matches real-world behavior where clients only send fields they have
            var treatmentData = new Dictionary<string, object?>
            {
                ["eventType"] = treatment.EventType,
                ["created_at"] = treatment.CreatedAt,
            };

            // Only include optional fields if they have values
            if (treatment.Insulin.HasValue) treatmentData["insulin"] = treatment.Insulin.Value;
            if (treatment.Carbs.HasValue) treatmentData["carbs"] = treatment.Carbs.Value;
            if (!string.IsNullOrEmpty(treatment.Notes)) treatmentData["notes"] = treatment.Notes;
            if (!string.IsNullOrEmpty(treatment.EnteredBy)) treatmentData["enteredBy"] = treatment.EnteredBy;
            if (treatment.Glucose.HasValue) treatmentData["glucose"] = treatment.Glucose.Value;
            if (!string.IsNullOrEmpty(treatment.GlucoseType)) treatmentData["glucoseType"] = treatment.GlucoseType;
            // Only include duration if non-zero (0 is default and means not explicitly set for seeding purposes)
            if (treatment.Duration > 0) treatmentData["duration"] = treatment.Duration.Value;

            var nsResponse = await NightscoutClient.PostAsJsonAsync("/api/v1/treatments", treatmentData);
            nsResponse.EnsureSuccessStatusCode();

            var nocResponse = await NocturneClient.PostAsJsonAsync("/api/v1/treatments", treatmentData);
            nocResponse.EnsureSuccessStatusCode();
        }
    }

    /// <summary>
    /// Seeds device status to both systems
    /// </summary>
    protected async Task SeedDeviceStatusAsync(params DeviceStatus[] statuses)
    {
        foreach (var status in statuses)
        {
            var nsStatus = new
            {
                device = status.Device,
                created_at = status.CreatedAt,
                uploaderBattery = status.Uploader?.Battery
            };

            var nsResponse = await NightscoutClient.PostAsJsonAsync("/api/v1/devicestatus", nsStatus);
            nsResponse.EnsureSuccessStatusCode();

            var nocResponse = await NocturneClient.PostAsJsonAsync("/api/v1/devicestatus", status);
            nocResponse.EnsureSuccessStatusCode();
        }
    }

    /// <summary>
    /// Creates and seeds a sequence of test entries using TestDataFactory
    /// </summary>
    protected async Task SeedEntrySequenceAsync(int count = 5, int intervalMinutes = 5)
    {
        var entries = TestDataFactory.CreateEntrySequence(count, intervalMinutes);
        await SeedEntriesAsync(entries);
    }

    #endregion

    #region Parity Assertion Helpers

    /// <summary>
    /// Asserts that GET requests to both systems return equivalent responses.
    /// Automatically uses V3 JWT client for /api/v3/* paths.
    /// </summary>
    protected async Task AssertGetParityAsync(
        string path,
        ComparisonOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Output.WriteLine($"Testing GET {path}");

        var nsClient = GetNightscoutClientForPath(path);
        var nsTask = nsClient.GetAsync(path, cancellationToken);
        var nocTask = NocturneClient.GetAsync(path, cancellationToken);

        await Task.WhenAll(nsTask, nocTask);

        var nsResponse = await nsTask;
        var nocResponse = await nocTask;

        await AssertResponseParityAsync(nsResponse, nocResponse, $"GET {path}", options, cancellationToken);
    }

    /// <summary>
    /// Asserts that POST requests to both systems return equivalent responses.
    /// Automatically uses V3 JWT client for /api/v3/* paths.
    /// </summary>
    protected async Task AssertPostParityAsync<T>(
        string path,
        T body,
        ComparisonOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Output.WriteLine($"Testing POST {path}");

        var nsClient = GetNightscoutClientForPath(path);
        var nsTask = nsClient.PostAsJsonAsync(path, body, cancellationToken);
        var nocTask = NocturneClient.PostAsJsonAsync(path, body, cancellationToken);

        await Task.WhenAll(nsTask, nocTask);

        var nsResponse = await nsTask;
        var nocResponse = await nocTask;

        await AssertResponseParityAsync(nsResponse, nocResponse, $"POST {path}", options, cancellationToken);
    }

    /// <summary>
    /// Asserts that PUT requests to both systems return equivalent responses.
    /// Automatically uses V3 JWT client for /api/v3/* paths.
    /// </summary>
    protected async Task AssertPutParityAsync<T>(
        string path,
        T body,
        ComparisonOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Output.WriteLine($"Testing PUT {path}");

        var nsClient = GetNightscoutClientForPath(path);
        var nsTask = nsClient.PutAsJsonAsync(path, body, cancellationToken);
        var nocTask = NocturneClient.PutAsJsonAsync(path, body, cancellationToken);

        await Task.WhenAll(nsTask, nocTask);

        var nsResponse = await nsTask;
        var nocResponse = await nocTask;

        await AssertResponseParityAsync(nsResponse, nocResponse, $"PUT {path}", options, cancellationToken);
    }

    /// <summary>
    /// Asserts that DELETE requests to both systems return equivalent responses.
    /// Automatically uses V3 JWT client for /api/v3/* paths.
    /// </summary>
    protected async Task AssertDeleteParityAsync(
        string path,
        ComparisonOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Output.WriteLine($"Testing DELETE {path}");

        var nsClient = GetNightscoutClientForPath(path);
        var nsTask = nsClient.DeleteAsync(path, cancellationToken);
        var nocTask = NocturneClient.DeleteAsync(path, cancellationToken);

        await Task.WhenAll(nsTask, nocTask);

        var nsResponse = await nsTask;
        var nocResponse = await nocTask;

        await AssertResponseParityAsync(nsResponse, nocResponse, $"DELETE {path}", options, cancellationToken);
    }

    /// <summary>
    /// Asserts parity for arbitrary HTTP requests with headers.
    /// Automatically uses V3 JWT client for /api/v3/* paths.
    /// </summary>
    protected async Task AssertParityAsync(
        HttpMethod method,
        string path,
        HttpContent? content = null,
        Dictionary<string, string>? headers = null,
        ComparisonOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Output.WriteLine($"Testing {method} {path}");

        var nsClient = GetNightscoutClientForPath(path);
        var nsRequest = CreateRequest(method, path, content, headers);
        var nocRequest = CreateRequest(method, path, content, headers);

        var nsTask = nsClient.SendAsync(nsRequest, cancellationToken);
        var nocTask = NocturneClient.SendAsync(nocRequest, cancellationToken);

        await Task.WhenAll(nsTask, nocTask);

        var nsResponse = await nsTask;
        var nocResponse = await nocTask;

        await AssertResponseParityAsync(nsResponse, nocResponse, $"{method} {path}", options, cancellationToken);
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string path,
        HttpContent? content,
        Dictionary<string, string>? headers)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };

        if (headers != null)
        {
            foreach (var (key, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(key, value);
            }
        }

        return request;
    }

    private async Task AssertResponseParityAsync(
        HttpResponseMessage nsResponse,
        HttpResponseMessage nocResponse,
        string context,
        ComparisonOptions? options,
        CancellationToken cancellationToken)
    {
        var comparer = options != null ? new ResponseComparer(options) : Comparer;
        var result = await comparer.CompareAsync(nsResponse, nocResponse, context, cancellationToken);

        if (!result.IsMatch)
        {
            Output.WriteLine(result.ToString());

            // Output raw responses for debugging
            Output.WriteLine("--- Nightscout Response ---");
            Output.WriteLine($"Status: {(int)nsResponse.StatusCode} {nsResponse.StatusCode}");
            Output.WriteLine(await nsResponse.Content.ReadAsStringAsync(cancellationToken));

            Output.WriteLine("--- Nocturne Response ---");
            Output.WriteLine($"Status: {(int)nocResponse.StatusCode} {nocResponse.StatusCode}");
            Output.WriteLine(await nocResponse.Content.ReadAsStringAsync(cancellationToken));
        }

        result.IsMatch.Should().BeTrue(result.ToString());
    }

    #endregion

    #region Utility Methods

    /// <summary>
    /// Logs a message to the test output
    /// </summary>
    protected void Log(string message) => Output.WriteLine(message);

    /// <summary>
    /// Gets a response from both systems for manual inspection
    /// </summary>
    protected async Task<(HttpResponseMessage Nightscout, HttpResponseMessage Nocturne)> GetBothResponsesAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var nsTask = NightscoutClient.GetAsync(path, cancellationToken);
        var nocTask = NocturneClient.GetAsync(path, cancellationToken);

        await Task.WhenAll(nsTask, nocTask);

        return (await nsTask, await nocTask);
    }

    #endregion
}
