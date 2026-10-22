using System.Net;
using FluentAssertions;
using Nocturne.API.Tests.GoldenFiles.Infrastructure;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.API.Tests.GoldenFiles.V1;

public class EntriesTextFormatTests : GoldenFileTestBase, IClassFixture<FixedClockGoldenFileWebAppFactory>
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private const long Mills = 1711454400000;

    private const string Row = "\"2024-03-26T12:00:00.000Z\"\t1711454400000\t120\t\"Flat\"\t\"xDrip-DexcomG6\"";

    private const string CsvRow = "\"2024-03-26T12:00:00.000Z\",1711454400000,120,\"Flat\",\"xDrip-DexcomG6\"";

    public EntriesTextFormatTests(FixedClockGoldenFileWebAppFactory factory) : base(factory) { }

    private Task SeedOne() =>
        SeedSensorGlucose(
            new SensorGlucoseEntity
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
                TenantId = TestTenantId,
                LegacyId = "aaaaaaaaaaaaaaaaaaaaa001",
                Mgdl = 120,
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(Mills).UtcDateTime,
                Direction = "Flat",
                Device = "xDrip-DexcomG6",
                SysCreatedAt = new DateTime(2024, 3, 26, 12, 0, 0, DateTimeKind.Utc),
                SysUpdatedAt = new DateTime(2024, 3, 26, 12, 0, 0, DateTimeKind.Utc),
            }
        );

    private async Task<(HttpStatusCode Status, string? ContentType, string Body)> Get(
        string url,
        string? accept = null
    )
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (accept is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept", accept);
        }

        using var plain = Factory.CreateClient();
        var response = await plain.SendAsync(request);
        return (
            response.StatusCode,
            response.Content.Headers.ContentType?.MediaType,
            await response.Content.ReadAsStringAsync()
        );
    }

    [Theory]
    [InlineData("/api/v1/entries.txt?count=1", "text/plain", Row)]
    [InlineData("/api/v1/entries/current.txt", "text/plain", Row)]
    [InlineData("/api/v1/entries/sgv.csv", "text/csv", CsvRow)]
    [InlineData("/api/v1/entries.tsv", "text/tab-separated-values", Row)]
    [InlineData("/api/v1/entries/sgv.txt?count=1", "text/plain", Row)]
    [InlineData("/api/v1/entries/aaaaaaaaaaaaaaaaaaaaa001.csv", "text/csv", CsvRow)]
    [InlineData("/api/v1/times/2024-03.txt?count=1", "text/plain", Row)]
    [InlineData("/api/v1/slice/entries/dateString/sgv/2024-03.tsv", "text/tab-separated-values", Row)]
    public async Task TextExtension_AnswersTheDocumentedLayout(
        string url,
        string contentType,
        string body
    )
    {
        await SeedOne();

        var result = await Get(url);

        result.Status.Should().Be(HttpStatusCode.OK);
        result.ContentType.Should().Be(contentType);
        result.Body.Should().Be(body);
    }

    [Theory]
    [InlineData("/api/v1/entries/current", "text/plain", "text/plain", Row)]
    [InlineData("/api/v1/entries/sgv", "text/csv", "text/csv", CsvRow)]
    [InlineData("/api/v1/entries", "text/tab-separated-values", "text/tab-separated-values", Row)]
    [InlineData("/api/v1/times/2024-03", "text/plain", "text/plain", Row)]
    [InlineData("/api/v1/slice/entries/dateString/sgv/2024-03", "text/csv", "text/csv", CsvRow)]
    public async Task TextAcceptHeader_AnswersTheDocumentedLayout(
        string url,
        string accept,
        string contentType,
        string body
    )
    {
        await SeedOne();

        var result = await Get(url, accept);

        result.Status.Should().Be(HttpStatusCode.OK);
        result.ContentType.Should().Be(contentType);
        result.Body.Should().Be(body);
    }

    [Theory]
    [InlineData("/api/v1/entries/current.txt")]
    [InlineData("/api/v1/entries.csv")]
    [InlineData("/api/v1/entries/sgv.tsv")]
    public async Task TextExtension_OnAnEmptyStoreAnswersAnEmptyBody(string url)
    {
        var result = await Get(url);

        result.Status.Should().Be(HttpStatusCode.OK);
        result.Body.Should().BeEmpty();
    }

    [Theory]
    [InlineData("/api/v1/entries/current.json")]
    [InlineData("/api/v1/entries.json?count=1")]
    [InlineData("/api/v1/entries/sgv.json")]
    public async Task JsonExtension_StillAnswersJson(string url)
    {
        await SeedOne();

        var result = await Get(url);

        result.Status.Should().Be(HttpStatusCode.OK);
        result.ContentType.Should().Be("application/json");
        result.Body.Should().StartWith("[{").And.Contain("\"sgv\":120");
    }

    [Theory]
    [InlineData("/api/v1/entries")]
    [InlineData("/api/v1/entries/current")]
    [InlineData("/api/v1/entries/sgv")]
    [InlineData("/api/v1/times/2024-03")]
    [InlineData("/api/v1/slice/entries/dateString/sgv/2024-03")]
    public async Task AJsonAcceptRankedAboveTextStaysJson(string url)
    {
        await SeedOne();

        foreach (var accept in new[] { "application/json, text/csv;q=0.1", "application/json, text/plain", "text/csv;q=0.1, application/json" })
        {
            var result = await Get(url, accept);

            result.Status.Should().Be(HttpStatusCode.OK, accept);
            result.ContentType.Should().Be("application/json", accept);
        }
    }

    [Theory]
    [InlineData("/api/v1/entries")]
    [InlineData("/api/v1/entries/current")]
    [InlineData("/api/v1/entries/sgv")]
    [InlineData("/api/v1/times/2024-03")]
    [InlineData("/api/v1/slice/entries/dateString/sgv/2024-03")]
    public async Task AnyOrMissingAcceptStaysJson(string url)
    {
        await SeedOne();

        (await Get(url, "*/*")).ContentType.Should().Be("application/json");
        (await Get(url)).ContentType.Should().Be("application/json");
    }

    [Theory]
    [InlineData("/api/v1/entries.txt")]
    [InlineData("/api/v1/entries/current.csv")]
    public async Task TextResponsesDeclareUtf8(string url)
    {
        await SeedOne();

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var plain = Factory.CreateClient();
        var response = await plain.SendAsync(request);

        response.Content.Headers.ContentType!.CharSet.Should().Be("utf-8");
    }

    [Theory]
    [InlineData("/api/v1/entries?format=xml")]
    [InlineData("/api/v1/entries/current?format=xml")]
    [InlineData("/api/v1/entries/sgv?format=xml")]
    [InlineData("/api/v1/times/2024-03?format=xml")]
    [InlineData("/api/v1/slice/entries/dateString/sgv/2024-03?format=xml")]
    public async Task AnUnsupportedFormatIsIgnoredOnEveryRoute(string url)
    {
        await SeedOne();

        var json = await Get(url, "application/json");
        var text = await Get(url, "text/csv");

        json.Status.Should().Be(HttpStatusCode.OK);
        json.ContentType.Should().Be("application/json");
        text.Status.Should().Be(HttpStatusCode.OK);
        text.ContentType.Should().Be("text/csv");
    }

    [Theory]
    [InlineData("/api/v1/entries/current.json")]
    [InlineData("/api/v1/entries/sgv.json")]
    [InlineData("/api/v1/entries.json")]
    [InlineData("/api/v1/times/2024-03.json")]
    [InlineData("/api/v1/slice/entries/dateString/sgv/2024-03.json")]
    public async Task AJsonExtensionAnswersJsonWhateverTheAccept(string url)
    {
        await SeedOne();

        foreach (var accept in new[] { "text/plain, application/json", "text/csv", "text/tab-separated-values" })
        {
            (await Get(url, accept)).ContentType.Should().Be("application/json", accept);
        }
    }

    [Theory]
    [InlineData("/api/v1/entries/current.txt")]
    [InlineData("/api/v1/entries/current.csv")]
    public async Task ATextExtensionOverridesTheAccept(string url)
    {
        await SeedOne();

        var result = await Get(url, "application/json");

        result.ContentType.Should().StartWith("text/");
    }

    [Theory]
    [InlineData("text/csv;q=0")]
    [InlineData("application/xml, text/csv;q=0")]
    public async Task ARefusedTextTypeIsNotSelected(string accept)
    {
        await SeedOne();

        (await Get("/api/v1/entries/current", accept)).ContentType.Should().Be("application/json");
    }

    [Fact]
    public async Task ATextRangeSelectsPlainText()
    {
        await SeedOne();

        var result = await Get("/api/v1/entries/current", "text/*");

        result.ContentType.Should().Be("text/plain");
        result.Body.Should().Be(Row);
    }

    [Fact]
    public async Task AnUnknownSuffixIsStillTreatedAsAType()
    {
        await SeedOne();

        var result = await Get("/api/v1/entries/sgv.xml");

        result.Status.Should().Be(HttpStatusCode.OK);
        result.ContentType.Should().Be("application/json");
        result.Body.Should().Be("[]");
    }

    [Fact]
    public async Task TextExtensionsAreNotStrippedFromV3Routes()
    {
        var result = await Get("/api/v3/entries.txt");

        result.Status.Should().Be(HttpStatusCode.NotFound);
    }
}
