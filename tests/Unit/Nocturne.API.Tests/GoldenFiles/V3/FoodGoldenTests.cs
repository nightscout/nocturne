using Nocturne.API.Tests.GoldenFiles.Infrastructure;

namespace Nocturne.API.Tests.GoldenFiles.V3;

public class FoodGoldenTests : GoldenFileTestBase
{
    public FoodGoldenTests(GoldenFileWebAppFactory factory) : base(factory) { }

    private static object Apple(int carbs = 10) =>
        new { date = 1768566512196L, type = "food", name = "Apple", carbs };

    [Fact]
    public async Task PostThenGet_Food_AnswersTheEnvelopeAroundTheIdentifier()
    {
        var (posted, fetched) = await PostThenGetV3Async("food", Apple());

        await Verify(new { Posted = posted, Fetched = fetched })
            .ScrubMembers("_id", "identifier", "lastModified", "srvModified", "srvCreated");
    }

    [Fact]
    public async Task Put_Food_AnswersStatusAndLastModified()
    {
        var replaced = await PostThenWriteV3Async("food", Apple(), HttpMethod.Put, Apple(25));

        await Verify(replaced).ScrubMembers("lastModified");
    }
}
