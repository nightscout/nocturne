using Nocturne.API.Tests.GoldenFiles.Infrastructure;

namespace Nocturne.API.Tests.GoldenFiles.V3;

public class DeviceStatusGoldenTests : GoldenFileTestBase
{
    public DeviceStatusGoldenTests(GoldenFileWebAppFactory factory) : base(factory) { }

    private static object AapsStatus() => new
    {
        app = "AAPS",
        device = "openaps://Pixel 7",
        created_at = "2026-01-16T11:15:00.000Z",
        date = 1768562100000L,
        uploaderBattery = 87,
        utcOffset = 120,
        isValid = true,
        pump = new
        {
            clock = "2026-01-16T11:15:00.000Z",
            battery = new { percent = 75 },
            reservoir = 120.5,
            status = new { status = "normal", timestamp = "2026-01-16T11:15:00.000Z" },
        },
        openaps = new
        {
            iob = new { iob = 1.25, basaliob = 0.4, activity = 0.02, time = "2026-01-16T11:15:00.000Z" },
            suggested = new
            {
                temp = "absolute",
                bg = 100,
                eventualBG = 95,
                rate = 0.8,
                duration = 30,
                reason = "In range",
                timestamp = "2026-01-16T11:15:00.000Z",
            },
        },
    };

    [Fact]
    public async Task PostThenGet_AapsDeviceStatus_RoundTripsTheEnvelopeAndTopLevelFields()
    {
        var (posted, fetched) = await PostThenGetV3Async("devicestatus", AapsStatus());

        await Verify(new { Posted = posted, Fetched = fetched })
            .ScrubMembers("_id", "identifier", "lastModified", "srvModified", "srvCreated");
    }

    [Fact]
    public async Task Put_AapsDeviceStatus_AnswersStatusAndLastModified()
    {
        var replaced = await PostThenWriteV3Async("devicestatus", AapsStatus(), HttpMethod.Put, AapsStatus());

        await Verify(replaced).ScrubMembers("lastModified");
    }
}
