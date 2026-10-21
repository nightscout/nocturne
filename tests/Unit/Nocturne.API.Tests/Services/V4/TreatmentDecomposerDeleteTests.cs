using Nocturne.API.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.Treatments;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
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
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
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

    private readonly SqliteTestDatabase _db;

    private readonly AuditContext _userAuditContext = new()
    {
        SubjectId = Guid.Parse("00000000-0000-0000-0000-0000000000aa"),
        SubjectName = "owner@example.com",
        AuthType = AuthType,
        Endpoint = "DELETE /api/v1/treatments"
    };

    public TreatmentDecomposerDeleteTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId, SqliteNpgsqlJson.Translate);
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private NocturneDbContext NewContext() => _db.CreateContext(TenantId);

    private TreatmentDecomposer CreateDecomposer(
        NocturneDbContext context,
        IAuditContext auditContext,
        IProfileDecomposer? profileDecomposer = null,
        Microsoft.Extensions.Logging.ILogger<TreatmentDecomposer>? logger = null) => new(
        context.AsFactory(),
        Mock.Of<IBolusRepository>(),
        Mock.Of<ITempBasalRepository>(),
        Mock.Of<ICarbIntakeRepository>(),
        Mock.Of<IBGCheckRepository>(),
        Mock.Of<INoteRepository>(),
        Mock.Of<IDeviceEventRepository>(),
        Mock.Of<IBolusCalculationRepository>(),
        Mock.Of<IStateSpanService>(),
        ProfileActivationTestDeps.Repository(context),
        ProfileActivationTestDeps.Writer(context),
        Mock.Of<ITreatmentFoodService>(),
        Mock.Of<IDeviceService>(),
        Mock.Of<IPatientDeviceStamper>(),
        profileDecomposer ?? Mock.Of<IProfileDecomposer>(),
        Mock.Of<IActiveProfileResolver>(),
        Mock.Of<IPatientInsulinRepository>(),
        TempBasalTestDeps.Unscheduled(),
        auditContext,
        logger ?? NullLogger<TreatmentDecomposer>.Instance);

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
            Timestamp = Inside,
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

        (await assertCtx.GetBlockingLegacyIdsAsync<BolusEntity>(["bolus-1"])).Held.Should().Contain("bolus-1");
        (await assertCtx.GetBlockingLegacyIdsAsync<CarbIntakeEntity>(["carb-1"])).Held.Should().Contain("carb-1");
        (await assertCtx.GetBlockingLegacyIdsAsync<BGCheckEntity>(["bgcheck-1"])).Held.Should().Contain("bgcheck-1");
        (await assertCtx.GetBlockingLegacyIdsAsync<NoteEntity>(["note-1"])).Held.Should().Contain("note-1");
        (await assertCtx.GetBlockingLegacyIdsAsync<DeviceEventEntity>(["devevent-1"])).Held.Should().Contain("devevent-1");
        (await assertCtx.GetBlockingLegacyIdsAsync<BolusCalculationEntity>(["boluscalc-1"])).Held.Should().Contain("boluscalc-1");
        (await assertCtx.GetBlockingLegacyIdsAsync<TempBasalEntity>(["tempbasal-1"])).Held.Should().Contain("tempbasal-1");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("token=x")]
    [InlineData("count=5&token=x")]
    [InlineData("{\"created_at\":")]
    public async Task BulkDelete_WithAFindThatHasNoConditions_DeletesNothing(string? find)
    {
        SeedOneOfEachType();

        await using (var ctx = NewContext())
            (await CreateDecomposer(ctx, _userAuditContext).BulkDeleteAsync(find, WriteOrigin.Live)).Should().Be(0);

        await using var assertCtx = NewContext();
        (await assertCtx.Boluses.IgnoreQueryFilters().Where(b => b.DeletedAt != null).CountAsync()).Should().Be(0);
        (await assertCtx.Notes.IgnoreQueryFilters().Where(n => n.DeletedAt != null).CountAsync()).Should().Be(0);
        (await assertCtx.CarbIntakes.IgnoreQueryFilters().Where(c => c.DeletedAt != null).CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("find[]=")]
    [InlineData("{\"$and\":[]}")]
    [InlineData("{\"created_at\":{}}")]
    [InlineData("{}")]
    public async Task BulkDelete_WhenTheFindHasNoConditions_RefusesAtTheEmptyGuard(string find)
    {
        var logger = new Mock<Microsoft.Extensions.Logging.ILogger<TreatmentDecomposer>>();

        await using var ctx = NewContext();
        (await CreateDecomposer(ctx, _userAuditContext, logger: logger.Object).BulkDeleteAsync(find, WriteOrigin.Live)).Should().Be(0);

        logger.Invocations
            .Where(i => i.Method.Name == nameof(Microsoft.Extensions.Logging.ILogger.Log))
            .Select(i => i.Arguments[2].ToString())
            .Should().Contain(m => m!.Contains("has no conditions"));
    }

    [Fact]
    public async Task BulkDelete_NeverLogsTheRawFind()
    {
        var logger = new Mock<Microsoft.Extensions.Logging.ILogger<TreatmentDecomposer>>();
        SeedOneOfEachType();

        foreach (var find in new[] { Find + "&token=secret-value", "token=secret-value", "find[eventType]=Note&token=secret-value" })
        {
            await using var ctx = NewContext();
            await CreateDecomposer(ctx, _userAuditContext, logger: logger.Object).BulkDeleteAsync(find, WriteOrigin.Live);
        }

        logger.Invocations
            .Where(i => i.Method.Name == nameof(Microsoft.Extensions.Logging.ILogger.Log))
            .Select(i => i.Arguments[2].ToString())
            .Should().NotBeEmpty()
            .And.NotContain(m => m!.Contains("secret-value"));
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

        (await assertCtx.GetBlockingLegacyIdsAsync<BolusEntity>(["bolus-1"])).Held.Should().BeEmpty();
        (await assertCtx.GetBlockingLegacyIdsAsync<TempBasalEntity>(["tempbasal-1"])).Held.Should().BeEmpty();
        (await assertCtx.MutationAuditLog.AnyAsync()).Should().BeFalse();
    }

    private void SeedStateSpan(string originalId, string category, string source, string? metadataJson, DateTime start)
    {
        using var db = NewContext();
        db.StateSpans.Add(new StateSpanEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            OriginalId = originalId,
            Category = category,
            State = "Active",
            StartTimestamp = start,
            Source = source,
            MetadataJson = metadataJson,
        });
        db.SaveChanges();
    }

    private void SeedActivation(
        string? legacyId, string? dataSource, DateTime start, string? syncIdentifier = null,
        string phase = nameof(Nocturne.Core.Models.V4.ProfileActivationPhase.Requested))
    {
        using var db = NewContext();
        db.ProfileActivations.Add(new ProfileActivationEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            LegacyId = legacyId,
            SyncIdentifier = syncIdentifier,
            DataSource = dataSource,
            Timestamp = start,
            Phase = phase,
            Origin = nameof(Nocturne.Core.Models.V4.ProfileActivationOrigin.Uploader),
            BaseProfileName = "Weekend",
        });
        db.SaveChanges();
    }

    private static Treatment ProfileSwitch(string id, DateTime at, string profile, double? minutes = null) => new()
    {
        Id = id,
        EventType = "Profile Switch",
        Mills = new DateTimeOffset(at).ToUnixTimeMilliseconds(),
        EnteredBy = "careportal",
        Profile = profile,
        Duration = minutes,
    };

    private async Task<ProfileActivationEntity> ActivationAsync(string legacyId)
    {
        await using var db = NewContext();
        return await db.ProfileActivations.AsNoTracking().IgnoreQueryFilters().SingleAsync(a => a.LegacyId == legacyId);
    }

    [Fact]
    public async Task DeletingTheNewestPermanentSwitchResumesThePreviousOne()
    {
        await using (var ctx = NewContext())
        {
            var decomposer = CreateDecomposer(ctx, _userAuditContext);
            await decomposer.DecomposeAsync(ProfileSwitch("weekday", Inside, "Weekday"), WriteOrigin.Live);
            await decomposer.DecomposeAsync(ProfileSwitch("weekend", Inside.AddHours(1), "Weekend"), WriteOrigin.Live);
        }
        (await ActivationAsync("weekday")).EndTimestamp.Should().Be(Inside.AddHours(1));

        await using (var ctx = NewContext())
            await CreateDecomposer(ctx, _userAuditContext).DeleteByLegacyIdAsync("weekend", WriteOrigin.Live);

        (await ActivationAsync("weekday")).EndTimestamp.Should().BeNull("nothing supersedes it any more");
    }

    [Fact]
    public async Task DeletingASwitchThatCutATemporaryOneShortRestoresItsLength()
    {
        await using (var ctx = NewContext())
        {
            var decomposer = CreateDecomposer(ctx, _userAuditContext);
            await decomposer.DecomposeAsync(ProfileSwitch("sick", Inside, "Sick", minutes: 120), WriteOrigin.Live);
            await decomposer.DecomposeAsync(ProfileSwitch("exercise", Inside.AddHours(1), "Exercise", minutes: 30), WriteOrigin.Live);
        }
        (await ActivationAsync("sick")).EndTimestamp.Should().Be(Inside.AddHours(1));

        await using (var ctx = NewContext())
            await CreateDecomposer(ctx, _userAuditContext).DeleteByLegacyIdAsync("exercise", WriteOrigin.Live);

        (await ActivationAsync("sick")).EndTimestamp.Should().Be(Inside.AddHours(2));
    }

    [Fact]
    public async Task BulkDeletingTheNewestSwitchResumesThePreviousOne()
    {
        await using (var ctx = NewContext())
        {
            var decomposer = CreateDecomposer(ctx, _userAuditContext);
            await decomposer.DecomposeAsync(ProfileSwitch("weekday", Inside.AddDays(-1), "Weekday"), WriteOrigin.Live);
            await decomposer.DecomposeAsync(ProfileSwitch("weekend", Inside, "Weekend"), WriteOrigin.Live);
        }

        await BulkDeleteAsync(_userAuditContext);

        (await ActivationAsync("weekday")).EndTimestamp.Should().BeNull();
    }

    [Fact]
    public async Task BulkDelete_UserContext_TombstonesTheServedStateSpansInTheWindowOnly()
    {
        SeedOneOfEachType();
        SeedStateSpan("override-1", "Override", "loop", """{"collection":"treatments","utcOffset":0}""", Inside);
        SeedStateSpan("tt-1", "TemporaryTarget", "aaps", """{"utcOffset":0}""", Inside);
        SeedStateSpan("old-profile-span", "Profile", "aaps", """{"profileName":"Weekend","utcOffset":0}""", Inside);
        SeedStateSpan("devicestatus-override", "Override", "loop", """{"collection":"devicestatus"}""", Inside);
        SeedActivation("ps-1", null, Inside);
        SeedActivation("ps-outside", null, Outside);
        SeedActivation(null, "glooko-connector", Inside, syncIdentifier: "glooko_profile_1",
            phase: nameof(Nocturne.Core.Models.V4.ProfileActivationPhase.Effective));

        var count = await BulkDeleteAsync(_userAuditContext);

        await using var assertCtx = NewContext();
        var deleted = await assertCtx.StateSpans.IgnoreQueryFilters()
            .Where(s => s.DeletedAt != null)
            .Select(s => new { s.OriginalId, ByUser = EF.Property<bool>(s, "DeletedByUser") })
            .ToListAsync();
        deleted.Select(s => s.OriginalId).Should().BeEquivalentTo(new[] { "override-1", "tt-1" });
        deleted.Should().OnlyContain(s => s.ByUser);
        var deletedActivations = await assertCtx.ProfileActivations.IgnoreQueryFilters()
            .Where(a => a.DeletedAt != null)
            .Select(a => new { a.LegacyId, ByUser = EF.Property<bool>(a, "DeletedByUser") })
            .ToListAsync();
        deletedActivations.Should().ContainSingle().Which.Should().Be(new { LegacyId = (string?)"ps-1", ByUser = true });
        count.Should().Be(10);
        (await assertCtx.MutationAuditLog.Where(a => a.Action == "bulk_delete" && a.EntityType == "StateSpan")
                .Select(a => a.ChangesJson).SingleAsync())
            .Should().Contain("\"count\":2");
    }

    [Fact]
    public async Task BulkDelete_ProfileSwitchesInTheWindow_DeleteTheProfileRowsTheyDecomposed()
    {
        SeedActivation("ps-1", null, Inside);
        SeedActivation("ps-outside", null, Outside);
        SeedStateSpan("override-1", "Override", "loop", """{"collection":"treatments","utcOffset":0}""", Inside);
        var profiles = new Mock<IProfileDecomposer>();

        await using var ctx = NewContext();
        await CreateDecomposer(ctx, _userAuditContext, profiles.Object).BulkDeleteAsync(Find, WriteOrigin.Live);

        profiles.Verify(p => p.DeleteByLegacyIdAsync("ps-1", WriteOrigin.Live, It.IsAny<CancellationToken>()), Times.Once);
        profiles.VerifyNoOtherCalls();
    }

    private async Task<int> DeleteByLegacyIdAsync(IAuditContext auditContext)
    {
        await using var ctx = NewContext();
        return await CreateDecomposer(ctx, auditContext)
            .DeleteByLegacyIdAsync(LegacyTreatmentId, WriteOrigin.Live);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteByLegacyId_ProfileSwitch_DeletesTheProfileRowsItDecomposed(bool spanAlreadyDeleted)
    {
        SeedActivation(LegacyTreatmentId, null, Inside);
        if (spanAlreadyDeleted)
        {
            await using var db = NewContext();
            (await db.ProfileActivations.SingleAsync()).DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        var profiles = new Mock<IProfileDecomposer>();

        await using var ctx = NewContext();
        await CreateDecomposer(ctx, _userAuditContext, profiles.Object)
            .DeleteByLegacyIdAsync(LegacyTreatmentId, WriteOrigin.Live);

        profiles.Verify(p => p.DeleteByLegacyIdAsync(LegacyTreatmentId, WriteOrigin.Live, It.IsAny<CancellationToken>()), Times.Once);
        profiles.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DeleteByLegacyId_TreatmentWithoutAProfileSpan_LeavesProfileRowsAlone()
    {
        SeedStateSpan(LegacyTreatmentId, "Override", "loop", """{"collection":"treatments"}""", Inside);
        SeedStateSpan("other-profile-switch", "Profile", "aaps", """{"profileName":"Weekend"}""", Inside);
        var profiles = new Mock<IProfileDecomposer>();

        await using var ctx = NewContext();
        await CreateDecomposer(ctx, _userAuditContext, profiles.Object)
            .DeleteByLegacyIdAsync(LegacyTreatmentId, WriteOrigin.Live);

        profiles.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DeleteFromSource_RemovesTheSourcesTreatmentSpansAndProfileRowsOnly()
    {
        const string storeName = "Weekend@@@@@1672567200000";
        SeedActivation("ps-1", "aaps", Inside);
        SeedStateSpan("override-1", "Override", "aaps", """{"collection":"treatments","utcOffset":0}""", Inside);
        SeedStateSpan("tt-1", "TemporaryTarget", "aaps", """{"utcOffset":0}""", Inside);
        SeedActivation("ps-2", "loop", Inside);
        SeedActivation("ps-unlisted", "aaps", Inside);
        SeedStateSpan("ds-override", "Override", "aaps", """{"reason":"devicestatus"}""", Inside);

        await using var ctx = NewContext();
        var factory = new TestNocturneDbContextFactory(ctx);
        var profiles = new ProfileDecomposer(
            new TherapySettingsRepository(factory, _userAuditContext, NullLogger<TherapySettingsRepository>.Instance),
            new BasalScheduleRepository(factory, _userAuditContext, NullLogger<BasalScheduleRepository>.Instance),
            new CarbRatioScheduleRepository(factory, _userAuditContext, NullLogger<CarbRatioScheduleRepository>.Instance),
            new SensitivityScheduleRepository(factory, _userAuditContext, NullLogger<SensitivityScheduleRepository>.Instance),
            new TargetRangeScheduleRepository(factory, _userAuditContext, NullLogger<TargetRangeScheduleRepository>.Instance),
            TempBasalTestDeps.NoReresolution(),
            NullLogger<ProfileDecomposer>.Instance);
        await profiles.DecomposeAsync(ProfileDocument("ps-1", storeName), WriteOrigin.Live);
        await profiles.DecomposeAsync(ProfileDocument("ps-2", storeName), WriteOrigin.Live);

        var deleted = await CreateDecomposer(ctx, SystemAuditContext.ForService("connector:aaps"), profiles)
            .DeleteFromSourceAsync("aaps", new HashSet<string> { "ps-1", "override-1", "tt-1", "ds-override" });

        deleted.Should().Be(3);
        await using var db = NewContext();
        var spans = await db.StateSpans.IgnoreQueryFilters().ToListAsync();
        spans.Where(s => s.DeletedAt != null).Select(s => s.OriginalId)
            .Should().BeEquivalentTo("override-1", "tt-1");
        (await db.ProfileActivations.IgnoreQueryFilters().Where(a => a.DeletedAt != null).Select(a => a.LegacyId).ToListAsync())
            .Should().Equal("ps-1");
        var snapshots = (await db.TherapySettings.IgnoreQueryFilters().ToListAsync()).Select(r => (r.LegacyId, r.DeletedAt))
            .Concat((await db.BasalSchedules.IgnoreQueryFilters().ToListAsync()).Select(r => (r.LegacyId, r.DeletedAt)))
            .Concat((await db.CarbRatioSchedules.IgnoreQueryFilters().ToListAsync()).Select(r => (r.LegacyId, r.DeletedAt)))
            .Concat((await db.SensitivitySchedules.IgnoreQueryFilters().ToListAsync()).Select(r => (r.LegacyId, r.DeletedAt)))
            .Concat((await db.TargetRangeSchedules.IgnoreQueryFilters().ToListAsync()).Select(r => (r.LegacyId, r.DeletedAt)))
            .ToList();
        snapshots.Where(r => r.LegacyId == $"ps-1:{storeName}").Should().HaveCount(5)
            .And.AllSatisfy(r => r.DeletedAt.Should().NotBeNull());
        snapshots.Where(r => r.LegacyId == $"ps-2:{storeName}").Should().HaveCount(5)
            .And.AllSatisfy(r => r.DeletedAt.Should().BeNull());
    }

    [Fact]
    public async Task ReadServiceDelete_ProfileSwitchWithProfileJson_TombstonesItsSnapshotRowsAndLeavesAnOrdinaryProfile()
    {
        const string switchId = "switch-1";
        const string storeName = "Weekend@@@@@1672567200000";
        SeedActivation(switchId, null, Inside);

        await using var ctx = NewContext();
        var factory = new TestNocturneDbContextFactory(ctx);
        var profiles = new ProfileDecomposer(
            new TherapySettingsRepository(factory, _userAuditContext, NullLogger<TherapySettingsRepository>.Instance),
            new BasalScheduleRepository(factory, _userAuditContext, NullLogger<BasalScheduleRepository>.Instance),
            new CarbRatioScheduleRepository(factory, _userAuditContext, NullLogger<CarbRatioScheduleRepository>.Instance),
            new SensitivityScheduleRepository(factory, _userAuditContext, NullLogger<SensitivityScheduleRepository>.Instance),
            new TargetRangeScheduleRepository(factory, _userAuditContext, NullLogger<TargetRangeScheduleRepository>.Instance),
            TempBasalTestDeps.NoReresolution(),
            NullLogger<ProfileDecomposer>.Instance);
        await profiles.DecomposeAsync(ProfileDocument(switchId, storeName), WriteOrigin.Live);
        await profiles.DecomposeAsync(ProfileDocument("document-1", "Default"), WriteOrigin.Live);
        var decomposer = CreateDecomposer(ctx, _userAuditContext, profiles);

        var activationId = (await ctx.ProfileActivations.SingleAsync()).Id.ToString();
        var projection = new Mock<IV4ToLegacyProjectionService>();
        projection.Setup(p => p.GetProjectedTreatmentsAsync(
                It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<int>(), false, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Treatment { Id = activationId, EventType = "Profile Switch" }]);
        var pipeline = new Mock<IDecompositionPipeline>();
        pipeline.Setup(p => p.DeleteByLegacyIdAsync<Treatment>(switchId, WriteOrigin.Live, It.IsAny<CancellationToken>()))
            .Returns((string id, WriteOrigin origin, CancellationToken token) => decomposer.DeleteByLegacyIdAsync(id, origin, token));
        var service = new TreatmentReadService(
            projection.Object, decomposer, pipeline.Object,
            Mock.Of<ITempBasalRepository>(), Mock.Of<IBolusRepository>(), Mock.Of<ICarbIntakeRepository>(),
            Mock.Of<IBGCheckRepository>(), Mock.Of<INoteRepository>(), Mock.Of<IDeviceEventRepository>(),
            Mock.Of<IBolusCalculationRepository>(), ProfileActivationTestDeps.Repository(ctx), Mock.Of<IStateSpanService>(),
            NullLogger<TreatmentReadService>.Instance);

        (await service.DeleteAsync(switchId)).Should().NotBeNull();

        await using var db = NewContext();
        var rows = (await db.TherapySettings.IgnoreQueryFilters().ToListAsync()).Select(r => (r.LegacyId, r.DeletedAt))
            .Concat((await db.BasalSchedules.IgnoreQueryFilters().ToListAsync()).Select(r => (r.LegacyId, r.DeletedAt)))
            .Concat((await db.CarbRatioSchedules.IgnoreQueryFilters().ToListAsync()).Select(r => (r.LegacyId, r.DeletedAt)))
            .Concat((await db.SensitivitySchedules.IgnoreQueryFilters().ToListAsync()).Select(r => (r.LegacyId, r.DeletedAt)))
            .Concat((await db.TargetRangeSchedules.IgnoreQueryFilters().ToListAsync()).Select(r => (r.LegacyId, r.DeletedAt)))
            .ToList();
        rows.Where(r => r.LegacyId == $"{switchId}:{storeName}").Should().HaveCount(5)
            .And.AllSatisfy(r => r.DeletedAt.Should().NotBeNull());
        rows.Where(r => r.LegacyId == "document-1:Default").Should().HaveCount(5)
            .And.AllSatisfy(r => r.DeletedAt.Should().BeNull());
    }

    private static Profile ProfileDocument(string id, string storeName) => new()
    {
        Id = id,
        Mills = 1672567200000,
        DefaultProfile = storeName,
        Store =
        {
            [storeName] = new ProfileData
            {
                Dia = 3.0,
                Timezone = "UTC",
                Basal = [new TimeValue { Time = "00:00", Value = 1.0 }],
                CarbRatio = [new TimeValue { Time = "00:00", Value = 10.0 }],
                Sens = [new TimeValue { Time = "00:00", Value = 50.0 }],
                TargetLow = [new TimeValue { Time = "00:00", Value = 80.0 }],
                TargetHigh = [new TimeValue { Time = "00:00", Value = 120.0 }],
            },
        },
    };

    [Fact]
    public async Task DeleteByLegacyId_UserContext_BlocksConnectorResyncFromRecreatingEveryType()
    {
        SeedOneOfEachType(_ => LegacyTreatmentId);

        (await DeleteByLegacyIdAsync(_userAuditContext)).Should().Be(7);

        await using var assertCtx = NewContext();

        (await assertCtx.GetBlockingLegacyIdsAsync<BolusEntity>([LegacyTreatmentId])).Held.Should().Contain(LegacyTreatmentId);
        (await assertCtx.GetBlockingLegacyIdsAsync<CarbIntakeEntity>([LegacyTreatmentId])).Held.Should().Contain(LegacyTreatmentId);
        (await assertCtx.GetBlockingLegacyIdsAsync<BGCheckEntity>([LegacyTreatmentId])).Held.Should().Contain(LegacyTreatmentId);
        (await assertCtx.GetBlockingLegacyIdsAsync<NoteEntity>([LegacyTreatmentId])).Held.Should().Contain(LegacyTreatmentId);
        (await assertCtx.GetBlockingLegacyIdsAsync<DeviceEventEntity>([LegacyTreatmentId])).Held.Should().Contain(LegacyTreatmentId);
        (await assertCtx.GetBlockingLegacyIdsAsync<BolusCalculationEntity>([LegacyTreatmentId])).Held.Should().Contain(LegacyTreatmentId);
        (await assertCtx.GetBlockingLegacyIdsAsync<TempBasalEntity>([LegacyTreatmentId])).Held.Should().Contain(LegacyTreatmentId);
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

        (await assertCtx.GetBlockingLegacyIdsAsync<BolusEntity>([LegacyTreatmentId])).Held.Should().BeEmpty();
        (await assertCtx.GetBlockingLegacyIdsAsync<TempBasalEntity>([LegacyTreatmentId])).Held.Should().BeEmpty();
        (await assertCtx.MutationAuditLog.AnyAsync()).Should().BeFalse();
    }
}
