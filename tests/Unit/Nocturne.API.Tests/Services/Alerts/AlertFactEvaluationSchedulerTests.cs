using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// Writes reach evaluation through <see cref="AlertFactsChangedSubscriber"/> and
/// <see cref="AlertFactEvaluationScheduler"/>: after the write, per tenant, debounced with a maximum
/// wait, glucose with no delay, and no change lost to an evaluation already running. Time is a
/// <see cref="FakeTimeProvider"/>; the only real wait is the guard on an evaluation that never comes.
/// A count is read only once no fact is pending, when no further evaluation can start.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AlertFactEvaluationSchedulerTests : IDisposable
{
    private static readonly TenantContext TenantA = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), "a", "A", true, false);
    private static readonly TenantContext TenantB = new(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"), "b", "B", true, false);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
    private readonly RecordingEvaluator _evaluator = new();
    private readonly ServiceProvider _services;
    private readonly AlertFactEvaluationScheduler _scheduler;

    public AlertFactEvaluationSchedulerTests()
    {
        var services = new ServiceCollection();
        services.AddScoped<ITenantAccessor, HttpContextTenantAccessor>();
        services.AddScoped<ICanonicalAlertEvaluator>(sp => new ScopedEvaluator(_evaluator, sp.GetRequiredService<ITenantAccessor>()));
        _services = services.BuildServiceProvider();
        _evaluator.Clock = _time;
        _scheduler = new AlertFactEvaluationScheduler(
            _services.GetRequiredService<IServiceScopeFactory>(), _time, NullLogger<AlertFactEvaluationScheduler>.Instance);
    }

    public void Dispose()
    {
        _scheduler.Dispose();
        _services.Dispose();
    }

    private sealed record Evaluation(
        Guid TenantId, IReadOnlySet<string> Facts, IReadOnlySet<Guid> Rules, DateTimeOffset At, string? Ambient);

    private static readonly AsyncLocal<string?> RequestAmbient = new();

    private sealed class RecordingEvaluator
    {
        private readonly Channel<Evaluation> _started = Channel.CreateUnbounded<Evaluation>();
        public List<Evaluation> Started { get; } = [];
        public TaskCompletionSource? Hold { get; set; }
        public FakeTimeProvider? Clock { get; set; }
        public int Finished;
        public int Cancelled;

        /// <summary>Stands for an evaluation pass; <see cref="Hold"/> keeps it inside its delivery.</summary>
        public async Task RunAsync(Guid tenantId, MovedFacts moved, CancellationToken ct)
        {
            var evaluation = new Evaluation(tenantId, moved.Facts, moved.Rules, Clock!.GetUtcNow(), RequestAmbient.Value);
            lock (Started) Started.Add(evaluation);
            _started.Writer.TryWrite(evaluation);
            try
            {
                if (Hold is { } hold)
                    await hold.Task.WaitAsync(ct);
                Interlocked.Increment(ref Finished);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref Cancelled);
                throw;
            }
        }

        public async Task<Evaluation> NextAsync() => await _started.Reader.ReadAsync().AsTask().WaitAsync(Eventually.DefaultPatience);

        public int Count
        {
            get { lock (Started) return Started.Count; }
        }
    }

    private sealed class ScopedEvaluator(RecordingEvaluator recording, ITenantAccessor tenant) : ICanonicalAlertEvaluator
    {
        public async Task<bool> EvaluateAsync(
            MovedFacts moved, ReadingReport report = ReadingReport.Here, CancellationToken ct = default)
        {
            await recording.RunAsync(tenant.TenantId, moved, ct);
            return true;
        }
    }

    private static RecordsChanged Change(Type type, IReadOnlyList<IV4Record>? created = null, IReadOnlyList<IV4Record>? updated = null, IReadOnlyList<IV4Record>? deleted = null) =>
        new([new RecordTypeChange(type, WriteOrigin.Live, created ?? [], updated ?? [], deleted ?? [])]);

    private Task WriteAsync(TenantContext tenant, RecordsChanged batch)
    {
        var accessor = new HttpContextTenantAccessor();
        accessor.SetTenant(tenant);
        return new AlertFactsChangedSubscriber(accessor, _scheduler).OnChangedAsync(batch, CancellationToken.None);
    }

    [Fact]
    public async Task AGlucoseWriteEvaluatesWithNoDelay()
    {
        var before = _time.GetUtcNow();

        await WriteAsync(TenantA, Change(typeof(SensorGlucose), created: [new SensorGlucose { Mgdl = 50 }]));

        var evaluation = await _evaluator.NextAsync();
        evaluation.At.Should().Be(before, "an urgent low waits for no debounce: the fake clock never moved");
        evaluation.Facts.Should().Contain(ContextFactWrites.Glucose);
        evaluation.TenantId.Should().Be(TenantA.TenantId, "the evaluation scope is pinned to the write's tenant");
    }

    [Fact]
    public async Task AGlucoseWriteIsNotHeldBehindADebouncedEvaluationThatIsRunning()
    {
        _evaluator.Hold = new TaskCompletionSource();
        await WriteAsync(TenantA, Change(typeof(PumpSnapshot)));
        _time.Advance(AlertFactEvaluationScheduler.Debounce);
        (await _evaluator.NextAsync()).Facts.Should().NotContain(ContextFactWrites.Glucose);
        var landed = _time.GetUtcNow();

        await WriteAsync(TenantA, Change(typeof(SensorGlucose), created: [new SensorGlucose { Mgdl = 50 }]));

        var glucose = await _evaluator.NextAsync();
        glucose.Facts.Should().Contain(ContextFactWrites.Glucose);
        glucose.At.Should().Be(landed);
        _evaluator.Hold!.SetResult();
    }

    [Fact]
    public async Task AGlucoseWriteDuringARunningGlucoseEvaluationStartsAtOnce()
    {
        _evaluator.Hold = new TaskCompletionSource();
        await WriteAsync(TenantA, Change(typeof(SensorGlucose)));
        await _evaluator.NextAsync();
        var landed = _time.GetUtcNow();

        await WriteAsync(TenantA, Change(typeof(SensorGlucose)));

        var second = await _evaluator.NextAsync();
        second.At.Should().Be(landed, "a reading never waits on another evaluation's delivery");
        second.Facts.Should().Contain(ContextFactWrites.Glucose);
        _evaluator.Hold.SetResult();
    }

    [Fact]
    public async Task AnEvaluationReadsNoneOfTheWritingRequestsExecutionContext()
    {
        RequestAmbient.Value = "request";

        await WriteAsync(TenantA, Change(typeof(SensorGlucose)));
        var glucose = await _evaluator.NextAsync();
        await WriteAsync(TenantA, Change(typeof(PumpSnapshot)));
        _time.Advance(AlertFactEvaluationScheduler.Debounce);
        var debounced = await _evaluator.NextAsync();

        glucose.Ambient.Should().BeNull();
        debounced.Ambient.Should().BeNull();
    }

    [Fact]
    public async Task StoppingStartsPendingWorkAtOnceAndWaitsForIt()
    {
        await WriteAsync(TenantA, Change(typeof(PumpSnapshot)));
        var before = _time.GetUtcNow();
        _evaluator.Hold = new TaskCompletionSource();

        var stopping = _scheduler.StoppedAsync(CancellationToken.None);

        var evaluation = await _evaluator.NextAsync();
        evaluation.At.Should().Be(before, "a stopping host waits for no debounce");
        stopping.IsCompleted.Should().BeFalse("the host waits for the running evaluation");

        _evaluator.Hold.SetResult();
        await stopping.WaitAsync(Eventually.DefaultPatience);
        _evaluator.Finished.Should().Be(1);
    }

    [Fact]
    public async Task StoppingWaitsForARunningEvaluation()
    {
        _evaluator.Hold = new TaskCompletionSource();
        await WriteAsync(TenantA, Change(typeof(SensorGlucose)));
        await _evaluator.NextAsync();

        var stopping = _scheduler.StoppedAsync(CancellationToken.None);
        stopping.IsCompleted.Should().BeFalse();

        _evaluator.Hold.SetResult();
        await stopping.WaitAsync(Eventually.DefaultPatience);
        _evaluator.Finished.Should().Be(1);
        _evaluator.Cancelled.Should().Be(0, "stopping alone cancels nothing");
    }

    [Fact]
    public async Task TheShutdownTimeoutAbandonsWhatIsStillRunning()
    {
        _evaluator.Hold = new TaskCompletionSource();
        await WriteAsync(TenantA, Change(typeof(SensorGlucose)));
        await _evaluator.NextAsync();
        using var timeout = new CancellationTokenSource();

        var stopping = _scheduler.StoppedAsync(timeout.Token);
        timeout.Cancel();

        await stopping.WaitAsync(Eventually.DefaultPatience);
        await Eventually.UntilAsync("the running evaluation to be cancelled", () => Volatile.Read(ref _evaluator.Cancelled) == 1);
        _evaluator.Finished.Should().Be(0);
    }

    [Fact]
    public async Task ABatchOf200TreatmentsIsOneEvaluation()
    {
        for (var i = 0; i < 200; i++)
            await WriteAsync(TenantA, Change(typeof(Bolus), created: [new Bolus()]));

        _time.Advance(AlertFactEvaluationScheduler.Debounce);
        var evaluation = await _evaluator.NextAsync();
        _time.Advance(TimeSpan.FromMinutes(1));

        _evaluator.Count.Should().Be(1);
        evaluation.Facts.Should().BeEquivalentTo(ContextFactWrites.MovedBy[typeof(Bolus)]);
    }

    [Fact]
    public async Task TheDebounceIsATrailingEdge()
    {
        var first = _time.GetUtcNow();
        await WriteAsync(TenantA, Change(typeof(ApsSnapshot)));
        _time.Advance(TimeSpan.FromMilliseconds(600));
        await WriteAsync(TenantA, Change(typeof(UploaderSnapshot)));

        _time.Advance(TimeSpan.FromMilliseconds(400));
        _evaluator.Count.Should().Be(0, "a change 600 ms in pushed the evaluation to a second after it");

        _time.Advance(TimeSpan.FromMilliseconds(600));
        var evaluation = await _evaluator.NextAsync();
        evaluation.At.Should().Be(first + TimeSpan.FromMilliseconds(600) + AlertFactEvaluationScheduler.Debounce);
        evaluation.Facts.Should().Contain([ContextFactWrites.Aps, ContextFactWrites.Uploader]);
    }

    [Fact]
    public async Task AStreamOfChangesCannotPostponeEvaluationPastTheMaximumWait()
    {
        var first = _time.GetUtcNow();
        var step = AlertFactEvaluationScheduler.Debounce / 2;
        while (_time.GetUtcNow() - first < AlertFactEvaluationScheduler.MaxWait - step)
        {
            await WriteAsync(TenantA, Change(typeof(PumpSnapshot)));
            _time.Advance(step);
            _evaluator.Count.Should().Be(0);
        }

        await WriteAsync(TenantA, Change(typeof(PumpSnapshot)));
        _time.Advance(step);

        (await _evaluator.NextAsync()).At.Should().Be(first + AlertFactEvaluationScheduler.MaxWait);
    }

    [Fact]
    public async Task AChangeDuringARunningEvaluationRunsOnceMore()
    {
        _evaluator.Hold = new TaskCompletionSource();
        await WriteAsync(TenantA, Change(typeof(PumpSnapshot)));
        _time.Advance(AlertFactEvaluationScheduler.Debounce);
        (await _evaluator.NextAsync()).Facts.Should().Contain(ContextFactWrites.Pump);

        await WriteAsync(TenantA, Change(typeof(ApsSnapshot)));
        _time.Advance(AlertFactEvaluationScheduler.MaxWait);
        _evaluator.Count.Should().Be(1, "a debounced evaluation waits for the running one");

        var hold = _evaluator.Hold;
        _evaluator.Hold = null;
        hold!.SetResult();

        (await _evaluator.NextAsync()).Facts.Should().Contain(ContextFactWrites.Aps).And.NotContain(ContextFactWrites.Pump);
        _evaluator.Count.Should().Be(2);
    }

    [Fact]
    public async Task TenantsAreDebouncedApartAndEachEvaluatesInItsOwnScope()
    {
        await WriteAsync(TenantA, Change(typeof(PumpSnapshot)));
        _time.Advance(TimeSpan.FromMilliseconds(500));
        await WriteAsync(TenantB, Change(typeof(ApsSnapshot)));

        _time.Advance(TimeSpan.FromMilliseconds(500));
        (await _evaluator.NextAsync()).TenantId.Should().Be(TenantA.TenantId);
        _time.Advance(TimeSpan.FromMilliseconds(500));
        var b = await _evaluator.NextAsync();

        b.TenantId.Should().Be(TenantB.TenantId);
        b.Facts.Should().Contain(ContextFactWrites.Aps).And.NotContain(ContextFactWrites.Pump);
    }

    [Fact]
    public async Task AWriteThatMovesNoFactEvaluatesNothing()
    {
        await WriteAsync(TenantA, Change(typeof(Note), created: [new Note()]));

        _time.Advance(AlertFactEvaluationScheduler.MaxWait);

        _evaluator.Count.Should().Be(0);
    }

    [Fact]
    public async Task DisposingDropsPendingWork()
    {
        await WriteAsync(TenantA, Change(typeof(PumpSnapshot)));

        _scheduler.Dispose();
        _time.Advance(AlertFactEvaluationScheduler.MaxWait);
        await WriteAsync(TenantA, Change(typeof(SensorGlucose)));

        _evaluator.Count.Should().Be(0);
    }

    [Fact]
    public async Task ASavedRuleIsEvaluatedAfterTheDebounceWithNoFactMoved()
    {
        var rule = Guid.CreateVersion7();
        var saved = _time.GetUtcNow();

        _scheduler.Moved(TenantA, MovedFacts.Saved([rule]));
        _evaluator.Count.Should().Be(0);
        _time.Advance(AlertFactEvaluationScheduler.Debounce);

        var evaluation = await _evaluator.NextAsync();
        evaluation.At.Should().Be(saved + AlertFactEvaluationScheduler.Debounce);
        evaluation.Rules.Should().Equal(rule);
        evaluation.Facts.Should().BeEmpty();
        evaluation.TenantId.Should().Be(TenantA.TenantId);
    }

    [Fact]
    public async Task SavedRulesAndWritesPendingTogetherAreOneEvaluation()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        _scheduler.Moved(TenantA, MovedFacts.Saved([first]));
        await WriteAsync(TenantA, Change(typeof(PumpSnapshot)));
        _scheduler.Moved(TenantA, MovedFacts.Saved([second]));
        _time.Advance(AlertFactEvaluationScheduler.Debounce);
        var evaluation = await _evaluator.NextAsync();
        _time.Advance(TimeSpan.FromMinutes(1));

        _evaluator.Count.Should().Be(1);
        evaluation.Rules.Should().BeEquivalentTo([first, second]);
        evaluation.Facts.Should().Contain(ContextFactWrites.Pump);
    }

    [Fact]
    public async Task ASavedRuleJoinsAGlucoseEvaluationThatHasNotBegun()
    {
        var rule = Guid.CreateVersion7();
        var before = _time.GetUtcNow();
        _scheduler.Moved(TenantA, MovedFacts.Saved([rule]));

        await WriteAsync(TenantA, Change(typeof(SensorGlucose)));

        var evaluation = await _evaluator.NextAsync();
        evaluation.At.Should().Be(before, "the reading waits for no debounce and takes the save with it");
        evaluation.Rules.Should().Equal(rule);
        evaluation.Facts.Should().Contain(ContextFactWrites.Glucose);
    }

    [Fact]
    public async Task StoppingStartsAPendingRuleEvaluationAtOnce()
    {
        var rule = Guid.CreateVersion7();
        var saved = _time.GetUtcNow();
        _scheduler.Moved(TenantA, MovedFacts.Saved([rule]));

        await _scheduler.StoppedAsync(CancellationToken.None).WaitAsync(Eventually.DefaultPatience);

        var evaluation = await _evaluator.NextAsync();
        evaluation.At.Should().Be(saved, "a stopping host waits for no debounce");
        evaluation.Rules.Should().Equal(rule);
        _evaluator.Finished.Should().Be(1);
    }

    [Fact]
    public async Task ASaveOfNoRuleEvaluatesNothing()
    {
        _scheduler.Moved(TenantA, MovedFacts.Saved([]));

        _time.Advance(AlertFactEvaluationScheduler.MaxWait);

        _evaluator.Count.Should().Be(0);
    }

    public static TheoryData<string> GlucoseChanges => new() { "created", "updated", "deleted", "restored", "type only" };

    [Theory]
    [MemberData(nameof(GlucoseChanges))]
    public async Task EveryKindOfGlucoseChangeEvaluates(string kind)
    {
        IV4Record reading = new SensorGlucose { Mgdl = 90 };
        var batch = kind switch
        {
            "created" or "restored" => Change(typeof(SensorGlucose), created: [reading]),
            "updated" => Change(typeof(SensorGlucose), updated: [reading]),
            "deleted" => Change(typeof(SensorGlucose), deleted: [reading]),
            _ => RecordsChanged.Written([typeof(SensorGlucose)]),
        };

        await WriteAsync(TenantA, batch);

        (await _evaluator.NextAsync()).Facts.Should().Contain(ContextFactWrites.Glucose);
    }

    public static TheoryData<Type> MappedRecordTypes()
    {
        var data = new TheoryData<Type>();
        foreach (var type in ContextFactWrites.MovedBy.Where(e => e.Value.Length > 0).Select(e => e.Key))
            data.Add(type);
        return data;
    }

    [Theory]
    [MemberData(nameof(MappedRecordTypes))]
    public async Task AWriteOfEachRecordTypeEvaluatesTheFactsItMovesOnce(Type recordType)
    {
        await WriteAsync(TenantA, Change(recordType));

        _time.Advance(AlertFactEvaluationScheduler.Debounce);
        var evaluation = await _evaluator.NextAsync();

        evaluation.Facts.Should().BeEquivalentTo(ContextFactWrites.MovedBy[recordType]);
        _evaluator.Count.Should().Be(1);
    }
}
