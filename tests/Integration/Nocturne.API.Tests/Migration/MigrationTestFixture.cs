using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Testcontainers.MongoDb;
using Xunit;

namespace Nocturne.API.Tests.Integration.Migration;

/// <summary>
/// Manages a MongoDB Testcontainer seeded with real Nightscout fixture data
/// and a lightweight mock Nightscout API server for testing both migration paths.
/// </summary>
public class MigrationTestFixture : IAsyncLifetime
{
    private readonly MongoDbContainer _mongoContainer;
    private WebApplication? _mockNightscoutApp;
    private string? _mockNightscoutUrl;

    public const string DatabaseName = "nightscout_test";
    public const string TestApiSecret = "test-migration-secret-12345";

    /// <summary>
    /// MongoDB connection string accessible from the host (where the Aspire API process runs)
    /// </summary>
    public string MongoConnectionString => _mongoContainer.GetConnectionString();

    /// <summary>
    /// URL of the mock Nightscout API server
    /// </summary>
    public string MockNightscoutUrl =>
        _mockNightscoutUrl ?? throw new InvalidOperationException("Mock server not started");

    /// <summary>
    /// Number of entries loaded from fixtures
    /// </summary>
    public int EntryCount { get; private set; }

    /// <summary>
    /// Number of treatments loaded from fixtures
    /// </summary>
    public int TreatmentCount { get; private set; }

    public MigrationTestFixture()
    {
        _mongoContainer = new MongoDbBuilder()
            .WithImage("mongo:7")
            .Build();
    }

    public async Task InitializeAsync()
    {
        await _mongoContainer.StartAsync();

        // Seed MongoDB with fixture data
        var client = new MongoClient(MongoConnectionString);
        var database = client.GetDatabase(DatabaseName);

        EntryCount = await SeedCollectionAsync(database, "entries", "nightscout-entries.json");
        TreatmentCount = await SeedCollectionAsync(database, "treatments", "nightscout-treatments.json");

        // Start mock Nightscout API server
        await StartMockNightscoutServerAsync();
    }

    public async Task DisposeAsync()
    {
        if (_mockNightscoutApp != null)
        {
            await _mockNightscoutApp.StopAsync();
            await _mockNightscoutApp.DisposeAsync();
        }

        await _mongoContainer.DisposeAsync();
    }

    private async Task<int> SeedCollectionAsync(
        IMongoDatabase database,
        string collectionName,
        string fixtureFileName)
    {
        var json = await LoadFixtureAsync(fixtureFileName);
        var documents = BsonSerializer.Deserialize<BsonArray>(json)
            .Select(v => v.AsBsonDocument)
            .ToList();

        if (documents.Count > 0)
        {
            var collection = database.GetCollection<BsonDocument>(collectionName);
            await collection.InsertManyAsync(documents);
        }

        return documents.Count;
    }

    private static async Task<string> LoadFixtureAsync(string fileName)
    {
        var assembly = typeof(MigrationTestFixture).Assembly;
        var basePath = Path.GetDirectoryName(assembly.Location)!;

        // Try file-based loading first (fixture files copied to output)
        var filePath = Path.Combine(basePath, "Migration", "Fixtures", fileName);
        if (File.Exists(filePath))
        {
            return await File.ReadAllTextAsync(filePath);
        }

        // Fallback: search relative to the test project directory
        var projectDir = FindProjectDirectory();
        if (projectDir != null)
        {
            filePath = Path.Combine(projectDir, "Migration", "Fixtures", fileName);
            if (File.Exists(filePath))
            {
                return await File.ReadAllTextAsync(filePath);
            }
        }

        throw new FileNotFoundException(
            $"Fixture file '{fileName}' not found. Searched: {filePath}");
    }

    private static string? FindProjectDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.csproj").Length > 0)
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private async Task StartMockNightscoutServerAsync()
    {
        // Load fixture data as standard JSON for the mock Nightscout API responses
        var entriesJson = await LoadFixtureAsync("nightscout-entries.json");
        var treatmentsJson = await LoadFixtureAsync("nightscout-treatments.json");

        // Convert EJSON ObjectIds to plain string _id for Nightscout API compatibility
        var entriesApiJson = ConvertEjsonToNightscoutApiJson(entriesJson);
        var treatmentsApiJson = ConvertEjsonToNightscoutApiJson(treatmentsJson);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        _mockNightscoutApp = builder.Build();

        _mockNightscoutApp.MapGet("/api/v1/status", () => Results.Json(new
        {
            status = "ok",
            name = "Nightscout",
            version = "15.0.0",
            apiEnabled = true
        }));

        _mockNightscoutApp.MapGet("/api/v1/entries.json", (HttpContext ctx) =>
        {
            return Results.Content(entriesApiJson, "application/json");
        });

        _mockNightscoutApp.MapGet("/api/v1/treatments.json", (HttpContext ctx) =>
        {
            return Results.Content(treatmentsApiJson, "application/json");
        });

        await _mockNightscoutApp.StartAsync();

        // Get the actual bound address (port 0 was resolved to a real port)
        var server = _mockNightscoutApp.Services.GetRequiredService<IServer>();
        var addressFeature = server.Features.Get<IServerAddressesFeature>();
        _mockNightscoutUrl = addressFeature?.Addresses.First()
            ?? throw new InvalidOperationException("Could not determine mock server address");
    }

    /// <summary>
    /// Converts EJSON format (with {"$oid": "..."}) to standard Nightscout API JSON format
    /// where _id is a plain string field.
    /// </summary>
    private static string ConvertEjsonToNightscoutApiJson(string ejson)
    {
        using var doc = JsonDocument.Parse(ejson);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartArray();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                writer.WriteStartObject();
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.Name == "_id" && prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        // Convert {"$oid": "hex"} to plain string "_id": "hex"
                        if (prop.Value.TryGetProperty("$oid", out var oidValue))
                        {
                            writer.WriteString("_id", oidValue.GetString());
                        }
                    }
                    else
                    {
                        prop.WriteTo(writer);
                    }
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
