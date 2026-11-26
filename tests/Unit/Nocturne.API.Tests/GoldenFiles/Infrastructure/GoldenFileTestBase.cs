using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Services;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace Nocturne.API.Tests.GoldenFiles.Infrastructure;

/// <summary>
/// Base class for golden file tests. Provides HTTP client, DB seeding, and Verify helpers.
/// </summary>
public abstract class GoldenFileTestBase : IClassFixture<GoldenFileWebAppFactory>, IAsyncLifetime
{
    protected readonly GoldenFileWebAppFactory Factory;
    protected readonly HttpClient Client;

    protected GoldenFileTestBase(GoldenFileWebAppFactory factory)
    {
        Factory = factory;
        Client = factory.CreateClient();
        // Default to JSON Accept header (Nightscout client behavior)
        Client.DefaultRequestHeaders.Add("Accept", "application/json");
        // Disable server-side response caching to ensure test isolation
        Client.DefaultRequestHeaders.Add("Cache-Control", "no-cache");
    }

    public virtual async Task InitializeAsync()
    {
        await CleanupDatabase();
    }

    public virtual Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Capture an HTTP response for Verify snapshot comparison.
    /// Includes status code, relevant headers, and parsed JSON body.
    /// </summary>
    protected async Task<object> CaptureResponse(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var contentType = response.Content.Headers.ContentType?.ToString();

        object? parsedBody = null;
        if (contentType?.Contains("application/json") == true && !string.IsNullOrEmpty(body))
        {
            // Use Argon JToken (Verify's native serializer) for proper snapshot output
            parsedBody = Argon.JToken.Parse(body);
        }
        else
        {
            parsedBody = body;
        }

        var headers = new Dictionary<string, string>();
        if (response.Headers.TryGetValues("Last-Modified", out var lastModified))
            headers["Last-Modified"] = "{scrubbed}";

        return new
        {
            StatusCode = (int)response.StatusCode,
            ContentType = contentType,
            Headers = headers.Count > 0 ? headers : null,
            Body = parsedBody,
        };
    }

    /// <summary>
    /// Seed sensor glucose entities directly into the SQLite database.
    /// </summary>
    protected async Task SeedSensorGlucose(params SensorGlucoseEntity[] entities)
    {
        using var scope = Factory.Services.CreateScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>()
            .CreateForTenantAsync(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        db.SensorGlucose.AddRange(entities);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Seed meter glucose entities directly into the SQLite database.
    /// </summary>
    protected async Task SeedMeterGlucose(params MeterGlucoseEntity[] entities)
    {
        using var scope = Factory.Services.CreateScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>()
            .CreateForTenantAsync(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        db.MeterGlucose.AddRange(entities);
        await db.SaveChangesAsync();
    }


    /// <summary>
    /// Seed food entities directly into the SQLite database.
    /// </summary>
    protected async Task SeedFoods(params FoodEntity[] foods)
    {
        using var scope = Factory.Services.CreateScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>()
            .CreateForTenantAsync(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        db.Foods.AddRange(foods);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// POST JSON to an endpoint and return the response.
    /// </summary>
    protected async Task<HttpResponseMessage> PostJsonAsync(string url, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await Client.PostAsync(url, content);
    }

    /// <summary>
    /// POST a document to a v3 collection and assert the 201 envelope; returns the response for
    /// Verify and the identifier it answered.
    /// </summary>
    protected async Task<(object Posted, string Identifier)> PostV3Async(string collection, object payload)
    {
        var post = await PostJsonAsync($"/api/v3/{collection}", payload);
        var postBody = await post.Content.ReadAsStringAsync();
        post.StatusCode.Should().Be(System.Net.HttpStatusCode.Created, postBody);

        using var posted = System.Text.Json.JsonDocument.Parse(postBody);
        posted.RootElement.GetProperty("status").GetInt32().Should().Be(201);
        posted.RootElement.GetProperty("lastModified").GetInt64().Should().BeGreaterThan(0);
        var identifier = posted.RootElement.GetProperty("identifier").GetString();
        identifier.Should().NotBeNullOrEmpty();

        return (await CaptureResponse(post), identifier!);
    }

    /// <summary>
    /// Read a v3 document by identifier and assert the 200 envelope around the same identifier;
    /// returns the response for Verify.
    /// </summary>
    protected async Task<object> GetV3Async(string collection, string identifier)
    {
        var get = await Client.GetAsync($"/api/v3/{collection}/{identifier}");
        var getBody = await get.Content.ReadAsStringAsync();
        get.StatusCode.Should().Be(System.Net.HttpStatusCode.OK, getBody);

        using var fetched = System.Text.Json.JsonDocument.Parse(getBody);
        fetched.RootElement.GetProperty("status").GetInt32().Should().Be(200);
        fetched.RootElement.GetProperty("result").GetProperty("identifier").GetString().Should().Be(identifier);

        return await CaptureResponse(get);
    }

    /// <summary>
    /// POST a document to a v3 collection, then read it back by the identifier the POST answered.
    /// </summary>
    protected async Task<(object Posted, object Fetched)> PostThenGetV3Async(string collection, object payload)
    {
        var (posted, identifier) = await PostV3Async(collection, payload);
        return (posted, await GetV3Async(collection, identifier));
    }

    /// <summary>
    /// POST a document to a v3 collection, then send <paramref name="method"/> with
    /// <paramref name="body"/> to the identifier the POST answered; returns that write's response.
    /// </summary>
    protected async Task<object> PostThenWriteV3Async(string collection, object payload, HttpMethod method, object body)
    {
        var (_, identifier) = await PostV3Async(collection, payload);
        var request = new HttpRequestMessage(method, $"/api/v3/{collection}/{identifier}")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        var response = await Client.SendAsync(request);
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        response.Content.Headers.LastModified.Should().NotBeNull("a v3 PUT and PATCH answer Last-Modified");
        return await CaptureResponse(response);
    }

    /// <summary>
    /// PUT JSON to an endpoint and return the response.
    /// </summary>
    protected async Task<HttpResponseMessage> PutJsonAsync(string url, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await Client.PutAsync(url, content);
    }

    private Task CleanupDatabase()
    {
        // Raw SQL on the factory's connection to the same database bypasses EF query filters and
        // change tracking.
        using var cmd = Factory.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM aps_snapshots; DELETE FROM pump_snapshots; DELETE FROM uploader_snapshots; DELETE FROM device_status_extras; DELETE FROM sensor_state_reports; DELETE FROM foods; DELETE FROM boluses; DELETE FROM carb_intakes; DELETE FROM temp_basals; DELETE FROM profile_activations; DELETE FROM sensor_glucose; DELETE FROM meter_glucose; DELETE FROM calibrations;";
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }
}
