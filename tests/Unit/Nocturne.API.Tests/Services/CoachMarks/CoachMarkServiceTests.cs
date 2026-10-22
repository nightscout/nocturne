using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.CoachMarks;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Services.CoachMarks;

public class CoachMarkServiceTests
{
    private static readonly Guid TestSubjectId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid OtherSubjectId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly NocturneDbContext _dbContext;
    private readonly CoachMarkService _service;

    public CoachMarkServiceTests()
    {
        _dbContext = TestDbContextFactory.CreateInMemoryContext();
        _dbContext.TenantId = TestTenantId;

        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            SubjectId = TestSubjectId,
        };

        var httpContextAccessor = new HttpContextAccessor { HttpContext = httpContext };
        var logger = new Mock<ILogger<CoachMarkService>>();

        _service = new CoachMarkService(_dbContext.AsFactory(), httpContextAccessor, logger.Object);
    }

    [Fact]
    public async Task GetAllAsync_ReturnsEmpty_WhenNoMarks()
    {
        var result = await _service.GetAllAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertAsync_CreatesNewMark_WhenKeyDoesNotExist()
    {
        var result = await _service.UpsertAsync("welcome-tour", "seen");

        result.MarkKey.Should().Be("welcome-tour");
        result.Status.Should().Be("seen");
        result.SubjectId.Should().Be(TestSubjectId);
        result.Id.Should().NotBeEmpty();
    }

    [Fact]
    public async Task UpsertAsync_UpdatesExistingMark_WhenKeyExists()
    {
        // Arrange: create a mark first
        await _service.UpsertAsync("welcome-tour", "seen");

        // Act: update it
        var result = await _service.UpsertAsync("welcome-tour", "completed");

        result.MarkKey.Should().Be("welcome-tour");
        result.Status.Should().Be("completed");

        // Should still be one record, not two
        var all = await _service.GetAllAsync();
        all.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetAllAsync_ReturnsOnlyCurrentSubjectMarks()
    {
        // Arrange: seed a mark for a different subject directly in the DB
        _dbContext.CoachMarkStates.Add(new CoachMarkStateEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = OtherSubjectId,
            MarkKey = "other-user-mark",
            Status = "seen",
            SeenAt = DateTime.UtcNow,
        });
        await _dbContext.SaveChangesAsync();

        // Also create one for the current subject
        await _service.UpsertAsync("my-mark", "seen");

        // Act
        var result = await _service.GetAllAsync();

        result.Should().HaveCount(1);
        result[0].MarkKey.Should().Be("my-mark");
    }

    [Fact]
    public async Task UpsertAsync_SetsSeenAt_OnFirstSeen()
    {
        var result = await _service.UpsertAsync("welcome-tour", "seen");

        result.SeenAt.Should().NotBeNull();
        result.SeenAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UpsertAsync_SetsCompletedAt_OnFirstCompleted()
    {
        var result = await _service.UpsertAsync("welcome-tour", "completed");

        result.CompletedAt.Should().NotBeNull();
        result.CompletedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        // SeenAt should also be set since completed implies seen
        result.SeenAt.Should().NotBeNull();
    }

    [Fact]
    public async Task UpsertAsync_DoesNotOverwriteSeenAt_OnSubsequentUpdates()
    {
        // Arrange: create with "seen" status
        var first = await _service.UpsertAsync("welcome-tour", "seen");
        var originalSeenAt = first.SeenAt;

        // Small delay to ensure timestamps would differ
        await Task.Delay(50);

        // Act: update to "completed"
        var updated = await _service.UpsertAsync("welcome-tour", "completed");

        updated.SeenAt.Should().Be(originalSeenAt);
        updated.CompletedAt.Should().NotBeNull();
    }
}
