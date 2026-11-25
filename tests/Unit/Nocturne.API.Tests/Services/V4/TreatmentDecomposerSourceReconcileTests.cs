using Nocturne.API.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;

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

    private readonly StatementRecorder _statements = new();
    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly TreatmentDecomposer _decomposer;

    public TreatmentDecomposerSourceReconcileTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId, "test", _statements);
        _context = _db.CreateContext();

        _decomposer = TreatmentDecomposerDoubles.Build(
            _context.AsFactory(),
            bolus: EmptyLegacyKeyed.Of<IBolusRepository, Nocturne.Core.Models.V4.Bolus>(),
            tempBasal: EmptyLegacyKeyed.Of<ITempBasalRepository, Nocturne.Core.Models.V4.TempBasal>(),
            carbIntake: EmptyLegacyKeyed.Of<ICarbIntakeRepository, Nocturne.Core.Models.V4.CarbIntake>(),
            bgCheck: EmptyLegacyKeyed.Of<IBGCheckRepository, Nocturne.Core.Models.V4.BGCheck>(),
            note: EmptyLegacyKeyed.Of<INoteRepository, Nocturne.Core.Models.V4.Note>(),
            deviceEvent: EmptyLegacyKeyed.Of<IDeviceEventRepository, Nocturne.Core.Models.V4.DeviceEvent>(),
            bolusCalculation: EmptyLegacyKeyed.Of<IBolusCalculationRepository, Nocturne.Core.Models.V4.BolusCalculation>(),
            audit: new AuditContext { IsSystem = true });
    }

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
        (await _decomposer.SelectForRepublishAsync(Connector, [Upstream("t-1", 20)])).Decompose.Should().ContainSingle();
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
    public async Task Stored_ids_include_the_spans_this_source_stored_treatments_as_in_the_window()
    {
        AddActivation("switch", Connector, At);
        AddSpan("override", "Override", Connector, """{"collection":"treatments","utcOffset":0}""", At.AddMinutes(5));
        AddSpan("target", "TemporaryTarget", Connector, """{"utcOffset":0}""", At.AddMinutes(10));
        AddSpan("devicestatus-override", "Override", Connector, """{"reason":"devicestatus"}""", At);
        AddActivation("other-source", Other, At);
        AddActivation("before-window", Connector, At.AddHours(-3));
        await _context.SaveChangesAsync();

        var stored = await _decomposer.GetLegacyIdsFromSourceAsync(Connector, At.AddHours(-1), At.AddHours(1));

        stored.Should().BeEquivalentTo(new Dictionary<string, DateTime>
        {
            ["switch"] = At, ["override"] = At.AddMinutes(5), ["target"] = At.AddMinutes(10),
        });
    }

    [Fact]
    public async Task A_profile_switch_is_deleted_once_the_source_drops_it()
    {
        AddActivation("switch", Connector, At);
        AddActivation("kept", Connector, At.AddMinutes(5));
        await _context.SaveChangesAsync();
        var upstream = new HashSet<string> { "kept" };

        var gone = (await _decomposer.GetLegacyIdsFromSourceAsync(Connector, At.AddHours(-1), At.AddHours(1)))
            .Keys.Where(id => !upstream.Contains(id)).ToHashSet();
        await _decomposer.DeleteFromSourceAsync(Connector, gone);

        gone.Should().BeEquivalentTo(["switch"]);
        (await _context.ProfileActivations.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.LegacyId == "switch"))
            .DeletedAt.Should().NotBeNull();
        (await _context.ProfileActivations.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.LegacyId == "kept"))
            .DeletedAt.Should().BeNull();
    }

    private void AddActivation(string legacyId, string source, DateTime start) =>
        _context.ProfileActivations.Add(new ProfileActivationEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, LegacyId = legacyId, DataSource = source, Timestamp = start,
            Phase = nameof(Nocturne.Core.Models.V4.ProfileActivationPhase.Requested),
            Origin = nameof(Nocturne.Core.Models.V4.ProfileActivationOrigin.Uploader), BaseProfileName = "Weekend",
        });

    private void AddSpan(string originalId, string category, string source, string metadataJson, DateTime start) =>
        _context.StateSpans.Add(new StateSpanEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, LegacyId = originalId, Category = category,
            State = "Active", StartTimestamp = start, Source = source, MetadataJson = metadataJson,
        });

    [Fact]
    public async Task A_legacy_span_with_a_note_beside_it_counts_once()
    {
        AddSpan("legacy", "Override", Connector, """{"collection":"treatments","utcOffset":0}""", At);
        _context.Notes.Add(new NoteEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, LegacyId = "legacy", DataSource = Connector,
            Timestamp = At, Text = "beside",
        });
        await _context.SaveChangesAsync();

        (await _decomposer.DeleteFromSourceAsync(Connector, new HashSet<string> { "legacy" })).Should().Be(1);
    }

    [Fact]
    public async Task A_treatment_nothing_holds_is_selected()
    {
        var swept = await AddCarbAsync("swept", Connector);
        await SoftDeleteAsync<CarbIntakeEntity>(swept, byUser: false);

        var selected = await _decomposer.SelectForRepublishAsync(Connector, [Upstream("new", 20), Upstream("swept", 20)]);

        selected.Decompose.Select(t => t.Id).Should().BeEquivalentTo(["new", "swept"]);
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
            State = "Custom", StartTimestamp = At, LegacyId = "override",
        });
        await _context.SaveChangesAsync();

        var selected = await _decomposer.SelectForRepublishAsync(Connector,
        [
            Upstream("user-deleted", 20), Upstream("other-source", 20),
            new Treatment { Id = "override", EventType = "Temporary Override", Created_at = "2026-03-01T12:00:00.000Z" },
        ]);

        selected.Count.Should().Be(0);
    }

    [Fact]
    public async Task A_batch_of_rows_stored_before_fingerprints_is_stamped_in_one_statement_per_table()
    {
        var upstream = Enumerable.Range(0, 12).Select(i => Upstream($"t-{i}", 20 + i)).ToList();
        var rows = new List<Guid>();
        foreach (var treatment in upstream)
            rows.Add(await AddCarbAsync(treatment.Id!, Connector));
        _statements.Clear();

        var selected = await _decomposer.SelectForRepublishAsync(Connector, upstream);

        selected.Count.Should().Be(0);
        _statements.Count("UPDATE").Should().Be(DecomposedTreatmentTables.All.Count + 1, "one per decomposed table, and one for the spans");
        for (var i = 0; i < upstream.Count; i++)
            (await FingerprintOfAsync(rows[i])).Should().Be(TreatmentDecomposer.UpstreamFingerprint(upstream[i]));
    }

    [Fact]
    public async Task Ids_only_another_source_holds_are_neither_selected_nor_stamped()
    {
        await AddCarbAsync("theirs", Other);
        await AddCarbAsync("also-theirs", Other);
        _statements.Clear();

        var selected = await _decomposer.SelectForRepublishAsync(Connector, [Upstream("theirs", 20), Upstream("also-theirs", 30)]);

        selected.Count.Should().Be(0);
        _statements.Count("UPDATE").Should().Be(0, "this source holds no rows under either id, so there is no baseline to stamp");
    }

    [Fact]
    public async Task A_changed_override_is_selected_for_its_span_and_not_decomposed_again()
    {
        AddOverrideSpan("override", fingerprint: TreatmentDecomposer.UpstreamFingerprint(UpstreamOverride("override", 60)));
        await _context.SaveChangesAsync();

        var unchanged = await _decomposer.SelectForRepublishAsync(Connector, [UpstreamOverride("override", 60)]);
        var changed = await _decomposer.SelectForRepublishAsync(Connector, [UpstreamOverride("override", 30)]);

        unchanged.Count.Should().Be(0);
        changed.Decompose.Should().BeEmpty();
        changed.Spans.Should().ContainSingle(t => t.Id == "override");
    }

    [Fact]
    public async Task An_override_span_with_no_fingerprint_is_stamped_and_not_selected()
    {
        var span = AddOverrideSpan("override", fingerprint: null);
        await _context.SaveChangesAsync();

        var selected = await _decomposer.SelectForRepublishAsync(Connector, [UpstreamOverride("override", 30)]);

        selected.Count.Should().Be(0);
        (await _context.StateSpans.AsNoTracking().Where(s => s.Id == span).Select(s => s.UpstreamFingerprint).SingleAsync())
            .Should().Be(TreatmentDecomposer.UpstreamFingerprint(UpstreamOverride("override", 30)));
    }

    [Fact]
    public async Task A_changed_profile_switch_is_selected_and_an_unchanged_one_is_not()
    {
        _context.ProfileActivations.Add(new ProfileActivationEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, LegacyId = "switch", DataSource = Connector,
            Timestamp = At, Phase = "Requested", Origin = "Uploader", BaseProfileName = "Default",
            UpstreamFingerprint = TreatmentDecomposer.UpstreamFingerprint(UpstreamSwitch(60)),
        });
        await _context.SaveChangesAsync();

        var unchanged = await _decomposer.SelectForRepublishAsync(Connector, [UpstreamSwitch(60)]);
        var changed = await _decomposer.SelectForRepublishAsync(Connector, [UpstreamSwitch(30)]);

        unchanged.Count.Should().Be(0);
        changed.Decompose.Should().ContainSingle(t => t.Id == "switch");
        changed.Spans.Should().BeEmpty();
    }

    [Fact]
    public async Task A_profile_switch_with_no_fingerprint_is_stamped_and_not_selected()
    {
        var row = Guid.CreateVersion7();
        _context.ProfileActivations.Add(new ProfileActivationEntity
        {
            Id = row, TenantId = TenantId, LegacyId = "switch", DataSource = Connector,
            Timestamp = At, Phase = "Requested", Origin = "Uploader", BaseProfileName = "Default",
        });
        await _context.SaveChangesAsync();

        var selected = await _decomposer.SelectForRepublishAsync(Connector, [UpstreamSwitch(30)]);

        selected.Count.Should().Be(0);
        (await _context.ProfileActivations.AsNoTracking().Where(a => a.Id == row).Select(a => a.UpstreamFingerprint).SingleAsync())
            .Should().Be(TreatmentDecomposer.UpstreamFingerprint(UpstreamSwitch(30)));
    }

    [Fact]
    public void An_effective_profile_switch_follows_the_rule_of_a_profile_switch()
    {
        var effective = new Treatment
        {
            Id = "eps-1", EventType = "Note", OriginalProfileName = "Rhys", Created_at = "2026-03-01T12:00:00.000Z",
        };

        _decomposer.CanRepublish(effective, stored: true).Should().BeTrue();
        _decomposer.CanRepublish(effective, stored: false).Should().BeTrue();
    }

    [Fact]
    public Task A_profile_switch_whose_percentage_changed_upstream_is_selected() =>
        AssertEditedSwitchIsSelectedAsync(t => t.Percentage = 120);

    [Fact]
    public Task A_profile_switch_whose_timeshift_changed_upstream_is_selected() =>
        AssertEditedSwitchIsSelectedAsync(t => t.Timeshift = 2);

    [Fact]
    public Task A_profile_switch_whose_embedded_profile_changed_upstream_is_selected() =>
        AssertEditedSwitchIsSelectedAsync(t => t.ProfileJson = """{"defaultProfile":"Default","store":{}}""");

    private async Task AssertEditedSwitchIsSelectedAsync(Action<Treatment> edit)
    {
        var stored = UpstreamSwitch(60);
        stored.Percentage = 100;
        stored.Timeshift = 0;
        stored.ProfileJson = """{"defaultProfile":"Default","store":{"Default":{}}}""";
        _context.ProfileActivations.Add(new ProfileActivationEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, LegacyId = "switch", DataSource = Connector,
            Timestamp = At, Phase = "Requested", Origin = "Uploader", BaseProfileName = "Default",
            UpstreamFingerprint = TreatmentDecomposer.UpstreamFingerprint(stored),
        });
        await _context.SaveChangesAsync();

        var edited = UpstreamSwitch(60);
        edited.Percentage = stored.Percentage;
        edited.Timeshift = stored.Timeshift;
        edited.ProfileJson = stored.ProfileJson;
        edit(edited);

        (await _decomposer.SelectForRepublishAsync(Connector, [stored])).Count.Should().Be(0);
        var selected = await _decomposer.SelectForRepublishAsync(Connector, [edited]);

        selected.Decompose.Should().ContainSingle(t => t.Id == "switch");
        selected.Spans.Should().BeEmpty();
    }

    private static Treatment UpstreamSwitch(double duration) => new()
    {
        Id = "switch", EventType = "Profile Switch", Profile = "Default", Duration = duration,
        Created_at = "2026-03-01T12:00:00.000Z", DataSource = Connector,
    };

    [Fact]
    public async Task A_row_stored_before_fingerprints_is_stamped_and_not_overwritten()
    {
        var row = await AddCarbAsync("t-1", Connector);

        var selected = await _decomposer.SelectForRepublishAsync(Connector, [Upstream("t-1", 35)]);

        selected.Decompose.Should().BeEmpty();
        (await FingerprintOfAsync(row)).Should().Be(TreatmentDecomposer.UpstreamFingerprint(Upstream("t-1", 35)));
    }

    [Fact]
    public async Task A_stored_treatment_is_selected_only_once_the_source_changes_it()
    {
        await AddCarbAsync("t-1", Connector);
        await _decomposer.SelectForRepublishAsync(Connector, [Upstream("t-1", 35)]);

        (await _decomposer.SelectForRepublishAsync(Connector, [Upstream("t-1", 35)])).Decompose.Should().BeEmpty();
        (await _decomposer.SelectForRepublishAsync(Connector, [Upstream("t-1", 50)])).Decompose.Should().ContainSingle();
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

        selected.Decompose.Should().ContainSingle();
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
    [InlineData("Profile Switch", true, true)]
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

    private static Treatment UpstreamOverride(string id, double duration) => new()
    {
        Id = id, EventType = "Temporary Override", Duration = duration,
        Created_at = "2026-03-01T12:00:00.000Z", DataSource = Connector,
    };

    private Guid AddOverrideSpan(string legacyId, string? fingerprint)
    {
        var id = Guid.CreateVersion7();
        _context.StateSpans.Add(new StateSpanEntity
        {
            Id = id, TenantId = TenantId, Category = nameof(StateSpanCategory.Override), State = "Custom",
            StartTimestamp = At, EndTimestamp = At.AddMinutes(60), LegacyId = legacyId, Source = Connector,
            UpstreamFingerprint = fingerprint,
        });
        return id;
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
            Rate = 1, Timestamp = At, Origin = "Algorithm",
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
