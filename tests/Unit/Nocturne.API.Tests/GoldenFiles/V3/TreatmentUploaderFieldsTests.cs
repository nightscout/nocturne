using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.GoldenFiles.Infrastructure;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Tests.GoldenFiles.V3;

/// <summary>
/// AAPS, Loop and xDrip match their records by the <c>enteredBy</c> and <c>device</c> they sent, so
/// a decomposed treatment serves each as uploaded on v1 and v3: one never sent is not served, and
/// one sent is not dropped.
/// </summary>
public class TreatmentUploaderFieldsTests : GoldenFileTestBase
{
    private const long Date = 1768566512196L;

    public TreatmentUploaderFieldsTests(GoldenFileWebAppFactory factory) : base(factory) { }

    public static TheoryData<string, string> Records()
    {
        var data = new TheoryData<string, string>();
        foreach (var record in Payloads.Keys)
        foreach (var version in new[] { "v1", "v3" })
            data.Add(record, version);
        return data;
    }

    private static readonly Dictionary<string, (Dictionary<string, object?> Payload, Type Entity)> Payloads = new()
    {
        ["Bolus"] = (new() { ["eventType"] = "Correction Bolus", ["insulin"] = 1.5 }, typeof(BolusEntity)),
        ["MealBolus"] = (new() { ["eventType"] = "Meal Bolus", ["insulin"] = 2.5, ["carbs"] = 30 }, typeof(BolusEntity)),
        ["CarbIntake"] = (new() { ["eventType"] = "Carb Correction", ["carbs"] = 12 }, typeof(CarbIntakeEntity)),
        ["BGCheck"] = (
            new() { ["eventType"] = "BG Check", ["glucose"] = 110, ["glucoseType"] = "Finger", ["units"] = "mg/dl" },
            typeof(BGCheckEntity)),
        ["Note"] = (new() { ["eventType"] = "Note", ["notes"] = "walked" }, typeof(NoteEntity)),
        ["DeviceEvent"] = (new() { ["eventType"] = "Site Change" }, typeof(DeviceEventEntity)),
        ["BolusCalculation"] = (
            new() { ["eventType"] = "Bolus Wizard", ["bloodGlucoseInput"] = 140, ["insulinOnBoard"] = 0.2 },
            typeof(BolusCalculationEntity)),
        ["TempBasal"] = (
            new() { ["eventType"] = "Temp Basal", ["duration"] = 30, ["absolute"] = 1.25, ["rate"] = 1.25 },
            typeof(TempBasalEntity)),
    };

