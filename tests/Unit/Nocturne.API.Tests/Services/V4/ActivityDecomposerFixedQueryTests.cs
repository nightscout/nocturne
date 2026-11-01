using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.V4;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.V4;

/// <summary>
/// An activity batch opens the same contexts and makes the same span-store calls whatever its size.
/// </summary>
/// <remarks>
/// Heart rates and step counts are read and written through one context, whose statements a
/// mock-level count cannot see; they are counted against PostgreSQL in
/// <c>ActivityBatchStatementsTests</c>.
/// </remarks>
public class ActivityDecomposerFixedQueryTests : IDisposable
{
    private const long Start = 1_780_000_000_000;

    private readonly NocturneDbContext _context = TestDbContextFactory.CreateInMemoryContext();
    private readonly Mock<IStateSpanRepository> _spans = new();
    private int _contextOpens;

    public ActivityDecomposerFixedQueryTests()
    {
        _context.TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        _spans.Setup(s => s.CreateActivitiesAsStateSpansAsync(It.IsAny<IEnumerable<StateSpan>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<StateSpan> spans, CancellationToken _) => spans);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ABatchOfFiftyCostsWhatABatchOfOneCosts()
    {
        var one = await CallsAsync(Batch(4));
        var fifty = await CallsAsync(Batch(50));

        fifty.Should().Be(one, "a batch of activities reads and writes each store a fixed number of times");
        one.Should().BeGreaterThan(0);
    }

    private static List<Activity> Batch(int size) =>
    [
        .. Enumerable.Range(0, size).Select(i => (i % 4) switch
        {
            0 => Of($"5a00{i:x4}0000000000000000", "heartrate", i, "bpm", 70),
            1 => Of(null, "hr-bpm", i, "bpm", 71),
            2 => Of($"5a00{i:x4}0000000000000000", "steps", i, "metric", 300),
            _ => new Activity { Type = "exercise", Mills = Start + i * 60_000L, Duration = 30 },
        }),
    ];

    private static Activity Of(string? id, string type, int i, string key, int value) => new()
    {
        Id = id,
        Type = type,
        Mills = Start + i * 60_000L,
        AdditionalProperties = new() { [key] = JsonSerializer.SerializeToElement(value) },
    };

    private async Task<int> CallsAsync(IReadOnlyList<Activity> activities)
    {
        _spans.Invocations.Clear();
        _contextOpens = 0;
        var decomposer = new ActivityDecomposer(
            new CountingContexts(new TestNocturneDbContextFactory(_context), () => _contextOpens++),
            _spans.Object, TimeProvider.System, NullLogger<ActivityDecomposer>.Instance);
        await decomposer.DecomposeBatchAsync(activities, WriteOrigin.Live);
        return _spans.Invocations.Count + _contextOpens;
    }
}
