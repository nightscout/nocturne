using FluentAssertions;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

[Trait("Category", "Unit")]
public class RecordChangePublisherTests
{
    private static readonly RecordsChanged Batch =
        new([new RecordTypeChange(typeof(Bolus), WriteOrigin.Live, [new Bolus()], [], [])]);

    private readonly List<string> _ran = [];

    [Fact]
    public async Task Every_invalidation_runs_before_any_announcement_whatever_the_registration_order()
    {
        var publisher = new RecordChangePublisher(
        [
            new Subscriber("announce-1", RecordChangeStage.Announce, _ran),
            new Subscriber("invalidate-1", RecordChangeStage.Invalidate, _ran),
            new Subscriber("announce-2", RecordChangeStage.Announce, _ran),
            new Subscriber("invalidate-2", RecordChangeStage.Invalidate, _ran),
        ]);

        await publisher.PublishAsync(Batch, CancellationToken.None);

        _ran.Should().Equal("invalidate-1", "invalidate-2", "announce-1", "announce-2");
    }

    [Fact]
    public async Task A_hand_off_runs_after_the_invalidations_and_before_an_announcement_that_throws()
    {
        var publisher = new RecordChangePublisher(
        [
            new Subscriber(
                "announce", RecordChangeStage.Announce, _ran,
                onChanged: _ => throw new InvalidOperationException("the hub is down")),
            new Subscriber("hand-off", RecordChangeStage.HandOff, _ran),
            new Subscriber("invalidate", RecordChangeStage.Invalidate, _ran),
        ]);

        var publish = () => publisher.PublishAsync(Batch, CancellationToken.None);

        await publish.Should().ThrowAsync<InvalidOperationException>();
        _ran.Should().Equal("invalidate", "hand-off", "announce");
    }

    [Fact]
    public async Task Every_subscriber_receives_the_batch_as_published()
    {
        RecordsChanged? received = null;
        var publisher = new RecordChangePublisher(
            [new Subscriber("one", RecordChangeStage.Announce, _ran, onChanged: batch => received = batch)]);

        await publisher.PublishAsync(Batch, CancellationToken.None);

        received.Should().BeSameAs(Batch);
    }

    [Fact]
    public async Task A_failing_subscriber_faults_the_publish_and_the_later_ones_do_not_run()
    {
        var publisher = new RecordChangePublisher(
        [
            new Subscriber("announce", RecordChangeStage.Announce, _ran),
            new Subscriber(
                "invalidate", RecordChangeStage.Invalidate, _ran,
                onChanged: _ => throw new OperationCanceledException("the client went away")),
        ]);

        var publish = () => publisher.PublishAsync(Batch, CancellationToken.None);

        await publish.Should().ThrowAsync<OperationCanceledException>();
        _ran.Should().Equal("invalidate");
    }

    [Fact]
    public async Task With_no_subscribers_a_publish_does_nothing()
    {
        var publish = () => new RecordChangePublisher([]).PublishAsync(Batch, CancellationToken.None);

        await publish.Should().NotThrowAsync();
    }

    private sealed class Subscriber(
        string name, RecordChangeStage stage, List<string> ran, Action<RecordsChanged>? onChanged = null)
        : IRecordChangeSubscriber
    {
        public RecordChangeStage Stage => stage;

        public Task OnChangedAsync(RecordsChanged batch, CancellationToken ct)
        {
            ran.Add(name);
            onChanged?.Invoke(batch);
            return Task.CompletedTask;
        }
    }
}
