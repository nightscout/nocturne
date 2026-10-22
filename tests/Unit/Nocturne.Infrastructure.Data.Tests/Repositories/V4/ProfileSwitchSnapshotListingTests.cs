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
/// The rows a Profile Switch's inline profile decomposes into
/// (<see cref="TherapySettings.ProfileSwitchStoreMarker"/>) are not profiles, so listing the
/// therapy settings or any schedule leaves them out, while a lookup by their own name still finds them.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class ProfileSwitchSnapshotListingTests : IDisposable
{
    private static readonly DateTime At = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
    private const string Snapshot = "Rhys (110%,1h)@@@@@1777636800000";

    private readonly NocturneDbContext _context;
    private readonly TestNocturneDbContextFactory _factory;
    private readonly IAuditContext _audit = new SystemAuditContext();

    public ProfileSwitchSnapshotListingTests()
    {
        _context = TestDbContextFactory.CreateInMemoryContext($"profile_switch_snapshots_{Guid.NewGuid()}");
        _context.TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        _factory = new TestNocturneDbContextFactory(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Therapy_settings_list_and_count_leave_out_snapshots()
    {
        var repository = new TherapySettingsRepository(_factory, _audit, NullLogger<TherapySettingsRepository>.Instance);
        await repository.CreateAsync(new TherapySettings { ProfileName = "Rhys", Timestamp = At }, WriteOrigin.Live);
        await repository.CreateAsync(new TherapySettings { ProfileName = Snapshot, Timestamp = At.AddHours(1) }, WriteOrigin.Live);

        var listed = await repository.GetAsync(null, null, null, null);

        listed.Select(s => s.ProfileName).Should().Equal("Rhys");
        (await repository.CountAsync(null, null)).Should().Be(1);
        (await repository.GetByProfileNameAsync(Snapshot)).Should().ContainSingle();
    }

    [Fact]
    public async Task Schedule_lists_leave_out_snapshots()
    {
        var basal = new BasalScheduleRepository(_factory, _audit, NullLogger<BasalScheduleRepository>.Instance);
        var carbRatio = new CarbRatioScheduleRepository(_factory, _audit, NullLogger<CarbRatioScheduleRepository>.Instance);
        var sensitivity = new SensitivityScheduleRepository(_factory, _audit, NullLogger<SensitivityScheduleRepository>.Instance);
        var targetRange = new TargetRangeScheduleRepository(_factory, _audit, NullLogger<TargetRangeScheduleRepository>.Instance);
        foreach (var name in new[] { "Rhys", Snapshot })
        {
            await basal.CreateAsync(new BasalSchedule { ProfileName = name, Timestamp = At }, WriteOrigin.Live);
            await carbRatio.CreateAsync(new CarbRatioSchedule { ProfileName = name, Timestamp = At }, WriteOrigin.Live);
            await sensitivity.CreateAsync(new SensitivitySchedule { ProfileName = name, Timestamp = At }, WriteOrigin.Live);
            await targetRange.CreateAsync(new TargetRangeSchedule { ProfileName = name, Timestamp = At }, WriteOrigin.Live);
        }

        (await basal.GetAsync(null, null, null, null)).Select(s => s.ProfileName).Should().Equal("Rhys");
        (await carbRatio.GetAsync(null, null, null, null)).Select(s => s.ProfileName).Should().Equal("Rhys");
        (await sensitivity.GetAsync(null, null, null, null)).Select(s => s.ProfileName).Should().Equal("Rhys");
        (await targetRange.GetAsync(null, null, null, null)).Select(s => s.ProfileName).Should().Equal("Rhys");
        (await basal.GetByProfileNameAsync(Snapshot)).Should().ContainSingle();
    }
}
