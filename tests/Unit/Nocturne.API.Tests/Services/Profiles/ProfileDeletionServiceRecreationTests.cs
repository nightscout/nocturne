using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Profiles;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Profiles;

/// <summary>
/// Whether a deleted therapy profile stays deleted when the connector that relayed it syncs again.
/// The delete writes a <c>deleted_by_user</c> tombstone and the ingest path drops any legacy id a
/// tombstone holds, so the two are exercised together against a real schema.
/// <c>ExecuteUpdateAsync</c> and the audit transaction need a relational provider, hence SQLite.
/// </summary>
[Trait("Category", "Unit")]
public class ProfileDeletionServiceRecreationTests : IDisposable
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private const string UpstreamDocumentId = "5f2b1c9e8a7d4b0012345678";

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly TestTenantDbContextFactory _factory;

    public ProfileDeletionServiceRecreationTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TestTenantId);
        _context = _db.CreateContext();
        _factory = new TestTenantDbContextFactory(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task UserDelete_ThenResyncOfTheSameUpstreamDocument_DoesNotResurrectTheProfile()
    {
        var decomposer = Decomposer(new UserAuditContext());
        await decomposer.DecomposeBatchAsync([UpstreamProfile()], WriteOrigin.Live);
        (await LiveProfileNames()).Should().BeEquivalentTo(["Default", "Weekend"]);

        var result = await Deletion(new UserAuditContext()).DeleteByProfileNameAsync("Default");

        result.Deleted.Should().BeTrue();
        result.DeletedRecords.Should().Be(5, "one row per name in each of the five profile tables");

        await decomposer.DecomposeBatchAsync([UpstreamProfile()], WriteOrigin.Live);

        (await LiveProfileNames()).Should().BeEquivalentTo(["Weekend"],
            "the user tombstone holds the legacy id, so the relay cannot re-create the profile");
    }

    /// <summary>
    /// The counterpart of the guard. A sweep the system issued leaves the rows re-creatable, so a
    /// connector that clears and re-pulls its own data still restores the profile.
    /// </summary>
    [Fact]
    public async Task SystemDelete_ThenResyncOfTheSameUpstreamDocument_RestoresTheProfile()
    {
        var decomposer = Decomposer(new SystemAuditContext());
        await decomposer.DecomposeBatchAsync([UpstreamProfile()], WriteOrigin.Live);

        var result = await Deletion(new SystemAuditContext()).DeleteByProfileNameAsync("Default");

        result.Deleted.Should().BeTrue();

        await decomposer.DecomposeBatchAsync([UpstreamProfile()], WriteOrigin.Live);

        (await LiveProfileNames()).Should().BeEquivalentTo(["Default", "Weekend"],
            "a system sweep leaves deleted_by_user false, which does not block re-creation");
    }

    /// <summary>
    /// Characterises the tombstone's reach. The guard keys on the legacy id, which for a profile is
    /// <c>{upstream _id}:{storeName}</c>. A second upstream document carrying the same store name
    /// therefore presents an id no tombstone holds.
    /// </summary>
    [Fact]
    public async Task UserDelete_ThenIngestOfANewUpstreamDocument_RecreatesTheProfileUnderTheSameName()
    {
        var decomposer = Decomposer(new UserAuditContext());
        await decomposer.DecomposeBatchAsync([UpstreamProfile()], WriteOrigin.Live);

        await Deletion(new UserAuditContext()).DeleteByProfileNameAsync("Default");

        await decomposer.DecomposeBatchAsync(
            [UpstreamProfile(documentId: "6a3c2d0f9b8e5c0087654321")], WriteOrigin.Live);

        (await LiveProfileNames()).Should().Contain("Default",
            "a different upstream document id is a different legacy id, which no tombstone holds");
    }

    [Fact]
    public async Task Delete_RemovesEveryProfileScopedTableForTheName_AndLeavesTheOthersAlone()
    {
        await Decomposer(new UserAuditContext()).DecomposeBatchAsync([UpstreamProfile()], WriteOrigin.Live);

        await Deletion(new UserAuditContext()).DeleteByProfileNameAsync("Default");

        await using var verify = _db.CreateContext();
        verify.TherapySettings.Count(e => e.ProfileName == "Default").Should().Be(0);
        verify.BasalSchedules.Count(e => e.ProfileName == "Default").Should().Be(0);
        verify.CarbRatioSchedules.Count(e => e.ProfileName == "Default").Should().Be(0);
        verify.SensitivitySchedules.Count(e => e.ProfileName == "Default").Should().Be(0);
        verify.TargetRangeSchedules.Count(e => e.ProfileName == "Default").Should().Be(0);

        verify.TherapySettings.Count(e => e.ProfileName == "Weekend").Should().Be(1);
        verify.BasalSchedules.Count(e => e.ProfileName == "Weekend").Should().Be(1);
    }

    /// <summary>
    /// The case the endpoint exists for: a stale <c>Default</c> relayed from an uploader beside the
    /// <c>default</c> a pump writes. Deleting one must not touch the other.
    /// </summary>
    [Fact]
    public async Task Delete_MatchesTheProfileNameCaseSensitively()
    {
        var decomposer = Decomposer(new UserAuditContext());
        await decomposer.DecomposeBatchAsync(
            [UpstreamProfile(stores: ["Default", "Weekend"]),
             UpstreamProfile(documentId: "6a3c2d0f9b8e5c0087654321", stores: ["default"])],
            WriteOrigin.Live);

        var result = await Deletion(new UserAuditContext()).DeleteByProfileNameAsync("Default");

        result.Deleted.Should().BeTrue();
        (await LiveProfileNames()).Should().BeEquivalentTo(["Weekend", "default"]);
    }

    [Fact]
    public async Task Delete_RefusesTheActiveProfile()
    {
        await Decomposer(new UserAuditContext()).DecomposeBatchAsync(
            [UpstreamProfile(defaultProfile: "Default")], WriteOrigin.Live);

        var result = await Deletion(new UserAuditContext()).DeleteByProfileNameAsync("Default");

        result.Refusal.Should().Be(ProfileDeletionRefusal.ActiveProfile,
            "the upstream document names Default as its defaultProfile");
        result.DeletedRecords.Should().Be(0);
        (await LiveProfileNames()).Should().Contain("Default");
    }

    [Fact]
    public async Task Delete_RefusesTheOnlyProfile()
    {
        await Decomposer(new UserAuditContext()).DecomposeBatchAsync(
            [UpstreamProfile(stores: ["Solo"], defaultProfile: "none")], WriteOrigin.Live);

        var result = await Deletion(new UserAuditContext()).DeleteByProfileNameAsync("Solo");

        result.Refusal.Should().Be(ProfileDeletionRefusal.OnlyProfile);
        (await LiveProfileNames()).Should().BeEquivalentTo(["Solo"]);
    }

    [Fact]
    public async Task Delete_ReportsAnUnknownNameAsNotFound()
    {
        await Decomposer(new UserAuditContext()).DecomposeBatchAsync([UpstreamProfile()], WriteOrigin.Live);

        var result = await Deletion(new UserAuditContext()).DeleteByProfileNameAsync("Nonexistent");

        result.Refusal.Should().Be(ProfileDeletionRefusal.NotFound);
    }

    private ProfileDecomposer Decomposer(IAuditContext audit) => new(
        new TherapySettingsRepository(_factory, audit, NullLogger<TherapySettingsRepository>.Instance),
        new BasalScheduleRepository(_factory, audit, NullLogger<BasalScheduleRepository>.Instance),
        new CarbRatioScheduleRepository(_factory, audit, NullLogger<CarbRatioScheduleRepository>.Instance),
        new SensitivityScheduleRepository(_factory, audit, NullLogger<SensitivityScheduleRepository>.Instance),
        new TargetRangeScheduleRepository(_factory, audit, NullLogger<TargetRangeScheduleRepository>.Instance),
        NullLogger<ProfileDecomposer>.Instance);

    private ProfileDeletionService Deletion(IAuditContext audit) => new(
        _factory,
        new TherapySettingsRepository(_factory, audit, NullLogger<TherapySettingsRepository>.Instance),
        audit,
        NullLogger<ProfileDeletionService>.Instance);

    private async Task<List<string>> LiveProfileNames()
    {
        await using var verify = _db.CreateContext();
        return await verify.TherapySettings.Select(e => e.ProfileName).Distinct().ToListAsync();
    }

    /// <summary>One Nightscout profile document, as the relay connector deserializes it.</summary>
    private static Profile UpstreamProfile(
        string documentId = UpstreamDocumentId,
        string[]? stores = null,
        string defaultProfile = "Weekend") => new()
    {
        Id = documentId,
        DefaultProfile = defaultProfile,
        Units = "mg/dL",
        EnteredBy = "Loop",
        StartDate = "2026-01-01T00:00:00.000Z",
        CreatedAt = "2026-01-01T00:00:00.000Z",
        Store = (stores ?? ["Default", "Weekend"]).ToDictionary(
            name => name,
            _ => new ProfileData
            {
                Dia = 5.0,
                CarbsHr = 20,
                Delay = 20,
                Timezone = "UTC",
                Units = "mg/dL",
                Basal = [new TimeValue { Time = "00:00", Value = 0.8, TimeAsSeconds = 0 }],
                CarbRatio = [new TimeValue { Time = "00:00", Value = 10, TimeAsSeconds = 0 }],
                Sens = [new TimeValue { Time = "00:00", Value = 50, TimeAsSeconds = 0 }],
                TargetLow = [new TimeValue { Time = "00:00", Value = 100, TimeAsSeconds = 0 }],
                TargetHigh = [new TimeValue { Time = "00:00", Value = 120, TimeAsSeconds = 0 }],
            }),
    };

    private sealed class UserAuditContext : IAuditContext
    {
        public Guid? SubjectId => Guid.Empty;
        public string? SubjectName => "tester";
        public string? AuthType => "SessionCookie";
        public string? IpAddress => "127.0.0.1";
        public Guid? TokenId => null;
        public string? TraceId => null;
        public string? Endpoint => "DELETE /api/v4/profile/by-name/Default";
        public bool IsSystem => false;
    }
}
