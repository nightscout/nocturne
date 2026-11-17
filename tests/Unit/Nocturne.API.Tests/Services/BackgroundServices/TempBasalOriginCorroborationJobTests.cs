using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.API.Tests.Services.Jobs;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.BackgroundServices;

/// <summary>
/// A run that cannot read fails rather than reporting a clean pass, so the ledger keeps its cursor and
/// retries it. The InMemory provider has no connection to read through. The cursor's watermark is
/// covered over PostgreSQL by <see cref="RestartSensitiveJobsPostgresTests"/>.
/// </summary>
[Trait("Category", "Unit")]
public class TempBasalOriginCorroborationJobTests
{
    [Fact]
    public async Task A_run_that_cannot_read_fails()
    {
        var factory = new Mock<INocturneDbContextFactory>();
        factory
            .Setup(f => f.CreateAsync(It.IsAny<CancellationToken>()))
            .Returns(() => ValueTask.FromResult(TestDbContextFactory.CreateInMemoryContext($"temp_basal_origin_{Guid.NewGuid()}")));
        await using var provider = new ServiceCollection().AddSingleton(factory.Object).BuildServiceProvider();
        var job = new TempBasalOriginCorroborationJob(NullLogger<TempBasalOriginCorroborationJob>.Instance);

        var run = () => job.RunAsync(JobRuns.For(Guid.CreateVersion7(), provider), CancellationToken.None);

        await run.Should().ThrowExactlyAsync<InvalidOperationException>().WithMessage("*relational*");
    }
}
