using Nocturne.Core.Contracts.Events;

namespace Nocturne.API.Tests.TestDoubles;

/// <summary>Records every batch the publisher hands its subscribers, beside the ones the API wires.</summary>
internal sealed class RecordingRecordChangeSubscriber : IRecordChangeSubscriber
{
    public List<RecordsChanged> Batches { get; } = [];

    public RecordChangeStage Stage => RecordChangeStage.HandOff;

    public Task OnChangedAsync(RecordsChanged batch, CancellationToken ct)
    {
        Batches.Add(batch);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The one batch published, which names <paramref name="recordTypes"/> and no record: a type-level
    /// announcement, which no client is sent.
    /// </summary>
    public void ShouldHaveAnnouncedOnly(params Type[] recordTypes)
    {
        var batch = Batches.Should().ContainSingle().Subject;
        batch.Changes.Select(c => c.RecordType).Should().BeEquivalentTo(recordTypes);
        batch.Changes.Should().OnlyContain(c => !c.HasRecords && c.Origin == Core.Contracts.V4.WriteOrigin.Backfill);
    }
}
