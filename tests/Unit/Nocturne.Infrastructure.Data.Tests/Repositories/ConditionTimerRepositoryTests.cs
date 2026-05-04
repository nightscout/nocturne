using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class ConditionTimerRepositoryTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly NocturneDbContext _context;
    private readonly ConditionTimerRepository _repository;

    public ConditionTimerRepositoryTests()
    {
        _context = TestDbContextFactory.CreateInMemoryContext();
        _context.TenantId = TenantId;
        _repository = new ConditionTimerRepository(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetFirstTrue_returns_null_when_no_row()
    {
        var ruleId = Guid.NewGuid();

        var result = await _repository.GetFirstTrueAsync(ruleId, "composite[0].sustained", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Set_then_Get_round_trips_utc()
    {
        var ruleId = Guid.NewGuid();
        var path = "composite[0].sustained";
        var at = new DateTime(2026, 4, 30, 12, 34, 56, DateTimeKind.Utc);

        await _repository.SetFirstTrueAsync(ruleId, path, at, CancellationToken.None);

        var result = await _repository.GetFirstTrueAsync(ruleId, path, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Value.Ticks.Should().Be(at.Ticks);
    }

    [Fact]
    public async Task Set_overwrites_existing_first_true_for_same_path()
    {
        var ruleId = Guid.NewGuid();
        var path = "composite[0].sustained";
        var first = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);
        var second = new DateTime(2026, 4, 30, 13, 0, 0, DateTimeKind.Utc);

        await _repository.SetFirstTrueAsync(ruleId, path, first, CancellationToken.None);
        await _repository.SetFirstTrueAsync(ruleId, path, second, CancellationToken.None);

        var result = await _repository.GetFirstTrueAsync(ruleId, path, CancellationToken.None);

        result!.Value.Ticks.Should().Be(second.Ticks);
        (await _context.AlertConditionTimers.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Clear_removes_only_the_targeted_path()
    {
        var ruleId = Guid.NewGuid();
        var at = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);
        await _repository.SetFirstTrueAsync(ruleId, "a", at, CancellationToken.None);
        await _repository.SetFirstTrueAsync(ruleId, "b", at, CancellationToken.None);

        await _repository.ClearAsync(ruleId, "a", CancellationToken.None);

        (await _repository.GetFirstTrueAsync(ruleId, "a", CancellationToken.None)).Should().BeNull();
        (await _repository.GetFirstTrueAsync(ruleId, "b", CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task ClearAllForRule_removes_all()
    {
        var ruleId = Guid.NewGuid();
        var otherRuleId = Guid.NewGuid();
        var at = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);
        await _repository.SetFirstTrueAsync(ruleId, "a", at, CancellationToken.None);
        await _repository.SetFirstTrueAsync(ruleId, "b", at, CancellationToken.None);
        await _repository.SetFirstTrueAsync(otherRuleId, "c", at, CancellationToken.None);

        await _repository.ClearAllForRuleAsync(ruleId, CancellationToken.None);

        (await _repository.GetFirstTrueAsync(ruleId, "a", CancellationToken.None)).Should().BeNull();
        (await _repository.GetFirstTrueAsync(ruleId, "b", CancellationToken.None)).Should().BeNull();
        (await _repository.GetFirstTrueAsync(otherRuleId, "c", CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task PruneToPaths_keeps_listed_paths_only()
    {
        var ruleId = Guid.NewGuid();
        var otherRuleId = Guid.NewGuid();
        var at = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);
        await _repository.SetFirstTrueAsync(ruleId, "a", at, CancellationToken.None);
        await _repository.SetFirstTrueAsync(ruleId, "b", at, CancellationToken.None);
        await _repository.SetFirstTrueAsync(ruleId, "c", at, CancellationToken.None);
        await _repository.SetFirstTrueAsync(otherRuleId, "a", at, CancellationToken.None);

        await _repository.PruneToPathsAsync(ruleId, new[] { "b", "c" }, CancellationToken.None);

        (await _repository.GetFirstTrueAsync(ruleId, "a", CancellationToken.None)).Should().BeNull();
        (await _repository.GetFirstTrueAsync(ruleId, "b", CancellationToken.None)).Should().NotBeNull();
        (await _repository.GetFirstTrueAsync(ruleId, "c", CancellationToken.None)).Should().NotBeNull();
        (await _repository.GetFirstTrueAsync(otherRuleId, "a", CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task PruneToPaths_with_empty_list_clears_all_for_rule()
    {
        var ruleId = Guid.NewGuid();
        var otherRuleId = Guid.NewGuid();
        var at = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);
        await _repository.SetFirstTrueAsync(ruleId, "a", at, CancellationToken.None);
        await _repository.SetFirstTrueAsync(ruleId, "b", at, CancellationToken.None);
        await _repository.SetFirstTrueAsync(otherRuleId, "a", at, CancellationToken.None);

        await _repository.PruneToPathsAsync(ruleId, Array.Empty<string>(), CancellationToken.None);

        (await _repository.GetFirstTrueAsync(ruleId, "a", CancellationToken.None)).Should().BeNull();
        (await _repository.GetFirstTrueAsync(ruleId, "b", CancellationToken.None)).Should().BeNull();
        (await _repository.GetFirstTrueAsync(otherRuleId, "a", CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task Set_persists_tenant_id_from_context()
    {
        var ruleId = Guid.NewGuid();
        var at = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);

        await _repository.SetFirstTrueAsync(ruleId, "a", at, CancellationToken.None);

        var row = await _context.AlertConditionTimers.SingleAsync();
        row.TenantId.Should().Be(TenantId);
        row.AlertRuleId.Should().Be(ruleId);
        row.ConditionPath.Should().Be("a");
    }
}
