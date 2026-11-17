using FluentAssertions;
using Moq;
using Nocturne.API.Services.Profiles;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Profiles;

public class ProfileProjectionServiceTests
{
    private readonly Mock<ITherapySettingsRepository> _therapyRepo = new();
    private readonly Mock<IBasalScheduleRepository> _basalRepo = new();
    private readonly Mock<ICarbRatioScheduleRepository> _carbRatioRepo = new();
    private readonly Mock<ISensitivityScheduleRepository> _sensitivityRepo = new();
    private readonly Mock<ITargetRangeScheduleRepository> _targetRangeRepo = new();
    private readonly List<BasalSchedule> _basals = [];
    private readonly List<CarbRatioSchedule> _carbRatios = [];
    private readonly List<SensitivitySchedule> _sensitivities = [];
    private readonly List<TargetRangeSchedule> _targetRanges = [];
    private readonly ProfileProjectionService _sut;

    public ProfileProjectionServiceTests()
    {
        _basalRepo.Serve(_basals);
        _carbRatioRepo.Serve(_carbRatios);
        _sensitivityRepo.Serve(_sensitivities);
        _targetRangeRepo.Serve(_targetRanges);
        _therapyRepo.Setup(r => r.GetStoreIndexAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        _sut = new ProfileProjectionService(
            _therapyRepo.Object,
            _basalRepo.Object,
            _carbRatioRepo.Object,
            _sensitivityRepo.Object,
            _targetRangeRepo.Object);
    }

    #region GetCurrentProfileAsync

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task GetCurrentProfileAsync_ReturnsTheStoredApnsEnvironmentAndNothingWhenAbsent(bool? stored)
    {
        var correlationId = Guid.NewGuid();
        var settings = CreateTherapySettings(correlationId: correlationId);
        settings.IsAPNSProduction = stored;

        SetupGetLatest(settings);
        SetupCorrelationLookups(correlationId);

        var result = await _sut.GetCurrentProfileAsync();

        result!.IsAPNSProduction.Should().Be(stored);
        System.Text.Json.JsonSerializer.Serialize(result).Contains("isAPNSProduction")
            .Should().Be(stored.HasValue, "an upload that never said stays without the field on a legacy read");
    }

    [Fact]
    public async Task GetCurrentProfileAsync_NoSettings_ReturnsNull()
    {
        _therapyRepo.Setup(r => r.GetDocumentsAsync(1, 0, true, default)).ReturnsAsync([]);

        var result = await _sut.GetCurrentProfileAsync();

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetCurrentProfileAsync_ReturnsLatestProfile()
    {
        var correlationId = Guid.NewGuid();
        var settings = CreateTherapySettings(correlationId: correlationId, profileName: "Default");
        var basal = CreateBasalSchedule(correlationId, "Default");
        var carbRatio = CreateCarbRatioSchedule(correlationId, "Default");
        var sensitivity = CreateSensitivitySchedule(correlationId, "Default");
        var targetRange = CreateTargetRangeSchedule(correlationId, "Default");

        SetupGetLatest(settings);
        SetupCorrelationLookups(correlationId, [basal], [carbRatio], [sensitivity], [targetRange]);

        var result = await _sut.GetCurrentProfileAsync();

        result.Should().NotBeNull();
        result!.DefaultProfile.Should().Be("Default");
        result.Mills.Should().Be(settings.Mills);
        result.Store.Should().ContainKey("Default");

        var data = result.Store["Default"];
        data.Basal.Should().HaveCount(1);
        data.CarbRatio.Should().HaveCount(1);
        data.Sens.Should().HaveCount(1);
        data.TargetLow.Should().HaveCount(1);
        data.TargetHigh.Should().HaveCount(1);
    }

    #endregion

    #region GetProfileByIdAsync

    [Fact]
    public async Task GetProfileByIdAsync_ByLegacyId_ReturnsProfile()
    {
        var correlationId = Guid.NewGuid();
        var settings = CreateTherapySettings(correlationId: correlationId, legacyId: "abc123:Default");

        _therapyRepo.Setup(r => r.GetByLegacyIdAsync("abc123", default))
            .ReturnsAsync(settings);
        SetupDocumentRows(settings);
        SetupCorrelationLookups(correlationId);

        var result = await _sut.GetProfileByIdAsync("abc123");

        result.Should().NotBeNull();
        result!.Id.Should().Be("abc123");
    }

    [Fact]
    public async Task GetProfileByIdAsync_ByGuid_ReturnsProfile()
    {
        var id = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var settings = CreateTherapySettings(id: id, correlationId: correlationId);

        _therapyRepo.Setup(r => r.GetByLegacyIdAsync(id.ToString(), default))
            .ReturnsAsync((TherapySettings?)null);
        _therapyRepo.Setup(r => r.GetByIdAsync(id, default))
            .ReturnsAsync(settings);
        SetupDocumentRows(settings);
        SetupCorrelationLookups(correlationId);

        var result = await _sut.GetProfileByIdAsync(id.ToString());

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task GetProfileByIdAsync_NotFound_ReturnsNull()
    {
        _therapyRepo.Setup(r => r.GetByLegacyIdAsync("missing", default))
            .ReturnsAsync((TherapySettings?)null);
        _therapyRepo.Setup(r => r.GetDocumentRowsAsync("missing", default))
            .ReturnsAsync([]);

        var result = await _sut.GetProfileByIdAsync("missing");

        result.Should().BeNull();
    }

    #endregion

    #region GetProfilesAsync

    [Fact]
    public async Task GetProfilesAsync_ReturnsPaginatedProfiles()
    {
        var corr1 = Guid.NewGuid();
        var corr2 = Guid.NewGuid();
        var settings1 = CreateTherapySettings(correlationId: corr1, profileName: "Default");
        var settings2 = CreateTherapySettings(correlationId: corr2, profileName: "Weekday");

        _therapyRepo.Setup(r => r.GetDocumentsAsync(5, 0, true, default))
            .ReturnsAsync([[settings1], [settings2]]);
        SetupCorrelationLookups(corr1);
        SetupCorrelationLookups(corr2);

        var result = (await _sut.GetProfilesAsync(count: 5, skip: 0)).ToList();

        result.Should().HaveCount(2);
        result[0].DefaultProfile.Should().Be("Default");
        result[1].DefaultProfile.Should().Be("Weekday");
    }

    [Fact]
    public async Task GetProfilesAsync_Ascending_PagesFromTheOldestProfile()
    {
        var corr = Guid.NewGuid();
        _therapyRepo.Setup(r => r.GetDocumentsAsync(1, 0, false, default))
            .ReturnsAsync([[CreateTherapySettings(correlationId: corr, profileName: "Oldest")]]);
        SetupCorrelationLookups(corr);

        var result = (await _sut.GetProfilesAsync(count: 1, skip: 0, descending: false)).ToList();

        result.Should().ContainSingle().Which.DefaultProfile.Should().Be("Oldest");
        _therapyRepo.Verify(r => r.GetDocumentsAsync(1, 0, true, default), Times.Never);
    }

    [Fact]
    public async Task GetProfilesAsync_EmptyRepo_ReturnsEmpty()
    {
        _therapyRepo.Setup(r => r.GetDocumentsAsync(10, 0, true, default)).ReturnsAsync([]);

        var result = await _sut.GetProfilesAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetProfilesAsync_DocumentWithSeveralStores_IsOneProfileCarryingEveryStore_NamedByItsDefault()
    {
        var correlationId = Guid.NewGuid();
        var day = CreateTherapySettings(correlationId: correlationId, profileName: "Day", legacyId: "doc1:Day");
        var night = CreateTherapySettings(correlationId: correlationId, profileName: "Night", legacyId: "doc1:Night");
        night.IsDefault = true;

        _therapyRepo.Setup(r => r.GetDocumentsAsync(10, 0, true, default)).ReturnsAsync([[day, night]]);
        SetupCorrelationLookups(
            correlationId,
            basals: [CreateBasalSchedule(correlationId, "Day"), CreateBasalSchedule(correlationId, "Night")]);

        var result = (await _sut.GetProfilesAsync()).ToList();

        var profile = result.Should().ContainSingle().Subject;
        profile.Id.Should().Be("doc1");
        profile.DefaultProfile.Should().Be("Night");
        profile.Store.Keys.Should().BeEquivalentTo("Day", "Night");
        profile.Store["Day"].Basal.Should().ContainSingle();
        profile.Store["Night"].Basal.Should().ContainSingle();
    }

    [Fact]
    public async Task GetProfilesAsync_DefaultStoreOlderThanItsSibling_StillNamesTheDocument()
    {
        var correlationId = Guid.NewGuid();
        var night = CreateTherapySettings(correlationId: correlationId, profileName: "Night", legacyId: "doc1:Night");
        night.IsDefault = true;
        var day = CreateTherapySettings(correlationId: correlationId, profileName: "Day", legacyId: "doc1:Day");
        day.Timestamp = night.Timestamp.AddDays(1);
        day.StartDate = "2025-06-02T12:00:00.000Z";

        _therapyRepo.Setup(r => r.GetDocumentsAsync(10, 0, true, default)).ReturnsAsync([[day, night]]);
        SetupCorrelationLookups(correlationId);

        var profile = (await _sut.GetProfilesAsync()).Should().ContainSingle().Subject;

        profile.DefaultProfile.Should().Be("Night");
        profile.Mills.Should().Be(night.Mills);
        profile.Store.Keys.Should().BeEquivalentTo("Day", "Night");
    }

    #endregion

    #region CountProfilesAsync

    [Fact]
    public async Task CountProfilesAsync_CountsDocuments()
    {
        _therapyRepo.Setup(r => r.CountDocumentsAsync(default)).ReturnsAsync(2);

        var result = await _sut.CountProfilesAsync();

        result.Should().Be(2);
    }

    #endregion

    #region Profile Assembly

    /// <summary>
    /// A row decomposed before <c>startDate</c> led may record one its versions do not start at; the
    /// document serves their start, so a client saving it back does not move them.
    /// </summary>
    [Fact]
    public async Task ServesTheVersionsStart_WhenTheRecordedStartDateDisagrees()
    {
        var correlationId = Guid.NewGuid();
        var settings = CreateTherapySettings(correlationId: correlationId, startDate: "2025-01-15T00:00:00.000Z");

        SetupGetLatest(settings);
        SetupCorrelationLookups(correlationId);

        var result = await _sut.GetCurrentProfileAsync();

        result!.StartDate.Should().Be("2025-06-01T12:00:00.000Z");
    }

    [Fact]
    public async Task AssemblesScalarFieldsFromTherapySettings()
    {
        var correlationId = Guid.NewGuid();
        var settings = CreateTherapySettings(
            correlationId: correlationId,
            profileName: "NightMode",
            timezone: "America/New_York",
            units: "mmol/L",
            dia: 4.5,
            carbsHr: 30,
            delay: 15,
            enteredBy: "Loop",
            isExternallyManaged: true,
            startDate: "2025-01-15T00:00:00.000Z");
        settings.Timestamp = new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc);

        SetupGetLatest(settings);
        SetupCorrelationLookups(correlationId);

        var result = await _sut.GetCurrentProfileAsync();

        result.Should().NotBeNull();
        result!.DefaultProfile.Should().Be("NightMode");
        result.Units.Should().Be("mmol/L");
        result.EnteredBy.Should().Be("Loop");
        result.IsExternallyManaged.Should().BeTrue();
        result.StartDate.Should().Be("2025-01-15T00:00:00.000Z");

        var data = result.Store["NightMode"];
        data.Dia.Should().Be(4.5);
        data.CarbsHr.Should().Be(30);
        data.Delay.Should().Be(15);
        data.Timezone.Should().Be("America/New_York");
        data.Units.Should().Be("mmol/L");
    }

    [Fact]
    public async Task TargetRange_SplitsIntoTargetLowAndTargetHigh()
    {
        var correlationId = Guid.NewGuid();
        var settings = CreateTherapySettings(correlationId: correlationId);
        var targetRange = new TargetRangeSchedule
        {
            Id = Guid.NewGuid(),
            CorrelationId = correlationId,
            ProfileName = "Default",
            Entries =
            [
                new TargetRangeEntry { Time = "00:00", Low = 80, High = 120, TimeAsSeconds = 0 },
                new TargetRangeEntry { Time = "06:00", Low = 90, High = 130, TimeAsSeconds = 21600 },
                new TargetRangeEntry { Time = "22:00", Low = 70, High = 110, TimeAsSeconds = 79200 },
            ]
        };

        SetupGetLatest(settings);
        SetupCorrelationLookups(correlationId, targetRanges: [targetRange]);

        var result = await _sut.GetCurrentProfileAsync();

        var data = result!.Store["Default"];

        data.TargetLow.Should().HaveCount(3);
        data.TargetLow[0].Time.Should().Be("00:00");
        data.TargetLow[0].Value.Should().Be(80);
        data.TargetLow[0].TimeAsSeconds.Should().Be(0);
        data.TargetLow[1].Value.Should().Be(90);
        data.TargetLow[2].Value.Should().Be(70);

        data.TargetHigh.Should().HaveCount(3);
        data.TargetHigh[0].Value.Should().Be(120);
        data.TargetHigh[1].Value.Should().Be(130);
        data.TargetHigh[2].Value.Should().Be(110);
    }

    [Fact]
    public async Task ScheduleEntries_MapToTimeValues()
    {
        var correlationId = Guid.NewGuid();
        var settings = CreateTherapySettings(correlationId: correlationId);
        var basal = new BasalSchedule
        {
            Id = Guid.NewGuid(),
            CorrelationId = correlationId,
            ProfileName = "Default",
            Entries =
            [
                new ScheduleEntry { Time = "00:00", Value = 0.8, TimeAsSeconds = 0 },
                new ScheduleEntry { Time = "08:00", Value = 1.2, TimeAsSeconds = 28800 },
            ]
        };

        SetupGetLatest(settings);
        SetupCorrelationLookups(correlationId, basals: [basal]);

        var result = await _sut.GetCurrentProfileAsync();

        var data = result!.Store["Default"];
        data.Basal.Should().HaveCount(2);
        data.Basal[0].Time.Should().Be("00:00");
        data.Basal[0].Value.Should().Be(0.8);
        data.Basal[0].TimeAsSeconds.Should().Be(0);
        data.Basal[1].Time.Should().Be("08:00");
        data.Basal[1].Value.Should().Be(1.2);
        data.Basal[1].TimeAsSeconds.Should().Be(28800);
    }

    [Fact]
    public async Task MultipleNamedProfiles_EachGetOwnStoreKey()
    {
        var corr1 = Guid.NewGuid();
        var corr2 = Guid.NewGuid();
        var settings1 = CreateTherapySettings(correlationId: corr1, profileName: "Default");
        var settings2 = CreateTherapySettings(correlationId: corr2, profileName: "Exercise");

        _therapyRepo.Setup(r => r.GetDocumentsAsync(10, 0, true, default))
            .ReturnsAsync([[settings1], [settings2]]);
        SetupCorrelationLookups(corr1);
        SetupCorrelationLookups(corr2);

        var result = (await _sut.GetProfilesAsync()).ToList();

        result[0].Store.Should().ContainKey("Default");
        result[0].Store.Should().NotContainKey("Exercise");
        result[1].Store.Should().ContainKey("Exercise");
        result[1].Store.Should().NotContainKey("Default");
    }

    [Fact]
    public async Task MissingSchedules_ProducesEmptyLists()
    {
        var correlationId = Guid.NewGuid();
        var settings = CreateTherapySettings(correlationId: correlationId);

        SetupGetLatest(settings);

        var result = await _sut.GetCurrentProfileAsync();

        var data = result!.Store["Default"];
        data.Basal.Should().BeEmpty();
        data.CarbRatio.Should().BeEmpty();
        data.Sens.Should().BeEmpty();
        data.TargetLow.Should().BeEmpty();
        data.TargetHigh.Should().BeEmpty();
    }

    [Fact]
    public async Task NoCorrelationId_FallsBackToProfileNameLookup()
    {
        var settings = CreateTherapySettings(correlationId: null, profileName: "Default");
        var basal = CreateBasalSchedule(null, "Default");

        SetupGetLatest(settings);
        _basals.Add(basal);

        var result = await _sut.GetCurrentProfileAsync();

        result.Should().NotBeNull();
        result!.Store["Default"].Basal.Should().HaveCount(1);
    }

    [Fact]
    public async Task NoCorrelationId_ServesTheNewestScheduleFiledUnderTheProfileName()
    {
        var settings = CreateTherapySettings(correlationId: null, profileName: "Default");
        var older = CreateBasalSchedule(null, "Default", value: 0.5);
        older.Timestamp = new DateTime(2025, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = CreateBasalSchedule(null, "Default", value: 0.8);
        newer.Timestamp = new DateTime(2025, 5, 2, 0, 0, 0, DateTimeKind.Utc);

        SetupGetLatest(settings);
        _basals.AddRange([older, newer]);

        var result = await _sut.GetCurrentProfileAsync();

        result!.Store["Default"].Basal.Should().ContainSingle().Which.Value.Should().Be(0.8);
    }

    /// <summary>
    /// A correlated row is served from its correlation group only; a newer row filed under the same
    /// store name belongs to another upload.
    /// </summary>
    [Fact]
    public async Task CorrelatedRow_IsNotServedTheNewestScheduleFiledUnderItsName()
    {
        var correlationId = Guid.NewGuid();
        var correlated = CreateTherapySettings(correlationId: correlationId, profileName: "Default");
        var uncorrelated = CreateTherapySettings(correlationId: null, profileName: "Default");
        var nameRow = CreateBasalSchedule(null, "Default", value: 2.5);
        nameRow.Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        _therapyRepo.Setup(r => r.GetDocumentsAsync(10, 0, true, default))
            .ReturnsAsync([[correlated], [uncorrelated]]);
        SetupCorrelationLookups(correlationId, basals: [CreateBasalSchedule(correlationId, "Default", value: 1.0)]);
        _basals.Add(nameRow);

        var result = (await _sut.GetProfilesAsync()).ToList();

        result[0].Store["Default"].Basal.Should().ContainSingle().Which.Value.Should().Be(1.0);
        result[1].Store["Default"].Basal.Should().ContainSingle().Which.Value.Should().Be(2.5);
    }

    /// <summary>
    /// A page reads each schedule kind with one query per rule, whatever its number of documents and
    /// stores. Every row here takes a different rule, and correlation groups span several stores.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 3)]
    public async Task GetProfilesAsync_ReadsEachScheduleKindWithAFixedNumberOfQueries(int documentCount, int storeCount)
    {
        var documents = new List<IReadOnlyList<TherapySettings>>();
        for (var d = 0; d < documentCount; d++)
        {
            var correlationId = Guid.NewGuid();
            var rows = new List<TherapySettings>();
            for (var s = 0; s < storeCount; s++)
            {
                var store = $"Store {s}";
                var value = d * 10 + s + 1;
                switch (s % 3)
                {
                    case 0:
                        rows.Add(CreateTherapySettings(correlationId: correlationId, profileName: store));
                        SetupCorrelationLookups(correlationId, basals: [CreateBasalSchedule(correlationId, store, value)]);
                        break;
                    case 1:
                        rows.Add(CreateTherapySettings(profileName: $"{store} of {d}"));
                        _basals.Add(CreateBasalSchedule(null, $"{store} of {d}", value));
                        break;
                    default:
                        var legacyId = $"profile{d}:{store}";
                        rows.Add(CreateTherapySettings(correlationId: correlationId, profileName: store, legacyId: legacyId));
                        SetupLegacyIdLookups(legacyId, store, value);
                        break;
                }
            }

            documents.Add(rows);
        }

        _therapyRepo.Setup(r => r.GetDocumentsAsync(10, 0, true, default)).ReturnsAsync(documents);

        var result = (await _sut.GetProfilesAsync()).ToList();

        for (var d = 0; d < documentCount; d++)
        {
            for (var s = 0; s < storeCount; s++)
            {
                var store = s % 3 == 1 ? $"Store {s} of {d}" : $"Store {s}";
                result[d].Store[store].Basal.Should().ContainSingle()
                    .Which.Value.Should().Be(d * 10 + s + 1, $"document {d} store {s} reads its own schedule");
            }
        }

        _basalRepo.Verify(r => r.GetByCorrelationIdsAsync(It.IsAny<IEnumerable<Guid>>(), default), Times.Once);
        _basalRepo.Verify(r => r.GetNewestByProfileNamesAsync(It.IsAny<IEnumerable<string>>(), default), Times.Once);
        _basalRepo.Verify(r => r.GetByLegacyIdsAsync(It.IsAny<IEnumerable<string>>(), default), Times.AtMostOnce);
        _basalRepo.Verify(r => r.GetByCorrelationIdAsync(It.IsAny<Guid>(), default), Times.Never);
        _basalRepo.Verify(r => r.GetByProfileNameAsync(It.IsAny<string>(), default), Times.Never);
        _basalRepo.Verify(r => r.GetByLegacyIdAsync(It.IsAny<string>(), default), Times.Never);
        _basalRepo.Invocations.Should().HaveCountLessThanOrEqualTo(3);
        _carbRatioRepo.Invocations.Should().HaveCountLessThanOrEqualTo(3);
        _sensitivityRepo.Invocations.Should().HaveCountLessThanOrEqualTo(3);
        _targetRangeRepo.Invocations.Should().HaveCountLessThanOrEqualTo(3);
    }

    /// <summary>
    /// One correlation id spans every store in a profile upload, so the correlated rows carry the
    /// other stores' schedules too. Serving one of those would hand an AID consumer another store's
    /// basal rates, carb ratios and targets.
    /// </summary>
    [Fact]
    public async Task CorrelationLookupReturnsAnotherStore_IsNotServed()
    {
        var correlationId = Guid.NewGuid();
        var settings = CreateTherapySettings(correlationId: correlationId, profileName: "Default");

        SetupGetLatest(settings);
        SetupCorrelationLookups(
            correlationId,
            basals: [CreateBasalSchedule(correlationId, "Night")],
            carbRatios: [CreateCarbRatioSchedule(correlationId, "Night")],
            sensitivities: [CreateSensitivitySchedule(correlationId, "Night")],
            targetRanges: [CreateTargetRangeSchedule(correlationId, "Night")]);

        var result = await _sut.GetCurrentProfileAsync();

        var data = result!.Store["Default"];
        data.Basal.Should().BeEmpty("a schedule belonging to another store must not be served here");
        data.CarbRatio.Should().BeEmpty();
        data.Sens.Should().BeEmpty();
        data.TargetLow.Should().BeEmpty();
        data.TargetHigh.Should().BeEmpty();
    }

    /// <summary>
    /// The decoy here is what splitting the <c>"{profileId}:{storeName}"</c> legacy id on its first
    /// colon would yield for this store, so a name-derived match would serve the wrong schedule.
    /// </summary>
    [Fact]
    public async Task CorrelationLookup_MatchesTheStoreNameVerbatimWhenItContainsAColon()
    {
        var correlationId = Guid.NewGuid();
        const string storeName = "u200 sedentary (120%) 4/16/24 08:09";
        var settings = CreateTherapySettings(
            correlationId: correlationId,
            profileName: storeName,
            legacyId: $"profile1:{storeName}");

        SetupGetLatest(settings);
        SetupCorrelationLookups(
            correlationId,
            basals:
            [
                new BasalSchedule
                {
                    ProfileName = "u200 sedentary (120%) 4/16/24 08",
                    Entries = [new ScheduleEntry { Time = "00:00", Value = 0.4, TimeAsSeconds = 0 }],
                },
                new BasalSchedule
                {
                    ProfileName = storeName,
                    Entries = [new ScheduleEntry { Time = "00:00", Value = 1.5, TimeAsSeconds = 0 }],
                },
            ]);

        var result = await _sut.GetCurrentProfileAsync();

        result!.Store[storeName].Basal.Should().ContainSingle().Which.Value.Should().Be(1.5);
    }

    [Fact]
    public async Task LegacyId_ExtractsProfileIdPrefix()
    {
        var correlationId = Guid.NewGuid();
        var settings = CreateTherapySettings(
            correlationId: correlationId,
            legacyId: "507f1f77bcf86cd799439011:Default");

        _therapyRepo.Setup(r => r.GetByLegacyIdAsync("507f1f77bcf86cd799439011", default))
            .ReturnsAsync(settings);
        SetupDocumentRows(settings);
        SetupCorrelationLookups(correlationId);

        var result = await _sut.GetProfileByIdAsync("507f1f77bcf86cd799439011");

        result.Should().NotBeNull();
        result!.Id.Should().Be("507f1f77bcf86cd799439011");
    }

    #endregion

    #region Glucose units

    [Theory]
    [InlineData("mmol")]
    [InlineData("mmol/L")]
    public async Task GetCurrentProfileAsync_MmolProfile_ReturnsGlucoseValuesInMmol(string units)
    {
        var correlationId = Guid.NewGuid();
        var settings = CreateTherapySettings(correlationId: correlationId, units: units);
        var sensitivity = CreateSensitivitySchedule(correlationId);
        sensitivity.Entries =
        [
            new ScheduleEntry { Time = "00:00", Value = 63, TimeAsSeconds = 0 },
            new ScheduleEntry { Time = "04:00", Value = 58, TimeAsSeconds = 14400 },
        ];
        var targetRange = CreateTargetRangeSchedule(correlationId);
        targetRange.Entries = [new TargetRangeEntry { Time = "00:00", Low = 90, High = 99, TimeAsSeconds = 0 }];

        SetupGetLatest(settings);
        SetupCorrelationLookups(
            correlationId,
            basals: [CreateBasalSchedule(correlationId)],
            carbRatios: [CreateCarbRatioSchedule(correlationId)],
            sensitivities: [sensitivity],
            targetRanges: [targetRange]);

        var result = await _sut.GetCurrentProfileAsync();

        result!.Units.Should().Be(units);
        var data = result.Store["Default"];
        data.Sens.Select(s => s.Value).Should().Equal(3.5, 3.2);
        data.TargetLow.Single().Value.Should().Be(5.0);
        data.TargetHigh.Single().Value.Should().Be(5.5);
        data.Basal.Single().Value.Should().Be(1.0, "basal is U/h, not glucose-unit-dependent");
        data.CarbRatio.Single().Value.Should().Be(10.0, "carb ratio is g/U, not glucose-unit-dependent");
    }

    [Fact]
    public async Task GetCurrentProfileAsync_MgdlProfile_ReturnsGlucoseValuesUnchanged()
    {
        var correlationId = Guid.NewGuid();
        var settings = CreateTherapySettings(correlationId: correlationId, units: "mg/dL");

        SetupGetLatest(settings);
        SetupCorrelationLookups(
            correlationId,
            sensitivities: [CreateSensitivitySchedule(correlationId)],
            targetRanges: [CreateTargetRangeSchedule(correlationId)]);

        var result = await _sut.GetCurrentProfileAsync();

        var data = result!.Store["Default"];
        data.Sens.Single().Value.Should().Be(50.0);
        data.TargetLow.Single().Value.Should().Be(80);
        data.TargetHigh.Single().Value.Should().Be(120);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// The five siblings are written in separate transactions, so a group can be mid-convergence when
    /// a read arrives. The correlation lookup then finds nothing and, without this fallback, an AID
    /// consumer reading v1/v3 profile is served an empty basal schedule rather than an error.
    /// </summary>
    [Fact]
    public async Task CorrelationLookupMisses_ServesScheduleFromTheSharedLegacyId()
    {
        var correlationId = Guid.NewGuid();
        var settings = CreateTherapySettings(correlationId: correlationId, legacyId: "profile1:Default");

        SetupGetLatest(settings);
        SetupCorrelationLookups(correlationId);
        SetupLegacyIdLookups("profile1:Default", profileName: "Default");

        var result = await _sut.GetCurrentProfileAsync();

        var data = result!.Store["Default"];
        data.Basal.Should().ContainSingle().Which.Value.Should().Be(1.5);
        data.CarbRatio.Should().ContainSingle();
        data.Sens.Should().ContainSingle();
        data.TargetLow.Should().ContainSingle();
    }

    [Fact]
    public async Task CorrelationLookupMisses_AndNoRowUnderTheLegacyId_StaysEmpty()
    {
        var correlationId = Guid.NewGuid();
        var settings = CreateTherapySettings(correlationId: correlationId, legacyId: "profile1:Default");

        SetupGetLatest(settings);
        SetupCorrelationLookups(correlationId);
        SetupLegacyIdLookups("profile1:Default", profileName: null);

        var result = await _sut.GetCurrentProfileAsync();

        var data = result!.Store["Default"];
        data.Basal.Should().BeEmpty();
        data.CarbRatio.Should().BeEmpty();
        data.Sens.Should().BeEmpty();
        data.TargetLow.Should().BeEmpty();
    }

    /// <summary>
    /// The correlation path guards on <c>ProfileName</c>; the fallback must too. A profile name may
    /// contain a colon, so the <c>"{profileId}:{storeName}"</c> legacy id cannot be parsed to recover
    /// the store name, and a renamed row would otherwise serve one store's schedule under another's.
    /// </summary>
    [Fact]
    public async Task CorrelationLookupMisses_AndTheLegacyIdRowIsADifferentStore_StaysEmpty()
    {
        var correlationId = Guid.NewGuid();
        var settings = CreateTherapySettings(correlationId: correlationId, legacyId: "profile1:Default");

        SetupGetLatest(settings);
        SetupCorrelationLookups(correlationId);
        SetupLegacyIdLookups("profile1:Default", profileName: "Night");

        var result = await _sut.GetCurrentProfileAsync();

        var data = result!.Store["Default"];
        data.Basal.Should().BeEmpty("a schedule belonging to another store must not be served here");
        data.CarbRatio.Should().BeEmpty();
        data.Sens.Should().BeEmpty();
        data.TargetLow.Should().BeEmpty();
    }

    [Fact]
    public async Task CorrelationHit_IsServedWithoutConsultingTheLegacyIdRow()
    {
        var correlationId = Guid.NewGuid();
        var settings = CreateTherapySettings(correlationId: correlationId, legacyId: "profile1:Default");

        SetupGetLatest(settings);
        SetupCorrelationLookups(
            correlationId,
            basals: [CreateBasalSchedule(correlationId)],
            carbRatios: [CreateCarbRatioSchedule(correlationId)],
            sensitivities: [CreateSensitivitySchedule(correlationId)],
            targetRanges: [CreateTargetRangeSchedule(correlationId)]);
        SetupLegacyIdLookups("profile1:Default", "Default");

        var result = await _sut.GetCurrentProfileAsync();

        result!.Store["Default"].Basal.Should().ContainSingle().Which.Value.Should().Be(1.0);
        _basalRepo.Verify(r => r.GetByLegacyIdsAsync(It.IsAny<IEnumerable<string>>(), default), Times.Never);
        _carbRatioRepo.Verify(r => r.GetByLegacyIdsAsync(It.IsAny<IEnumerable<string>>(), default), Times.Never);
        _sensitivityRepo.Verify(r => r.GetByLegacyIdsAsync(It.IsAny<IEnumerable<string>>(), default), Times.Never);
        _targetRangeRepo.Verify(r => r.GetByLegacyIdsAsync(It.IsAny<IEnumerable<string>>(), default), Times.Never);
    }

    /// <summary>
    /// Schedules stored under <paramref name="legacyId"/> carrying <paramref name="profileName"/>, or
    /// no row at all when it is null.
    /// </summary>
    private void SetupLegacyIdLookups(string legacyId, string? profileName, double basalValue = 1.5)
    {
        if (profileName is null)
            return;

        _basals.Add(new BasalSchedule
        {
            LegacyId = legacyId,
            ProfileName = profileName,
            Entries = [new ScheduleEntry { Time = "00:00", Value = basalValue, TimeAsSeconds = 0 }],
        });
        _carbRatios.Add(new CarbRatioSchedule
        {
            LegacyId = legacyId,
            ProfileName = profileName,
            Entries = [new ScheduleEntry { Time = "00:00", Value = 10.0, TimeAsSeconds = 0 }],
        });
        _sensitivities.Add(new SensitivitySchedule
        {
            LegacyId = legacyId,
            ProfileName = profileName,
            Entries = [new ScheduleEntry { Time = "00:00", Value = 50.0, TimeAsSeconds = 0 }],
        });
        _targetRanges.Add(new TargetRangeSchedule
        {
            LegacyId = legacyId,
            ProfileName = profileName,
            Entries = [new TargetRangeEntry { Time = "00:00", Low = 80, High = 120, TimeAsSeconds = 0 }],
        });
    }

    private static TherapySettings CreateTherapySettings(
        Guid? id = null,
        Guid? correlationId = null,
        string profileName = "Default",
        string? legacyId = null,
        string? timezone = null,
        string? units = null,
        double dia = 3.0,
        int carbsHr = 20,
        int delay = 20,
        string? enteredBy = null,
        bool isExternallyManaged = false,
        string? startDate = null)
    {
        return new TherapySettings
        {
            Id = id ?? Guid.NewGuid(),
            CorrelationId = correlationId,
            ProfileName = profileName,
            LegacyId = legacyId,
            Timestamp = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc),
            CreatedAt = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc),
            ModifiedAt = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc),
            Timezone = timezone,
            Units = units ?? "mg/dL",
            Dia = dia,
            CarbsHr = carbsHr,
            Delay = delay,
            EnteredBy = enteredBy,
            IsExternallyManaged = isExternallyManaged,
            StartDate = startDate,
        };
    }

    private static BasalSchedule CreateBasalSchedule(
        Guid? correlationId, string profileName = "Default", double value = 1.0)
    {
        return new BasalSchedule
        {
            Id = Guid.NewGuid(),
            CorrelationId = correlationId,
            ProfileName = profileName,
            Entries = [new ScheduleEntry { Time = "00:00", Value = value, TimeAsSeconds = 0 }]
        };
    }

    private static CarbRatioSchedule CreateCarbRatioSchedule(Guid? correlationId, string profileName = "Default")
    {
        return new CarbRatioSchedule
        {
            Id = Guid.NewGuid(),
            CorrelationId = correlationId,
            ProfileName = profileName,
            Entries = [new ScheduleEntry { Time = "00:00", Value = 10.0, TimeAsSeconds = 0 }]
        };
    }

    private static SensitivitySchedule CreateSensitivitySchedule(Guid? correlationId, string profileName = "Default")
    {
        return new SensitivitySchedule
        {
            Id = Guid.NewGuid(),
            CorrelationId = correlationId,
            ProfileName = profileName,
            Entries = [new ScheduleEntry { Time = "00:00", Value = 50.0, TimeAsSeconds = 0 }]
        };
    }

    private static TargetRangeSchedule CreateTargetRangeSchedule(Guid? correlationId, string profileName = "Default")
    {
        return new TargetRangeSchedule
        {
            Id = Guid.NewGuid(),
            CorrelationId = correlationId,
            ProfileName = profileName,
            Entries = [new TargetRangeEntry { Time = "00:00", Low = 80, High = 120, TimeAsSeconds = 0 }]
        };
    }

    private void SetupGetLatest(TherapySettings settings)
    {
        _therapyRepo.Setup(r => r.GetDocumentsAsync(1, 0, true, default))
            .ReturnsAsync([[settings]]);
    }

    private void SetupDocumentRows(TherapySettings settings)
    {
        _therapyRepo.Setup(r => r.GetDocumentRowsAsync(TherapySettings.DocumentIdOf(settings), default))
            .ReturnsAsync([settings]);
    }

    private void SetupCorrelationLookups(
        Guid correlationId,
        BasalSchedule[]? basals = null,
        CarbRatioSchedule[]? carbRatios = null,
        SensitivitySchedule[]? sensitivities = null,
        TargetRangeSchedule[]? targetRanges = null)
    {
        Correlate(_basals, basals);
        Correlate(_carbRatios, carbRatios);
        Correlate(_sensitivities, sensitivities);
        Correlate(_targetRanges, targetRanges);

        void Correlate<T>(List<T> stored, T[]? schedules) where T : IV4Record
        {
            foreach (var schedule in schedules ?? [])
            {
                schedule.CorrelationId = correlationId;
                stored.Add(schedule);
            }
        }
    }

    #endregion
}
