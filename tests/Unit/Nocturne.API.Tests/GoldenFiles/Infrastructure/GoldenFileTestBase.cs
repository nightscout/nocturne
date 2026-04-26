using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
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
        var db = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();
        db.TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        db.SensorGlucose.AddRange(entities);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Seed meter glucose entities directly into the SQLite database.
    /// </summary>
    protected async Task SeedMeterGlucose(params MeterGlucoseEntity[] entities)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();
        db.TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        db.MeterGlucose.AddRange(entities);
        await db.SaveChangesAsync();
    }


    /// <summary>
    /// Seed food entities directly into the SQLite database.
    /// </summary>
    protected async Task SeedFoods(params FoodEntity[] foods)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();
        db.TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
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
        // Use raw SQLite connection directly (same shared connection used by all DbContexts)
        // to delete all data, bypassing EF query filters and change tracking.
        using var cmd = Factory.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM aps_snapshots; DELETE FROM pump_snapshots; DELETE FROM uploader_snapshots; DELETE FROM device_status_extras; DELETE FROM foods; DELETE FROM sensor_glucose; DELETE FROM meter_glucose; DELETE FROM calibrations;";
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }
}
