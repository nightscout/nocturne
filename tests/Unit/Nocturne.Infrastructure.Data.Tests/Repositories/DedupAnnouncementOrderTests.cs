using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// A deduplicated insert announces only after its rows are linked, so an alert evaluation the
/// announcement starts never reads a copy linking is about to hide. A link that is cancelled still
/// announces, so no legacy read cache keeps serving the rows as they were.
/// </summary>
[Trait("Category", "Unit")]
public sealed class DedupAnnouncementOrderTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly SqliteTestDatabase _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId);
    private readonly List<string> _steps = [];
    private readonly Mock<IDuplicateLinker> _linker = new();
    private readonly Mock<IRecordChangePublisher> _changes = new();

    public DedupAnnouncementOrderTests()
    {
        _changes
            .Setup(c => c.PublishAsync(It.IsAny<RecordsChanged>(), It.IsAny<CancellationToken>()))
            .Callback(() => _steps.Add("announce"))
            .Returns(Task.CompletedTask);
    }

    public void Dispose() => _db.Dispose();

    private BolusRepository Repository()
    {
        var context = _db.CreateContext();
        context.TenantId = TenantId;
        return new BolusRepository(
            new TestNocturneDbContextFactory(context), _linker.Object, Mock.Of<IAuditContext>(),
            NullLogger<BolusRepository>.Instance, _changes.Object);
    }

    private static Bolus NewBolus() => new() { Timestamp = DateTime.UtcNow, DataSource = "aaps", Insulin = 1.5 };

    [Fact]
    public async Task AnInsertIsLinkedBeforeItIsAnnounced()
    {
        _linker
            .Setup(l => l.DeduplicateBatchAsync(It.IsAny<RecordType>(), It.IsAny<IReadOnlyList<DeduplicationInput>>(), It.IsAny<CancellationToken>()))
            .Callback(() => _steps.Add("link"))
            .ReturnsAsync(new DeduplicationBatchResult(1, 1, 1, 0));

        await Repository().BulkCreateAsync([NewBolus()], WriteOrigin.Live);

        _steps.Should().StartWith("link").And.Contain("announce");
        _steps.IndexOf("announce").Should().BeGreaterThan(_steps.IndexOf("link"));
    }

    [Fact]
    public async Task ACancelledLinkStillAnnounces()
    {
        _linker
            .Setup(l => l.DeduplicateBatchAsync(It.IsAny<RecordType>(), It.IsAny<IReadOnlyList<DeduplicationInput>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var write = () => Repository().BulkCreateAsync([NewBolus()], WriteOrigin.Live);

        await write.Should().ThrowAsync<OperationCanceledException>();
        _steps.Should().Contain("announce");
    }
}
