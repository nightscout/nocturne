using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Core.Models.V4;
using Xunit;
using static Nocturne.API.Tests.Services.Identity.TenantOverviewTestKit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// The overview reads its people several at a time and judges them afterwards, one at a time, in order, on the
/// request's own flow; a person whose read or judgement fails costs no one else their item, and a read that fails as a
/// whole still starts the figure reads it found missing.
/// </summary>
public partial class TenantOverviewServiceTests
{
    private const int Crowd = 12;

    private static readonly FakeTimeProvider CrowdClock = new(new DateTimeOffset(2026, 11, 28, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task The_overview_reads_people_at_once_then_judges_each_in_order_after_every_read_and_off_every_reads_flow()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var people = Enumerable.Range(0, Crowd)
            .Select(i => SeedMembership(options, subjectId, $"person-{i:00}", [Scope.GlucoseRead]))
            .ToArray();
        var mgdl = people.Select((tenant, i) => (tenant, value: 100.0 + i)).ToDictionary(p => p.tenant, p => p.value);
        var rules = new RecordingRules();
        var reads = new ReadLog(rules, mgdl);
        var fakes = new Fakes { ReadRecent = reads.ReadAsync };

        var response = await NewService(options, fakes: fakes, clock: CrowdClock, flagRules: rules)
            .GetOverviewAsync(subjectId, FullTokenScopes, AuthType.SessionCookie, OverviewRuleSet.All, PeopleQuery.Empty);

        response.Tenants.Should().HaveCount(Crowd);
        response.Tenants.Should().AllSatisfy(t => t.Latest!.Mgdl.Should().Be(mgdl[t.TenantId], "each item holds its own person's reads"));
        reads.MostAtOnce.Should().BeInRange(2, 8, "people are read several at a time, and never more than eight");
        rules.Judged.Select(j => j.Tenant).Should().Equal(rules.BegunFor, "people are judged one at a time in the order they were given");
        rules.Judged.Should().AllSatisfy(j =>
        {
            j.ReadsDone.Should().Be(Crowd, "every person is read before anyone is judged");
            j.OnAReadsFlow.Should().BeFalse("judging runs on the request's own flow, never on a person's read");
        });
        rules.MostJudgingAtOnce.Should().Be(1);
    }

    [Fact]
    public async Task A_person_whose_read_fails_and_a_person_whose_judgement_fails_cost_no_one_else_their_item()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var people = Enumerable.Range(0, Crowd)
            .Select(i => SeedMembership(options, subjectId, $"person-{i:00}", [Scope.GlucoseRead]))
            .ToArray();
        var unreadable = people[3];
        var unjudgeable = people[8];
        var fakes = new Fakes();
        foreach (var person in people.Where(p => p != unreadable))
        {
            fakes.Data[person] = new TenantData
            {
                Recent = [new SensorGlucose { Mgdl = 120, Timestamp = CrowdClock.GetUtcNow().UtcDateTime.AddMinutes(-1) }],
            };
        }
        var rules = new RecordingRules { Failing = unjudgeable };

        var response = await NewService(options, failingTenantId: unreadable, fakes: fakes, clock: CrowdClock, flagRules: rules)
            .GetOverviewAsync(subjectId, FullTokenScopes, AuthType.SessionCookie, OverviewRuleSet.All, PeopleQuery.Empty);

        response.Tenants.Select(t => t.TenantId).Should().BeEquivalentTo(people);
        var failed = new[] { unreadable, unjudgeable };
        response.Tenants.Where(t => failed.Contains(t.TenantId)).Should().AllSatisfy(t =>
        {
            t.Latest.Should().BeNull();
            t.Attention.Should().NotContain(a => a.Kind == TenantOverviewAttentionKind.FlagRule);
        });
        response.Tenants.Where(t => !failed.Contains(t.TenantId)).Should().AllSatisfy(t =>
        {
            t.Latest!.Mgdl.Should().Be(120);
            t.Attention.Should().ContainSingle(a => a.Kind == TenantOverviewAttentionKind.FlagRule);
        });
        rules.Judged.Select(j => j.Tenant).Should().NotContain(unreadable, "a person whose read failed is not judged");
    }

