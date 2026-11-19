using Nocturne.API.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Treatments;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services.WriteBack;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Treatments;

/// <summary>
/// Only a single delete of a record write-back may have sent removes its upstream copy; every delete
/// still raises the same events for the other sinks.
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentServiceUpstreamDeleteTests
{
    private readonly Mock<ITreatmentStore> _store = new();
    private readonly Mock<IPerTreatmentDecomposer> _decomposer = new Mock<IPerTreatmentDecomposer>().AnswersBatchPerTreatment();
    private readonly Mock<IDataEventSink<Treatment>> _events = new();
    private readonly Mock<ITreatmentUpstreamDeletion> _upstream = new();

    private TreatmentService Service(ITreatmentUpstreamDeletion? upstream) => new(
        _store.Object, _decomposer.Object, new Mock<ITreatmentCache>().Object, _events.Object,
        new Mock<IPatientInsulinRepository>().Object, NullLogger<TreatmentService>.Instance,
        upstreamDeletion: upstream);

    private static Treatment Served(string id) => new() { Id = id, LegacyId = id, EventType = "Correction Bolus", Insulin = 1 };

    [Fact]
    public async Task A_single_delete_of_a_record_write_back_may_have_sent_removes_its_upstream_copy()
    {
        var served = Served("a");
        _store.Setup(s => s.DeleteAsync("a", true, It.IsAny<CancellationToken>())).ReturnsAsync(new TreatmentDeletion(served, ["a"]));

        (await Service(_upstream.Object).DeleteTreatmentAsync("a")).Should().BeTrue();

        _upstream.Verify(u => u.DeleteUpstreamAsync(served, It.Is<IReadOnlyCollection<string>>(ids => ids.SequenceEqual(new[] { "a" })), It.IsAny<CancellationToken>()), Times.Once);
        _events.Verify(e => e.OnDeletedAsync(served, It.IsAny<CancellationToken>()), Times.Once);
        _events.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_single_delete_of_a_record_write_back_never_sent_leaves_upstream_alone()
    {
        var served = Served("a");
        _store.Setup(s => s.DeleteAsync("a", true, It.IsAny<CancellationToken>())).ReturnsAsync(new TreatmentDeletion(served));

        (await Service(_upstream.Object).DeleteTreatmentAsync("a")).Should().BeTrue();

        _upstream.VerifyNoOtherCalls();
        _events.Verify(e => e.OnDeletedAsync(served, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_delete_with_no_write_back_installed_still_deletes()
    {
        var served = Served("a");
        _store.Setup(s => s.DeleteAsync("a", true, It.IsAny<CancellationToken>())).ReturnsAsync(new TreatmentDeletion(served, ["a"]));

        (await Service(null).DeleteTreatmentAsync("a")).Should().BeTrue();

        _events.Verify(e => e.OnDeletedAsync(served, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_filtered_bulk_delete_announces_each_record_but_sends_no_upstream_delete()
    {
        const string find = "find[eventType]=Correction Bolus&find[created_at][$gte]=2023-01-01T00:00:00.000Z";
        var matching = new List<Treatment> { Served("a"), Served("b") };
        _store.Setup(s => s.QueryAsync(It.Is<TreatmentQuery>(q => q.Find == find), It.IsAny<CancellationToken>())).ReturnsAsync(matching);
        _store.Setup(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, bool _, CancellationToken _) => new TreatmentDeletion(Served(id), [id]));

        (await Service(_upstream.Object).DeleteTreatmentsAsync(find)).Should().Be(2);

        _store.Verify(s => s.DeleteAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()), Times.Never);
        _upstream.VerifyNoOtherCalls();
        _events.Verify(e => e.OnDeletedAsync(matching[0], It.IsAny<CancellationToken>()), Times.Once);
        _events.Verify(e => e.OnDeletedAsync(matching[1], It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_time_window_bulk_delete_sends_no_upstream_delete()
    {
        const string find = "find[created_at][$gte]=2023-01-01T00:00:00.000Z";
        _decomposer.Setup(d => d.BulkDeleteAsync(find, WriteOrigin.Live, It.IsAny<CancellationToken>())).ReturnsAsync(3);

        (await Service(_upstream.Object).DeleteTreatmentsAsync(find)).Should().Be(3);

        _upstream.VerifyNoOtherCalls();
    }

    private static NightscoutTreatmentWriteBackSink WriteBack(Mock<IConnectorConfigurationLoader<NightscoutConnectorConfiguration>> loader) =>
        new(new HttpClient(), loader.Object, new NightscoutCircuitBreaker(), new NeverSentUpstream(),
            NullLogger<NightscoutTreatmentWriteBackSink>.Instance);

    [Theory]
    [InlineData("")]
    [InlineData("ftp://nightscout.example.com")]
    public async Task A_delete_whose_upstream_url_is_unusable_still_deletes(string url)
    {
        var served = Served("a");
        _store.Setup(s => s.DeleteAsync("a", true, It.IsAny<CancellationToken>())).ReturnsAsync(new TreatmentDeletion(served, ["a"]));
        var loader = new Mock<IConnectorConfigurationLoader<NightscoutConnectorConfiguration>>();
        loader.Setup(l => l.LoadForTenantAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NightscoutConnectorConfiguration { Url = url, ApiSecret = "test-secret-12345", WriteBackEnabled = true });

        (await Service(WriteBack(loader)).DeleteTreatmentAsync("a")).Should().BeTrue();

        _events.Verify(e => e.OnDeletedAsync(served, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_delete_whose_upstream_configuration_fails_to_load_still_deletes()
    {
        var served = Served("a");
        _store.Setup(s => s.DeleteAsync("a", true, It.IsAny<CancellationToken>())).ReturnsAsync(new TreatmentDeletion(served, ["a"]));
        var loader = new Mock<IConnectorConfigurationLoader<NightscoutConnectorConfiguration>>();
        loader.Setup(l => l.LoadForTenantAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new UnreadableConfiguration());

        (await Service(WriteBack(loader)).DeleteTreatmentAsync("a")).Should().BeTrue();

        _events.Verify(e => e.OnDeletedAsync(served, It.IsAny<CancellationToken>()), Times.Once);
    }

    private sealed class UnreadableConfiguration() : System.Data.Common.DbException("The connection broke.");
}
