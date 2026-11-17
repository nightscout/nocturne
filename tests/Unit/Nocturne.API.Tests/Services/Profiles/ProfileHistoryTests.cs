using FluentAssertions;
using Moq;
using Nocturne.API.Services.Profiles;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Queries;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Profiles;

/// <summary>
/// The v3 profile <c>history/{lastModified}</c> read pages on <c>srvModified</c>, as Nightscout's
/// <c>lib/api3/generic/history</c> does. A projected profile is written as five rows, so its stamp is
/// the newest of them: editing only a schedule must re-deliver the profile, however old its
/// <c>mills</c>.
/// </summary>
[Trait("Category", "Unit")]
public class ProfileHistoryTests
{
    private static readonly DateTime Created = new(2026, 1, 5, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Cursor = new(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);

    private readonly Mock<ITherapySettingsRepository> _therapyRepo = new();
    private readonly Mock<IBasalScheduleRepository> _basalRepo = new();
    private readonly Mock<ICarbRatioScheduleRepository> _carbRatioRepo = new();
    private readonly Mock<ISensitivityScheduleRepository> _sensitivityRepo = new();
    private readonly Mock<ITargetRangeScheduleRepository> _targetRangeRepo = new();
    private readonly List<BasalSchedule> _basals = [];
    private readonly List<TherapySettings> _owners = [];
    private readonly List<TherapySettings> _liveRows = [];
    private readonly ProfileProjectionService _sut;

    public ProfileHistoryTests()
    {
        _therapyRepo.Setup(r => r.GetModifiedSinceAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _basalRepo.Setup(r => r.GetModifiedSinceAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _carbRatioRepo.Setup(r => r.GetModifiedSinceAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _sensitivityRepo.Setup(r => r.GetModifiedSinceAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _targetRangeRepo.Setup(r => r.GetModifiedSinceAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _therapyRepo.Serve(_owners);
        _therapyRepo.ServeDocuments(_liveRows);
        _basalRepo.Serve(_basals);
        _therapyRepo.Setup(r => r.GetStoreIndexAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        _sut = new ProfileProjectionService(
            _therapyRepo.Object,
            _basalRepo.Object,
            _carbRatioRepo.Object,
            _sensitivityRepo.Object,
            _targetRangeRepo.Object);
    }

    [Fact]
    public async Task ProfileWhoseScheduleWasEditedAfterTheCursor_IsDelivered_StampedByThatEdit()
    {
        var correlationId = Guid.CreateVersion7();
        var settings = Settings(correlationId, modified: Created);
        var basal = Basal(correlationId, modified: Cursor.AddMinutes(3));
        _basalRepo.Setup(r => r.GetModifiedSinceAsync(Mills(Cursor), 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Live(basal)]);
        SetupProfile(settings, basal);
        SetupDocument(settings);

        var page = await _sut.GetProfilesModifiedSinceAsync(Mills(Cursor), 10);

        var profile = page.Records.Should().ContainSingle().Subject;
        profile.Mills.Should().Be(Mills(Created));
        profile.SrvModified.Should().Be(Mills(basal.ModifiedAt));
        profile.Store["Default"].Basal.Should().ContainSingle().Which.Value.Should().Be(0.9);
        page.CursorMills.Should().Be(Mills(basal.ModifiedAt));
    }

    [Fact]
    public async Task NothingWrittenAfterTheCursor_ReturnsAnEmptyPageWithoutACursor()
    {
        var page = await _sut.GetProfilesModifiedSinceAsync(Mills(Cursor), 10);

        page.Records.Should().BeEmpty();
        page.CursorMills.Should().BeNull();
    }

    [Fact]
    public async Task ProfileStampedPastAFullTablePage_WaitsForThatPage_AndTheReadStillAdvances()
    {
        // limit 1: the settings page fills at t1, but the profile's newest row (its basal, t2) lies
        // past that horizon, so delivering it now would move the cursor past settings rows at t1
        // that were never read. The read resumes from t1 and delivers it on the basal page.
        var correlationId = Guid.CreateVersion7();
        var t1 = Cursor.AddMinutes(1);
        var t2 = Cursor.AddMinutes(2);
        var settings = Settings(correlationId, modified: t1);
        var basal = Basal(correlationId, modified: t2);
        _therapyRepo.Setup(r => r.GetModifiedSinceAsync(Mills(Cursor), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Live(settings)]);
        _basalRepo.Setup(r => r.GetModifiedSinceAsync(Mills(Cursor), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Live(basal)]);
        _basalRepo.Setup(r => r.GetModifiedSinceAsync(Mills(t1), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Live(basal)]);
        SetupProfile(settings, basal);
        SetupDocument(settings);

        var page = await _sut.GetProfilesModifiedSinceAsync(Mills(Cursor), 1);

        page.Records.Should().ContainSingle().Which.SrvModified.Should().Be(Mills(t2));
        page.CursorMills.Should().Be(Mills(t2));
    }

    [Fact]
    public async Task DeletedProfile_IsDeliveredWithIsValidFalse_StampedByItsDelete()
    {
        var correlationId = Guid.CreateVersion7();
        var deletedAt = Cursor.AddMinutes(2);
        var settings = Settings(correlationId, modified: deletedAt);
        settings.LegacyId = "65f000000000000000000abc:Default";
        var basal = Basal(correlationId, modified: deletedAt);
        _therapyRepo.Setup(r => r.GetModifiedSinceAsync(Mills(Cursor), 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new HistoryRecord<TherapySettings>(settings, Deleted: true)]);
        _basalRepo.Setup(r => r.GetModifiedSinceAsync(Mills(Cursor), 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new HistoryRecord<BasalSchedule>(basal, Deleted: true)]);

        var page = await _sut.GetProfilesModifiedSinceAsync(Mills(Cursor), 10);

        var profile = page.Records.Should().ContainSingle().Subject;
        profile.Id.Should().Be("65f000000000000000000abc");
        profile.IsValid.Should().BeFalse();
        profile.SrvModified.Should().Be(Mills(deletedAt));
        page.CursorMills.Should().Be(Mills(deletedAt));
    }

    [Fact]
    public async Task OneStoreOfATwoStoreDocumentDeleted_ReSendsTheLiveStoreAtTheDeleteStamp()
    {
        const string documentId = "65f000000000000000000abd";
        var deletedAt = Cursor.AddMinutes(2);
        var deletedCorrelation = Guid.CreateVersion7();
        var deleted = Settings(deletedCorrelation, modified: deletedAt);
        deleted.LegacyId = $"{documentId}:Default";
        var liveCorrelation = Guid.CreateVersion7();
        var live = Settings(liveCorrelation, modified: Created);
        live.ProfileName = "Night";
        live.LegacyId = $"{documentId}:Night";
        var liveBasal = Basal(liveCorrelation, modified: Created);
        liveBasal.ProfileName = "Night";
        _therapyRepo.Setup(r => r.GetModifiedSinceAsync(Mills(Cursor), 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new HistoryRecord<TherapySettings>(deleted, Deleted: true)]);
        SetupProfile(deleted, Basal(deletedCorrelation, modified: deletedAt));
        SetupProfile(live, liveBasal);
        SetupDocument(live);

        var page = await _sut.GetProfilesModifiedSinceAsync(Mills(Cursor), 10);

        var profile = page.Records.Should().ContainSingle().Subject;
        profile.Id.Should().Be(documentId);
        profile.IsValid.Should().BeNull();
        profile.Store.Keys.Should().Equal("Night");
        profile.SrvModified.Should().Be(Mills(deletedAt));
        page.CursorMills.Should().Be(Mills(deletedAt));
    }

    [Fact]
    public async Task EveryStoreOfADocumentDeleted_IsDeliveredAsATombstone()
    {
        const string documentId = "65f000000000000000000abe";
        var deletedAt = Cursor.AddMinutes(2);
        var correlation = Guid.CreateVersion7();
        var deleted = Settings(correlation, modified: deletedAt);
        deleted.LegacyId = $"{documentId}:Default";
        _therapyRepo.Setup(r => r.GetModifiedSinceAsync(Mills(Cursor), 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new HistoryRecord<TherapySettings>(deleted, Deleted: true)]);
        SetupProfile(deleted, Basal(correlation, modified: deletedAt));

        var page = await _sut.GetProfilesModifiedSinceAsync(Mills(Cursor), 10);

        var profile = page.Records.Should().ContainSingle().Subject;
        profile.Id.Should().Be(documentId);
        profile.IsValid.Should().BeFalse();
        _therapyRepo.Verify(
            r => r.GetDocumentRowsAsync(
                It.Is<IReadOnlyCollection<string>>(ids => ids.SequenceEqual(new[] { documentId })),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task TwoStoresOfOneDocumentWritten_AreDeliveredOnce_CarryingBothStores()
    {
        const string documentId = "65f000000000000000000abf";
        var modified = Cursor.AddMinutes(1);
        var dayCorrelation = Guid.CreateVersion7();
        var day = Settings(dayCorrelation, modified);
        day.ProfileName = "Day";
        day.LegacyId = $"{documentId}:Day";
        var nightCorrelation = Guid.CreateVersion7();
        var night = Settings(nightCorrelation, modified);
        night.ProfileName = "Night";
        night.LegacyId = $"{documentId}:Night";
        night.IsDefault = true;
        _therapyRepo.Setup(r => r.GetModifiedSinceAsync(Mills(Cursor), 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Live(day), Live(night)]);
        SetupProfile(day, Basal(dayCorrelation, modified));
        SetupProfile(night, Basal(nightCorrelation, modified));
        SetupDocument(day, night);

        var page = await _sut.GetProfilesModifiedSinceAsync(Mills(Cursor), 10);

        var profile = page.Records.Should().ContainSingle().Subject;
        profile.Id.Should().Be(documentId);
        profile.DefaultProfile.Should().Be("Night");
        profile.Store.Keys.Should().BeEquivalentTo("Day", "Night");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(12)]
    public async Task PageOfDocuments_ReadsTherapyOwnersAndDocumentRowsInConstantCalls(int documents)
    {
        var basals = new List<BasalSchedule>();
        for (var i = 0; i < documents; i++)
        {
            var correlationId = Guid.CreateVersion7();
            var settings = Settings(correlationId, modified: Created);
            settings.ProfileName = $"Store{i}";
            settings.LegacyId = $"65f0000000000000000{i:D5}:Store{i}";
            var basal = Basal(correlationId, modified: Cursor.AddMinutes(1 + i));
            basal.ProfileName = settings.ProfileName;
            basals.Add(basal);
            SetupProfile(settings, basal);
            SetupDocument(settings);
        }
        _basalRepo.Setup(r => r.GetModifiedSinceAsync(Mills(Cursor), 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(basals.Select(Live).ToList());

        var page = await _sut.GetProfilesModifiedSinceAsync(Mills(Cursor), 100);

        page.Records.Should().HaveCount(documents);
        _therapyRepo.Verify(r => r.GetByCorrelationIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()), Times.Once);
        _therapyRepo.Verify(r => r.GetByLegacyIdsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Once);
        _therapyRepo.Verify(r => r.GetByProfileNamesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Once);
        _therapyRepo.Verify(r => r.GetDocumentRowsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()), Times.Once);
        _therapyRepo.Verify(r => r.GetByCorrelationIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _therapyRepo.Verify(r => r.GetByLegacyIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _therapyRepo.Verify(r => r.GetByProfileNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _therapyRepo.Verify(r => r.GetDocumentRowsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MixedPage_AssemblesEachDocumentByHowItsSchedulesReachIt_InStampOrder()
    {
        var correlated = Settings(Guid.CreateVersion7(), modified: Created);
        correlated.LegacyId = "65f000000000000000000c01:Default";
        var correlatedBasal = Basal(correlated.CorrelationId!.Value, modified: Cursor.AddMinutes(1));
        correlatedBasal.Entries[0].Value = 1.1;
        SetupProfile(correlated, correlatedBasal);
        SetupDocument(correlated);

        var legacy = Settings(Guid.CreateVersion7(), modified: Created);
        legacy.ProfileName = "Legacy";
        legacy.LegacyId = "65f000000000000000000c02:Legacy";
        var legacyBasal = Basal(null, modified: Cursor.AddMinutes(2));
        legacyBasal.ProfileName = "Legacy";
        legacyBasal.LegacyId = legacy.LegacyId;
        legacyBasal.Entries[0].Value = 2.2;
        _owners.Add(legacy);
        _basals.Add(legacyBasal);
        SetupDocument(legacy);

        var named = Settings(null, modified: Created);
        named.ProfileName = "Named";
        var namedBasal = Basal(null, modified: Cursor.AddMinutes(3));
        namedBasal.ProfileName = "Named";
        namedBasal.Entries[0].Value = 3.3;
        _owners.Add(named);
        _basals.Add(namedBasal);
        SetupDocument(named);

        var deletedAt = Cursor.AddMinutes(4);
        var deleted = Settings(Guid.CreateVersion7(), modified: deletedAt);
        deleted.ProfileName = "Gone";
        deleted.LegacyId = "65f000000000000000000c03:Gone";
        var deletedBasal = Basal(deleted.CorrelationId!.Value, modified: deletedAt);
        deletedBasal.ProfileName = "Gone";
        _basals.Add(deletedBasal);

        _therapyRepo.Setup(r => r.GetModifiedSinceAsync(Mills(Cursor), 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new HistoryRecord<TherapySettings>(deleted, Deleted: true)]);
        _basalRepo.Setup(r => r.GetModifiedSinceAsync(Mills(Cursor), 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Live(correlatedBasal), Live(legacyBasal), Live(namedBasal)]);

        var page = await _sut.GetProfilesModifiedSinceAsync(Mills(Cursor), 10);

        page.Records.Select(p => p.Id).Should().Equal(
            "65f000000000000000000c01", "65f000000000000000000c02", named.Id.ToString(), "65f000000000000000000c03");
        page.Records.Select(p => p.IsValid).Should().Equal(null, null, null, false);
        page.Records.Select(p => p.SrvModified).Should().Equal(
            Mills(correlatedBasal.ModifiedAt), Mills(legacyBasal.ModifiedAt), Mills(namedBasal.ModifiedAt), Mills(deletedAt));
        page.Records.Take(3).Select(p => p.Store.Single().Value.Basal.Single().Value)
            .Should().Equal(1.1, 2.2, 3.3);
        page.Records[3].Store.Keys.Should().Equal("Gone");
        page.CursorMills.Should().Be(Mills(deletedAt));
    }

    private void SetupProfile(TherapySettings settings, BasalSchedule basal)
    {
        _owners.Add(settings);
        _basals.Add(basal);
    }

    private void SetupDocument(params TherapySettings[] rows) => _liveRows.AddRange(rows);

    private static HistoryRecord<T> Live<T>(T record) => new(record, Deleted: false);

    private static long Mills(DateTime value) => new DateTimeOffset(value, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static TherapySettings Settings(Guid? correlationId, DateTime modified) => new()
    {
        Id = Guid.CreateVersion7(),
        CorrelationId = correlationId,
        ProfileName = "Default",
        Timestamp = Created,
        CreatedAt = Created,
        ModifiedAt = modified,
        Units = "mg/dL",
    };

    private static BasalSchedule Basal(Guid? correlationId, DateTime modified) => new()
    {
        Id = Guid.CreateVersion7(),
        CorrelationId = correlationId,
        ProfileName = "Default",
        Timestamp = Created,
        CreatedAt = Created,
        ModifiedAt = modified,
        Entries = [new ScheduleEntry { Time = "00:00", Value = 0.9, TimeAsSeconds = 0 }],
    };
}