    [Theory]
    [MemberData(nameof(Records))]
    public async Task UploadedWithOnlyDevice_ServesDeviceAndNoEnteredBy(string record, string version)
    {
        var served = await UploadAndReadAsync(record, version, new() { ["device"] = "Pixel" });

        Served(served, "device").Should().Be("Pixel");
        Served(served, "enteredBy").Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(Records))]
    public async Task UploadedWithBoth_ServesBoth(string record, string version)
    {
        var served = await UploadAndReadAsync(record, version, new() { ["enteredBy"] = "openaps://AAPS", ["device"] = "Pixel" });

        Served(served, "enteredBy").Should().Be("openaps://AAPS");
        Served(served, "device").Should().Be("Pixel");
    }

    [Theory]
    [MemberData(nameof(Records))]
    public async Task UploadedWithOnlyEnteredBy_ServesEnteredByAndNoDevice(string record, string version)
    {
        var served = await UploadAndReadAsync(record, version, new() { ["enteredBy"] = "openaps://AAPS" });

        Served(served, "enteredBy").Should().Be("openaps://AAPS");
        Served(served, "device").Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(Records))]
    public async Task UploadedWithNeither_ServesNeither(string record, string version)
    {
        var served = await UploadAndReadAsync(record, version, []);

        Served(served, "enteredBy").Should().BeNull();
        Served(served, "device").Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(Records))]
    public async Task RowStoredWithoutKeptUploaderFields_ServesItsDeviceAsItDidBefore(string record, string version)
    {
        var served = await UploadAndReadAsync(
            record, version, new() { ["enteredBy"] = "openaps://AAPS", ["device"] = "Pixel" }, dropKeptFields: true);

        if (record == "TempBasal")
        {
            Served(served, "device").Should().Be("openaps://AAPS");
            Served(served, "enteredBy").Should().BeNull();
        }
        else
        {
            Served(served, "enteredBy").Should().Be("openaps://AAPS");
            Served(served, "device").Should().BeNull();
        }
    }

    [Fact]
    public async Task TempBasalWrittenByAV4Client_ServesItsAppAsEnteredByAndItsDeviceAsDevice()
    {
        var post = await PostJsonAsync("/api/v4/insulin/temp-basals", new Dictionary<string, object?>
        {
            ["timestamp"] = DateTimeOffset.FromUnixTimeMilliseconds(Date),
            ["rate"] = 1.25,
            ["durationMinutes"] = 30,
            ["device"] = "pump-1",
            ["app"] = "MyApp",
        });
        var postBody = await post.Content.ReadAsStringAsync();
        post.IsSuccessStatusCode.Should().BeTrue(postBody);
        var id = JsonDocument.Parse(postBody).RootElement.GetProperty("id").GetString();

        var get = await Client.GetAsync($"/api/v3/treatments/{id}");
        var body = await get.Content.ReadAsStringAsync();
        get.IsSuccessStatusCode.Should().BeTrue(body);
        var served = JsonDocument.Parse(body).RootElement.GetProperty("result");

        Served(served, "enteredBy").Should().Be("MyApp");
        Served(served, "device").Should().Be("pump-1");
    }

    private async Task<JsonElement> UploadAndReadAsync(
        string record, string version, Dictionary<string, object?> uploaderFields, bool dropKeptFields = false)
    {
        var (template, entity) = Payloads[record];
        var payload = new Dictionary<string, object?>(template) { ["date"] = Date };
        foreach (var (key, value) in uploaderFields)
            payload[key] = value;

        string? identifier = null;
        if (version == "v3")
            (_, identifier) = await PostV3Async("treatments", payload);
        else
        {
            var post = await PostJsonAsync("/api/v1/treatments", payload);
            post.IsSuccessStatusCode.Should().BeTrue(await post.Content.ReadAsStringAsync());
        }

        if (dropKeptFields)
            await DropAdditionalPropertiesAsync(entity);

        if (version == "v3")
        {
            var get = await Client.GetAsync($"/api/v3/treatments/{identifier}");
            var body = await get.Content.ReadAsStringAsync();
            get.IsSuccessStatusCode.Should().BeTrue(body);
            return JsonDocument.Parse(body).RootElement.GetProperty("result").Clone();
        }

        var list = await Client.GetAsync($"/api/v1/treatments?find[date]={Date}&count=10");
        var listBody = await list.Content.ReadAsStringAsync();
        list.IsSuccessStatusCode.Should().BeTrue(listBody);
        return JsonDocument.Parse(listBody).RootElement.EnumerateArray()
            .Single(t => t.GetProperty("eventType").GetString() == (string)payload["eventType"]!)
            .Clone();
    }

    private async Task DropAdditionalPropertiesAsync(Type entity)
    {
        using var scope = Factory.Services.CreateScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>()
            .CreateForTenantAsync(GoldenTenantId);

        var type = db.Model.FindEntityType(entity)!;
        var table = type.GetTableName();
        var column = type.FindProperty("AdditionalPropertiesJson")!.GetColumnName();
        await db.Database.ExecuteSqlRawAsync($"UPDATE \"{table}\" SET \"{column}\" = NULL");
    }

    private static string? Served(JsonElement treatment, string field) =>
        treatment.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
