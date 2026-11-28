using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Services;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// A treatment stored as a state span or a profile activation (a profile switch, an effective profile
/// switch, an override, a temporary target) serves the <c>enteredBy</c>, <c>device</c>, <c>isReadOnly</c>
/// and <c>app</c> it was uploaded with on v1 and v3, by the rule a decomposed record serves them by.
/// </summary>
[Collection(ApiIntegrationShards.Two)]
[Trait("Category", "Integration")]
public class SpanTreatmentUploaderFieldsIntegrationTests : ApiIntegrationTestBase
{
    public SpanTreatmentUploaderFieldsIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    public static TheoryData<string> Types => new()
    {
        "Profile Switch", "Effective Profile Switch", "Temporary Override", "Temporary Target",
    };

    private static JsonObject Upload(string type)
    {
        var created = DateTime.UtcNow.AddMinutes(-30);
        var createdAt = created.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        return type switch
        {
            "Effective Profile Switch" => JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(
                AapsProfileSwitchReadbackIntegrationTests.EffectiveSwitchV1(new DateTimeOffset(created).ToUnixTimeMilliseconds())))!
                .AsObject(),
            "Profile Switch" => new JsonObject { ["eventType"] = type, ["created_at"] = createdAt, ["profile"] = "Weekend", ["duration"] = 0 },
            "Temporary Override" => new JsonObject
            {
                ["eventType"] = type, ["created_at"] = createdAt, ["reason"] = "Running", ["duration"] = 60,
                ["targetTop"] = 140, ["targetBottom"] = 120,
            },
            _ => new JsonObject
            {
                ["eventType"] = type, ["created_at"] = createdAt, ["reason"] = "Activity", ["duration"] = 45,
                ["targetTop"] = 8.0, ["targetBottom"] = 8.0, ["units"] = "mmol",
            },
        };
    }

    private static string? ServedString(JsonObject served, string field) =>
        served[field] is JsonValue { } value && value.TryGetValue<string>(out var text) ? text : null;

    private async Task<JsonObject[]> UploadAndReadAsync(string type, params (string Key, JsonNode? Value)[] fields)
    {
        var client = CreateAuthenticatedClient();
        var treatment = Upload(type);
        treatment.Remove("enteredBy");
        foreach (var (key, value) in fields)
            treatment[key] = value;

        var post = await client.PostAsJsonAsync("/api/v1/treatments", new[] { treatment });
        post.StatusCode.Should().Be(HttpStatusCode.OK, await post.Content.ReadAsStringAsync());
        return await ReadAsync(client);
    }

    private async Task<JsonObject[]> ReadAsync(HttpClient client)
    {
        await SettleHistoryAsync();
        var v1 = await GetAsync(client, "/api/v1/treatments");
        var list = await GetAsync(client, "/api/v3/treatments");
        var identifier = list["result"]!.AsArray().Should().ContainSingle().Subject!["identifier"]!.GetValue<string>();
        var byId = await GetAsync(client, $"/api/v3/treatments/{identifier}");
        var history = await GetAsync(client, "/api/v3/treatments/history/0");

        return
        [
            v1.AsArray().Should().ContainSingle().Subject!.AsObject(),
            byId["result"]!.AsObject(),
            history["result"]!.AsArray().Should().ContainSingle().Subject!.AsObject(),
        ];
    }

    private static async Task<JsonNode> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    [Theory]
    [MemberData(nameof(Types))]
    public async Task Uploaded_with_a_device_is_served_the_device(string type)
    {
        var served = await UploadAndReadAsync(type, ("enteredBy", "openaps://AAPS"), ("device", "Pixel"));

        foreach (var treatment in served)
        {
            ServedString(treatment, "enteredBy").Should().Be("openaps://AAPS");
            ServedString(treatment, "device").Should().Be("Pixel");
        }
    }

    public static TheoryData<string> V3Types => new() { "Temporary Override", "Temporary Target" };

    [Theory]
    [MemberData(nameof(V3Types))]
    public async Task Uploaded_on_v3_is_served_every_uploader_field(string type)
    {
        var client = CreateAuthenticatedClient();
        var treatment = Upload(type);
        treatment.Remove("created_at");
        treatment["date"] = DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeMilliseconds();
        treatment["utcOffset"] = 0;
        treatment["app"] = "AAPS";
        treatment["device"] = "Pixel";
        treatment["enteredBy"] = "openaps://AAPS";
        treatment["isReadOnly"] = true;

        var post = await client.PostAsJsonAsync("/api/v3/treatments", treatment);
        post.IsSuccessStatusCode.Should().BeTrue(await post.Content.ReadAsStringAsync());

        foreach (var served in await ReadAsync(client))
        {
            ServedString(served, "enteredBy").Should().Be("openaps://AAPS");
            ServedString(served, "device").Should().Be("Pixel");
            ServedString(served, "app").Should().Be("AAPS");
            served["isReadOnly"]!.GetValue<bool>().Should().BeTrue();
        }
    }

    [Theory]
    [MemberData(nameof(Types))]
    public async Task Uploaded_with_only_a_device_is_served_no_entered_by(string type)
    {
        var served = await UploadAndReadAsync(type, ("device", "Pixel"));

        foreach (var treatment in served)
        {
            ServedString(treatment, "device").Should().Be("Pixel");
            ServedString(treatment, "enteredBy").Should().BeNull();
        }
    }

    [Theory]
    [MemberData(nameof(Types))]
    public async Task Uploaded_with_only_an_entered_by_is_served_no_device(string type)
    {
        var served = await UploadAndReadAsync(type, ("enteredBy", "openaps://AAPS"));

        foreach (var treatment in served)
        {
            ServedString(treatment, "enteredBy").Should().Be("openaps://AAPS");
            ServedString(treatment, "device").Should().BeNull();
        }
    }

    [Theory]
    [MemberData(nameof(Types))]
    public async Task Uploaded_with_neither_is_served_neither(string type)
    {
        var served = await UploadAndReadAsync(type);

        foreach (var treatment in served)
        {
            ServedString(treatment, "enteredBy").Should().BeNull();
            ServedString(treatment, "device").Should().BeNull();
            treatment["isReadOnly"].Should().BeNull();
            ServedString(treatment, "app").Should().BeNull();
        }
    }

    [Theory]
    [MemberData(nameof(Types))]
    public async Task Uploaded_read_only_with_an_app_is_served_both(string type)
    {
        var served = await UploadAndReadAsync(type, ("isReadOnly", true), ("app", "AAPS"));

        foreach (var treatment in served)
        {
            treatment["isReadOnly"]!.GetValue<bool>().Should().BeTrue();
            ServedString(treatment, "app").Should().Be("AAPS");
        }
    }

    [Theory]
    [MemberData(nameof(Types))]
    public async Task Uploaded_not_read_only_is_served_false(string type)
    {
        var served = await UploadAndReadAsync(type, ("isReadOnly", false));

        foreach (var treatment in served)
            treatment["isReadOnly"]!.GetValue<bool>().Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Types))]
    public async Task A_row_stored_with_only_an_entered_by_is_served_it_and_no_device(string type)
    {
        var client = CreateAuthenticatedClient();
        var treatment = Upload(type);
        treatment["enteredBy"] = "openaps://AAPS";
        treatment["device"] = "Pixel";
        treatment["isReadOnly"] = true;
        var post = await client.PostAsJsonAsync("/api/v1/treatments", new[] { treatment });
        post.StatusCode.Should().Be(HttpStatusCode.OK, await post.Content.ReadAsStringAsync());

        await StripKeptFieldsAsync();

        foreach (var served in await ReadAsync(client))
        {
            ServedString(served, "enteredBy").Should().Be("openaps://AAPS");
            ServedString(served, "device").Should().BeNull();
            served["isReadOnly"].Should().BeNull();
        }
    }

    /// <summary>Rewrites each stored span and activation as one written before the uploader fields were kept.</summary>
    private Task StripKeptFieldsAsync() =>
        WithTenantScopeAsync(async sp =>
        {
            await using var db = await sp.GetRequiredService<INocturneDbContextFactory>().CreateAsync();
            foreach (var (entity, property) in new[]
                     {
                         (typeof(StateSpanEntity), nameof(StateSpanEntity.MetadataJson)),
                         (typeof(ProfileActivationEntity), nameof(V4TimeSeriesEntityBase.AdditionalPropertiesJson)),
                     })
            {
                var type = db.Model.FindEntityType(entity)!;
                var column = type.FindProperty(property)!;
                await db.Database.ExecuteSqlRawAsync(
                    $"""
                     UPDATE "{type.GetTableName()}"
                     SET "{column.GetColumnName()}" = ("{column.GetColumnName()}"::jsonb - 'device' - 'isReadOnly')::{column.GetColumnType()}
                     WHERE "{column.GetColumnName()}" IS NOT NULL
                     """);
            }

            return true;
        });
}
