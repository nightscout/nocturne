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
        _repository = new ConditionTimerRepository(_context.AsFactory());
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<DateTime?> FirstTrueAsync(Guid ruleId, string path) =>
        (await _repository.GetAllForRuleAsync(ruleId, CancellationToken.None)).FirstTrue.TryGetValue(path, out var at)
            ? at
            : null;

    private async Task<DateTime?> NotTrueAsync(Guid ruleId, string path) =>
        (await _repository.GetAllForRuleAsync(ruleId, CancellationToken.None)).NotTrue.TryGetValue(path, out var at)
            ? at
            : null;

    [Fact]
    public async Task GetAll_returns_nothing_when_no_row()
    {
        var ruleId = Guid.NewGuid();

        var result = await _repository.GetAllForRuleAsync(ruleId, CancellationToken.None);

        result.FirstTrue.Should().BeEmpty();
        result.NotTrue.Should().BeEmpty();
    }

    [Fact]
    public async Task Set_then_Get_round_trips_utc()
    {
        var ruleId = Guid.NewGuid();
        var path = "composite[0].sustained";
        var at = new DateTime(2026, 4, 30, 12, 34, 56, DateTimeKind.Utc);

        await _repository.SetFirstTrueAsync(ruleId, path, at, CancellationToken.None);

        var result = await FirstTrueAsync(ruleId, path);

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

        var result = await FirstTrueAsync(ruleId, path);

        result!.Value.Ticks.Should().Be(second.Ticks);
        (await _context.AlertConditionTimers.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task SetNotTrue_ends_only_the_targeted_path_s_run_and_records_its_instant()
    {
        var ruleId = Guid.NewGuid();
        var at = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);
        var notTrue = at.AddMinutes(5);
        await _repository.SetFirstTrueAsync(ruleId, "a", at, CancellationToken.None);
        await _repository.SetFirstTrueAsync(ruleId, "b", at, CancellationToken.None);

        await _repository.SetNotTrueAsync(ruleId, "a", notTrue, CancellationToken.None);

        (await FirstTrueAsync(ruleId, "a")).Should().BeNull();
        (await NotTrueAsync(ruleId, "a")).Should().Be(notTrue);
        (await FirstTrueAsync(ruleId, "b")).Should().NotBeNull();
        (await NotTrueAsync(ruleId, "b")).Should().BeNull();
    }

    /// <summary>
    /// The image before not-true instants existed reads <c>alert_condition_timers</c> as
    /// <c>path → first_true_at</c> and takes every row for a run. After this image records a
    /// not-true path, that read still finds exactly the runs, each with its instant.
    /// </summary>
    [Fact]
    public async Task The_previous_image_s_read_of_the_timers_table_finds_only_runs()
    {
        var ruleId = Guid.NewGuid();
        var at = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);
        await _repository.SetFirstTrueAsync(ruleId, "ran", at, CancellationToken.None);
        await _repository.SetFirstTrueAsync(ruleId, "ended", at, CancellationToken.None);
        await _repository.SetNotTrueAsync(ruleId, "ended", at.AddMinutes(5), CancellationToken.None);
        await _repository.SetNotTrueAsync(ruleId, "never", at.AddMinutes(5), CancellationToken.None);

        var previousImageRead = await _context.AlertConditionTimers
            .AsNoTracking()
            .Where(t => t.AlertRuleId == ruleId)
            .Select(t => new { t.ConditionPath, t.FirstTrueAt })
            .ToDictionaryAsync(r => r.ConditionPath, r => r.FirstTrueAt);

        previousImageRead.Should().Equal(new Dictionary<string, DateTime> { ["ran"] = at });
    }

    [Fact]
    public async Task A_run_the_previous_image_started_over_a_not_true_row_reads_as_a_run()
    {
        var ruleId = Guid.NewGuid();
        var at = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);
        await _repository.SetNotTrueAsync(ruleId, "a", at, CancellationToken.None);
        _context.AlertConditionTimers.Add(new AlertConditionTimerEntity
        {
            TenantId = TenantId,
            AlertRuleId = ruleId,
            ConditionPath = "a",
            FirstTrueAt = at.AddMinutes(1),
        });
        await _context.SaveChangesAsync();

        var timers = await _repository.GetAllForRuleAsync(ruleId, CancellationToken.None);

        timers.FirstTrue.Should().Equal(new Dictionary<string, DateTime> { ["a"] = at.AddMinutes(1) });
        timers.NotTrue.Should().BeEmpty();
    }

    [Fact]
    public async Task SetFirstTrue_after_SetNotTrue_replaces_the_not_true_row_with_a_run()
    {
        var ruleId = Guid.NewGuid();
        var at = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);
        await _repository.SetNotTrueAsync(ruleId, "a", at, CancellationToken.None);

        await _repository.SetFirstTrueAsync(ruleId, "a", at.AddMinutes(1), CancellationToken.None);

        (await FirstTrueAsync(ruleId, "a")).Should().Be(at.AddMinutes(1));
        (await NotTrueAsync(ruleId, "a")).Should().BeNull();
        (await _context.AlertConditionTimers.CountAsync()).Should().Be(1);
        (await _context.AlertConditionNotTrue.CountAsync()).Should().Be(0);
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
        await _repository.SetNotTrueAsync(ruleId, "d", at, CancellationToken.None);
        await _repository.SetNotTrueAsync(ruleId, "e", at, CancellationToken.None);

        await _repository.PruneToPathsAsync(ruleId, new[] { "b", "c", "e" }, CancellationToken.None);

        (await NotTrueAsync(ruleId, "d")).Should().BeNull();
        (await NotTrueAsync(ruleId, "e")).Should().Be(at);
        (await FirstTrueAsync(ruleId, "a")).Should().BeNull();
        (await FirstTrueAsync(ruleId, "b")).Should().NotBeNull();
        (await FirstTrueAsync(ruleId, "c")).Should().NotBeNull();
        (await FirstTrueAsync(otherRuleId, "a")).Should().NotBeNull();
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

        (await FirstTrueAsync(ruleId, "a")).Should().BeNull();
        (await FirstTrueAsync(ruleId, "b")).Should().BeNull();
        (await FirstTrueAsync(otherRuleId, "a")).Should().NotBeNull();
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
