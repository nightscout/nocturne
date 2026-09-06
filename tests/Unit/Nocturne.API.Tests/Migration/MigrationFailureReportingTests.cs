using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Migration;
using Nocturne.Infrastructure.Data.Entities;

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
    public async Task A_forbidden_response_is_a_rejected_secret_and_not_just_a_status()
    {
        // Nightscout answers 403 for a secret it will not honour as readily as 401, and the two
        // need the same advice; classifying 403 as an ordinary status would print the number.
        var handler = new RoutedNightscout(_ => Json(HttpStatusCode.Forbidden));

        await using var provider = MigrationJobHarness.BuildProvider(handler);
        var status = await MigrationJobHarness.RunAsync(provider, "entries");

        status.State.Should().Be(MigrationJobState.Failed);
        status.ErrorMessage.Should().StartWith(ApiSecretMessage);
        status.ErrorMessage.Should().NotContain("403");
    }

    [Fact]
    public async Task A_rejected_count_fails_the_run_even_when_the_pages_would_have_succeeded()
    {
        // The count runs before any collection, so nothing else may be relied on to notice the
        // rejection: with the pages answering normally, only the count step can fail this run.
        var handler = new RoutedNightscout(path => path.StartsWith("/api/v1/count/")
            ? Json(HttpStatusCode.Unauthorized)
            : Json(HttpStatusCode.OK));

        await using var provider = MigrationJobHarness.BuildProvider(handler);
        var status = await MigrationJobHarness.RunAsync(provider, "entries");

        status.State.Should().Be(MigrationJobState.Failed);
        status.ErrorMessage.Should().StartWith(ApiSecretMessage);
    }

    [Fact]
    public async Task A_user_cancelling_mid_fetch_is_not_reported_as_an_unreachable_server()
    {
        MigrationJob? running = null;
        var handler = new ThrowingHost(() =>
        {
            running!.Cancel();
            return new TaskCanceledException("The operation was canceled.");
        });

        await using var provider = MigrationJobHarness.BuildProvider(handler);
        var status = await MigrationJobHarness.RunAsync(
            provider, job => running = job, ["entries"]);

        status.State.Should().Be(MigrationJobState.Cancelled);
        status.ErrorMessage.Should().NotBe(UnreachableMessage);
    }

    [Fact]
    public async Task An_error_on_the_first_collection_with_nothing_imported_fails_the_run()
    {
        var handler = new RoutedNightscout(path => path.StartsWith("/api/v1/count/")
            ? Json(HttpStatusCode.NotFound)
            : Json(HttpStatusCode.InternalServerError));

        await using var provider = MigrationJobHarness.BuildProvider(handler);
        var status = await MigrationJobHarness.RunAsync(provider, "entries");

        status.State.Should().Be(MigrationJobState.Failed);
        status.ErrorMessage.Should().Be("Nightscout answered 500 for entries.");
    }

    [Fact]
    public async Task A_read_only_secret_skips_subjects_without_putting_the_run_at_fault()
    {
        // Nightscout's admin routes refuse a read-only secret, and that is the ordinary way a
        // hosted site is configured — so this must not put a fault on an otherwise clean import.
        var handler = new RoutedNightscout(path => path switch
        {
            "/api/v2/authorization/subjects" => Json(HttpStatusCode.Forbidden),
            "/api/v1/entries.json" => Json(HttpStatusCode.OK, """[{"date":1770000000000}]"""),
            _ => Json(HttpStatusCode.NotFound),
        });

        await using var provider = MigrationJobHarness.BuildProvider(handler);
        var status = await MigrationJobHarness.RunAsync(provider, "subjects", "entries");

        status.State.Should().Be(MigrationJobState.Completed);
        status.CollectionProgress["subjects"].SkippedReason.Should().Be(
            "Skipped: listing the people and devices that can sign in needs an admin API secret.");
        status.CollectionProgress["subjects"].FailureReason.Should().BeNull();
        status.ErrorMessage.Should().Be(
            "1 of 2 collections imported, 1 skipped. "
            + "Skipped: listing the people and devices that can sign in needs an admin API secret.");
        status.ErrorMessage.Should().NotContain("failed");
    }

    [Fact]
    public async Task A_subjects_route_that_errors_is_still_a_failure_rather_than_a_skip()
    {
        // Only the credential case is ordinary. A 500 there is a fault, and calling it a skip
        // would hide it behind the same untroubled wording.
        var handler = new RoutedNightscout(path => path == "/api/v2/authorization/subjects"
            ? Json(HttpStatusCode.InternalServerError)
            : Json(HttpStatusCode.NotFound));

        await using var provider = MigrationJobHarness.BuildProvider(handler);
        var status = await MigrationJobHarness.RunAsync(provider, "subjects");

        status.CollectionProgress["subjects"].SkippedReason.Should().BeNull();
        status.CollectionProgress["subjects"].FailureReason.Should()
            .Be("Nightscout answered 500 for subjects.");
    }

    [Fact]
    public async Task A_skipped_collection_is_not_taken_as_proof_the_source_is_healthy()
    {
        // Subjects is skipped before entries is tried. If the skip counted as a clean completion,
        // the entries failure would look like a partial success and the run would report Completed
        // with nothing imported at all.
        var handler = new RoutedNightscout(path => path switch
        {
            "/api/v2/authorization/subjects" => Json(HttpStatusCode.Forbidden),
            _ => Json(HttpStatusCode.InternalServerError),
        });

        await using var provider = MigrationJobHarness.BuildProvider(handler);
        var status = await MigrationJobHarness.RunAsync(provider, "subjects", "entries");

        status.State.Should().Be(MigrationJobState.Failed);
    }

    [Fact]
    public async Task Subjects_does_not_tolerate_a_server_it_cannot_reach()
    {
        await using var provider = MigrationJobHarness.BuildProvider(UnreachableHost());
        var status = await MigrationJobHarness.RunAsync(provider, "subjects");

        status.State.Should().Be(MigrationJobState.Failed);
        status.ErrorMessage.Should().StartWith(UnreachableMessage);
    }

    [Fact]
    public async Task A_host_that_dies_mid_run_abandons_the_rest_and_says_so_once()
    {
        var handler = new RoutedNightscout(path => path switch
        {
            "/api/v1/entries.json" => Json(HttpStatusCode.OK, """[{"date":1770000000000}]"""),
            "/api/v1/treatments.json" => throw new HttpRequestException("Connection timed out."),
            _ => Json(HttpStatusCode.NotFound),
        });

        await using var provider = MigrationJobHarness.BuildProvider(handler);
        var status = await MigrationJobHarness.RunAsync(
            provider, "entries", "treatments", "profile", "food", "activity");

        status.State.Should().Be(MigrationJobState.Completed);
        status.ErrorMessage.Should().StartWith(
            "1 of 5 collections imported, 1 failed, 3 not attempted.");
        status.ErrorMessage.Should().Contain(UnreachableMessage);

        // The connection failed once; saying so once is the point of abandoning the rest.
        Regex.Matches(status.ErrorMessage!, Regex.Escape(UnreachableMessage)).Should().ContainSingle();
        status.CollectionProgress["profile"].FailureReason.Should().BeNull();
    }

    [Fact]
    public async Task A_rejection_arriving_after_data_does_not_repeat_the_api_secret_advice()
    {
        var handler = new RoutedNightscout(path => path switch
        {
            "/api/v1/entries.json" => Json(HttpStatusCode.OK, """[{"date":1770000000000}]"""),
            "/api/v1/treatments.json" => Json(HttpStatusCode.Unauthorized),
            _ => Json(HttpStatusCode.NotFound),
        });

        await using var provider = MigrationJobHarness.BuildProvider(handler);
        var status = await MigrationJobHarness.RunAsync(
            provider, "entries", "treatments", "profile");

        status.State.Should().Be(MigrationJobState.Completed);
        status.CollectionProgress["treatments"].FailureReason.Should().StartWith(
            "Nightscout refused to hand this over.");

        // The secret plainly works — it fetched the entries — so telling them to check it is wrong.
        status.ErrorMessage.Should().NotContain("API_SECRET");
    }

    [Fact]
    public async Task A_collection_Nocturne_cannot_store_is_named_without_leaking_the_exception()
    {
        // A malformed page reaches the collection loop as a JsonException; its text is a parser
        // position, which tells whoever ran the import nothing they can act on.
        var handler = new RoutedNightscout(path => path switch
        {
            "/api/v1/entries.json" => Json(HttpStatusCode.OK, """[{"date":1770000000000}]"""),
            "/api/v1/treatments.json" => Json(HttpStatusCode.OK, "{ not json"),
            _ => Json(HttpStatusCode.NotFound),
        });

        await using var provider = MigrationJobHarness.BuildProvider(handler);
        var status = await MigrationJobHarness.RunAsync(provider, "entries", "treatments");

        status.State.Should().Be(MigrationJobState.Completed);
        status.CollectionProgress["treatments"].FailureReason.Should()
            .Be("Nocturne could not store the treatments it received.");
    }

    [Fact]
    public void Per_collection_outcomes_survive_a_round_trip_through_the_run_record()
    {
        var run = new MigrationRunEntity
        {
            State = nameof(MigrationJobState.Completed),
            CollectionOutcomes = """
                [{"collectionName":"entries","totalDocuments":9,"documentsMigrated":9,"documentsFailed":0,"isComplete":true,"failureReason":null},
                 {"collectionName":"treatments","totalDocuments":0,"documentsMigrated":0,"documentsFailed":0,"isComplete":true,"failureReason":"Nightscout answered 500 for treatments."}]
                """,
        };

        var status = MigrationJob.StatusFromRecord(run);

        status.CollectionProgress["entries"].DocumentsMigrated.Should().Be(9);
        status.CollectionProgress["treatments"].FailureReason.Should()
            .Be("Nightscout answered 500 for treatments.");
    }

    [Fact]
    public void A_run_record_whose_outcomes_cannot_be_read_falls_back_to_the_count_columns()
    {
        var run = new MigrationRunEntity
        {
            State = nameof(MigrationJobState.Completed),
            EntriesMigrated = 4,
            TreatmentsMigrated = 2,
            CollectionOutcomes = "{ not an outcome array",
        };

        var status = MigrationJob.StatusFromRecord(run);

        status.CollectionProgress["entries"].DocumentsMigrated.Should().Be(4);
        status.CollectionProgress["treatments"].DocumentsMigrated.Should().Be(2);
    }

    [Theory]
    [InlineData("""[{"collectionName":"subjects","isComplete":true,"skippedReason":"Skipped: needs an admin API secret."}]""", false)]
    [InlineData("""[{"collectionName":"treatments","isComplete":true,"failureReason":"Nightscout answered 500 for treatments."}]""", true)]
    [InlineData(null, false)]
    public void History_marks_a_run_at_fault_only_when_a_collection_failed(string? outcomes, bool expected)
    {
        // The settings page colours a history entry off this: history carries no per-collection
        // detail, so a skip-only run and a failed one would otherwise look identical to it.
        var run = new MigrationRunEntity
        {
            State = nameof(MigrationJobState.Completed),
            CollectionOutcomes = outcomes,
        };

        MigrationJob.InfoFromRecord(run).HasFailures.Should().Be(expected);
    }

    [Fact]
    public void History_marks_a_failed_run_at_fault_even_with_no_collection_outcomes()
    {
        // A run rejected while counting never reaches a collection, so nothing records a reason.
        var run = new MigrationRunEntity { State = nameof(MigrationJobState.Failed) };

        MigrationJob.InfoFromRecord(run).HasFailures.Should().BeTrue();
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
