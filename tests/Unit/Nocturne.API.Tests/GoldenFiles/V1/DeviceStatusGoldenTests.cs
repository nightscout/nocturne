using Nocturne.API.Tests.GoldenFiles.Infrastructure;

namespace Nocturne.API.Tests.GoldenFiles.V1;

public class DeviceStatusGoldenTests : GoldenFileTestBase
{
    public DeviceStatusGoldenTests(GoldenFileWebAppFactory factory) : base(factory) { }

    #region GET /api/v1/devicestatus

    [Fact]
    public async Task GetDeviceStatus_WithEmptyDb_ReturnsEmptyArray()
    {
        var response = await Client.GetAsync("/api/v1/devicestatus");
        var captured = await CaptureResponse(response);

        await Verify(captured);
    }

    #endregion
}
