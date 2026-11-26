using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.ConnectorPublishing;
using Nocturne.API.Services.Connectors;
using Nocturne.API.Services.Health.GoogleHealth;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.GoogleHealth.Services;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Health;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Services.ConnectorPublishing;

/// <summary>
/// A connector learns that the database behind any store it holds failed through one named exception,
/// raised for exactly the failures <c>StoreFailure</c> names, from every method of every store. A defect
/// passes through unchanged, and the API's own readers of a store see the raw failure.
/// </summary>
[Trait("Category", "Unit")]
public class ConnectorStoreFailuresTests
{
    private const string Source = "nightscout-connector";

    private static readonly Type[] Stores =
    [
        typeof(IGlucosePublisher),
        typeof(ITreatmentPublisher),
        typeof(IDevicePublisher),
        typeof(IMetadataPublisher),
        typeof(IConnectorStateStore),
        typeof(IGoogleHealthReadingWriter),
    ];

    private static readonly Exception[] StoreFailures =
    [
        new NpgsqlException("connection refused"),
        new RetryLimitExceededException("retries exhausted", new NpgsqlException("connection refused")),
        new DbUpdateException("save failed", new NpgsqlException("connection refused")),
    ];

    private static readonly Exception[] Defects =
    [
        new InvalidOperationException("a second operation was started on this context"),
        new InvalidCastException("defect"),
        new NullReferenceException(),
        new OperationCanceledException(),
    ];

    /// <summary>A store whose every method fails with the exception it was made with.</summary>
    public class Failing<TStore> : DispatchProxy
        where TStore : class
    {
        private Exception _failure = null!;

        public static TStore Of(Exception failure)
        {
            var store = Create<TStore, Failing<TStore>>();
            ((Failing<TStore>)(object)store)._failure = failure;
            return store;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var returns = targetMethod!.ReturnType;
            if (returns == typeof(Task))
                return Task.FromException(_failure);

            return typeof(Task).GetMethod(nameof(Task.FromException), 1, [typeof(Exception)])!
                .MakeGenericMethod(returns.GetGenericArguments()[0])
                .Invoke(null, [_failure]);
        }
    }

    private static object FailingStore(Type store, Exception failure) =>
        typeof(Failing<>).MakeGenericType(store).GetMethod("Of")!.Invoke(null, [failure])!;

    private static object Translated(Type store, object inner) =>
        typeof(ConnectorStoreFailures<>).MakeGenericType(store).GetMethod("Over")!.Invoke(null, [inner])!;

