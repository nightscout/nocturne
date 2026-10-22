using Nocturne.API.Tests.GoldenFiles.Infrastructure;

namespace Nocturne.API.Tests.GoldenFiles.V1;

public class StatusGoldenTests : GoldenFileTestBase
{
    public StatusGoldenTests(GoldenFileWebAppFactory factory) : base(factory) { }

    #region GET /api/v1/status (JSON)

    [Fact]
    public async Task GetStatus_WithJsonAccept_ReturnsJsonStatus()
    {
        // Client already has Accept: application/json set by base class
        var response = await Client.GetAsync("/api/v1/status");
        var captured = await CaptureResponse(response);

        await Verify(captured)
            .ScrubMembers("serverTime", "serverTimeEpoch", "version");
    }

    #endregion

    #region GET /api/v1/status.json

    [Fact]
    public async Task GetStatusJson_AlwaysReturnsJson()
    {
        var response = await Client.GetAsync("/api/v1/status.json");
        var captured = await CaptureResponse(response);

        await Verify(captured)
            .ScrubMembers("serverTime", "serverTimeEpoch", "version");
    }

    #endregion

    #region GET /api/v1/status (HTML)

    [Fact]
    public async Task GetStatus_WithHtmlAccept_ReturnsHtmlStatusOk()
    {
        // Override the default JSON Accept header to request HTML
        Client.DefaultRequestHeaders.Remove("Accept");
        Client.DefaultRequestHeaders.Add("Accept", "text/html");

        var response = await Client.GetAsync("/api/v1/status");
        var captured = await CaptureResponse(response);

        await Verify(captured);
    }

    #endregion
}
