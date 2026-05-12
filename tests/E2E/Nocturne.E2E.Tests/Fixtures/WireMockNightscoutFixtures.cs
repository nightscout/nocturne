using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Nocturne.E2E.Tests.Fixtures;

internal static class WireMockNightscoutFixtures
{
    public static void Load(WireMockServer server)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Nightscout");

        Stub(server, "/api/v1/status.json", File.ReadAllText(Path.Combine(dir, "status.json")));
        Stub(server, "/api/v1/profile.json", File.ReadAllText(Path.Combine(dir, "profile.json")));
        Stub(server, "/api/v1/entries.json", File.ReadAllText(Path.Combine(dir, "entries.json")));
        Stub(server, "/api/v1/treatments.json", File.ReadAllText(Path.Combine(dir, "treatments.json")));
    }

    private static void Stub(WireMockServer server, string path, string body)
    {
        server.Given(Request.Create().WithPath(path).UsingGet())
              .RespondWith(Response.Create()
                  .WithStatusCode(200)
                  .WithHeader("Content-Type", "application/json")
                  .WithBody(body));
    }
}
