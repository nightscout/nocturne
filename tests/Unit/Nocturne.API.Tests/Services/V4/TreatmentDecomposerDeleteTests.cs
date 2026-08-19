using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Xunit;

namespace Nocturne.API.Tests.Services.V4;

/// <summary>
/// Covers the legacy delete paths through <see cref="TreatmentDecomposer"/> — the v1 bulk delete
/// (<c>DELETE /api/v1/treatments?find[created_at][…]</c>) and the v1/v3 single-treatment delete: every
/// record either removes must be soft-deleted through the audited path so the delete is attributed and
/// <see cref="SoftDeleteDedupExtensions"/> stops a connector resync re-creating what the user removed.
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentDecomposerDeleteTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private const string AuthType = "SessionCookie";

    /// <summary>Every seeded record sits inside this window; the find query below brackets it.</summary>
    private static readonly DateTime Inside = new(2023, 1, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Outside = new(2024, 6, 1, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The legacy treatment id every decomposed row shares on the single-delete path.</summary>
    private const string LegacyTreatmentId = "treat-1";

    private const string Find =
        "find[created_at][$gte]=2023-01-01T00:00:00.000Z&find[created_at][$lte]=2023-01-02T00:00:00.000Z";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _dbOptions;

    private readonly AuditContext _userAuditContext = new()
    {
        SubjectId = Guid.Parse("00000000-0000-0000-0000-0000000000aa"),
        SubjectName = "owner@example.com",
        AuthType = AuthType,
        Endpoint = "DELETE /api/v1/treatments"
    };

    public TreatmentDecomposerDeleteTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        using var db = NewContext();
        db.Database.EnsureCreated();
        db.Tenants.Add(new TenantEntity { Id = TenantId, Slug = "test" });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private NocturneDbContext NewContext() => new(_dbOptions) { TenantId = TenantId };

    private TreatmentDecomposer CreateDecomposer(NocturneDbContext context, IAuditContext auditContext) => new(
        context,
        Mock.Of<IBolusRepository>(),
        Mock.Of<ITempBasalRepository>(),
        Mock.Of<ICarbIntakeRepository>(),
        Mock.Of<IBGCheckRepository>(),
        Mock.Of<INoteRepository>(),
        Mock.Of<IDeviceEventRepository>(),
        Mock.Of<IBolusCalculationRepository>(),
        Mock.Of<IStateSpanService>(),
        Mock.Of<ITreatmentFoodService>(),
        Mock.Of<IDeviceService>(),
        Mock.Of<IPatientDeviceStamper>(),
        Mock.Of<IProfileDecomposer>(),
        Mock.Of<IActiveProfileResolver>(),
        Mock.Of<IPatientInsulinRepository>(),
        auditContext,
        NullLogger<TreatmentDecomposer>.Instance);

    /// <summary>One record of every type the sweep covers, plus a bolus outside the window.</summary>
    /// <param name="legacyIdFor">
    /// Maps a record type to the legacy id it is seeded with, so the by-time sweep can seed distinct
    /// ids while the by-legacy-id delete seeds one treatment's worth of correlated rows.
    /// </param>
    private void SeedOneOfEachType(Func<string, string>? legacyIdFor = null)
    {
        legacyIdFor ??= type => $"{type}-1";

        using var db = NewContext();

        db.Boluses.Add(new BolusEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            LegacyId = legacyIdFor("bolus"),
            Timestamp = Inside,
            Insulin = 1.5
        });
        db.Boluses.Add(new BolusEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            LegacyId = "bolus-outside",
            Timestamp = Outside,
            Insulin = 2.5
        });
        db.CarbIntakes.Add(new CarbIntakeEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            LegacyId = legacyIdFor("carb"),
            Timestamp = Inside,
            Carbs = 20
        });
        db.BGChecks.Add(new BGCheckEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            LegacyId = legacyIdFor("bgcheck"),
            Timestamp = Inside,
            Glucose = 100
        });
        db.Notes.Add(new NoteEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            LegacyId = legacyIdFor("note"),
            Timestamp = Inside,
            Text = "hello"
        });
        db.DeviceEvents.Add(new DeviceEventEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            LegacyId = legacyIdFor("devevent"),
            Timestamp = Inside,
            EventType = "Site Change"
        });
        db.BolusCalculations.Add(new BolusCalculationEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            LegacyId = legacyIdFor("boluscalc"),
            Timestamp = Inside
        });
        db.TempBasals.Add(new TempBasalEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            LegacyId = legacyIdFor("tempbasal"),
            StartTimestamp = Inside,
            Rate = 0.8,
            Origin = "Algorithm"
        });

        db.SaveChanges();
    }

    private async Task<long> BulkDeleteAsync(IAuditContext auditContext)
    {
        await using var ctx = NewContext();
        return await CreateDecomposer(ctx, auditContext).BulkDeleteAsync(Find, WriteOrigin.Live);
    }

    [Fact]
    public async Task BulkDelete_UserContext_BlocksConnectorResyncFromRecreatingEveryType()
    {
        SeedOneOfEachType();

        (await BulkDeleteAsync(_userAuditContext)).Should().Be(7);

        await using var assertCtx = NewContext();

        (await assertCtx.GetBlockingLegacyIdsAsync<BolusEntity>(["bolus-1"])).Should().Contain("bolus-1");
        (await assertCtx.GetBlockingLegacyIdsAsync<CarbIntakeEntity>(["carb-1"])).Should().Contain("carb-1");
        (await assertCtx.GetBlockingLegacyIdsAsync<BGCheckEntity>(["bgcheck-1"])).Should().Contain("bgcheck-1");
        (await assertCtx.GetBlockingLegacyIdsAsync<NoteEntity>(["note-1"])).Should().Contain("note-1");
        (await assertCtx.GetBlockingLegacyIdsAsync<DeviceEventEntity>(["devevent-1"])).Should().Contain("devevent-1");
        (await assertCtx.GetBlockingLegacyIdsAsync<BolusCalculationEntity>(["boluscalc-1"])).Should().Contain("boluscalc-1");
        (await assertCtx.GetBlockingLegacyIdsAsync<TempBasalEntity>(["tempbasal-1"])).Should().Contain("tempbasal-1");
    }

    [Fact]
    public async Task BulkDelete_UserContext_LeavesRecordsOutsideTheWindowUntouched()
    {
        SeedOneOfEachType();

        await BulkDeleteAsync(_userAuditContext);

        await using var assertCtx = NewContext();
        var outside = await assertCtx.Boluses.IgnoreQueryFilters()
            .SingleAsync(b => b.LegacyId == "bolus-outside");
        outside.DeletedAt.Should().BeNull();
    }

    [Fact]
    public async Task BulkDelete_UserContext_WritesOneSummaryRowPerTypeScopedToTheWindow()
    {
        SeedOneOfEachType();

        await BulkDeleteAsync(_userAuditContext);

        await using var assertCtx = NewContext();
        var summaries = await assertCtx.MutationAuditLog.Where(a => a.Action == "bulk_delete").ToListAsync();

        summaries.Select(s => s.EntityType).Should().BeEquivalentTo(
            new[] { "Bolus", "CarbIntake", "BGCheck", "Note", "DeviceEvent", "BolusCalculation", "TempBasal" });
        summaries.Should().OnlyContain(s => s.EntityId == null && s.AuthType == AuthType);
        summaries.Should().OnlyContain(s => s.ChangesJson!.Contains(
            "timestamp=2023-01-01T00:00:00.0000000Z..2023-01-02T00:00:00.0000000Z"));
    }

    [Fact]
    public async Task BulkDelete_SystemContext_LeavesRecordsRecreatableAndUnaudited()
    {
        SeedOneOfEachType();

        (await BulkDeleteAsync(SystemAuditContext.ForService("connector:nightscout"))).Should().Be(7);

        await using var assertCtx = NewContext();
        var bolus = await assertCtx.Boluses.IgnoreQueryFilters().SingleAsync(b => b.LegacyId == "bolus-1");
        bolus.DeletedAt.Should().NotBeNull();

        (await assertCtx.GetBlockingLegacyIdsAsync<BolusEntity>(["bolus-1"])).Should().BeEmpty();
        (await assertCtx.GetBlockingLegacyIdsAsync<TempBasalEntity>(["tempbasal-1"])).Should().BeEmpty();
        (await assertCtx.MutationAuditLog.AnyAsync()).Should().BeFalse();
    }

    private async Task<int> DeleteByLegacyIdAsync(IAuditContext auditContext)
    {
        await using var ctx = NewContext();
        return await CreateDecomposer(ctx, auditContext)
            .DeleteByLegacyIdAsync(LegacyTreatmentId, WriteOrigin.Live);
    }

    [Fact]
    public async Task DeleteByLegacyId_UserContext_BlocksConnectorResyncFromRecreatingEveryType()
    {
        SeedOneOfEachType(_ => LegacyTreatmentId);

        (await DeleteByLegacyIdAsync(_userAuditContext)).Should().Be(7);

        await using var assertCtx = NewContext();

        (await assertCtx.GetBlockingLegacyIdsAsync<BolusEntity>([LegacyTreatmentId])).Should().Contain(LegacyTreatmentId);
        (await assertCtx.GetBlockingLegacyIdsAsync<CarbIntakeEntity>([LegacyTreatmentId])).Should().Contain(LegacyTreatmentId);
        (await assertCtx.GetBlockingLegacyIdsAsync<BGCheckEntity>([LegacyTreatmentId])).Should().Contain(LegacyTreatmentId);
        (await assertCtx.GetBlockingLegacyIdsAsync<NoteEntity>([LegacyTreatmentId])).Should().Contain(LegacyTreatmentId);
        (await assertCtx.GetBlockingLegacyIdsAsync<DeviceEventEntity>([LegacyTreatmentId])).Should().Contain(LegacyTreatmentId);
        (await assertCtx.GetBlockingLegacyIdsAsync<BolusCalculationEntity>([LegacyTreatmentId])).Should().Contain(LegacyTreatmentId);
        (await assertCtx.GetBlockingLegacyIdsAsync<TempBasalEntity>([LegacyTreatmentId])).Should().Contain(LegacyTreatmentId);
    }

    [Fact]
    public async Task DeleteByLegacyId_UserContext_LeavesOtherTreatmentsUntouched()
    {
        SeedOneOfEachType(_ => LegacyTreatmentId);

        await DeleteByLegacyIdAsync(_userAuditContext);

        await using var assertCtx = NewContext();
        var other = await assertCtx.Boluses.IgnoreQueryFilters()
            .SingleAsync(b => b.LegacyId == "bolus-outside");
        other.DeletedAt.Should().BeNull();
    }

    /// <summary>
    /// One treatment's fan-out is a handful of rows, not a set, so each gets its own <c>delete</c>
    /// audit row rather than the <c>bulk_delete</c> summary the by-time sweep writes.
    /// </summary>
    [Fact]
    public async Task DeleteByLegacyId_UserContext_AuditsEachDecomposedRecordIndividually()
    {
        SeedOneOfEachType(_ => LegacyTreatmentId);

        await DeleteByLegacyIdAsync(_userAuditContext);

        await using var assertCtx = NewContext();
        var entries = await assertCtx.MutationAuditLog.ToListAsync();

        entries.Should().OnlyContain(a => a.Action == "delete" && a.EntityId != null && a.AuthType == AuthType);
        entries.Select(a => a.EntityType).Should().BeEquivalentTo(
            new[] { "Bolus", "CarbIntake", "BGCheck", "Note", "DeviceEvent", "BolusCalculation", "TempBasal" });
    }

    [Fact]
    public async Task DeleteByLegacyId_SystemContext_LeavesRecordsRecreatableAndUnaudited()
    {
        SeedOneOfEachType(_ => LegacyTreatmentId);

        (await DeleteByLegacyIdAsync(SystemAuditContext.ForService("connector:nightscout"))).Should().Be(7);

        await using var assertCtx = NewContext();
        var bolus = await assertCtx.Boluses.IgnoreQueryFilters()
            .SingleAsync(b => b.LegacyId == LegacyTreatmentId);
        bolus.DeletedAt.Should().NotBeNull();

        (await assertCtx.GetBlockingLegacyIdsAsync<BolusEntity>([LegacyTreatmentId])).Should().BeEmpty();
        (await assertCtx.GetBlockingLegacyIdsAsync<TempBasalEntity>([LegacyTreatmentId])).Should().BeEmpty();
        (await assertCtx.MutationAuditLog.AnyAsync()).Should().BeFalse();
    }
}
