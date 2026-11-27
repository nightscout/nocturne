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
using Npgsql;
using Nocturne.Tests.Shared.Mocks;
using Xunit;

namespace Nocturne.API.Tests.Services.Profiles;

/// <summary>
/// A profile write publishes each document once, after its last row is written, as a read then serves
/// it; a delete publishes the document as it was read before the delete.
/// </summary>
[Trait("Category", "Unit")]
public class ProfileWriteServicePublishTests
{
    private const string Id = "5f8d0c1e8a7b4c3d9e5f0001";

    private readonly Mock<IProfileDecomposer> _decomposer = new();
    private readonly Mock<IProfileProjectionService> _projection = new();
    private readonly Mock<ITherapySettingsRepository> _therapySettings = new();
    private readonly RecordingDocumentChanges<Profile> _changes = new();
    private readonly Dictionary<string, Profile> _stored = new(StringComparer.Ordinal);

    public ProfileWriteServicePublishTests()
    {
        _projection
            .Setup(p => p.GetProfileByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => _stored.GetValueOrDefault(id));
        _projection
            .Setup(p => p.ResolveDocumentIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => id);
        _decomposer
            .Setup(d => d.DecomposeBatchAsync(It.IsAny<IReadOnlyList<Profile>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InvalidatingDecomposition<Profile>());
    }

    private ProfileWriteService Service() => new(
        _decomposer.Object,
        _projection.Object,
        _therapySettings.Object,
        _changes,
        Mock.Of<IDataEventSink<Profile>>(),
        NullLogger<ProfileWriteService>.Instance);

    private static Profile Document(string id = Id, string defaultProfile = "Default") => new()
    {
        Id = id,
        DefaultProfile = defaultProfile,
        Store = new Dictionary<string, ProfileData> { [defaultProfile] = new() },
    };

    private Profile Store(string id = Id) => _stored[id] = Document(id, "Served");

    [Fact]
    public async Task ACreate_PublishesTheDocumentAsAReadServesIt()
    {
        var served = Store();

        await Service().CreateProfilesAsync([Document()]);

        _changes.Created.Should().ContainSingle().Which.Should().BeSameAs(served);
        _changes.Batches.Should().ContainSingle();
    }

    [Fact]
    public async Task ACreateThatCannotBeReadBack_IsNotPublished()
    {
        await Service().CreateProfilesAsync([Document()]);

        _changes.Batches.Should().BeEmpty();
    }

    [Fact]
    public async Task AnUpdate_PublishesTheDocumentAsAReadServesIt()
    {
        var served = Store();
        _decomposer
            .Setup(d => d.ReplaceDocumentAsync(It.IsAny<Profile>(), WriteOrigin.Live, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await Service().UpdateProfileAsync(Id, Document());

        _changes.Updated.Should().ContainSingle().Which.Should().BeSameAs(served);
    }

    [Fact]
    public async Task AnUpdateOfNothing_PublishesNothing()
    {
        Store();
        _decomposer
            .Setup(d => d.ReplaceDocumentAsync(It.IsAny<Profile>(), WriteOrigin.Live, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        (await Service().UpdateProfileAsync(Id, Document())).Should().BeNull();

        _changes.Batches.Should().BeEmpty();
    }

    [Fact]
    public async Task ADelete_PublishesTheDocumentReadBeforeTheDelete()
    {
        var served = Store();
        _decomposer
            .Setup(d => d.DeleteDocumentAsync(Id, WriteOrigin.Live, It.IsAny<CancellationToken>()))
            .Callback(() => _stored.Remove(Id))
            .ReturnsAsync(1);

        (await Service().DeleteProfileAsync(Id)).Should().BeTrue();

        _changes.Deleted.Should().ContainSingle().Which.Should().BeSameAs(served);
    }

    [Fact]
    public async Task ADeleteOfNothing_PublishesNothing()
    {
        (await Service().DeleteProfileAsync(Id)).Should().BeFalse();

        _changes.Batches.Should().BeEmpty();
    }

    [Fact]
    public async Task APrune_PublishesEachDocumentItDeleted_InOneChange()
    {
        string[] ids = ["5f8d0c1e8a7b4c3d9e5f0001", "5f8d0c1e8a7b4c3d9e5f0002", "5f8d0c1e8a7b4c3d9e5f0003"];
        foreach (var id in ids)
            Store(id);
        _therapySettings.Setup(t => t.GetDocumentIdsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(ids);
        _decomposer
            .Setup(d => d.DeleteDocumentAsync(It.IsAny<string>(), WriteOrigin.Live, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        (await Service().PruneProfilesAsync(keep: 1)).Should().Be(2);

        _changes.Deleted.Select(p => p.Id).Should().Equal(ids[1], ids[2]);
        _changes.Batches.Should().ContainSingle();
    }

    [Fact]
    public async Task ACreateThatAlsoInvalidates_PublishesItsCreatesAndDeletesTogether()
    {
        const string invalidId = "5f8d0c1e8a7b4c3d9e5f0002";
        var served = Store();
        var stored = Store(invalidId);
        var invalid = Document(invalidId);
        invalid.IsValid = false;
        var decomposition = new InvalidatingDecomposition<Profile>();
        decomposition.AddInvalidated(invalid);
        _decomposer
            .Setup(d => d.DecomposeBatchAsync(It.IsAny<IReadOnlyList<Profile>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(decomposition);

        await Service().CreateProfilesAsync([Document(), invalid]);

        _changes.Created.Should().ContainSingle().Which.Should().BeSameAs(served);
        _changes.Deleted.Should().ContainSingle().Which.Should().BeSameAs(stored);
    }

    [Fact]
    public async Task AReadBackTheStoreFails_DoesNotFailTheCommittedWrite()
    {
        var events = new Mock<IDataEventSink<Profile>>();
        _projection
            .Setup(p => p.GetProfileByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NpgsqlException("connection refused"));
        var service = new ProfileWriteService(
            _decomposer.Object, _projection.Object, _therapySettings.Object, _changes, events.Object,
            NullLogger<ProfileWriteService>.Instance);

        var written = await service.CreateProfilesAsync([Document()]);

        written.Should().ContainSingle();
        _changes.Batches.Should().BeEmpty();
        events.Verify(e => e.OnCreatedAsync(It.IsAny<IReadOnlyList<Profile>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task APruneWhoseReadFails_StillDeletesEveryChosenDocument()
    {
        string[] ids = ["5f8d0c1e8a7b4c3d9e5f0001", "5f8d0c1e8a7b4c3d9e5f0002", "5f8d0c1e8a7b4c3d9e5f0003"];
        _therapySettings.Setup(t => t.GetDocumentIdsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(ids);
        _projection
            .Setup(p => p.GetProfileByIdAsync(ids[1], It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NpgsqlException("connection refused"));
        _decomposer
            .Setup(d => d.DeleteDocumentAsync(It.IsAny<string>(), WriteOrigin.Live, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        (await Service().PruneProfilesAsync(keep: 1)).Should().Be(2);

        _decomposer.Verify(d => d.DeleteDocumentAsync(ids[2], WriteOrigin.Live, It.IsAny<CancellationToken>()), Times.Once);
    }
}
