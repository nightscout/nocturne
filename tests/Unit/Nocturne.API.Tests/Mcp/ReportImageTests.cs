using System.Net;
using System.Security.Cryptography;
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
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;
using static Nocturne.API.Tests.Mcp.RecordDatasetTests;
using MvcJsonOptions = Microsoft.AspNetCore.Mvc.JsonOptions;

namespace Nocturne.API.Tests.Mcp;

/// <summary><c>render_report_image</c>: its arguments, which panels it draws, the size budget and the web call.</summary>
public class ReportImageTests
{
    /// <summary>A PNG header naming its size, padded to <paramref name="length"/> bytes.</summary>
    private static byte[] Png(int width = 1200, int height = 600, int length = 32)
    {
        var png = new byte[Math.Max(length, 24)];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), height);
        return png;
    }

    /// <summary>Records each request and its budget, and answers with <see cref="Answer"/>.</summary>
    private sealed class FakeImageClient : IReportImageClient
    {
        public List<(ReportImageRequest Request, long MaxBytes)> Requests { get; } = [];

        public Func<ReportImageRequest, long, byte[]> Answer { get; set; } = (_, _) => Png();

        public Task<byte[]> RenderAsync(ReportImageRequest request, long maxBytes, CancellationToken cancellationToken)
        {
            Requests.Add((request, maxBytes));
            return Task.FromResult(Answer(request, maxBytes));
        }
    }

    private static ReportPanelResult Panel(string id, ReportPrimitive primitive, ReportPanelStatus status = ReportPanelStatus.Ok) => new()
    {
        Id = id, Dataset = "glucose_series", Primitive = primitive, Bindings = new ReportBindings(), Status = status,
        Summary = $"[{id}] summary", Window = new ReportWindow(0, 3_600_000),
    };

    private static ReportResult Report(params ReportPanelResult[] panels) => new()
    {
        Spec = new ReportSpec { Version = 1, Title = "A title", Panels = [] },
        Units = "mmol",
        TimeZone = "Australia/Sydney",
        Locale = "en-AU",
        Hour12 = true,
        Panels = panels,
        Summary = string.Join("\n", panels.Select(p => p.Summary)),
        Warnings = ["Limited to the last 24 hours."],
        OpenUrl = "https://dev.example/reports/custom/new?spec=abc",
    };

    private static ReportImageOptions Options(string? panelId = null, int? height = null, int ratio = 1) =>
        new(panelId, 1200, height, "light", ratio);

    private static (ReportImageRenderer Renderer, FakeImageClient Client) Images()
    {
        var client = new FakeImageClient();
        return (new ReportImageRenderer(null!, client, new ReportImageRateLimit(), Mock.Of<ITenantAccessor>()), client);
    }

    private static string Text(CallToolResult result) => result.Content.OfType<TextContentBlock>().Single().Text;

    // ---- arguments ----

    [Fact]
    public void Without_arguments_it_draws_every_chart_panel_1200_wide_light_ratio_1_at_each_charts_height()
    {
        ReportImageRenderer.ReadOptions("render_report_image", null, null, null, null, null)
            .Should().Be(new ReportImageOptions(null, 1200, null, "light", 1));
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

        var missing = () => ReportImageRenderer.CheckPanels(spec, Options("nope"), "render_report_image");
        missing.Should().Throw<McpInvalidArgumentsException>().WithMessage("panelId: The spec has no panel 'nope'. Send one of: line, figures.");

        var tiles = () => ReportImageRenderer.CheckPanels(spec, Options("figures"), "render_report_image");
        tiles.Should().Throw<McpInvalidArgumentsException>().WithMessage("*'figures' is drawn as stat_tiles, which has no image*");

        ReportImageRenderer.CheckPanels(spec, Options("line"), "render_report_image");
    }

    [Fact]
    public void A_spec_with_no_chart_panel_is_refused_before_anything_is_read()
    {
        var spec = Spec(("figures", "glucose_statistics", "stat_tiles"));

        var check = () => ReportImageRenderer.CheckPanels(spec, Options(), "render_report_image");

        check.Should().Throw<McpInvalidArgumentsException>().WithMessage("The spec has no chart panel*");
    }

    [Fact]
    public async Task A_tenant_that_has_asked_for_images_twenty_times_this_minute_is_refused_before_anything_is_read()
    {
        var tenant = Mock.Of<ITenantAccessor>(t => t.TenantId == Guid.NewGuid());
        var other = Mock.Of<ITenantAccessor>(t => t.TenantId == Guid.NewGuid());
        var limit = new ReportImageRateLimit();
        for (var i = 0; i < ReportImageRateLimit.PerMinute; i++)
            limit.TryAcquire(tenant.TenantId).Should().BeTrue();

        // A null renderer: the refusal comes before the spec runs.
        var images = new ReportImageRenderer(null!, new FakeImageClient(), limit, tenant);
        var call = () => images.RenderAsync(Spec(("line", "glucose_series", "time_series")), Options(), CancellationToken.None);

        (await call.Should().ThrowAsync<McpInvalidArgumentsException>()).WithMessage("*20 times in the last minute*");
        limit.TryAcquire(other.TenantId).Should().BeTrue("the limit is the tenant's own");
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
        client.Answer = (_, _) => Png(1600, 800);

        var result = await images.DrawAsync(report, new ReportImageOptions(null, 800, 400, "dark", 2), CancellationToken.None);

        result.IsError.Should().NotBe(true);
        result.StructuredContent.Should().BeNull("an image result is not a view");
        result.Content[0].Should().BeOfType<TextContentBlock>();
        result.Content.Skip(1).Should().HaveCount(2).And.AllBeOfType<ImageContentBlock>();
        result.Content.OfType<ImageContentBlock>().Should().OnlyContain(i => i.MimeType == "image/png");
        client.Requests.Select(r => r.Request.Panel.Id).Should().Equal("line", "bands");
        client.Requests.Should().OnlyContain(r => r.Request.Width == 800 && r.Request.Height == 400 && r.Request.Theme == "dark" && r.Request.DevicePixelRatio == 2);
        Text(result).Should().StartWith(report.Summary).And.Contain("Warning: Limited to the last 24 hours.")
            .And.Contain("No image of panel denied: its summary above says why it was not read.")
            .And.Contain("Image 1: panel line, 1600 by 800 pixels, dark theme.")
            .And.Contain("Image 2: panel bands");
    }

    [Fact]
    public async Task The_web_is_sent_the_panel_and_what_draws_it_and_nothing_else_of_the_report()
    {
        var (images, client) = Images();
        var report = Report(Panel("line", ReportPrimitive.TimeSeries), Panel("other", ReportPrimitive.Bar));

        await images.DrawAsync(report, Options("line"), CancellationToken.None);

        var sent = client.Requests.Single().Request;
        sent.Should().Be(new ReportImageRequest(report.Panels[0] with { Summary = "" }, "mmol", "Australia/Sydney", "continuous", "en-AU", true, 1200, null, "light", 1));
    }

    [Fact]
    public async Task At_most_four_panels_are_drawn_and_the_text_names_the_rest()
    {
        var (images, client) = Images();
        var report = Report([.. Enumerable.Range(1, 6).Select(i => Panel($"p{i}", ReportPrimitive.TimeSeries))]);

        var result = await images.DrawAsync(report, Options(), CancellationToken.None);

        client.Requests.Should().HaveCount(4);
        result.Content.OfType<ImageContentBlock>().Should().HaveCount(4);
        Text(result).Should().Contain("Images are drawn for at most 4 panels. Not drawn: p5, p6. Send panelId to draw one of them.");
    }

    [Fact]
    public async Task A_named_panel_is_the_only_one_drawn()
    {
        var (images, client) = Images();
        var report = Report(Panel("a", ReportPrimitive.TimeSeries), Panel("b", ReportPrimitive.Bar));

        var result = await images.DrawAsync(report, Options("b"), CancellationToken.None);

        client.Requests.Select(r => r.Request.Panel.Id).Should().Equal("b");
        result.Content.OfType<ImageContentBlock>().Should().ContainSingle();
    }

    [Fact]
    public async Task Each_image_may_use_only_what_is_left_of_the_budget()
    {
        var (images, client) = Images();
        client.Answer = (_, _) => Png(length: 300 * 1024);
        var report = Report(Panel("a", ReportPrimitive.TimeSeries), Panel("b", ReportPrimitive.TimeSeries));

        await images.DrawAsync(report, Options(), CancellationToken.None);

        var whole = ReportImageRenderer.RawBytesFor(ReportImageRenderer.MaxTotalBase64Bytes);
        client.Requests[0].MaxBytes.Should().Be(whole);
        client.Requests[1].MaxBytes.Should().Be(
            ReportImageRenderer.RawBytesFor(ReportImageRenderer.MaxTotalBase64Bytes - ReportImageRenderer.Base64Length(300 * 1024)));
    }

    [Fact]
    public async Task Images_past_the_budget_are_named_and_the_ones_that_fit_are_kept()
    {
        var (images, client) = Images();
        var calls = 0;
        client.Answer = (_, _) => ++calls == 1 ? Png(length: 900 * 1024) : throw new ReportImageTooLargeException();
        var report = Report(Panel("a", ReportPrimitive.TimeSeries), Panel("b", ReportPrimitive.TimeSeries), Panel("c", ReportPrimitive.TimeSeries));

        var result = await images.DrawAsync(report, Options(), CancellationToken.None);

        result.IsError.Should().NotBe(true);
        result.Content.OfType<ImageContentBlock>().Should().ContainSingle();
        client.Requests.Should().HaveCount(2, "the panel that did not fit ends the drawing; the one after is not asked for");
        Text(result).Should().Contain(
            "Not drawn, because the images would pass 1.5 MB, the most one answer carries: b, c. Send a smaller width or height, or one panelId, to draw them.");
    }

    [Theory]
    [InlineData(1, "Send a smaller width or height, and call again.")]
    [InlineData(2, "Send a smaller width or height, or devicePixelRatio 1, and call again.")]
    public async Task A_first_image_past_the_budget_is_refused_with_what_to_lower(int ratio, string advice)
    {
        var (images, client) = Images();
        client.Answer = (_, _) => throw new ReportImageTooLargeException();

        var draw = () => images.DrawAsync(Report(Panel("a", ReportPrimitive.TimeSeries)), Options(ratio: ratio), CancellationToken.None);

        (await draw.Should().ThrowAsync<McpInvalidArgumentsException>()).Which.Message.Should().Be(
            "The image of panel a is larger than 1.5 MB, the most one answer carries. " + advice);
    }

    [Fact]
    public void The_budget_is_counted_in_base64()
    {
        ReportImageRenderer.Base64Length(3).Should().Be(4);
        ReportImageRenderer.Base64Length(4).Should().Be(8);
        ReportImageRenderer.Base64Length(ReportImageRenderer.RawBytesFor(ReportImageRenderer.MaxTotalBase64Bytes))
            .Should().Be(ReportImageRenderer.MaxTotalBase64Bytes);
        ReportImageRenderer.RawBytesFor(-5).Should().Be(0);
    }

    [Fact]
    public async Task When_the_web_cannot_draw_the_text_comes_back_with_the_reason_as_a_tool_error()
    {
        var (images, client) = Images();
        client.Answer = (_, _) => throw new ReportImageUnavailableException("the image renderer is busy. Try again.");

        var result = await images.DrawAsync(Report(Panel("a", ReportPrimitive.TimeSeries)), Options(), CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().ContainSingle();
        Text(result).Should().StartWith("[a] summary")
            .And.EndWith("The images could not be drawn: the image renderer is busy. Try again. The report's text is above.");
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

    private const string InstanceKey = "instance-secret";

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

    /// <summary>A body that hands out <paramref name="bytes"/> bytes, then fails with <paramref name="failure"/>, or hangs when it is null.</summary>
    private sealed class FailingStream(int bytes, Exception? failure) : Stream
    {
        private int _left = bytes;

        public int Served { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_left == 0)
            {
                if (failure is null)
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                throw failure!;
            }
            var n = Math.Min(_left, buffer.Length);
            buffer.Span[..n].Fill(0x89);
            _left -= n;
            Served += n;
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static WebReportImageClient Client(StubHandler handler, string? webUrl = "http://web.internal:8000/", string? key = InstanceKey)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(WebReportImageClient.HttpClientName)).Returns(() => new HttpClient(handler, false));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WEB_URL"] = webUrl,
            ["INSTANCE_KEY"] = key,
        }).Build();
        return new WebReportImageClient(factory.Object, configuration, Microsoft.Extensions.Options.Options.Create(new MvcJsonOptions()), NullLogger<WebReportImageClient>.Instance);
    }

    private static ReportImageRequest Request() =>
        ReportImageRequest.For(Report(Panel("a", ReportPrimitive.TimeSeries)), Panel("a", ReportPrimitive.TimeSeries), Options());

    [Fact]
    public async Task It_posts_the_panel_alone_to_the_web_with_the_instance_key_digest_and_the_api_service_marker()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png()) });

        var png = await Client(handler).RenderAsync(Request(), 1024, CancellationToken.None);

        png.Should().Equal(Png());
        handler.Seen!.Method.Should().Be(HttpMethod.Post);
        handler.Seen.RequestUri.Should().Be(new Uri("http://web.internal:8000/api/internal/report-image"));
        handler.Seen.Headers.GetValues("X-Instance-Key").Single()
            .Should().Be(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(InstanceKey))));
        handler.Seen.Headers.GetValues("X-Instance-Service").Single().Should().Be("nocturne-api");
        handler.Seen.Headers.AcceptLanguage.Single().Value.Should().Be("en-AU");
        using var body = JsonDocument.Parse(handler.Body!);
        body.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            ["panel", "units", "timeZone", "glucoseColorMode", "locale", "hour12", "width", "height", "theme", "devicePixelRatio"]);
        body.RootElement.GetProperty("panel").GetProperty("primitive").GetString().Should().Be("time_series");
        body.RootElement.GetProperty("height").ValueKind.Should().Be(JsonValueKind.Null);
        handler.Body.Should().NotContain("A title").And.NotContain("[a] summary").And.NotContain("openUrl").And.NotContain("Limited to the last");
    }

    public static TheoryData<string, Func<HttpRequestMessage, HttpResponseMessage>, string> Failures => new()
    {
        { "down", _ => throw new HttpRequestException("refused"), "the web app that draws images did not answer." },
        { "busy", _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), "the image renderer is busy. Try again." },
        { "an error", _ => new HttpResponseMessage(HttpStatusCode.InternalServerError), "the web app that draws images answered with an error (500)." },
        { "a refusal", _ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"width: Send a whole number from 320 to 2400."}""", Encoding.UTF8, "application/json") }, "the web app refused the panel: width: Send a whole number from 320 to 2400." },
        { "too many points", _ => new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge) { Content = new StringContent("""{"error":"Panel 'a' has 20000 points."}""", Encoding.UTF8, "application/json") }, "the web app refused the panel: Panel 'a' has 20000 points." },
        { "not a png", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>") }, "the web app that draws images did not answer with a PNG." },
        { "a body cut off", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FailingStream(8, new IOException("reset"))) }, "the web app that draws images did not answer." },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task A_web_failure_is_an_unavailable_image_with_a_reason_for_the_assistant(string _, Func<HttpRequestMessage, HttpResponseMessage> answer, string reason)
    {
        var render = () => Client(new StubHandler(answer)).RenderAsync(Request(), 1024, CancellationToken.None);

        (await render.Should().ThrowAsync<ReportImageUnavailableException>()).Which.Message.Should().Be(reason);
    }

    [Fact]
    public async Task A_body_past_the_budget_is_too_large_and_is_not_read_to_its_end()
    {
        var body = new FailingStream(4 * 1024 * 1024, new IOException("should not be read this far"));
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });

        var render = () => Client(handler).RenderAsync(Request(), 100_000, CancellationToken.None);

        await render.Should().ThrowAsync<ReportImageTooLargeException>();
        body.Served.Should().BeLessThan(100_000 + 81_920 + 1);
    }

    [Fact]
    public async Task A_body_that_stops_arriving_is_ended_by_the_clients_own_deadline_not_the_http_client_timeout()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FailingStream(8, null)) });
        var client = new WebReportImageClient(
            Mock.Of<IHttpClientFactory>(f => f.CreateClient(WebReportImageClient.HttpClientName) == new HttpClient(handler, false)),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["WEB_URL"] = "http://web", ["INSTANCE_KEY"] = InstanceKey }).Build(),
            Microsoft.Extensions.Options.Options.Create(new MvcJsonOptions()), NullLogger<WebReportImageClient>.Instance) { Deadline = TimeSpan.FromMilliseconds(300) };

        var render = client.RenderAsync(Request(), 1024, CancellationToken.None);
        var finished = await Task.WhenAny(render, Task.Delay(TimeSpan.FromSeconds(10)));

        finished.Should().BeSameAs(render, "the deadline covers reading the body");
        (await FluentActions.Awaiting(() => render).Should().ThrowAsync<ReportImageUnavailableException>())
            .WithMessage("*did not answer within 0.3 seconds*");
    }

    [Fact]
    public async Task Without_the_web_address_or_the_instance_key_nothing_is_sent()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        await FluentActions.Awaiting(() => Client(handler, webUrl: null).RenderAsync(Request(), 1024, CancellationToken.None))
            .Should().ThrowAsync<ReportImageUnavailableException>().WithMessage("*address of the web app*");
        await FluentActions.Awaiting(() => Client(handler, key: null).RenderAsync(Request(), 1024, CancellationToken.None))
            .Should().ThrowAsync<ReportImageUnavailableException>().WithMessage("*no instance key*");
        handler.Seen.Should().BeNull();
    }
}
