using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Services.SetupHub;
using Nocturne.API.Services.SetupHub.Items;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.SetupHub;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.SetupHub;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.SetupHub;

[Trait("Category", "Unit")]
public class TherapySetupServiceTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly SqliteTestDatabase _database;
    private readonly NocturneDbContext _db;
    private readonly TestTenantDbContextFactory _factory;
    private readonly IAuditContext _audit = new SystemAuditContext();

    public TherapySetupServiceTests()
    {
        _database = TestDbContextFactory.CreateSqliteWithTenant(TenantId);
        _db = _database.CreateContext();
        _factory = new TestTenantDbContextFactory(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private TherapySettingsRepository SettingsRepo => new(_factory, _audit, NullLogger<TherapySettingsRepository>.Instance);

    internal TherapySetupService Service()
    {
        var patients = new Mock<IPatientRecordRepository>();
        patients.Setup(p => p.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PatientRecord { Timezone = "Australia/Melbourne" });
        return new TherapySetupService(
            _db,
            SettingsRepo,
            new BasalScheduleRepository(_factory, _audit, NullLogger<BasalScheduleRepository>.Instance),
            new CarbRatioScheduleRepository(_factory, _audit, NullLogger<CarbRatioScheduleRepository>.Instance),
            new SensitivityScheduleRepository(_factory, _audit, NullLogger<SensitivityScheduleRepository>.Instance),
            new TargetRangeScheduleRepository(_factory, _audit, NullLogger<TargetRangeScheduleRepository>.Instance),
            patients.Object);
    }

    private SetupHubService Hub() => new(_db, [new TherapyItem(_db)]);

    private ProfileDecomposer Decomposer() => new(
        SettingsRepo,
        new BasalScheduleRepository(_factory, _audit, NullLogger<BasalScheduleRepository>.Instance),
        new CarbRatioScheduleRepository(_factory, _audit, NullLogger<CarbRatioScheduleRepository>.Instance),
        new SensitivityScheduleRepository(_factory, _audit, NullLogger<SensitivityScheduleRepository>.Instance),
        new TargetRangeScheduleRepository(_factory, _audit, NullLogger<TargetRangeScheduleRepository>.Instance),
        NullLogger<ProfileDecomposer>.Instance);

    private static readonly DateTime ProfileMadeAt = new(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc);

    private static Profile UploadedProfile(string documentId, string enteredBy = "Loop", string units = "mg/dL") => new()
    {
        Id = documentId,
        DefaultProfile = "Default",
        Units = units,
        EnteredBy = enteredBy,
        Mills = new DateTimeOffset(ProfileMadeAt).ToUnixTimeMilliseconds(),
        Store = new Dictionary<string, ProfileData>
        {
            ["Default"] = new()
            {
                Units = units,
                Basal = [new TimeValue { Time = "00:00", Value = 0.8 }],
                CarbRatio = [new TimeValue { Time = "00:00", Value = 10 }],
                Sens = [new TimeValue { Time = "00:00", Value = units == "mmol" ? 3 : 50 }],
                TargetLow = [new TimeValue { Time = "00:00", Value = units == "mmol" ? 5.5 : 100 }],
                TargetHigh = [new TimeValue { Time = "00:00", Value = units == "mmol" ? 7 : 120 }],
            },
        },
    };

    private async Task MigrationRunning(DateTime startedAt, DateTime? completedAt = null)
    {
        var source = new MigrationSourceEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, SourceIdentifier = $"https://ns-{Guid.NewGuid()}.example",
        };
        _db.MigrationSources.Add(source);
        _db.MigrationRuns.Add(new MigrationRunEntity
        {
            Id = Guid.CreateVersion7(), SourceId = source.Id, TenantId = TenantId,
            CreatedAt = startedAt, StartedAt = startedAt, CompletedAt = completedAt,
            State = completedAt is null ? "Running" : "Completed",
        });
        await _db.SaveChangesAsync();
    }

    private static List<TherapyEntryInput> One(double? value) => [new("00:00", value)];

    [Fact]
    public async Task ATenantWithNoProfile_HasNothingToReview()
    {
        var review = await Service().GetReviewAsync(CancellationToken.None);

        review.Source.Should().Be(TherapySource.None);
        review.Settings.Should().BeNull();
        review.Basal.Should().BeNull();
        review.WrongUnitRules.Should().BeEquivalentTo(TherapyUnitPlausibility.Rules);
    }

    [Fact]
    public async Task AProfileAnAppUploaded_IsSyncedFromThatApp_AndReadsFromTheV4Store()
    {
        await Decomposer().DecomposeAsync(UploadedProfile("5f0000000000000000000001", units: "mmol"), WriteOrigin.Live);

        var review = await Service().GetReviewAsync(CancellationToken.None);

        review.Source.Should().Be(TherapySource.Synced);
        review.SourceName.Should().Be("Loop");
        review.LastUpdated.Should().Be(ProfileMadeAt);
        review.Basal!.Entries.Single().Value.Should().Be(0.8);
        review.Sensitivity!.Entries.Single().Value.Should().Be(54, "3 mmol/L per unit is stored as mg/dL");
        review.TargetRange!.Entries.Single().Low.Should().Be(99);
        review.TargetRange.Entries.Single().High.Should().Be(126);
        review.Confirmed.Should().BeFalse();
    }

    [Fact]
    public async Task AProfileWrittenWhileANightscoutImportRan_IsImported()
    {
        await MigrationRunning(DateTime.UtcNow.AddMinutes(-5));
        await Decomposer().DecomposeAsync(UploadedProfile("5f0000000000000000000001"), WriteOrigin.Backfill);

        var review = await Service().GetReviewAsync(CancellationToken.None);

        review.Source.Should().Be(TherapySource.Imported);
        review.SourceName.Should().BeNull();
        review.LastUpdated.Should().Be(ProfileMadeAt);
    }

    [Fact]
    public async Task AProfileAnAppSentAfterTheImportFinished_IsSynced()
    {
        await MigrationRunning(DateTime.UtcNow.AddHours(-2), DateTime.UtcNow.AddHours(-1));
        await Decomposer().DecomposeAsync(UploadedProfile("5f0000000000000000000002", enteredBy: "Trio"), WriteOrigin.Live);

        var review = await Service().GetReviewAsync(CancellationToken.None);

        review.Source.Should().Be(TherapySource.Synced);
        review.SourceName.Should().Be("Trio");
    }

    [Fact]
    public async Task AnExternallyManagedProfile_IsSynced_EvenDuringAnImport()
    {
        await MigrationRunning(DateTime.UtcNow.AddMinutes(-5));
        var profile = UploadedProfile("5f0000000000000000000003", enteredBy: "Glooko");
        profile.IsExternallyManaged = true;
        await Decomposer().DecomposeAsync(profile, WriteOrigin.Live);

        (await Service().GetReviewAsync(CancellationToken.None)).Source.Should().Be(TherapySource.Synced);
    }

    [Fact]
    public async Task ASyncedProfile_LeavesTheItemOpen_UntilTheOwnerConfirmsIt()
    {
        await Decomposer().DecomposeAsync(UploadedProfile("5f0000000000000000000001"), WriteOrigin.Live);

        (await Hub().GetAsync(CancellationToken.None)).Items.Single().State.Should().Be(SetupHubItemState.Open,
            "a profile arriving is not the owner having checked it");

        var confirmed = await Service().ConfirmAsync(CancellationToken.None);

        confirmed.Confirmed.Should().BeTrue();
        (await Service().GetReviewAsync(CancellationToken.None)).Confirmed.Should().BeTrue();
        (await Hub().GetAsync(CancellationToken.None)).Items.Single().State.Should().Be(SetupHubItemState.Done);
    }

    [Fact]
    public async Task ConfirmingTwice_RecordsOneConfirmation()
    {
        await Decomposer().DecomposeAsync(UploadedProfile("5f0000000000000000000001"), WriteOrigin.Live);

        await Service().ConfirmAsync(CancellationToken.None);
        await Service().ConfirmAsync(CancellationToken.None);

        (await _db.Settings.CountAsync(s => s.Key == TherapySetupService.ConfirmedSettingsKey)).Should().Be(1);
    }

    [Fact]
    public async Task Confirming_WithNothingToReview_IsRefused()
    {
        var confirm = () => Service().ConfirmAsync(CancellationToken.None);

        await confirm.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task EnteringInMmol_StoresMgdl_LabelledMgdl_AsTheDefaultProfile_AndFinishesTheItem()
    {
        var review = await Service().EnterAsync(
            "mmol", One(0.8), One(10), One(3), [new("00:00", 5.5, 7)], CancellationToken.None);

        review.Source.Should().Be(TherapySource.Entered);
        review.Settings!.Units.Should().Be("mg/dL");
        review.Settings.IsDefault.Should().BeTrue();
        review.Settings.Timezone.Should().Be("Australia/Melbourne");
        review.Basal!.Entries.Single().Value.Should().Be(0.8);
        review.CarbRatio!.Entries.Single().Value.Should().Be(10);
        review.Sensitivity!.Entries.Single().Value.Should().Be(54);
        review.TargetRange!.Entries.Single().Low.Should().Be(99);
        review.TargetRange.Entries.Single().High.Should().Be(126);

        (await Hub().GetAsync(CancellationToken.None)).Items.Single().State.Should().Be(SetupHubItemState.Done);
    }

    [Fact]
    public async Task EnteringInMgdl_StoresTheValuesAsTyped_InTimeOrder()
    {
        var review = await Service().EnterAsync(
            "mg/dl", [], [],
            [new("06:00", 45), new("00:00", 50)],
            [], CancellationToken.None);

        review.Sensitivity!.Entries.Select(e => (e.Time, e.Value)).Should().Equal(("00:00", 50d), ("06:00", 45d));
        review.Basal.Should().BeNull("a schedule left blank is not written");
        review.TargetRange.Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(IncompleteEntries))]
    public async Task AnIncompleteEntry_IsRefused_AndWritesNothing(
        List<TherapyEntryInput> basal, List<TargetEntryInput> target)
    {
        var enter = () => Service().EnterAsync("mg/dl", basal, [], [], target, CancellationToken.None);

        await enter.Should().ThrowAsync<ArgumentException>();
        (await _db.TherapySettings.AnyAsync()).Should().BeFalse();
    }

    public static TheoryData<List<TherapyEntryInput>, List<TargetEntryInput>> IncompleteEntries => new()
    {
        { [], [] },
        { [new("00:00", null), new("00:00", null)], [] },
        { [new("00:00", 0.8), new("06:00", null)], [] },
        { [new("06:00", 0.8)], [] },
        { [new("00:00", 0.8), new("00:00", 0.9)], [] },
        { [new("00:00", 0)], [] },
        { [], [new("00:00", 100, null)] },
        { [], [new("00:00", 140, 100)] },
    };

    [Fact]
    public async Task Entering_OverAnExistingProfile_IsRefused()
    {
        await Decomposer().DecomposeAsync(UploadedProfile("5f0000000000000000000001"), WriteOrigin.Live);

        var enter = () => Service().EnterAsync("mg/dl", One(0.8), [], [], [], CancellationToken.None);

        await enter.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task NotForMe_ResolvesTheItemWithoutAProfile()
    {
        await Hub().GetAsync(CancellationToken.None);

        var hub = await Hub().SetStateAsync(SetupHubItemKey.Therapy, SetupHubItemState.NotForMe, CancellationToken.None);

        hub.Items.Single().State.Should().Be(SetupHubItemState.NotForMe);
        hub.ResolvedCount.Should().Be(1);
    }

    [Theory]
    [InlineData(TherapyGlucoseField.Sensitivity, "mmol", 50, true)]
    [InlineData(TherapyGlucoseField.Sensitivity, "mmol", 3, false)]
    [InlineData(TherapyGlucoseField.Sensitivity, "mmol", 22, false)]
    [InlineData(TherapyGlucoseField.Sensitivity, "mg/dl", 2, true)]
    [InlineData(TherapyGlucoseField.Sensitivity, "mg/dl", 50, false)]
    [InlineData(TherapyGlucoseField.Sensitivity, "mg/dl", 400, false)]
    [InlineData(TherapyGlucoseField.Target, "mmol", 100, true)]
    [InlineData(TherapyGlucoseField.Target, "mmol", 5.5, false)]
    [InlineData(TherapyGlucoseField.Target, "mg/dl", 5.5, true)]
    [InlineData(TherapyGlucoseField.Target, "mg/dl", 100, false)]
    public void TheUnitCheck_FlagsValuesThatReadAsTheOtherUnit(
        TherapyGlucoseField field, string units, double value, bool flagged)
    {
        TherapyUnitPlausibility.LooksLikeTheOtherUnit(field, units, value).Should().Be(flagged);
    }

    private SetupTherapyController Controller(params string[] grantedScopes)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string>(grantedScopes);
        return new SetupTherapyController(Service())
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    [Fact]
    public async Task TheController_RefusesAMemberWhoIsNotAnOwner()
    {
        var controller = Controller(Scope.TherapyReadWrite);
        var request = new EnterTherapySettingsRequest { GlucoseUnits = "mg/dl", Basal = One(0.8) };

        (await controller.GetTherapyReview(CancellationToken.None)).Result.Should().BeOfType<ForbidResult>();
        (await controller.ConfirmTherapySettings(CancellationToken.None)).Result.Should().BeOfType<ForbidResult>();
        (await controller.EnterTherapySettings(request, CancellationToken.None)).Result.Should().BeOfType<ForbidResult>();
        (await _db.TherapySettings.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task TheController_EntersOnce_ThenRefusesToEnterOrConfirmOverIt()
    {
        var controller = Controller(Scope.FullAccess);
        var request = new EnterTherapySettingsRequest { GlucoseUnits = "mg/dl", Basal = One(0.8) };

        (await controller.EnterTherapySettings(new EnterTherapySettingsRequest { GlucoseUnits = "mg/dl" }, CancellationToken.None))
            .Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
        (await controller.EnterTherapySettings(request, CancellationToken.None))
            .Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<TherapyReview>()
            .Which.Source.Should().Be(TherapySource.Entered);
        (await controller.EnterTherapySettings(request, CancellationToken.None))
            .Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(409);
        (await controller.ConfirmTherapySettings(CancellationToken.None))
            .Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(409, "an entered profile needs no confirming");
    }
}
