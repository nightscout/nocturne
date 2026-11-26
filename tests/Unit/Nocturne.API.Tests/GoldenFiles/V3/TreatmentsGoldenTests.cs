using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.GoldenFiles.Infrastructure;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Tests.GoldenFiles.V3;

public class TreatmentsGoldenTests : GoldenFileTestBase
{
    private static readonly string[] ServerStamped = ["_id", "identifier", "lastModified", "srvModified", "srvCreated"];

    public TreatmentsGoldenTests(GoldenFileWebAppFactory factory) : base(factory) { }

    private static object AapsSmb() => new
    {
        app = "AAPS",
        date = 1768566512196L,
        eventType = "Correction Bolus",
        insulin = 0.05,
        isBasalInsulin = false,
        isReadOnly = false,
        isValid = true,
        pumpId = 1,
        pumpSerial = "1",
        pumpType = "OMNIPOD_DASH",
        type = "SMB",
        utcOffset = 120,
    };

    [Fact]
    public async Task PostThenGet_AapsSmb_RoundTripsEveryUploadedField()
    {
        var (posted, fetched) = await PostThenGetV3Async("treatments", AapsSmb());

        await Verify(new { Posted = posted, Fetched = fetched }).DontScrubDateTimes().ScrubMembers(ServerStamped);
    }

    [Fact]
    public async Task PostThenGet_AapsMealBolus_RoundTripsEveryUploadedField()
    {
        var payload = new
        {
            app = "AAPS",
            date = 1768566512196L,
            eventType = "Meal Bolus",
            carbs = 30.0,
            insulin = 2.5,
            isBasalInsulin = false,
            isReadOnly = false,
            isValid = true,
            pumpId = 2,
            pumpSerial = "1",
            pumpType = "OMNIPOD_DASH",
            type = "NORMAL",
            utcOffset = 120,
        };

        var (posted, fetched) = await PostThenGetV3Async("treatments", payload);

        await Verify(new { Posted = posted, Fetched = fetched }).DontScrubDateTimes().ScrubMembers(ServerStamped);
    }

    [Fact]
    public async Task PostThenGet_AapsTempBasal_RoundTripsThePumpFields()
    {
        var payload = new
        {
            app = "AAPS",
            date = 1768566512196L,
            device = "Pixel",
            duration = 30,
            durationInMilliseconds = 1_800_000,
            eventType = "Temp Basal",
            isReadOnly = false,
            isValid = true,
            pumpId = 3,
            pumpSerial = "1",
            pumpType = "OMNIPOD_DASH",
            rate = 1.25,
            absolute = 1.25,
            type = "NORMAL",
            utcOffset = 120,
        };

        var (posted, fetched) = await PostThenGetV3Async("treatments", payload);

        await Verify(new { Posted = posted, Fetched = fetched }).DontScrubDateTimes().ScrubMembers(ServerStamped);
    }

    [Fact]
    public async Task PostThenGet_BolusWithoutTheAapsFields_ServesNoneOfThem()
    {
        var payload = new { date = 1768566512196L, eventType = "Correction Bolus", insulin = 1.5, enteredBy = "xDrip" };

        var (_, fetched) = await PostThenGetV3Async("treatments", payload);

        await Verify(fetched).DontScrubDateTimes().ScrubMembers(ServerStamped);
    }

    [Fact]
    public async Task Get_ConnectorBolusWithAPumpRecordId_ServesNoPumpIdOnV1AndV3()
    {
        var id = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
        var at = DateTime.UtcNow;
        using (var scope = Factory.Services.CreateScope())
        {
            await using var db = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>()
                .CreateForTenantAsync(GoldenTenantId);
            db.Boluses.Add(new BolusEntity
            {
                Id = id,
                TenantId = GoldenTenantId,
                Timestamp = at,
                Insulin = 2.0,
                PumpRecordId = "123",
                DataSource = "tandem-connector",
                SysCreatedAt = at,
                SysUpdatedAt = at,
            });
            await db.SaveChangesAsync();
        }

        var v1 = await Client.GetAsync("/api/v1/treatments");
        var v3 = await Client.GetAsync("/api/v3/treatments");

        var captured = new { V3 = await CaptureResponse(v3), V1 = await CaptureResponse(v1) };
        await Verify(captured)
            .ScrubMembers("_id", "identifier", "srvModified", "srvCreated", "mills", "date", "created_at", "sysTime", "dateString");
    }

    [Fact]
    public async Task Get_BolusStoredAsAV4Record_ServesItByItsGuid()
    {
        var id = Guid.Parse("5f0a1b2c-3d4e-5f60-8172-93a4b5c6d7e8");
        var at = DateTimeOffset.FromUnixTimeMilliseconds(1768566512196L).UtcDateTime;
        using (var scope = Factory.Services.CreateScope())
        {
            await using var db = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>()
                .CreateForTenantAsync(GoldenTenantId);
            db.Boluses.Add(new BolusEntity
            {
                Id = id,
                TenantId = GoldenTenantId,
                Timestamp = at,
                Insulin = 2.0,
                DataSource = "tandem-connector",
                SysCreatedAt = at,
                SysUpdatedAt = at,
            });
            await db.SaveChangesAsync();
        }

        var response = await Client.GetAsync($"/api/v3/treatments/{id}");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        await Verify(await CaptureResponse(response)).DontScrubDateTimes().ScrubMembers("srvModified", "srvCreated");
    }

    [Fact]
    public async Task Put_AapsSmb_AnswersStatusAndLastModified()
    {
        var replaced = await PostThenWriteV3Async("treatments", AapsSmb(), HttpMethod.Put, AapsSmb());

        await Verify(replaced).ScrubMembers("lastModified");
    }

    [Fact]
    public async Task Patch_AapsSmb_AnswersStatusOnly()
    {
        var patched = await PostThenWriteV3Async("treatments", AapsSmb(), HttpMethod.Patch, new { insulin = 0.1 });

        await Verify(patched);
    }
}
