using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Connectors;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Connectors;

/// <summary>
/// The connector state store over real configuration rows: a source finds its row through its
/// registration, the resume slot round-trips UTC times key by key, and nothing lands on a row the
/// source does not own.
/// </summary>
[Trait("Category", "Unit")]
public class ConnectorStateStoreTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();

    private readonly NocturneDbContext _db;
    private readonly ConnectorStateStore _store;

    public ConnectorStateStoreTests()
    {
        _db = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase($"connector-state-{Guid.NewGuid():N}").Options)
        {
            TenantId = TenantId,
        };

        foreach (var name in new[] { "nightscout", "tandem" })
            _db.ConnectorConfigurations.Add(new ConnectorConfigurationEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = TenantId,
                ConnectorName = name,
            });
        _db.SaveChanges();

        _store = new ConnectorStateStore(
            new ConnectorConfigurationRepository(_db.AsFactory()), NullLogger<ConnectorStateStore>.Instance);
    }

    private ConnectorConfigurationEntity Row(string name)
    {
        _db.ChangeTracker.Clear();
        return _db.ConnectorConfigurations.Single(c => c.ConnectorName == name);
    }

    [Fact]
    public async Task Resume_state_round_trips_through_the_data_source_id_as_utc()
    {
        var mark = new DateTime(2024, 6, 1, 12, 30, 15, DateTimeKind.Utc);

        await _store.SetResumeAsync("nightscout-connector", Changes(("Glucose", mark)));

        var read = (await _store.ResumeAsync("nightscout-connector"))["Glucose"];
        read.Should().Be(mark, "the value must round-trip without a timezone shift");
        read.Kind.Should().Be(DateTimeKind.Utc);
        Row("nightscout").ResumeStateJson.Should().NotBeNull();
    }

    [Fact]
    public async Task One_write_stores_and_removes_keys_and_keeps_the_keys_it_does_not_name()
    {
        var glucoseMark = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var treatmentsMark = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var gapFrom = new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        await _store.SetResumeAsync("nightscout-connector",
            Changes(("Glucose", glucoseMark), ("Treatments", treatmentsMark)));
        await _store.SetResumeAsync("nightscout-connector",
            Changes(("Glucose", null), ("Treatments.GapFrom", gapFrom)));

        (await _store.ResumeAsync("nightscout-connector")).Should().BeEquivalentTo(
            new Dictionary<string, DateTime> { ["Treatments"] = treatmentsMark, ["Treatments.GapFrom"] = gapFrom });
    }

    [Fact]
    public async Task Removing_the_last_key_empties_the_column()
    {
        await _store.SetResumeAsync(
            "nightscout-connector", Changes(("Glucose", new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc))));
        await _store.SetResumeAsync("nightscout-connector", Changes(("Glucose", null)));

        Row("nightscout").ResumeStateJson.Should().BeNull();
    }

    /// <summary>
    /// Tandem's data source id does not spell its connector's name; the registration names the row.
    /// </summary>
    [Fact]
    public async Task A_source_whose_id_differs_from_its_connector_name_finds_its_row()
    {
        await _store.SetVendorAsync("tconnectsync-connector", "profile:42", JsonValue.Create("fingerprint"));

        (await _store.VendorAsync("tconnectsync-connector", "profile:42"))!.GetValue<string>().Should().Be("fingerprint");
        JsonNode.Parse(Row("tandem").VendorStateJson!)!["profile:42"]!.GetValue<string>().Should().Be("fingerprint");
        Row("nightscout").VendorStateJson.Should().BeNull();
    }

    [Fact]
    public async Task A_null_vendor_value_removes_the_key()
    {
        await _store.SetVendorAsync("tconnectsync-connector", "profile:42", JsonValue.Create("fingerprint"));
        await _store.SetVendorAsync("tconnectsync-connector", "profile:43", JsonValue.Create("other"));
        await _store.SetVendorAsync("tconnectsync-connector", "profile:42", null);

        (await _store.VendorAsync("tconnectsync-connector", "profile:42")).Should().BeNull();
        (await _store.VendorAsync("tconnectsync-connector", "profile:43"))!.GetValue<string>().Should().Be("other");
    }

    [Fact]
    public async Task Clearing_one_slot_keeps_the_other()
    {
        await _store.SetResumeAsync(
            "tconnectsync-connector", Changes(("Glucose", new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc))));
        await _store.SetVendorAsync("tconnectsync-connector", "profile:42", JsonValue.Create("fingerprint"));

        await _store.ClearAsync("tconnectsync-connector", ConnectorStateSlots.Resume);

        (await _store.ResumeAsync("tconnectsync-connector")).Should().BeEmpty();
        (await _store.VendorAsync("tconnectsync-connector", "profile:42")).Should().NotBeNull();
    }

    [Fact]
    public async Task An_unregistered_source_reads_nothing_and_writes_nowhere()
    {
        await _store.SetResumeAsync(
            "not-a-connector", Changes(("Glucose", new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc))));
        await _store.SetVendorAsync("not-a-connector", "key", JsonValue.Create(1));
        await _store.ClearAsync("not-a-connector", ConnectorStateSlots.Resume | ConnectorStateSlots.Vendor);

        (await _store.ResumeAsync("not-a-connector")).Should().BeEmpty();
        (await _store.VendorAsync("not-a-connector", "key")).Should().BeNull();
        foreach (var name in new[] { "nightscout", "tandem" })
        {
            Row(name).ResumeStateJson.Should().BeNull();
            Row(name).VendorStateJson.Should().BeNull();
        }
    }

    /// <summary>A registered connector the tenant has not configured has no row to write to.</summary>
    [Fact]
    public async Task A_connector_without_a_row_writes_nowhere()
    {
        await _store.SetResumeAsync(
            "dexcom-connector", Changes(("Glucose", new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc))));

        (await _store.ResumeAsync("dexcom-connector")).Should().BeEmpty();
        _db.ChangeTracker.Clear();
        _db.ConnectorConfigurations.Should().HaveCount(2);
        Row("nightscout").ResumeStateJson.Should().BeNull();
    }

    private static Dictionary<string, DateTime?> Changes(params (string Key, DateTime? Mark)[] changes) =>
        changes.ToDictionary(c => c.Key, c => c.Mark);
}
