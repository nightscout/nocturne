using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Integration.Infrastructure;

/// <summary>
/// The record changes a repository built over it publishes, which SignalR, the alert facts and the
/// caches read, the legacy entries it announces, and the readings it relays to Home Assistant.
/// </summary>
internal sealed class RecordedWrites
{
    public RecordingPublisher Changes { get; } = new();

    public RecordingEntrySink Entries { get; } = new();

    public RecordingReadingBroadcaster Readings { get; } = new();

    /// <summary>Every record a published change created, across all record types.</summary>
    public IReadOnlyList<IV4Record> Created => [.. Changes.Batches.SelectMany(b => b.Changes).SelectMany(c => c.Created)];

    public IReadOnlyList<IV4Record> Updated => [.. Changes.Batches.SelectMany(b => b.Changes).SelectMany(c => c.Updated)];

    public IReadOnlyList<IV4Record> Deleted => [.. Changes.Batches.SelectMany(b => b.Changes).SelectMany(c => c.Deleted)];

    internal sealed class RecordingPublisher : IRecordChangePublisher
    {
        public List<RecordsChanged> Batches { get; } = [];

        public Task PublishAsync(RecordsChanged batch, CancellationToken ct)
        {
            Batches.Add(batch);
            return Task.CompletedTask;
        }
    }

    internal sealed class RecordingReadingBroadcaster : IGlucoseReadingBroadcaster
    {
        public List<SensorGlucose> Relayed { get; } = [];

        public Task BroadcastAsync(IReadOnlyList<SensorGlucose> readings, CancellationToken ct = default)
        {
            Relayed.AddRange(readings);
            return Task.CompletedTask;
        }
    }

    internal sealed class RecordingEntrySink : IDataEventSink<Entry>
    {
        public List<Entry> Created { get; } = [];
        public List<Entry> Updated { get; } = [];
        public List<Entry?> Deleted { get; } = [];

        public Task OnCreatedAsync(IReadOnlyList<Entry> items, CancellationToken ct = default)
        {
            Created.AddRange(items);
            return Task.CompletedTask;
        }

        public Task OnCreatedAsync(Entry item, CancellationToken ct = default)
        {
            Created.Add(item);
            return Task.CompletedTask;
        }

        public Task OnUpdatedAsync(Entry item, CancellationToken ct = default)
        {
            Updated.Add(item);
            return Task.CompletedTask;
        }

        public Task OnDeletedAsync(Entry? item, CancellationToken ct = default)
        {
            Deleted.Add(item);
            return Task.CompletedTask;
        }
    }
}
