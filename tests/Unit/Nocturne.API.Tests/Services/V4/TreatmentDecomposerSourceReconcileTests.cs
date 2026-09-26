using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.V4;

/// <summary>
/// The reads and the delete a connector reconciles its recent treatments through, over a real
/// schema: only the named source's rows are touched, and the event stays visible when another
/// source holds a copy of it.
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentDecomposerSourceReconcileTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private const string Connector = "nightscout-connector";
    private const string Other = "other-connector";

    private static readonly DateTime At = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly TreatmentDecomposer _decomposer;

    public TreatmentDecomposerSourceReconcileTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId);
        _context = _db.CreateContext();

        _decomposer = NewDecomposer(new DeduplicationService(
            _context, Mock.Of<IServiceScopeFactory>(), NullLogger<DeduplicationService>.Instance));
    }

    private TreatmentDecomposer NewDecomposer(IDeduplicationService deduplication) =>
        new(
            _context,
            Mock.Of<IBolusRepository>(), Mock.Of<ITempBasalRepository>(),
            Mock.Of<ICarbIntakeRepository>(), Mock.Of<IBGCheckRepository>(), Mock.Of<INoteRepository>(),
            Mock.Of<IDeviceEventRepository>(), Mock.Of<IBolusCalculationRepository>(),
            Mock.Of<IStateSpanService>(),
            Mock.Of<ITreatmentFoodService>(),
            Mock.Of<IDeviceService>(),
            Mock.Of<IPatientDeviceStamper>(),
            Mock.Of<IProfileDecomposer>(),
            Mock.Of<IActiveProfileResolver>(),
            Mock.Of<IPatientInsulinRepository>(),
            new AuditContext { IsSystem = true },
            deduplication,
            NullLogger<TreatmentDecomposer>.Instance);

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Deleting_from_a_source_leaves_other_sources_and_unnamed_ids_alone()
    {
        var named = await AddCarbAsync("t-1", Connector);
        var otherSource = await AddCarbAsync("t-2", Other);
        var unnamed = await AddCarbAsync("t-3", Connector);
        var namedBolus = await AddBolusAsync("t-1", Connector);

        var deleted = await _decomposer.DeleteFromSourceAsync(Connector, new HashSet<string> { "t-1", "t-2" });

        deleted.Should().Be(2);
        (await DeletedAtAsync<CarbIntakeEntity>(named)).Should().NotBeNull();
        (await DeletedAtAsync<BolusEntity>(namedBolus)).Should().NotBeNull("a treatment's decomposed rows go together");
        (await DeletedAtAsync<CarbIntakeEntity>(otherSource)).Should().BeNull();
        (await DeletedAtAsync<CarbIntakeEntity>(unnamed)).Should().BeNull();
    }

    [Fact]
    public async Task A_sweep_delete_does_not_block_the_source_publishing_it_again()
    {
        var named = await AddCarbAsync("t-1", Connector);

        await _decomposer.DeleteFromSourceAsync(Connector, new HashSet<string> { "t-1" });

        var row = await _context.CarbIntakes.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == named);
        _context.Entry(row).Property<bool>("DeletedByUser").CurrentValue.Should().BeFalse();
        (await _decomposer.SelectForRepublishAsync(Connector, [Upstream("t-1", 20)])).Should().ContainSingle();
    }

    [Fact]
    public async Task Deleting_a_groups_primary_hands_the_flag_to_the_other_sources_copy()
    {
        var connectorCopy = await AddCarbAsync("t-1", Connector);
        var otherCopy = await AddCarbAsync("t-9", Other, At.AddSeconds(30));
        var canonical = Guid.CreateVersion7();
        AddLink(canonical, connectorCopy, At, Connector, isPrimary: true);
        AddLink(canonical, otherCopy, At.AddSeconds(30), Other, isPrimary: false);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await _decomposer.DeleteFromSourceAsync(Connector, new HashSet<string> { "t-1" });

        var primary = await _context.LinkedRecords.AsNoTracking().SingleAsync(l => l.IsPrimary);
        primary.RecordId.Should().Be(otherCopy);
    }

    [Fact]
    public async Task A_failed_repoint_rolls_the_delete_back()
    {
        var named = await AddCarbAsync("t-1", Connector);
        var deduplication = new Mock<IDeduplicationService>();
        deduplication
            .Setup(d => d.RepointPrimariesAwayFromAsync(
                It.IsAny<RecordType>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("repoint failed"));
        var decomposer = NewDecomposer(deduplication.Object);

        var act = () => decomposer.DeleteFromSourceAsync(Connector, new HashSet<string> { "t-1" });

        await act.Should().ThrowAsync<InvalidOperationException>();
        _context.ChangeTracker.Clear();
        (await DeletedAtAsync<CarbIntakeEntity>(named)).Should().BeNull(
            "a deleted primary left in place would hide the other sources' copies");
    }

    [Fact]
    public async Task Stored_ids_are_this_sources_live_rows_in_the_window()
    {
        await AddCarbAsync("in-window", Connector);
        await AddCarbAsync("before-window", Connector, At.AddHours(-3));
        await AddCarbAsync("other-source", Other);
        var gone = await AddCarbAsync("deleted", Connector);
        await SoftDeleteAsync<CarbIntakeEntity>(gone, byUser: false);
        await AddTempBasalAsync("temp-basal", Connector);

        var stored = await _decomposer.GetLegacyIdsFromSourceAsync(Connector, At.AddHours(-1), At.AddHours(1));

        stored.Should().BeEquivalentTo(new Dictionary<string, DateTime> { ["in-window"] = At, ["temp-basal"] = At });
    }

    [Fact]
    public async Task A_treatment_nothing_holds_is_selected()
    {
        var swept = await AddCarbAsync("swept", Connector);
        await SoftDeleteAsync<CarbIntakeEntity>(swept, byUser: false);

        var selected = await _decomposer.SelectForRepublishAsync(Connector, [Upstream("new", 20), Upstream("swept", 20)]);

        selected.Select(t => t.Id).Should().BeEquivalentTo(["new", "swept"]);
    }

    [Fact]
    public async Task What_the_user_deleted_another_source_holds_or_a_span_carries_is_not_selected()
    {
        var userDeleted = await AddCarbAsync("user-deleted", Connector);
        await SoftDeleteAsync<CarbIntakeEntity>(userDeleted, byUser: true);
        await AddCarbAsync("other-source", Other);
        _context.StateSpans.Add(new StateSpanEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, Category = nameof(StateSpanCategory.Override),
            State = "Custom", StartTimestamp = At, OriginalId = "override",
        });
        await _context.SaveChangesAsync();

        var selected = await _decomposer.SelectForRepublishAsync(Connector,
        [
            Upstream("user-deleted", 20), Upstream("other-source", 20),
            new Treatment { Id = "override", EventType = "Temporary Override", Created_at = "2026-03-01T12:00:00.000Z" },
        ]);

        selected.Should().BeEmpty();
    }

    [Fact]
    public async Task A_row_stored_before_fingerprints_is_stamped_and_not_overwritten()
    {
        var row = await AddCarbAsync("t-1", Connector);

        var selected = await _decomposer.SelectForRepublishAsync(Connector, [Upstream("t-1", 35)]);

        selected.Should().BeEmpty();
        (await FingerprintOfAsync(row)).Should().Be(TreatmentDecomposer.UpstreamFingerprint(Upstream("t-1", 35)));
    }

    [Fact]
    public async Task A_stored_treatment_is_selected_only_once_the_source_changes_it()
    {
        await AddCarbAsync("t-1", Connector);
        await _decomposer.SelectForRepublishAsync(Connector, [Upstream("t-1", 35)]);

        (await _decomposer.SelectForRepublishAsync(Connector, [Upstream("t-1", 35)])).Should().BeEmpty();
        (await _decomposer.SelectForRepublishAsync(Connector, [Upstream("t-1", 50)])).Should().ContainSingle();
    }

    [Fact]
    public async Task A_row_left_on_an_old_fingerprint_marks_the_treatment_changed()
    {
        // A meal bolus decomposes into a bolus and a carb row. The carb carries the current
        // fingerprint, the bolus an older one: the treatment has not been fully written as it now is.
        var current = TreatmentDecomposer.UpstreamFingerprint(Upstream("t-1", 35));
        await AddCarbAsync("t-1", Connector, fingerprint: current);
        var bolus = await AddBolusAsync("t-1", Connector);
        await _context.Boluses.Where(b => b.Id == bolus)
            .ExecuteUpdateAsync(u => u.SetProperty(b => b.UpstreamFingerprint, "an-older-fingerprint"));

        var selected = await _decomposer.SelectForRepublishAsync(Connector, [Upstream("t-1", 35)]);

        selected.Should().ContainSingle();
    }

    [Fact]
    public async Task A_treatment_decomposed_outside_a_connector_publish_loses_its_fingerprint()
    {
        var row = await AddCarbAsync("t-1", Connector, fingerprint: "a-connector-fingerprint");

        _context.ChangeTracker.Clear();
        var tracked = await _context.CarbIntakes.SingleAsync(c => c.Id == row);
        tracked.Carbs = 40;
        using (UpstreamFingerprintScope.Open(new Dictionary<(string?, string), string?> { [(Connector, "t-1")] = null }))
            await _context.SaveChangesAsync();

        (await FingerprintOfAsync(row)).Should().BeNull();
    }

    [Fact]
    public async Task A_write_outside_any_scope_keeps_the_fingerprint()
    {
        var row = await AddCarbAsync("t-1", Connector, fingerprint: "a-connector-fingerprint");

        _context.ChangeTracker.Clear();
        (await _context.CarbIntakes.SingleAsync(c => c.Id == row)).Carbs = 40;
        await _context.SaveChangesAsync();

        (await FingerprintOfAsync(row)).Should().Be("a-connector-fingerprint");
    }

    [Theory]
    [InlineData("Carb Correction", false, true)]
    [InlineData("Carb Correction", true, true)]
    [InlineData("Temp Basal", true, true)]
    [InlineData("Temporary Override", false, true)]
    [InlineData("Temporary Override", true, false)]
    [InlineData("Temporary Target", true, false)]
    [InlineData("Profile Switch", true, false)]
    [InlineData("Pump Suspend", false, true)]
    [InlineData("Pump Suspend", true, false)]
    [InlineData("Pump Resume", true, false)]
    [InlineData("Not A Nocturne Type", false, false)]
    public void Only_treatments_that_decompose_again_safely_are_republished(
        string eventType, bool stored, bool republished)
    {
        var treatment = new Treatment { Id = "t-1", EventType = eventType, Created_at = "2026-03-01T12:00:00.000Z" };

        _decomposer.CanRepublish(treatment, stored).Should().Be(republished);
    }

    private static Treatment Upstream(string id, double carbs) => new()
    {
        Id = id, EventType = "Carb Correction", Carbs = carbs,
        Created_at = "2026-03-01T12:00:00.000Z", DataSource = Connector,
    };

    private Task<string?> FingerprintOfAsync(Guid id) =>
        _context.CarbIntakes.AsNoTracking().Where(c => c.Id == id).Select(c => c.UpstreamFingerprint).SingleAsync();

    private async Task<Guid> AddCarbAsync(string legacyId, string source, DateTime? at = null, string? fingerprint = null)
    {
        var id = Guid.CreateVersion7();
        _context.CarbIntakes.Add(new CarbIntakeEntity
        {
            Id = id, TenantId = TenantId, LegacyId = legacyId, DataSource = source,
            Carbs = 20, Timestamp = at ?? At, UpstreamFingerprint = fingerprint,
        });
        await _context.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> AddBolusAsync(string legacyId, string source)
    {
        var id = Guid.CreateVersion7();
        _context.Boluses.Add(new BolusEntity
        {
            Id = id, TenantId = TenantId, LegacyId = legacyId, DataSource = source,
            Insulin = 1, Timestamp = At,
        });
        await _context.SaveChangesAsync();
        return id;
    }

    private async Task AddTempBasalAsync(string legacyId, string source)
    {
        _context.TempBasals.Add(new TempBasalEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, LegacyId = legacyId, DataSource = source,
            Rate = 1, StartTimestamp = At, Origin = "Algorithm",
        });
        await _context.SaveChangesAsync();
    }

    private void AddLink(Guid canonical, Guid recordId, DateTime at, string source, bool isPrimary) =>
        _context.LinkedRecords.Add(new LinkedRecordEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, CanonicalId = canonical,
            RecordType = "carbintake", RecordId = recordId,
            SourceTimestamp = new DateTimeOffset(at).ToUnixTimeMilliseconds(),
            DataSource = source, IsPrimary = isPrimary, SysCreatedAt = DateTime.UtcNow,
        });

    private async Task SoftDeleteAsync<T>(Guid id, bool byUser) where T : class, IV4Entity
    {
        var row = await _context.Set<T>().SingleAsync(e => e.Id == id);
        row.DeletedAt = DateTime.UtcNow;
        _context.Entry(row).Property<bool>("DeletedByUser").CurrentValue = byUser;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private Task<DateTime?> DeletedAtAsync<T>(Guid id) where T : class, IV4Entity =>
        _context.Set<T>().IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.Id == id).Select(e => e.DeletedAt).SingleAsync();
}
