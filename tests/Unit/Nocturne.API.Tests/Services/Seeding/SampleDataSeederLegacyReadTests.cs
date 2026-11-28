using Nocturne.Core.Contracts.Connectors;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Seeding;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.DeviceStatuses;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Seeding;

/// <summary>
/// The seeder's writes after its decomposer flushes reach the legacy read caches.
/// </summary>
public class SampleDataSeederLegacyReadTests : IDisposable
{
    private readonly SampleDataSeederHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task The_cache_hears_the_writes_made_after_the_last_decomposer_flush()
    {
        await _harness.CreateSeeder().SeedAsync(
            new TenantContext(SampleDataSeederHarness.TenantId, "test", "Test", true, false), days: 5, ownerSubjectId: null);

        await using var db = _harness.Db.CreateContext();
        (await db.TreatmentFoods.AnyAsync()).Should().BeTrue("the seed attributed foods to its meals");
        (await db.TempBasals.AnyAsync(t => t.ScheduledRate != null)).Should().BeTrue("the seed stamped its temps");
        (await db.StateSpans.AnyAsync()).Should().BeTrue();
        (await db.ProfileActivations.AnyAsync()).Should().BeTrue();
        _harness.Written.Models.Should().Contain([
            typeof(TreatmentFood), typeof(TempBasal), typeof(StateSpan), typeof(ProfileActivation),
        ]);
    }
}
