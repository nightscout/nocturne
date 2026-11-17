using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Services.Migration;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Tests.Migration;

/// <summary>
/// A job stamps its started and completed times from the injected clock, and the stored run and
/// source carry the same instants.
/// </summary>
public class MigrationJobClockTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
    private static readonly TimeSpan RunLength = TimeSpan.FromMinutes(5);

    private sealed class ClockAdvancingSource(FakeTimeProvider time) : HttpMessageHandler
    {
        private int _advanced;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _advanced, 1) == 0)
                time.Advance(RunLength);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public async Task A_run_stamps_its_start_and_completion_from_the_clock()
    {
        var time = new FakeTimeProvider(Start);
        await using var provider = MigrationJobHarness.BuildProvider(new ClockAdvancingSource(time));

        var status = await MigrationJobHarness.RunAsync(
            provider, onCreated: null, ["entries"], time: time);

        status.StartedAt.Should().Be(Start.UtcDateTime);
        status.CompletedAt.Should().Be(Start.UtcDateTime + RunLength);

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();
        var run = await db.MigrationRuns.SingleAsync();
        run.StartedAt.Should().Be(Start.UtcDateTime);
        run.CompletedAt.Should().Be(Start.UtcDateTime + RunLength);
    }
}
