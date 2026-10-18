using Nocturne.API.Tests.GoldenFiles.Infrastructure;

namespace Nocturne.API.Tests.GoldenFiles.V3;

public class StatusGoldenTests : GoldenFileTestBase
{
    public StatusGoldenTests(GoldenFileWebAppFactory factory) : base(factory) { }

    #region GET /api/v3/status

    [Fact]
    public async Task GetStatus_ReturnsExtendedStatusWithPermissions()
    {
        var response = await Client.GetAsync("/api/v3/status");
        var captured = await CaptureResponse(response);

        await Verify(captured)
            .ScrubMembers("serverTime", "serverTimeEpoch", "srvDate", "uptimeMs", "head", "version");
    }

    #endregion

    #region GET /api/v3/version

    [Fact]
    public async Task GetVersion_ReturnsV3Envelope()
    {
        var response = await Client.GetAsync("/api/v3/version");
        var captured = await CaptureResponse(response);

        await Verify(captured)
            .ScrubMembers("version");
    }

    #endregion
}
