using System.Net;
using System.Text;
using Aspire.Hosting;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Nocturne.Aspire.Host.Tests;

public class DevTenantSeederTests
{
    private const string NoTenants = """{"error":"setup_required","setupRequired":true}""";

    [Fact]
    public async Task A_503_setup_required_seeds_the_dev_tenant()
    {
        var api = new FakeApi(HttpStatusCode.ServiceUnavailable, NoTenants);

        await Seeder(api).RunAsync(sampleDataDays: 30, CancellationToken.None);

        api.Requests.Should().Equal(
            $"GET /{DevTenantSeeder.ProbePath}",
            "POST /api/v4/dev-only/admin/seed-tenant");
        api.SeedBody.Should().Contain("\"slug\":\"dev\"")
            .And.Contain("\"displayName\":\"Dev\"")
            .And.Contain("\"ownerUsername\":\"dev\"")
            .And.Contain("\"sampleData\":true")
            .And.Contain("\"sampleDataDays\":30");
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "{}")]
    [InlineData(HttpStatusCode.NotFound, "")]
    [InlineData(HttpStatusCode.ServiceUnavailable,
        """{"error":"recovery_mode_active","setupRequired":false,"recoveryMode":true}""")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "upstream unavailable")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "")]
    public async Task Any_other_answer_skips_the_seed(HttpStatusCode status, string body)
    {
        var api = new FakeApi(status, body);

        await Seeder(api).RunAsync(sampleDataDays: 30, CancellationToken.None);

        api.Requests.Should().Equal($"GET /{DevTenantSeeder.ProbePath}");
    }

    [Fact]
    public async Task A_refused_seed_does_not_throw()
    {
        var api = new FakeApi(HttpStatusCode.ServiceUnavailable, NoTenants)
        {
            SeedStatus = HttpStatusCode.InternalServerError,
        };

        var run = () => Seeder(api).RunAsync(sampleDataDays: 30, CancellationToken.None);

        await run.Should().NotThrowAsync();
        api.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task An_unreachable_api_does_not_throw()
    {
        var api = new FakeApi(HttpStatusCode.ServiceUnavailable, NoTenants) { Unreachable = true };

        var run = () => Seeder(api).RunAsync(sampleDataDays: 30, CancellationToken.None);

        await run.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_timed_out_seed_does_not_throw()
    {
        var api = new FakeApi(HttpStatusCode.ServiceUnavailable, NoTenants) { SeedTimesOut = true };

        var run = () => Seeder(api).RunAsync(sampleDataDays: 30, CancellationToken.None);

        await run.Should().NotThrowAsync();
    }

    private static DevTenantSeeder Seeder(FakeApi api) =>
        new(new HttpClient(api) { BaseAddress = new Uri("http://localhost:1610/") }, NullLogger.Instance);

    private sealed class FakeApi(HttpStatusCode probeStatus, string probeBody) : HttpMessageHandler
    {
        public HttpStatusCode SeedStatus { get; init; } = HttpStatusCode.OK;
        public bool Unreachable { get; init; }
        public bool SeedTimesOut { get; init; }

        public List<string> Requests { get; } = [];
        public string? SeedBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");

            if (Unreachable)
                throw new HttpRequestException("Connection refused");

            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(probeStatus) { Content = new StringContent(probeBody) };

            if (SeedTimesOut)
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");

            SeedBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            var body = SeedStatus == HttpStatusCode.OK
                ? """{"tenantId":"0199a0c0-0000-7000-8000-000000000000","url":"https://dev.nocturne.localhost:1612","entriesSeeded":8640}"""
                : """{"error":"boom"}""";
            return new HttpResponseMessage(SeedStatus)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
