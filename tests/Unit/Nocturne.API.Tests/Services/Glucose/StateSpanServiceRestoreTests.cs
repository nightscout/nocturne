using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Glucose;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Glucose;

public class StateSpanServiceRestoreTests
{
    private readonly Mock<IStateSpanRepository> _repository = new();
    private readonly StateSpanService _service;

    public StateSpanServiceRestoreTests()
    {
        _service = new StateSpanService(_repository.Object, NullLogger<StateSpanService>.Instance);
    }

    [Fact]
    public async Task ResolveDeletedAsync_ReturnsTheRepositoryResolution()
    {
        var id = Guid.CreateVersion7();
        IReadOnlyDictionary<Guid, DeletedStateSpan> resolved = new Dictionary<
            Guid,
            DeletedStateSpan
        >
        {
            [id] = new DeletedStateSpan("sent-1", StateSpanCategory.Override),
        };
        _repository
            .Setup(r =>
                r.ResolveDeletedAsync(
                    It.Is<IReadOnlyCollection<string>>(ids => ids.Single() == "sent-1"),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(resolved);

        var result = await _service.ResolveDeletedAsync(["sent-1"]);

        result.Should().BeSameAs(resolved);
    }

    [Fact]
    public async Task RestoreStateSpanAsync_RestoresAsALiveWrite()
    {
        var id = Guid.CreateVersion7();
        var span = new StateSpan { Id = id.ToString() };
        _repository
            .Setup(r => r.RestoreAsync(id, WriteOrigin.Live, It.IsAny<CancellationToken>()))
            .ReturnsAsync(span);

        var result = await _service.RestoreStateSpanAsync(id);

        result.Should().BeSameAs(span);
    }

    [Fact]
    public async Task BulkRestoreStateSpansAsync_RestoresAsALiveWrite()
    {
        Guid[] ids = [Guid.CreateVersion7(), Guid.CreateVersion7()];
        var restored = new BulkRestoreResult<StateSpan> { Conflicts = [ids[1]] };
        _repository
            .Setup(r => r.BulkRestoreAsync(ids, WriteOrigin.Live, It.IsAny<CancellationToken>()))
            .ReturnsAsync(restored);

        var result = await _service.BulkRestoreStateSpansAsync(ids);

        result.Should().BeSameAs(restored);
    }
}
