using FluentAssertions;
using Moq;
using Nocturne.API.Services.Profiles;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Profiles;

/// <summary>
/// The v3 profile read-by-id resolves the <c>identifier</c> the profile is served under, which is
/// <see cref="MongoObjectId.Coerce"/> of its document id.
/// </summary>
[Trait("Category", "Unit")]
public class ProfileByIdResolutionTests
{
    private readonly Mock<ITherapySettingsRepository> _therapyRepo = new();
    private readonly ProfileProjectionService _sut;

    public ProfileByIdResolutionTests()
    {
        _therapyRepo.Setup(r => r.GetByLegacyIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TherapySettings?)null);
        _therapyRepo.Setup(r => r.GetDocumentRowsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _therapyRepo.Setup(r => r.GetDocumentIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _therapyRepo.Setup(r => r.GetByGuidRangeAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TherapySettings?)null);

        _sut = new ProfileProjectionService(
            _therapyRepo.Object,
            Mock.Of<IBasalScheduleRepository>(),
            Mock.Of<ICarbRatioScheduleRepository>(),
            Mock.Of<ISensitivityScheduleRepository>(),
            Mock.Of<ITargetRangeScheduleRepository>());
    }

    private static TherapySettings Row(string? legacyId) => new()
    {
        Id = Guid.Parse("0197a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2b"),
        LegacyId = legacyId,
        ProfileName = "Default",
        Timestamp = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc),
        CreatedAt = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc),
        ModifiedAt = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public async Task DocumentIdOfAStoredProfile_ResolvesItsStores()
    {
        const string documentId = "5f1d7a3b9c0e4a2b8d6c1e0f";
        _therapyRepo.Setup(r => r.GetDocumentRowsAsync(documentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Row($"{documentId}:Default")]);

        var profile = await _sut.GetProfileByIdAsync(documentId);

        profile.Should().NotBeNull();
        profile!.Id.Should().Be(documentId);
    }

    [Fact]
    public async Task ProfileKeyedByItsRowId_ResolvesByTheObjectIdItIsServedUnder()
    {
        var row = Row(null);
        var identifier = MongoObjectId.Coerce(row.Id.ToString())!;
        MongoObjectId.TryGetGuidPrefixRange(identifier, out var low, out var high);
        _therapyRepo.Setup(r => r.GetByGuidRangeAsync(low, high, It.IsAny<CancellationToken>()))
            .ReturnsAsync(row);
        _therapyRepo.Setup(r => r.GetDocumentRowsAsync(row.Id.ToString(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([row]);

        var profile = await _sut.GetProfileByIdAsync(identifier);

        profile.Should().NotBeNull();
        MongoObjectId.Coerce(profile!.Id).Should().Be(identifier);
    }

    [Fact]
    public async Task ProfileWithANonHexDocumentId_ResolvesByTheHashItIsServedUnder()
    {
        const string documentId = "profile-1A2B3C";
        var identifier = MongoObjectId.Coerce(documentId)!;
        _therapyRepo.Setup(r => r.GetDocumentIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([documentId]);
        _therapyRepo.Setup(r => r.GetDocumentRowsAsync(documentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Row($"{documentId}:Default")]);

        var profile = await _sut.GetProfileByIdAsync(identifier);

        profile.Should().NotBeNull();
        profile!.Id.Should().Be(documentId);
    }

    [Fact]
    public async Task DocumentWithSeveralStores_ServesEveryStore_NamedByItsNewest_WhicheverOrderTheyAreStoredIn()
    {
        const string documentId = "5f1d7a3b9c0e4a2b8d6c1e0f";
        var older = Row($"{documentId}:Weekday");
        older.ProfileName = "Weekday";
        var newer = Row($"{documentId}:Weekend");
        newer.ProfileName = "Weekend";
        newer.Timestamp = older.Timestamp.AddHours(1);

        _therapyRepo.Setup(r => r.GetDocumentRowsAsync(documentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([older, newer]);
        var first = await _sut.GetProfileByIdAsync(documentId);
        _therapyRepo.Setup(r => r.GetDocumentRowsAsync(documentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([newer, older]);
        var second = await _sut.GetProfileByIdAsync(documentId);

        first!.DefaultProfile.Should().Be("Weekend");
        second!.DefaultProfile.Should().Be("Weekend");
        first.Store.Keys.Should().BeEquivalentTo("Weekday", "Weekend");
        second.Store.Keys.Should().BeEquivalentTo("Weekday", "Weekend");
    }

    [Fact]
    public async Task RowIdOfAStoreKeyedUnderADocument_ResolvesThatDocument()
    {
        var row = Row("5f1d7a3b9c0e4a2b8d6c1e0f:Default");
        _therapyRepo.Setup(r => r.GetByIdAsync(row.Id, It.IsAny<CancellationToken>())).ReturnsAsync(row);

        (await _sut.ResolveDocumentIdAsync(row.Id.ToString())).Should().Be("5f1d7a3b9c0e4a2b8d6c1e0f");
    }

    [Fact]
    public async Task ServedIdentifierOfANonHexDocumentId_ResolvesThatDocumentId()
    {
        const string documentId = "profile-1A2B3C";
        _therapyRepo.Setup(r => r.GetDocumentIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([documentId]);
        _therapyRepo.Setup(r => r.GetDocumentRowsAsync(documentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Row($"{documentId}:Default")]);

        (await _sut.ResolveDocumentIdAsync(MongoObjectId.Coerce(documentId)!)).Should().Be(documentId);
    }

    [Fact]
    public async Task IdThatIsAPrefixOfAColonBearingStoreKey_ResolvesTheWholeDocument()
    {
        var row = Row("a:b:c");
        row.ProfileName = "b:c";
        _therapyRepo.Setup(r => r.GetDocumentRowsAsync("a:b", It.IsAny<CancellationToken>())).ReturnsAsync([row]);

        (await _sut.ResolveDocumentIdAsync("a:b")).Should().Be("a");
    }

    [Fact]
    public async Task UnknownIdentifier_ResolvesNothing()
    {
        (await _sut.GetProfileByIdAsync("5f1d7a3b9c0e4a2b8d6c1e0f")).Should().BeNull();
        (await _sut.ResolveDocumentIdAsync("5f1d7a3b9c0e4a2b8d6c1e0f")).Should().BeNull();
    }
}
