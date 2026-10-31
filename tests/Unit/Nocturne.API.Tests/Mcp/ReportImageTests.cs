using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using Moq;
using Nocturne.API.Mcp;
using Nocturne.API.Mcp.Images;
using Nocturne.API.Mcp.Reports;
using Nocturne.Core.Contracts.Audit;
using Xunit;
using static Nocturne.API.Tests.Mcp.RecordDatasetTests;
using MvcJsonOptions = Microsoft.AspNetCore.Mvc.JsonOptions;

namespace Nocturne.API.Tests.Mcp;

/// <summary><c>render_report_image</c>: its arguments, which panels it draws, the size guard and the web call.</summary>
public class ReportImageTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    /// <summary>Records each request and answers with <see cref="Answer"/>.</summary>
    private sealed class FakeImageClient : IReportImageClient
    {
        public List<ReportImageRequest> Requests { get; } = [];

        public Func<ReportImageRequest, byte[]> Answer { get; set; } = _ => Png;

        public Task<byte[]> RenderAsync(ReportImageRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Answer(request));
        }
    }

    private static ReportPanelResult Panel(string id, ReportPrimitive primitive, ReportPanelStatus status = ReportPanelStatus.Ok) => new()
    {
        Id = id, Dataset = "glucose_series", Primitive = primitive, Bindings = new ReportBindings(), Status = status,
        Summary = $"[{id}] summary", Window = new ReportWindow(0, 3_600_000),
    };

    private static ReportResult Report(params ReportPanelResult[] panels) => new()
    {
        Spec = new ReportSpec { Version = 1, Panels = [] },
        Units = "mg/dl",
        Panels = panels,
        Summary = string.Join("\n", panels.Select(p => p.Summary)),
        Warnings = ["Limited to the last 24 hours."],
    };

    private static (ReportImageRenderer Renderer, FakeImageClient Client) Images()
    {
        var client = new FakeImageClient();
        return (new ReportImageRenderer(null!, client), client);
    }

    private static string Text(CallToolResult result) => result.Content.OfType<TextContentBlock>().Single().Text;

    // ---- arguments ----

    [Fact]
    public void Without_arguments_it_draws_every_chart_panel_at_1200_by_600_light_ratio_1()
    {
        ReportImageRenderer.ReadOptions("render_report_image", null, null, null, null, null)
            .Should().Be(new ReportImageOptions(null, 1200, 600, "light", 1));
        ReportImageRenderer.Defaults.Should().Be(new ReportImageOptions(null, 1200, 600, "light", 1));
    }

    [Theory]
    [InlineData(2400, 1600, "dark", 2)]
    [InlineData(320, 200, "light", 1)]
    public void The_caps_are_inclusive(int width, int height, string theme, int ratio)
    {
        ReportImageRenderer.ReadOptions("render_report_image", "p1", width, height, theme, ratio)
            .Should().Be(new ReportImageOptions("p1", width, height, theme, ratio));
    }

    [Fact]
    public void Every_argument_out_of_range_is_named_in_one_refusal()
    {
        var read = () => ReportImageRenderer.ReadOptions("render_report_image", "", 2401, 199, "sepia", 3);

        read.Should().Throw<McpInvalidArgumentsException>().Which.Message.Should().Be(
            "The image arguments are not valid. Change these and call render_report_image again:\n"
            + "- width: Send a whole number from 320 to 2400.\n"
            + "- height: Send a whole number from 200 to 1600.\n"
            + "- theme: Send light or dark.\n"
            + "- devicePixelRatio: Send 1 or 2.\n"
            + "- panelId: Send the id of one panel in the spec.");
    }

    private static ReportSpec Spec(params (string Id, string Dataset, string Primitive)[] panels)
    {
        var validation = Validator.Validate(JsonSerializer.SerializeToElement(new
        {
            version = 1,
            range = new { hours = 24 },
            panels = panels.Select(p => new
            {
                id = p.Id,
                dataset = new { name = p.Dataset },
                primitive = p.Primitive,
                bindings = p.Primitive == "time_series" ? new { x = "time", y = new[] { "glucose" } } : null,
            }),
        }), false);
        validation.IsValid.Should().BeTrue(string.Join("; ", validation.Errors));
        return validation.Spec!;
    }

    [Fact]
    public void A_named_panel_must_be_in_the_spec_and_be_a_chart()
    {
        var spec = Spec(("line", "glucose_series", "time_series"), ("figures", "glucose_statistics", "stat_tiles"));

        var missing = () => ReportImageRenderer.CheckPanels(spec, new ReportImageOptions("nope", 1200, 600, "light", 1), "render_report_image");
        missing.Should().Throw<McpInvalidArgumentsException>().WithMessage("panelId: The spec has no panel 'nope'. Send one of: line, figures.");

        var tiles = () => ReportImageRenderer.CheckPanels(spec, new ReportImageOptions("figures", 1200, 600, "light", 1), "render_report_image");
        tiles.Should().Throw<McpInvalidArgumentsException>().WithMessage("*'figures' is drawn as stat_tiles, which has no image*");

        ReportImageRenderer.CheckPanels(spec, new ReportImageOptions("line", 1200, 600, "light", 1), "render_report_image");
    }

    [Fact]
    public void A_spec_with_no_chart_panel_is_refused_before_anything_is_read()
    {
        var spec = Spec(("figures", "glucose_statistics", "stat_tiles"));

        var check = () => ReportImageRenderer.CheckPanels(spec, ReportImageRenderer.Defaults, "render_report_image");

        check.Should().Throw<McpInvalidArgumentsException>().WithMessage("The spec has no chart panel*");
    }

    // ---- drawing ----

    [Fact]
    public async Task Each_chart_panel_read_becomes_one_png_block_after_the_summary_text_and_nothing_else()
    {
        var (images, client) = Images();
        var report = Report(
            Panel("line", ReportPrimitive.TimeSeries),
            Panel("figures", ReportPrimitive.StatTiles),
            Panel("bands", ReportPrimitive.RangeBands),
            Panel("denied", ReportPrimitive.Bar, ReportPanelStatus.NotPermitted));

        var result = await images.DrawAsync(report, new ReportImageOptions(null, 800, 400, "dark", 2), CancellationToken.None);

        result.IsError.Should().NotBe(true);
        result.StructuredContent.Should().BeNull("an image result is not a view");
        result.Content[0].Should().BeOfType<TextContentBlock>();
        result.Content.Skip(1).Should().HaveCount(2).And.AllBeOfType<ImageContentBlock>();
        result.Content.OfType<ImageContentBlock>().Should().OnlyContain(i => i.MimeType == "image/png" && i.DecodedData.ToArray().SequenceEqual(Png));
        client.Requests.Select(r => r.PanelId).Should().Equal("line", "bands");
        client.Requests.Should().OnlyContain(r => r.Width == 800 && r.Height == 400 && r.Theme == "dark" && r.DevicePixelRatio == 2);
        client.Requests.Should().OnlyContain(r => r.Result.Panels.Count == 1 && r.Result.Panels[0].Id == r.PanelId,
            "the web is sent only the panel it draws");
        var text = Text(result);
        text.Should().StartWith(report.Summary).And.Contain("Warning: Limited to the last 24 hours.")
            .And.Contain("No image of panel denied: its summary above says why it was not read.")
            .And.Contain("Image 1: panel line, 800 by 400 pixels at ratio 2, dark theme.")
            .And.Contain("Image 2: panel bands");
    }

    [Fact]
    public async Task At_most_four_panels_are_drawn_and_the_text_names_the_rest()
    {
        var (images, client) = Images();
        var report = Report([.. Enumerable.Range(1, 6).Select(i => Panel($"p{i}", ReportPrimitive.TimeSeries))]);

        var result = await images.DrawAsync(report, ReportImageRenderer.Defaults, CancellationToken.None);

        client.Requests.Should().HaveCount(4);
        result.Content.OfType<ImageContentBlock>().Should().HaveCount(4);
        Text(result).Should().Contain("Images are drawn for at most 4 panels. Not drawn: p5, p6. Send panelId to draw one of them.");
    }

    [Fact]
    public async Task A_named_panel_is_the_only_one_drawn()
    {
        var (images, client) = Images();
        var report = Report(Panel("a", ReportPrimitive.TimeSeries), Panel("b", ReportPrimitive.Bar));

        var result = await images.DrawAsync(report, new ReportImageOptions("b", 1200, 600, "light", 1), CancellationToken.None);

        client.Requests.Select(r => r.PanelId).Should().Equal("b");
        result.Content.OfType<ImageContentBlock>().Should().ContainSingle();
    }

    [Fact]
    public async Task Images_over_one_and_a_half_megabytes_in_all_are_refused_with_what_to_lower_and_no_more_are_drawn()
    {
        var (images, client) = Images();
        client.Answer = _ => [.. Png, .. new byte[700 * 1024]];
        var report = Report(Panel("a", ReportPrimitive.TimeSeries), Panel("b", ReportPrimitive.TimeSeries), Panel("c", ReportPrimitive.TimeSeries));

        var draw = () => images.DrawAsync(report, ReportImageRenderer.Defaults, CancellationToken.None);

        (await draw.Should().ThrowAsync<McpInvalidArgumentsException>()).Which.Message.Should().Be(
            "The images are larger than 1.5 MB, the most one answer carries. Send a smaller width or height, devicePixelRatio 1, "
            + "or one panelId, and call again.");
        client.Requests.Should().HaveCount(2, "the second image passes the limit, so the third is never asked for");
    }

    [Fact]
    public void The_guard_counts_base64_not_raw_bytes()
    {
        ReportImageRenderer.Base64Length(3).Should().Be(4);
        ReportImageRenderer.Base64Length(4).Should().Be(8);
        ReportImageRenderer.Base64Length(1_179_648).Should().Be(ReportImageRenderer.MaxTotalBase64Bytes);
    }

    [Fact]
    public async Task When_the_web_cannot_draw_the_text_comes_back_with_the_reason_as_a_tool_error()
    {
        var (images, client) = Images();
        client.Answer = _ => throw new ReportImageUnavailableException("the web app that draws images did not answer.");

        var result = await images.DrawAsync(Report(Panel("a", ReportPrimitive.TimeSeries)), ReportImageRenderer.Defaults, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().ContainSingle();
        Text(result).Should().StartWith("[a] summary")
            .And.EndWith("The images could not be drawn: the web app that draws images did not answer. The report's text is above.");
    }

    // ---- audit ----

    [Fact]
    public void An_image_call_is_audited_from_the_same_read_log_as_render_report()
    {
        var readLog = new McpToolReadLog();
        readLog.Record("glucose", 288);
        readLog.Record("treatments", 3);
        var audit = Mock.Of<IAuditContext>();
        var http = new DefaultHttpContext();

        var image = McpToolGuard.BuildAuditEntry(Guid.NewGuid(), new CallToolRequestParams { Name = "render_report_image" },
            new HashSet<string>(), new CallToolResult(), false, readLog, audit, http, DateTime.UtcNow);
        var report = McpToolGuard.BuildAuditEntry(Guid.NewGuid(), new CallToolRequestParams { Name = "render_report" },
            new HashSet<string>(), new CallToolResult(), false, readLog, audit, http, DateTime.UtcNow);
        var tooLarge = McpToolGuard.BuildAuditEntry(Guid.NewGuid(), new CallToolRequestParams { Name = "render_report_image" },
            new HashSet<string>(), null, true, readLog, audit, http, DateTime.UtcNow);

        (image.EntityType, image.RecordCount, image.StatusCode).Should().Be((report.EntityType, report.RecordCount, report.StatusCode));
        image.Endpoint.Should().Be("MCP render_report_image");
        (tooLarge.EntityType, tooLarge.RecordCount, tooLarge.StatusCode).Should().Be(("glucose,treatments", 291, 400),
            "a refused answer still read the data, and says so");
    }

    // ---- the web call ----

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return answer(request);
        }
    }

    private static WebReportImageClient Client(StubHandler handler, string? webUrl = "http://web.internal:8000/", string? key = "instance-secret")
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(WebReportImageClient.HttpClientName)).Returns(() => new HttpClient(handler, false));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WEB_URL"] = webUrl,
            ["INSTANCE_KEY"] = key,
        }).Build();
        return new WebReportImageClient(factory.Object, configuration, Options.Create(new MvcJsonOptions()), NullLogger<WebReportImageClient>.Instance);
    }

    private static ReportImageRequest Request() =>
        new(Report(Panel("a", ReportPrimitive.TimeSeries)), "a", 1200, 600, "light", 1);

    [Fact]
    public async Task It_posts_one_panel_to_the_web_with_the_instance_key_as_the_bot_dispatch_does()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) });

        var png = await Client(handler).RenderAsync(Request(), CancellationToken.None);

        png.Should().Equal(Png);
        handler.Seen!.Method.Should().Be(HttpMethod.Post);
        handler.Seen.RequestUri.Should().Be(new Uri("http://web.internal:8000/api/internal/report-image"));
        handler.Seen.Headers.GetValues("X-Instance-Key").Single().Should().MatchRegex("^[0-9a-f]{64}$").And.NotBe("instance-secret");
        handler.Seen.Headers.GetValues("X-Instance-Service").Single().Should().Be("nocturne-api");
        using var body = JsonDocument.Parse(handler.Body!);
        body.RootElement.GetProperty("panelId").GetString().Should().Be("a");
        body.RootElement.GetProperty("result").GetProperty("panels").GetArrayLength().Should().Be(1);
        body.RootElement.GetProperty("result").GetProperty("panels")[0].GetProperty("primitive").GetString().Should().Be("time_series");
        body.RootElement.GetProperty("devicePixelRatio").GetInt32().Should().Be(1);
    }

    public static TheoryData<string, Func<HttpRequestMessage, HttpResponseMessage>, string> Failures => new()
    {
        { "down", _ => throw new HttpRequestException("refused"), "the web app that draws images did not answer." },
        { "an error", _ => new HttpResponseMessage(HttpStatusCode.InternalServerError), "the web app that draws images answered with an error (500)." },
        { "a refusal", _ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"width: Send a whole number from 320 to 2400."}""", Encoding.UTF8, "application/json") }, "the web app refused the panel: width: Send a whole number from 320 to 2400." },
        { "not a png", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>") }, "the web app that draws images did not answer with a PNG." },
        { "a timeout", _ => throw new TaskCanceledException("timeout"), "the web app that draws images did not answer within 20 seconds." },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task A_web_failure_is_an_unavailable_image_with_a_reason_for_the_assistant(string _, Func<HttpRequestMessage, HttpResponseMessage> answer, string reason)
    {
        var render = () => Client(new StubHandler(answer)).RenderAsync(Request(), CancellationToken.None);

        (await render.Should().ThrowAsync<ReportImageUnavailableException>()).Which.Message.Should().Be(reason);
    }

    [Fact]
    public async Task Without_the_web_address_or_the_instance_key_nothing_is_sent()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        await FluentActions.Awaiting(() => Client(handler, webUrl: null).RenderAsync(Request(), CancellationToken.None))
            .Should().ThrowAsync<ReportImageUnavailableException>().WithMessage("*address of the web app*");
        await FluentActions.Awaiting(() => Client(handler, key: null).RenderAsync(Request(), CancellationToken.None))
            .Should().ThrowAsync<ReportImageUnavailableException>().WithMessage("*no instance key*");
        handler.Seen.Should().BeNull();
    }
}
