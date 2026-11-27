using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Profiles;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Tests.Shared.Mocks;
using Xunit;

namespace Nocturne.API.Tests.Services.Profiles;

/// <summary>
/// A profile upload announces what the decomposition settled for the documents sent with
/// <c>isValid: false</c>: only the documents that write are announced as created, and a document that
/// deleted a live one is answered as invalidated and announced as a delete.
/// </summary>
[Trait("Category", "Unit")]
public class ProfileWriteServiceInvalidUploadTests
{
    private const string Id = "5f8d0c1e8a7b4c3d9e5f0001";

    private readonly Mock<IProfileDecomposer> _decomposer = new();
    private readonly Mock<IProfileProjectionService> _projection = new();
    private readonly RecordingDocumentChanges<Profile> _changes = new();
    private readonly Mock<IDataEventSink<Profile>> _events = new();
    private InvalidatingDecomposition<Profile> _decomposition = new();

    public ProfileWriteServiceInvalidUploadTests()
    {
        _decomposer
            .Setup(d => d.DecomposeBatchAsync(It.IsAny<IReadOnlyList<Profile>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _decomposition);
        _projection
            .Setup(p => p.GetProfileByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => Document(id, isValid: null));
    }

    private ProfileWriteService Service() => new(
        _decomposer.Object,
        _projection.Object,
        Mock.Of<ITherapySettingsRepository>(),
        _changes,
        _events.Object,
        NullLogger<ProfileWriteService>.Instance);

    private static Profile Document(string id = Id, bool? isValid = false) => new()
    {
        Id = id,
        IsValid = isValid,
        DefaultProfile = "Default",
        Store = new Dictionary<string, ProfileData> { ["Default"] = new() },
    };

    private static InvalidatingDecomposition<Profile> Invalidating(Profile invalidated)
    {
        var decomposition = new InvalidatingDecomposition<Profile>();
        decomposition.AddInvalidated(invalidated);
        return decomposition;
    }

    [Fact]
    public async Task ADocumentStoredDeleted_IsNotAnnounced()
    {
        var upload = Document();

        var written = await Service().CreateProfilesAsync([upload]);

        _decomposer.Verify(d => d.DecomposeBatchAsync(
            It.Is<IReadOnlyList<Profile>>(p => p.Single() == upload), WriteOrigin.Live, It.IsAny<CancellationToken>()), Times.Once);
        _events.Verify(e => e.OnCreatedAsync(It.IsAny<IReadOnlyList<Profile>>(), It.IsAny<CancellationToken>()), Times.Never);
        _events.Verify(e => e.OnDeletedAsync(It.IsAny<Profile?>(), It.IsAny<CancellationToken>()), Times.Never);
        written.Should().ContainSingle().Which.Should().BeSameAs(upload);
        written.Invalidated.Should().BeEmpty();
        _changes.Batches.Should().BeEmpty();
    }

    [Fact]
    public async Task ADocumentThatDeletedALiveOne_IsInvalidated_AndAnnouncedAsADelete()
    {
        var upload = Document();
        _decomposition = Invalidating(upload);

        var written = await Service().CreateProfilesAsync([upload]);

        written.Invalidated.Should().ContainSingle().Which.Should().BeSameAs(upload);
        _events.Verify(e => e.OnDeletedAsync(null, It.IsAny<CancellationToken>()), Times.Once);
        _events.Verify(e => e.OnCreatedAsync(It.IsAny<IReadOnlyList<Profile>>(), It.IsAny<CancellationToken>()), Times.Never);
        _changes.Deleted.Should().ContainSingle().Which.Id.Should().Be(Id, "the delete names the document as it was stored");
        _changes.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task OnlyTheValidDocumentsOfTheBatchAreAnnouncedAsCreated()
    {
        var valid = Document("5f8d0c1e8a7b4c3d9e5f0002", isValid: null);

        await Service().CreateProfilesAsync([valid, Document()]);

        _events.Verify(e => e.OnCreatedAsync(
            It.Is<IReadOnlyList<Profile>>(p => p.Single() == valid), It.IsAny<CancellationToken>()), Times.Once);
        _changes.Created.Should().ContainSingle().Which.Id.Should().Be(valid.Id, "a created document is published as a read serves it");
    }
}
