using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.TestDoubles;

/// <summary>
/// <see cref="StateSpanServiceMockExtensions.BacksWith"/> ends and releases stored spans as the store
/// does, so a fold's unit tests cannot pass on a succession the store would refuse. Each sequence of
/// writes goes through the double and through the store, and every span ends where the store ends it.
/// </summary>
[Trait("Category", "Unit")]
public class StateSpanDoubleParityTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteTestDatabase _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId);

    private sealed record Write(bool Update, params StateSpan[] Spans);

    private sealed record Scenario(Write[] Writes, Dictionary<string, (DateTime? End, string? SupersededBy)> Expected);

    private static DateTime At(int minutes) => Start.AddMinutes(minutes);

    private static StateSpan Span(string id, StateSpanCategory category, string state, int startMinutes, int? endMinutes = null) => new()
    {
        Id = id,
        OriginalId = id,
        Category = category,
        State = state,
        StartTimestamp = At(startMinutes),
        EndTimestamp = endMinutes is { } end ? At(end) : null,
        Source = "test",
    };

    private static StateSpan Target(string id, int startMinutes, int? endMinutes = null) =>
        Span(id, StateSpanCategory.TemporaryTarget, "Active", startMinutes, endMinutes);

    private static StateSpan Mode(string id, int startMinutes, int? endMinutes = null) =>
        Span(id, StateSpanCategory.PumpMode, "Automatic", startMinutes, endMinutes);

    private static readonly Dictionary<string, Scenario> Scenarios = new()
    {
        ["an open span, a later span, an earlier span arriving last, and a pump mode span ended at its own start"] = new(
            [
                new(false, Target("target-a", 0)),
                new(false, Target("target-c", 60)),
                new(false, Target("target-b", 30)),
                new(false, Mode("mode-a", 0)),
                new(false, Mode("mode-b", 60)),
                new(false, Mode("mode-b", 60, 60)),
            ],
            new()
            {
                ["target-a"] = (At(30), "target-b"),
                ["target-b"] = (At(60), "target-c"),
                ["target-c"] = (null, null),
                ["mode-a"] = (null, null),
                ["mode-b"] = (At(60), null),
            }),
        ["one upsert batch moves a superseder ahead of the span it ended"] = new(
            [
                new(false, Target("target-a", 0)),
                new(false, Target("target-s", 60)),
                new(false, Target("target-s", 30), Target("target-a", 0, 60)),
            ],
            new()
            {
                ["target-a"] = (At(30), "target-s"),
                ["target-s"] = (null, null),
            }),
        ["one update batch moves a superseder ahead of the span it ended"] = new(
            [
                new(false, Target("target-a", 0)),
                new(false, Target("target-c", 60)),
                new(true, Target("target-a", 0), Target("target-c", 30)),
            ],
            new()
            {
                ["target-a"] = (At(30), "target-c"),
                ["target-c"] = (null, null),
            }),
    };

    public static TheoryData<string> ScenarioNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in Scenarios.Keys)
        {
            data.Add(name);
        }

        return data;
    }

    private static Dictionary<string, (DateTime? End, string? SupersededBy)> Outcome(IEnumerable<StateSpan> spans)
    {
        var all = spans.ToList();
        return all.ToDictionary(
            s => s.OriginalId!,
            s => (s.EndTimestamp, SupersededBy: all.FirstOrDefault(o => RecordIdKey.ParseIdToGuid(o.Id).ToString() == s.SupersededById)?.OriginalId));
    }

    private async Task<Dictionary<string, (DateTime? End, string? SupersededBy)>> InTheStore(Write[] writes)
    {
        var repository = new StateSpanRepository(
            _db.CreateContext().AsFactory(), Mock.Of<IDuplicateLinker>(), Mock.Of<IAuditContext>(), Mock.Of<ILogger<StateSpanRepository>>());
        foreach (var write in writes)
        {
            if (write.Update)
            {
                await repository.UpdateStateSpansAsync(write.Spans);
            }
            else
            {
                await repository.BulkUpsertAsync(write.Spans);
            }
        }

        await using var context = _db.CreateContext();
        return Outcome(context.StateSpans.AsEnumerable().Select(StateSpanMapper.ToDomainModel));
    }

    private static async Task<Dictionary<string, (DateTime? End, string? SupersededBy)>> InTheDouble(Write[] writes)
    {
        var service = new Mock<IStateSpanService>();
        var rows = service.BacksWith([]);
        foreach (var write in writes)
        {
            if (write.Update)
            {
                await service.Object.UpdateStateSpansAsync(write.Spans);
            }
            else
            {
                await service.Object.UpsertStateSpansAsync(write.Spans);
            }
        }

        return Outcome(rows);
    }

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public async Task TheDoubleEndsAndReleasesEverySpanAsTheStoreDoes(string name)
    {
        var store = await InTheStore(Scenarios[name].Writes);
        var fake = await InTheDouble(Scenarios[name].Writes);

        fake.Should().BeEquivalentTo(store);
    }

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public async Task TheStoreEndsAndReleasesTheSequenceAsTheRuleSays(string name)
    {
        var store = await InTheStore(Scenarios[name].Writes);

        store.Should().BeEquivalentTo(Scenarios[name].Expected);
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }
}
