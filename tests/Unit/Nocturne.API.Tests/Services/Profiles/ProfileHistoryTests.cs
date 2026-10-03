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
        _therapyRepo.Setup(r => r.GetByProfileNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _therapyRepo.Setup(r => r.GetByLegacyIdPrefixAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
        _therapyRepo.Setup(r => r.GetByCorrelationIdAsync(correlationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _therapyRepo.Setup(r => r.GetByLegacyIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TherapySettings?)null);
        _basalRepo.Setup(r => r.GetByCorrelationIdAsync(correlationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _carbRatioRepo.Setup(r => r.GetByCorrelationIdAsync(correlationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _sensitivityRepo.Setup(r => r.GetByCorrelationIdAsync(correlationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _targetRangeRepo.Setup(r => r.GetByCorrelationIdAsync(correlationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

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
        _therapyRepo.Setup(r => r.GetByLegacyIdPrefixAsync($"{documentId}:", It.IsAny<CancellationToken>()))
            .ReturnsAsync([live]);
        SetupProfile(deleted, Basal(deletedCorrelation, modified: deletedAt));
        SetupProfile(live, liveBasal);

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
        _therapyRepo.Verify(r => r.GetByLegacyIdPrefixAsync($"{documentId}:", It.IsAny<CancellationToken>()), Times.Once);
    }

    private void SetupProfile(TherapySettings settings, BasalSchedule basal)
    {
        var correlationId = settings.CorrelationId!.Value;
        _therapyRepo.Setup(r => r.GetByCorrelationIdAsync(correlationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([settings]);
        _basalRepo.Setup(r => r.GetByCorrelationIdAsync(correlationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([basal]);
        _carbRatioRepo.Setup(r => r.GetByCorrelationIdAsync(correlationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _sensitivityRepo.Setup(r => r.GetByCorrelationIdAsync(correlationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _targetRangeRepo.Setup(r => r.GetByCorrelationIdAsync(correlationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    private static HistoryRecord<T> Live<T>(T record) => new(record, Deleted: false);

    private static long Mills(DateTime value) => new DateTimeOffset(value, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static TherapySettings Settings(Guid correlationId, DateTime modified) => new()
    {
        Id = Guid.CreateVersion7(),
        CorrelationId = correlationId,
        ProfileName = "Default",
        Timestamp = Created,
        CreatedAt = Created,
        ModifiedAt = modified,
        Units = "mg/dL",
    };

    private static BasalSchedule Basal(Guid correlationId, DateTime modified) => new()
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
