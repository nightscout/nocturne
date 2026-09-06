using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Migration;

namespace Nocturne.API.Tests.Migration;

/// <summary>
/// What a run reports when the source will not answer. Every collection pull ends on a short page,
/// so a rejection that merely stops the loop is indistinguishable from "no more data": a run whose
/// every fetch was refused used to finish Completed with zero records and no message, which reads
/// as "my Nightscout is empty" to the person who ran it. The cause has to reach them in words they
/// can act on — a wrong secret and an unreachable host need entirely different fixes.
/// </summary>
public class MigrationFailureReportingTests
{
    private const string ApiSecretMessage = "Nightscout rejected the API secret.";
    private const string UnreachableMessage = "Could not reach your Nightscout server.";

    /// <summary>Answers each request from <paramref name="respond"/>, keyed on the path alone.</summary>
    private sealed class RoutedNightscout(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request.RequestUri!.AbsolutePath));
    }

    private sealed class ThrowingHost(Func<Exception> fail) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw fail();
    }

    private static HttpMessageHandler UnreachableHost() =>
        new ThrowingHost(() => new HttpRequestException("No such host is known."));

    private static HttpResponseMessage Json(HttpStatusCode status, string body = "[]") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static MigrationJobService BuildService(IServiceProvider provider) =>
        new(NullLogger<MigrationJobService>.Instance, provider, new ConfigurationBuilder().Build());

    private static TestMigrationConnectionRequest TestRequest() => new()
    {
        Mode = MigrationMode.Api,
        NightscoutUrl = "https://example-nightscout.invalid",
    };

    [Fact]
    public async Task A_rejected_api_secret_on_the_first_page_fails_the_run()
    {
        // The count endpoint is absent, as it is on older Nightscout versions, so the rejection
        // has to be caught by the page loop rather than by the count probe.
        var handler = new RoutedNightscout(path => path.StartsWith("/api/v1/count/")
            ? Json(HttpStatusCode.NotFound)
            : Json(HttpStatusCode.Unauthorized));

        await using var provider = MigrationJobHarness.BuildProvider(handler);
        var status = await MigrationJobHarness.RunAsync(provider, "entries");

        status.State.Should().Be(MigrationJobState.Failed);
        status.ErrorMessage.Should().StartWith(ApiSecretMessage);
        status.CollectionProgress.Values.Sum(c => c.DocumentsMigrated).Should().Be(0);
    }

    [Fact]
    public async Task A_rejected_api_secret_while_counting_fails_the_run_rather_than_reading_as_zero()
    {
        var handler = new RoutedNightscout(_ => Json(HttpStatusCode.Unauthorized));

        await using var provider = MigrationJobHarness.BuildProvider(handler);
        var status = await MigrationJobHarness.RunAsync(provider, "entries");

        status.State.Should().Be(MigrationJobState.Failed);
        status.ErrorMessage.Should().StartWith(ApiSecretMessage);
    }

    [Fact]
    public async Task A_host_that_never_answers_fails_the_run_as_unreachable()
    {
        await using var provider = MigrationJobHarness.BuildProvider(UnreachableHost());
        var status = await MigrationJobHarness.RunAsync(provider, "entries");

        status.State.Should().Be(MigrationJobState.Failed);
        status.ErrorMessage.Should().StartWith(UnreachableMessage);
        status.ErrorMessage.Should().NotContain("secret");
    }

    [Fact]
    public async Task A_refusal_by_the_outbound_guard_keeps_its_own_wording()
    {
        // The migration client is a guarded connector client, so a mistyped host is refused before
        // the socket opens. Its message says which of "not found" and "forbidden address" happened;
        // the generic unreachable wording would throw that away.
        var handler = new ThrowingHost(() => new Nocturne.Core.Models.Net.OutboundRefusedException(
            "Could not find 'mynightscout.xyx'. Check the address is spelled correctly and that the site is online."));

        await using var provider = MigrationJobHarness.BuildProvider(handler);
        var status = await MigrationJobHarness.RunAsync(provider, "entries");

        status.State.Should().Be(MigrationJobState.Failed);
        status.ErrorMessage.Should().StartWith("Could not find 'mynightscout.xyx'.");
    }

    [Fact]
    public async Task The_connection_test_reports_a_rejected_secret_in_the_same_words_as_a_run()
    {
        await using var provider = MigrationJobHarness.BuildProvider(
            new RoutedNightscout(_ => Json(HttpStatusCode.Unauthorized)));

        var result = await BuildService(provider).TestConnectionAsync(TestRequest());

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith(ApiSecretMessage);
    }

    [Fact]
    public async Task The_connection_test_reports_an_unreachable_host_in_the_same_words_as_a_run()
    {
        await using var provider = MigrationJobHarness.BuildProvider(UnreachableHost());

        var result = await BuildService(provider).TestConnectionAsync(TestRequest());

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith(UnreachableMessage);
    }

    [Fact]
    public async Task One_collection_failing_after_another_imported_is_reported_without_failing_the_run()
    {
        var handler = new RoutedNightscout(path => path switch
        {
            "/api/v1/entries.json" => Json(HttpStatusCode.OK, """[{"date":1770000000000}]"""),
            "/api/v1/treatments.json" => Json(HttpStatusCode.InternalServerError),
            _ => Json(HttpStatusCode.NotFound),
        });

        await using var provider = MigrationJobHarness.BuildProvider(handler);
        var status = await MigrationJobHarness.RunAsync(provider, "entries", "treatments");

        status.State.Should().Be(MigrationJobState.Completed);
        status.CollectionProgress["entries"].DocumentsMigrated.Should().Be(1);
        status.CollectionProgress["entries"].FailureReason.Should().BeNull();
        status.CollectionProgress["treatments"].FailureReason.Should()
            .Be("Nightscout answered 500 for treatments.");
        status.ErrorMessage.Should().StartWith("1 of 2 collections imported, 1 failed.");
    }
}