    [Fact]
    public async Task An_overview_cancelled_while_judging_still_starts_the_figure_reads_it_found_missing()
    {
        var subjectId = Guid.NewGuid();
        var options = NewOptions();
        var people = Enumerable.Range(0, 3)
            .Select(i => SeedMembership(options, subjectId, $"person-{i:00}", [Scope.GlucoseRead]))
            .ToArray();
        var rules = new RecordingRules { Cancelling = people[1] };

        var read = () => NewService(options, clock: CrowdClock, flagRules: rules)
            .GetOverviewAsync(subjectId, FullTokenScopes, AuthType.SessionCookie, OverviewRuleSet.All, PeopleQuery.Empty);

        await read.Should().ThrowAsync<OperationCanceledException>();
        rules.Finished.Should().BeTrue();
    }

    /// <summary>The overview's glucose reads, each slow enough that reads run at once overlap.</summary>
    private sealed class ReadLog(RecordingRules rules, IReadOnlyDictionary<Guid, double> mgdl)
    {
        private readonly Lock _gate = new();
        private int _reading;
        private int _mostAtOnce;

        public int MostAtOnce => _mostAtOnce;

        public Task<IReadOnlyList<SensorGlucose>> ReadAsync(Guid tenantId)
        {
            // Set before the read's first await, so the value stays on the flow of the read that called it.
            RecordingRules.ReadFlow.Value = true;
            return SlowReadAsync(tenantId);
        }

        private async Task<IReadOnlyList<SensorGlucose>> SlowReadAsync(Guid tenantId)
        {
            lock (_gate)
                _mostAtOnce = Math.Max(_mostAtOnce, ++_reading);
            await Task.Delay(TimeSpan.FromMilliseconds(20));
            lock (_gate)
                _reading--;
            rules.ReadDone();
            return [new SensorGlucose { Mgdl = mgdl[tenantId], Timestamp = CrowdClock.GetUtcNow().UtcDateTime.AddMinutes(-1) }];
        }
    }

    /// <summary>
    /// A run that gives everyone one reason and records, for each judgement, how many reads had finished, whether it ran
    /// on a read's flow, and how many judgements ran at once. It throws for <see cref="Failing"/>, is cancelled at
    /// <see cref="Cancelling"/>, and records whether it was finished.
    /// </summary>
    private sealed class RecordingRules : IViewerFlagRules
    {
        public static readonly AsyncLocal<bool> ReadFlow = new();

        private int _readsDone;
        private int _judging;

        public Guid? Failing { get; init; }
        public Guid? Cancelling { get; init; }
        public List<Guid> BegunFor { get; } = [];
        public List<(Guid Tenant, int ReadsDone, bool OnAReadsFlow)> Judged { get; } = [];
        public int MostJudgingAtOnce { get; private set; }
        public bool Finished { get; private set; }

        public void ReadDone() => Interlocked.Increment(ref _readsDone);

        public Task<IViewerFlagRuleRun?> BeginAsync(
            Guid subjectId, OverviewRuleSet rules, IReadOnlyList<PeopleFilter> facts, IReadOnlyList<GlucoseReadTenant> people,
            bool credentialLimitTo24Hours, CancellationToken ct)
        {
            BegunFor.AddRange(people.Select(p => p.Tenant.Id));
            return Task.FromResult<IViewerFlagRuleRun?>(new Run(this));
        }

        private sealed class Run(RecordingRules owner) : IViewerFlagRuleRun
        {
            public int PeopleWithRulesPending => 0;

            public IReadOnlyList<TenantOverviewRuleTeam> Teams => [];

            public async Task<ViewerJudgement> JudgeAsync(
                GlucoseReadTenant person, IServiceProvider personScope, SensorGlucose? latest,
                IReadOnlyList<TrackerInstance>? activeTrackers, PumpSnapshot? pump, CancellationToken ct)
            {
                var judging = Interlocked.Increment(ref owner._judging);
                owner.MostJudgingAtOnce = Math.Max(owner.MostJudgingAtOnce, judging);
                lock (owner.Judged)
                    owner.Judged.Add((person.Tenant.Id, Volatile.Read(ref owner._readsDone), ReadFlow.Value));
                await Task.Yield();
                Interlocked.Decrement(ref owner._judging);
                if (person.Tenant.Id == owner.Failing)
                    throw new InvalidOperationException("judging failed");
                if (person.Tenant.Id == owner.Cancelling)
                    throw new OperationCanceledException("the request was cancelled");
                return new ViewerJudgement([RuleReason(NotificationUrgency.Warn, null)], true);
            }

            public void Finish() => owner.Finished = true;
        }
    }
}
