using FluentAssertions;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class LabHbA1cResultRepositoryTests : IDisposable
{
    private readonly NocturneDbContext _context;
    private readonly LabHbA1cResultRepository _repository;

    public LabHbA1cResultRepositoryTests()
    {
        _context = TestDbContextFactory.CreateInMemoryContext();
        _context.TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        _repository = new LabHbA1cResultRepository(new TestNocturneDbContextFactory(_context));
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(2025, 1, 1)]
    [InlineData(2025, 3, 30)]
    [InlineData(2025, 12, 31)]
    public async Task A_day_reads_back_as_the_day_it_was_created_with(int year, int month, int day)
    {
        var measuredAt = new DateOnly(year, month, day);

        await _repository.CreateAsync(new LabHbA1cResult { MeasuredAt = measuredAt, ValuePercent = 6.5 });

        var stored = _context.LabHbA1cResults.Single();
        stored.MeasuredAt.Should().Be(new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc));
        stored.MeasuredAt.Kind.Should().Be(DateTimeKind.Utc);
        (await _repository.GetAllAsync()).Single().MeasuredAt.Should().Be(measuredAt);
    }

    [Fact]
    public async Task A_row_stored_at_utc_midnight_reads_as_that_utc_day()
    {
        _context.LabHbA1cResults.Add(new LabHbA1cResultEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _context.TenantId,
            MeasuredAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ValuePercent = 6.5,
            CreatedAt = DateTime.UtcNow,
        });
        await _context.SaveChangesAsync();

        (await _repository.GetAllAsync()).Single().MeasuredAt.Should().Be(new DateOnly(2025, 1, 1));
    }
}