    private static async Task<Exception?> Raised(object store, MethodInfo method)
    {
        var arguments = method.GetParameters()
            .Select(p => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)
            .ToArray();
        try
        {
            await (Task)method.Invoke(store, arguments)!;
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    [Fact]
    public void Every_method_of_a_store_returns_a_task()
    {
        foreach (var store in Stores)
        {
            store.GetMethods().Should().NotBeEmpty();
            store.GetMethods().Should().OnlyContain(m => typeof(Task).IsAssignableFrom(m.ReturnType), store.Name);
        }
    }

    [Fact]
    public async Task A_failure_of_the_database_is_raised_as_the_named_exception_by_every_method_of_every_store()
    {
        var checkedMethods = 0;
        foreach (var store in Stores)
        foreach (var failure in StoreFailures)
        {
            var translated = Translated(store, FailingStore(store, failure));
            foreach (var method in store.GetMethods())
            {
                var raised = await Raised(translated, method);

                raised.Should().BeOfType<ConnectorStoreUnavailableException>($"{store.Name}.{method.Name} raised a store failure");
                raised!.InnerException.Should().BeSameAs(failure);
                raised.Message.Should().Be(failure.Message);
                checkedMethods++;
            }
        }

        checkedMethods.Should().BeGreaterThan(Stores.Length * StoreFailures.Length);
    }

    [Fact]
    public async Task A_defect_passes_through_unchanged_from_every_method_of_every_store()
    {
        foreach (var store in Stores)
        foreach (var defect in Defects)
        {
            var translated = Translated(store, FailingStore(store, defect));
            foreach (var method in store.GetMethods())
                (await Raised(translated, method)).Should().BeSameAs(defect, $"{store.Name}.{method.Name} raised a defect");
        }
    }

    [Fact]
    public async Task A_failure_raised_before_the_call_returns_its_task_is_translated_too()
    {
        var failure = new DbUpdateException("save failed", new NpgsqlException("connection refused"));
        var inner = new Mock<IGlucosePublisher>();
        inner.Setup(p => p.GetLatestEntryTimestampAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Throws(failure);

        var read = () => ConnectorStoreFailures<IGlucosePublisher>.Over(inner.Object).GetLatestEntryTimestampAsync(Source);

        (await read.Should().ThrowAsync<ConnectorStoreUnavailableException>()).Which.InnerException.Should().BeSameAs(failure);
    }

    [Fact]
    public async Task What_a_store_returns_is_returned_as_it_is_and_the_arguments_reach_it()
    {
        var at = new DateTime(2026, 11, 24, 9, 0, 0, DateTimeKind.Utc);
        var inner = new Mock<IDevicePublisher>();
        inner.Setup(p => p.GetLatestDeviceStatusTimestampAsync(Source, It.IsAny<CancellationToken>())).ReturnsAsync(at);

        var store = ConnectorStoreFailures<IDevicePublisher>.Over(inner.Object);

        (await store.GetLatestDeviceStatusTimestampAsync(Source)).Should().Be(at);
        inner.Verify(p => p.GetLatestDeviceStatusTimestampAsync(Source, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_method_returning_a_plain_task_completes_when_the_store_does()
    {
        var inner = new Mock<IConnectorStateStore>();
        inner.Setup(s => s.SetVendorAsync(Source, "k", null, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        await ConnectorStoreFailures<IConnectorStateStore>.Over(inner.Object).SetVendorAsync(Source, "k", null);

        inner.Verify(s => s.SetVendorAsync(Source, "k", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Every_store_the_publisher_hands_a_connector_raises_the_named_exception()
    {
        var failure = new DbUpdateException("save failed", new NpgsqlException("connection refused"));
        var publisher = new InProcessConnectorPublisher(
            Failing<IGlucosePublisher>.Of(failure),
            Failing<ITreatmentPublisher>.Of(failure),
            Failing<IDevicePublisher>.Of(failure),
            Failing<IMetadataPublisher>.Of(failure),
            Failing<IConnectorStateStore>.Of(failure),
            new PublishSkipTally());

        var handed = new (Type Store, object Held)[]
        {
            (typeof(IGlucosePublisher), publisher.Glucose),
            (typeof(ITreatmentPublisher), publisher.Treatments),
            (typeof(IDevicePublisher), publisher.Device),
            (typeof(IMetadataPublisher), publisher.Metadata),
            (typeof(IConnectorStateStore), publisher.State),
        };

        foreach (var (store, held) in handed)
        foreach (var method in store.GetMethods())
        {
            var raised = await Raised(held, method);

            raised.Should().BeOfType<ConnectorStoreUnavailableException>($"{store.Name}.{method.Name} is held by the connector");
            raised!.InnerException.Should().BeSameAs(failure);
        }
    }

    [Fact]
    public async Task The_writer_the_google_health_connector_holds_raises_the_named_exception_and_the_apis_keeps_the_raw_failure()
    {
        var failure = new DbUpdateException("save failed", new NpgsqlException("connection refused"));
        var services = new ServiceCollection();
        services.AddScoped(_ => Failing<IGoogleHealthReadingWriter>.Of(failure));
        services.AddGoogleHealthPlatform();
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var connector = scope.ServiceProvider.GetRequiredKeyedService<IGoogleHealthReadingWriter>(GoogleHealthServiceKeys.Writer);
        var api = scope.ServiceProvider.GetRequiredService<IGoogleHealthReadingWriter>();

        var held = () => connector.AbandonReconciliationAsync(Guid.Empty, CancellationToken.None);
        var raw = () => api.AbandonReconciliationAsync(Guid.Empty, CancellationToken.None);

        (await held.Should().ThrowAsync<ConnectorStoreUnavailableException>()).Which.InnerException.Should().BeSameAs(failure);
        (await raw.Should().ThrowAsync<DbUpdateException>()).Which.Should().BeSameAs(failure);
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
