using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
[Trait("Category", "SensorGlucose")]
public class SensorGlucoseRepositoryBaseReadTests : IDisposable
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly SensorGlucoseRepository _repo;

    public SensorGlucoseRepositoryBaseReadTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TestTenantId);
        _context = _db.CreateContext();
        _repo = new SensorGlucoseRepository(
            new TestNocturneDbContextFactory(_context),
            new Mock<IDeduplicationService>().Object,
            new Mock<IAuditContext>().Object,
            NullLogger<SensorGlucoseRepository>.Instance,
            TimeProvider.System);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task TheBaseRepositoryReadServesAReadingAClientUploaded()
    {
        var at = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        await _repo.CreateAsync(
            new SensorGlucose { Mgdl = 120, Timestamp = at, DataSource = "test-source", LegacyId = "legacy-1" },
            WriteOrigin.Live);

        var served = await ((IV4Repository<SensorGlucose>)_repo)
            .GetAsync(at.AddHours(-1), at.AddHours(1), null, null, 10, 0, true, CancellationToken.None);

        served.Should().ContainSingle().Which.LegacyId.Should().Be("legacy-1");
    }
}
