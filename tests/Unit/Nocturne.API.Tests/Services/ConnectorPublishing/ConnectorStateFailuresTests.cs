using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.ConnectorPublishing;
using Nocturne.API.Services.Connectors;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Contracts.Connectors;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Services.ConnectorPublishing;

/// <summary>
/// A connector learns that the database behind its state failed through one named exception, raised
/// for exactly the failures <c>StoreFailure</c> names. A defect passes through unchanged, and the
/// API's own readers of the store see the raw failure.
/// </summary>
[Trait("Category", "Unit")]
public class ConnectorStateFailuresTests
{
    private const string Source = "nightscout-connector";

    public static TheoryData<Exception> StoreFailures => new()
    {
        new NpgsqlException("connection refused"),
        new RetryLimitExceededException("retries exhausted", new NpgsqlException("connection refused")),
        new DbUpdateException("save failed", new NpgsqlException("connection refused")),
    };

    public static TheoryData<Exception> Defects => new()
    {
        new InvalidOperationException("a second operation was started on this context"),
        new InvalidCastException("defect"),
        new NullReferenceException(),
        new OperationCanceledException(),
    };

    private static readonly IReadOnlyDictionary<string, DateTime?> Changes = new Dictionary<string, DateTime?> { ["k"] = null };

    private static IEnumerable<Func<IConnectorStateStore, Task>> Operations()
    {
        yield return store => store.ResumeAsync(Source);
        yield return store => store.SetResumeAsync(Source, Changes);
        yield return store => store.VendorAsync(Source, "k");
        yield return store => store.SetVendorAsync(Source, "k", null);
        yield return store => store.ClearAsync(Source, ConnectorStateSlots.Resume);
    }

    private static Mock<IConnectorStateStore> FailingWith(Exception failure)
    {
        var inner = new Mock<IConnectorStateStore>();
        inner.Setup(s => s.ResumeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        inner.Setup(s => s.SetResumeAsync(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, DateTime?>>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        inner.Setup(s => s.VendorAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        inner.Setup(s => s.SetVendorAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        inner.Setup(s => s.ClearAsync(It.IsAny<string>(), It.IsAny<ConnectorStateSlots>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        return inner;
    }

    [Theory]
    [MemberData(nameof(StoreFailures))]
    public async Task A_failure_of_the_database_is_raised_as_the_named_exception_with_the_failure_kept(Exception failure)
    {
        var store = new ConnectorStateFailures(FailingWith(failure).Object);

        foreach (var operation in Operations())
        {
            var run = () => operation(store);

            var raised = (await run.Should().ThrowAsync<ConnectorStateUnavailableException>()).Which;
            raised.InnerException.Should().BeSameAs(failure);
            raised.Message.Should().Be(failure.Message);
        }
    }

    [Theory]
    [MemberData(nameof(Defects))]
    public async Task A_defect_passes_through_unchanged(Exception defect)
    {
        var store = new ConnectorStateFailures(FailingWith(defect).Object);

        foreach (var operation in Operations())
        {
            var run = () => operation(store);

            (await run.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(defect);
        }
    }

    [Fact]
    public async Task What_the_store_returns_is_returned_as_it_is()
    {
        var resume = new Dictionary<string, DateTime> { ["k"] = new(2026, 11, 24, 9, 0, 0, DateTimeKind.Utc) };
        var inner = new Mock<IConnectorStateStore>();
        inner.Setup(s => s.ResumeAsync(Source, It.IsAny<CancellationToken>())).ReturnsAsync(resume);

        (await new ConnectorStateFailures(inner.Object).ResumeAsync(Source)).Should().BeSameAs(resume);
    }

    [Fact]
    public async Task The_store_a_connector_holds_through_the_publisher_raises_the_named_exception()
    {
        var failure = new DbUpdateException("save failed", new NpgsqlException("connection refused"));
        var publisher = new InProcessConnectorPublisher(
            Mock.Of<IGlucosePublisher>(),
            Mock.Of<ITreatmentPublisher>(),
            Mock.Of<IDevicePublisher>(),
            Mock.Of<IMetadataPublisher>(),
            FailingWith(failure).Object,
            new PublishSkipTally());

        var write = () => publisher.State.SetResumeAsync(Source, Changes);

        (await write.Should().ThrowAsync<ConnectorStateUnavailableException>()).Which.InnerException.Should().BeSameAs(failure);
    }

    [Fact]
    public async Task The_store_the_apis_own_readers_take_raises_the_raw_failure()
    {
        var failure = new DbUpdateException("save failed", new NpgsqlException("connection refused"));
        var repository = new Mock<IConnectorConfigurationRepository>();
        repository.Setup(r => r.GetResumeStateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        var store = new ConnectorStateStore(repository.Object, NullLogger<ConnectorStateStore>.Instance);

        var read = () => store.ResumeAsync(Source);

        (await read.Should().ThrowAsync<DbUpdateException>()).Which.Should().BeSameAs(failure);
    }
}
