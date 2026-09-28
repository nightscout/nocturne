using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

/// <summary>
/// <see cref="TherapySettings.IsDefault"/> is a tenant-wide singleton, so setting it on one row
/// must take it off every other.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class TherapySettingsRepositoryDefaultTests : IDisposable
{
    private readonly NocturneDbContext _context;
    private readonly TherapySettingsRepository _repository;

    public TherapySettingsRepositoryDefaultTests()
    {
        _context = TestDbContextFactory.CreateInMemoryContext($"therapy_settings_default_{Guid.NewGuid()}");
        _context.TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        _repository = new TherapySettingsRepository(
            new TestTenantDbContextFactory(_context),
            new SystemAuditContext(),
            NullLogger<TherapySettingsRepository>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<Guid> SeedAsync(string legacyId, bool isDefault)
    {
        var created = await _repository.BulkUpsertByLegacyIdAsync(
            [new TherapySettings
            {
                LegacyId = legacyId,
                Timestamp = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
                ProfileName = legacyId.Split(':')[^1],
                IsDefault = isDefault,
            }],
            WriteOrigin.Live);
        return created.Outcomes[legacyId].Record.Id;
    }

    [Fact]
    public async Task SetDefault_LeavesOnlyTheGivenRowFlagged()
    {
        await SeedAsync("old:Default", isDefault: true);
        await SeedAsync("new:default", isDefault: true);
        var target = await SeedAsync("new:Weekend", isDefault: false);

        await _repository.SetDefaultAsync(target);

        var defaults = await _repository.GetDefaultsAsync();
        defaults.Select(d => d.Id).Should().Equal([target]);
    }

    [Fact]
    public async Task SetDefault_WithNull_ClearsEveryFlag()
    {
        await SeedAsync("old:Default", isDefault: true);
        await SeedAsync("new:default", isDefault: true);

        await _repository.SetDefaultAsync(null);

        (await _repository.GetDefaultsAsync()).Should().BeEmpty();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Update_LeavesTheStoredFlagAsItIs(bool stored, bool sent)
    {
        var id = await SeedAsync("doc:Default", isDefault: stored);

        var updated = await _repository.UpdateAsync(id, new TherapySettings
        {
            LegacyId = "doc:Default",
            Timestamp = new DateTime(2026, 5, 2, 12, 0, 0, DateTimeKind.Utc),
            ProfileName = "Default",
            IsDefault = sent,
        }, WriteOrigin.Live);

        updated.IsDefault.Should().Be(stored);
        (await _repository.GetByIdAsync(id))!.IsDefault.Should().Be(stored);
    }
}
