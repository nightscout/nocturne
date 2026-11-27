using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.GoldenFiles.Infrastructure;

namespace Nocturne.API.Tests.GoldenFiles.V3;

/// <summary>
/// AAPS reads its own temp basals back and compares them with what it sent, so <c>app</c>,
/// <c>enteredBy</c> and <c>isReadOnly</c> come back as uploaded on v1 and v3, and the temp keeps the
/// origin a closed-loop uploader's temp defaults to.
/// </summary>
public class TempBasalUploadedIdentityTests : GoldenFileTestBase
{
    public TempBasalUploadedIdentityTests(GoldenFileWebAppFactory factory) : base(factory) { }

    private static object AapsTempBasal(long date) => new
    {
        app = "AAPS",
        enteredBy = "openaps://AndroidAPS",
        isReadOnly = false,
        date,
        duration = 30,
        eventType = "Temp Basal",
        absolute = 1.25,
        rate = 1.25,
        isValid = true,
        pumpId = 7,
        pumpSerial = "1",
        pumpType = "OMNIPOD_DASH",
        type = "NORMAL",
        utcOffset = 120,
    };

    [Fact]
    public async Task V3_PostThenGet_ServesAppEnteredByAndIsReadOnlyAsUploaded()
    {
        var (_, identifier) = await PostV3Async("treatments", AapsTempBasal(1768566512196L));

        var get = await Client.GetAsync($"/api/v3/treatments/{identifier}");
        using var body = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        var result = body.RootElement.GetProperty("result");

        AssertUploadedIdentity(result);
    }

    [Fact]
    public async Task V1_PostThenGet_ServesAppEnteredByAndIsReadOnlyAsUploaded()
    {
        const long date = 1768566812196L;
        var post = await PostJsonAsync("/api/v1/treatments", AapsTempBasal(date));
        post.IsSuccessStatusCode.Should().BeTrue(await post.Content.ReadAsStringAsync());

        var get = await Client.GetAsync($"/api/v1/treatments?find[date]={date}&count=10");
        using var body = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        var result = body.RootElement.EnumerateArray()
            .Single(t => t.GetProperty("eventType").GetString() == "Temp Basal");

        AssertUploadedIdentity(result);
    }

    private static void AssertUploadedIdentity(JsonElement treatment)
    {
        treatment.GetProperty("app").GetString().Should().Be("AAPS");
        treatment.GetProperty("enteredBy").GetString().Should().Be("openaps://AndroidAPS");
        treatment.TryGetProperty("isReadOnly", out var isReadOnly).Should().BeTrue("the upload sent isReadOnly");
        isReadOnly.ValueKind.Should().Be(JsonValueKind.False);
        treatment.GetProperty("basalOrigin").GetString().Should().Be("Algorithm");
        treatment.GetProperty("originEvidence").GetString().Should().Be("Uploader");
    }
}
