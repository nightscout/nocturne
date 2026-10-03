using System.Collections.Concurrent;
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Helpers;
using Nocturne.API.Services;
using Nocturne.API.Services.Migration;
using Nocturne.Core.Models;

namespace Nocturne.API.Tests.Migration;

/// <summary>
/// A Nightscout can be served under a sub-path behind a reverse proxy, so every read has to stay
/// under the configured URL rather than resolve against the host.
/// </summary>
public class MigrationSourceAddressTests
{
    private const string Prefix = "/nightscout";

    /// <summary>Serves a one-document Nightscout under <see cref="Prefix"/> and 404s everything else.</summary>
    private sealed class PrefixedNightscout : HttpMessageHandler
    {
        public ConcurrentQueue<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request.RequestUri!);
            var path = request.RequestUri!.AbsolutePath;
            if (!path.StartsWith(Prefix + "/", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            var body = path[Prefix.Length..] switch
            {
                "/api/v1/status" => """{"status":"ok"}""",
                "/api/v1/entries.json" => """[{"_id":"e1","type":"sgv","sgv":120,"date":1770000000000}]""",
                "/api/v1/treatments.json" => """[{"_id":"t1","eventType":"Note","created_at":"2026-02-02T02:40:00Z"}]""",
                "/api/v1/devicestatus.json" => """[{"_id":"d1","device":"synthetic","created_at":"2026-02-02T02:40:00Z"}]""",
                "/api/v1/profile.json" => """[{"_id":"p1","defaultProfile":"Default","store":{}}]""",
                "/api/v1/food.json" => "[]",
                "/api/v2/authorization/subjects" => "[]",
                "/api/v2/authorization/roles" => "[]",
                var p when p.StartsWith("/api/v1/count/", StringComparison.Ordinal) => """[{"count":1}]""",
                _ => null,
            };

            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
        }
    }

    [Theory]
    [InlineData("https://example-nightscout.invalid/nightscout")]
    [InlineData("https://example-nightscout.invalid/nightscout/")]
    [InlineData("https://example-nightscout.invalid/nightscout?token=synthetic-token")]
    public async Task A_run_reads_every_collection_under_the_configured_path(string nightscoutUrl)
    {
        var source = new PrefixedNightscout();
        await using var provider = MigrationJobHarness.BuildProvider(source);

        var status = await MigrationJobHarness.RunAsync(
            provider,
            onCreated: null,
            ["subjects", "entries", "treatments", "profile", "devicestatus", "food"],
            nightscoutUrl: nightscoutUrl);

        status.State.Should().Be(MigrationJobState.Completed, status.ErrorMessage);
        status.CollectionProgress.Values.Should().OnlyContain(c => c.FailureReason == null);
        status.CollectionProgress["entries"].DocumentsMigrated.Should().Be(1);

        var paths = source.Requests.Select(u => u.AbsolutePath).ToList();
        paths.Should().OnlyContain(p => p.StartsWith(Prefix + "/api/", StringComparison.Ordinal));
        source.Requests.Should().NotContain(u => u.Query.Contains("token"));
        paths.Should().Contain(
        [
            $"{Prefix}/api/v1/count/entries/where",
            $"{Prefix}/api/v2/authorization/roles",
            $"{Prefix}/api/v2/authorization/subjects",
            $"{Prefix}/api/v1/entries.json",
            $"{Prefix}/api/v1/treatments.json",
            $"{Prefix}/api/v1/profile.json",
            $"{Prefix}/api/v1/devicestatus.json",
            $"{Prefix}/api/v1/food.json",
        ]);
    }

    [Theory]
    [InlineData("https://example-nightscout.invalid/nightscout")]
    [InlineData("https://example-nightscout.invalid/nightscout/")]
    [InlineData("https://example-nightscout.invalid/nightscout?token=synthetic-token#section")]
    [InlineData("example-nightscout.invalid/nightscout")]
    public async Task The_connection_test_reads_status_under_the_configured_path(string nightscoutUrl)
    {
        var source = new PrefixedNightscout();
        await using var provider = MigrationJobHarness.BuildProvider(source);
        var service = new MigrationJobService(
            NullLogger<MigrationJobService>.Instance, provider, new ConfigurationBuilder().Build(), new TenantRunGuard());

        var result = await service.TestConnectionAsync(new TestMigrationConnectionRequest
        {
            Mode = MigrationMode.Api,
            NightscoutUrl = nightscoutUrl,
        });

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.SiteName.Should().Be("https://example-nightscout.invalid/nightscout");
        source.Requests.Select(u => u.AbsoluteUri).Should().Equal(
            "https://example-nightscout.invalid/nightscout/api/v1/status");
    }

    [Theory]
    [InlineData("ftp://example-nightscout.invalid/nightscout")]
    [InlineData("/nightscout")]
    public async Task The_connection_test_refuses_an_address_that_is_not_http(string nightscoutUrl)
    {
        var source = new PrefixedNightscout();
        await using var provider = MigrationJobHarness.BuildProvider(source);
        var service = new MigrationJobService(
            NullLogger<MigrationJobService>.Instance, provider, new ConfigurationBuilder().Build(), new TenantRunGuard());

        var result = await service.TestConnectionAsync(new TestMigrationConnectionRequest
        {
            Mode = MigrationMode.Api,
            NightscoutUrl = nightscoutUrl,
        });

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be(NightscoutBaseUri.InvalidUrlMessage);
        source.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_run_from_an_address_that_is_not_http_fails_with_the_address_named()
    {
        var source = new PrefixedNightscout();
        await using var provider = MigrationJobHarness.BuildProvider(source);

        var status = await MigrationJobHarness.RunAsync(
            provider, onCreated: null, ["entries"], nightscoutUrl: "ftp://example-nightscout.invalid/nightscout");

        status.State.Should().Be(MigrationJobState.Failed);
        status.ErrorMessage.Should().Be(NightscoutBaseUri.InvalidUrlMessage);
        source.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_run_from_a_host_without_a_scheme_reads_over_https()
    {
        var source = new PrefixedNightscout();
        await using var provider = MigrationJobHarness.BuildProvider(source);

        var status = await MigrationJobHarness.RunAsync(
            provider, onCreated: null, ["entries"], nightscoutUrl: "example-nightscout.invalid/nightscout");

        status.State.Should().Be(MigrationJobState.Completed, status.ErrorMessage);
        source.Requests.Should().OnlyContain(u =>
            u.AbsoluteUri.StartsWith("https://example-nightscout.invalid/nightscout/api/", StringComparison.Ordinal));
    }
}
