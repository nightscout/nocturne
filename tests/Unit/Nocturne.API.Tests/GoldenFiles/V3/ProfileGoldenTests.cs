using Nocturne.API.Tests.GoldenFiles.Infrastructure;

namespace Nocturne.API.Tests.GoldenFiles.V3;

public class ProfileGoldenTests : GoldenFileTestBase
{
    private static readonly string[] ServerStamped = ["_id", "identifier", "lastModified", "srvModified", "srvCreated", "created_at"];

    public ProfileGoldenTests(GoldenFileWebAppFactory factory) : base(factory) { }

    private static object DefaultProfile(double basal = 1.0) => new
    {
        defaultProfile = "Default",
        startDate = "2026-01-16T00:00:00.000Z",
        mills = 1768521600000L,
        units = "mg/dl",
        store = new Dictionary<string, object>
        {
            ["Default"] = new
            {
                dia = 5,
                timezone = "UTC",
                units = "mg/dl",
                carbratio = new[] { new { time = "00:00", value = 10.0 } },
                sens = new[] { new { time = "00:00", value = 50.0 } },
                basal = new[] { new { time = "00:00", value = basal } },
                target_low = new[] { new { time = "00:00", value = 100.0 } },
                target_high = new[] { new { time = "00:00", value = 120.0 } },
            },
        },
    };

    [Fact]
    public async Task PostThenGet_Profile_AnswersTheEnvelopeAroundTheIdentifier()
    {
        var (posted, fetched) = await PostThenGetV3Async("profile", DefaultProfile());

        await Verify(new { Posted = posted, Fetched = fetched }).DontScrubDateTimes().ScrubMembers(ServerStamped);
    }

    [Fact]
    public async Task Put_Profile_ServesTheReplacementOnTheNextRead()
    {
        var (_, identifier) = await PostV3Async("profile", DefaultProfile());

        var put = await PutJsonAsync($"/api/v3/profile/{identifier}", DefaultProfile(1.5));
        put.StatusCode.Should().Be(System.Net.HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        put.Content.Headers.LastModified.Should().NotBeNull("a v3 PUT answers Last-Modified");
        var fetched = await GetV3Async("profile", identifier);

        await Verify(new { Put = await CaptureResponse(put), Fetched = fetched })
            .DontScrubDateTimes()
            .ScrubMembers(ServerStamped);
    }
}
