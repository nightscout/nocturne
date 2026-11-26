using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Profiles;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Effects;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Profiles;

/// <summary>
/// A profile document sent with <c>isValid: false</c> is settled by the rule entries and treatments
/// follow: stored deleted under an id no document holds, deleting the live document its id names, and
/// writing nothing under an id whose stores a deleted document holds.
/// </summary>
[Trait("Category", "Unit")]
public class ProfileWriteServiceInvalidUploadTests
{
    private const string Id = "5f8d0c1e8a7b4c3d9e5f0001";

    private readonly Mock<IProfileDecomposer> _decomposer = new();
    private readonly Mock<ITherapySettingsRepository> _settings = new();
    private readonly Mock<IWriteSideEffects> _sideEffects = new();
    private readonly Mock<IDataEventSink<Profile>> _events = new();

    public ProfileWriteServiceInvalidUploadTests()
    {
        _settings
            .Setup(r => r.GetDocumentRowsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, IReadOnlyList<TherapySettings>>());
        _settings
            .Setup(r => r.GetDeletedLegacyIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>());
    }

    private ProfileWriteService Service() => new(
        _decomposer.Object,
        Mock.Of<IProfileProjectionService>(),
        _settings.Object,
        _sideEffects.Object,
        _events.Object,
        Mock.Of<IAuditContext>(),
        NullLogger<ProfileWriteService>.Instance);

    private static Profile Document(string id = Id, bool? isValid = false) => new()
    {
        Id = id,
        IsValid = isValid,
        DefaultProfile = "Default",
        Store = new Dictionary<string, ProfileData> { ["Default"] = new() },
    };

    [Fact]
    public async Task AnIdNoDocumentHolds_IsStoredDeleted_AndNotDecomposed()
    {
        var upload = Document();

        var written = await Service().CreateProfilesAsync([upload]);

        _decomposer.Verify(d => d.StoreDeletedAsync(
            It.Is<IReadOnlyList<Profile>>(p => p.Single() == upload), It.IsAny<CancellationToken>()), Times.Once);
        _decomposer.Verify(d => d.DecomposeBatchAsync(
            It.IsAny<IReadOnlyList<Profile>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
        _events.Verify(e => e.OnCreatedAsync(It.IsAny<IReadOnlyList<Profile>>(), It.IsAny<CancellationToken>()), Times.Never);
        written.Should().ContainSingle().Which.Should().BeSameAs(upload);
        written.Invalidated.Should().BeEmpty();
    }

    [Fact]
    public async Task AnIdALiveDocumentHolds_DeletesIt_AndAnnouncesTheDelete()
    {
        _settings
            .Setup(r => r.GetDocumentRowsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, IReadOnlyList<TherapySettings>>
            {
                [Id] = [new TherapySettings { LegacyId = $"{Id}:Default" }],
            });
        _decomposer.Setup(d => d.DeleteDocumentAsync(Id, WriteOrigin.Live, It.IsAny<CancellationToken>())).ReturnsAsync(5);
        var upload = Document();

        var written = await Service().CreateProfilesAsync([upload]);

        written.Invalidated.Should().ContainSingle().Which.Should().BeSameAs(upload);
        _events.Verify(e => e.OnDeletedAsync(null, It.IsAny<CancellationToken>()), Times.Once);
        _decomposer.Verify(d => d.StoreDeletedAsync(
            It.Is<IReadOnlyList<Profile>>(p => p.Count > 0), It.IsAny<CancellationToken>()), Times.Never);
        _decomposer.Verify(d => d.DecomposeBatchAsync(
            It.IsAny<IReadOnlyList<Profile>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AnIdWhoseStoresADeletedDocumentHolds_WritesNothing()
    {
        _settings
            .Setup(r => r.GetDeletedLegacyIdsAsync(
                It.Is<IReadOnlyCollection<string>>(ids => ids.Contains($"{Id}:Default")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string> { $"{Id}:Default" });

        await Service().CreateProfilesAsync([Document()]);

        _decomposer.Verify(d => d.StoreDeletedAsync(
            It.Is<IReadOnlyList<Profile>>(p => p.Count > 0), It.IsAny<CancellationToken>()), Times.Never);
        _decomposer.Verify(d => d.DeleteDocumentAsync(
            It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
        _decomposer.Verify(d => d.DecomposeBatchAsync(
            It.IsAny<IReadOnlyList<Profile>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TheValidDocumentsOfTheBatchAreDecomposedAndAnnounced()
    {
        const string otherId = "5f8d0c1e8a7b4c3d9e5f0002";
        var valid = Document(otherId, isValid: null);

        await Service().CreateProfilesAsync([valid, Document()]);

        _decomposer.Verify(d => d.DecomposeBatchAsync(
            It.Is<IReadOnlyList<Profile>>(p => p.Single() == valid), WriteOrigin.Live, It.IsAny<CancellationToken>()), Times.Once);
        _events.Verify(e => e.OnCreatedAsync(
            It.Is<IReadOnlyList<Profile>>(p => p.Single() == valid), It.IsAny<CancellationToken>()), Times.Once);
    }
}
