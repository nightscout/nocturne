using Nocturne.Core.Contracts.Treatments;

namespace Nocturne.API.Tests.TestDoubles;

/// <summary>An <see cref="IWriteBackSentRecords"/> under which upstream never accepted a send of any record.</summary>
internal sealed class NeverSentUpstream : IWriteBackSentRecords
{
    public Task RecordSentAsync(IReadOnlyList<WriteBackSentRecord> records, CancellationToken ct = default) => Task.CompletedTask;

    public Task RecordSendingAsync(IReadOnlyList<WriteBackSentRecord> records, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlySet<string>> GetSentUpstreamAsync(IReadOnlyCollection<string> legacyIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());

    public Task<IReadOnlySet<Guid>> GetSentRowsAsync(IReadOnlyCollection<Guid> rowIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
}
