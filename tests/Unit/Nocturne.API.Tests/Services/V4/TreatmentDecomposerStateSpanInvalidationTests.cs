using Nocturne.API.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.V4;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Services.V4;

/// <summary>
/// An override or temporary target uploaded again with <c>isValid: false</c> is deleted, and the open
/// one it ended runs again.
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentDecomposerStateSpanInvalidationTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private const long At = 1_780_000_000_000;
    private const long Hour = 3_600_000;

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly TestNocturneDbContextFactory _factory;
    private readonly AuditContext _audit = new();
    private readonly IDuplicateLinker _dedup = Mock.Of<IDuplicateLinker>();
    private readonly StateSpanRepository _spans;

    public TreatmentDecomposerStateSpanInvalidationTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId);
        _context = _db.CreateContext();
        _factory = new TestNocturneDbContextFactory(_context);
        _spans = new StateSpanRepository(_factory, _dedup, _audit, NullLogger<StateSpanRepository>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    public static TheoryData<string, StateSpanCategory> EventTypes() => new()
    {
        { "Temporary Override", StateSpanCategory.Override },
        { "Temporary Target", StateSpanCategory.TemporaryTarget },
    };

    [Theory]
    [MemberData(nameof(EventTypes))]
    public async Task Invalidating_the_span_that_ended_an_open_one_reopens_it(string eventType, StateSpanCategory category)
    {
        await DecomposeAsync(Open(eventType, "earlier", At, "Running"));
        await DecomposeAsync(Open(eventType, "later", At + Hour, "Walking"));
        (await SpanAsync("earlier")).EndTimestamp.Should().NotBeNull("the later one ended it");

        var invalid = Open(eventType, "later", At + Hour, "Walking");
        invalid.IsValid = false;
        await DecomposeAsync(invalid);

        var earlier = await SpanAsync("earlier");
        earlier.EndTimestamp.Should().BeNull();
        earlier.SupersededById.Should().BeNull();
        (await _context.StateSpans.AsNoTracking().AnyAsync(s => s.LegacyId == "later"))
            .Should().BeFalse("the invalidated one is deleted");
        (await _spans.GetActiveAtAsync(category, null, DateTimeOffset.FromUnixTimeMilliseconds(At + 2 * Hour).UtcDateTime))
            .Should().NotBeNull().And.Subject.As<StateSpan>().OriginalId.Should().Be("earlier");
    }

    private static Treatment Open(string eventType, string id, long mills, string reason) => eventType == "Temporary Target"
        ? new Treatment
        {
            Id = id, EventType = eventType, Mills = mills, Reason = reason,
            TargetTop = 140, TargetBottom = 140, Units = "mg/dl", EnteredBy = "Loop",
        }
        : new Treatment
        {
            Id = id, EventType = eventType, Mills = mills, Reason = reason,
            InsulinNeedsScaleFactor = 0.8, EnteredBy = "Loop",
        };

    private Task<StateSpanEntity> SpanAsync(string legacyId) =>
        _context.StateSpans.AsNoTracking().SingleAsync(s => s.LegacyId == legacyId);

    private Task DecomposeAsync(Treatment treatment) => Decomposer().DecomposeOneAsync(treatment, WriteOrigin.Live);

    private TreatmentDecomposer Decomposer() =>
        TreatmentDecomposerDoubles.Stored(
            _factory, _dedup, _audit, stateSpans: new StateSpanService(_spans, NullLogger<StateSpanService>.Instance));
}
