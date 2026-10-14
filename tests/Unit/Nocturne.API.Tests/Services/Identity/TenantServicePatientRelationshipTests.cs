using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// "Who is Nocturne for" is stored once on the tenant row: unset until answered, overwritten by a
/// later answer, and never visible on another tenant.
/// </summary>
[Trait("Category", "Unit")]
public sealed class TenantServicePatientRelationshipTests : IDisposable
{
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _otherTenantId = Guid.CreateVersion7();
    private readonly SqliteTestDatabase _db;

    public TenantServicePatientRelationshipTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(_tenantId, "mine");
        _db.SeedTenant(_otherTenantId, "theirs");
    }

    public void Dispose() => _db.Dispose();

    private TenantService Service() => new(
        _db.ContextFactory,
        new MemoryCache(new MemoryCacheOptions()),
        Options.Create(new OperatorConfiguration()),
        Mock.Of<IHttpClientFactory>(),
        Mock.Of<ITenantRoleService>(),
        Mock.Of<ILogger<TenantService>>());

    [Fact]
    public async Task IsUnsetUntilAnswered()
    {
        (await Service().GetPatientRelationshipAsync(_tenantId)).Should().BeNull();
    }

    [Theory]
    [InlineData(PatientRelationship.Self)]
    [InlineData(PatientRelationship.Caregiver)]
    [InlineData(PatientRelationship.Helper)]
    public async Task StoresEachAnswer(PatientRelationship answer)
    {
        await Service().SetPatientRelationshipAsync(_tenantId, answer);

        (await Service().GetPatientRelationshipAsync(_tenantId)).Should().Be(answer);
    }

    [Fact]
    public async Task ALaterAnswerOverwritesTheFirst()
    {
        await Service().SetPatientRelationshipAsync(_tenantId, PatientRelationship.Helper);
        await Service().SetPatientRelationshipAsync(_tenantId, PatientRelationship.Self);

        (await Service().GetPatientRelationshipAsync(_tenantId)).Should().Be(PatientRelationship.Self);
    }

    [Fact]
    public async Task LeavesOtherTenantsUnanswered()
    {
        await Service().SetPatientRelationshipAsync(_tenantId, PatientRelationship.Caregiver);

        (await Service().GetPatientRelationshipAsync(_otherTenantId)).Should().BeNull();
    }

    [Fact]
    public async Task DefaultGlucoseUnits_AreUnsetUntilChosenAndStayOnTheirTenant()
    {
        (await Service().GetDefaultGlucoseUnitsAsync(_tenantId)).Should().BeNull();

        await Service().SetDefaultGlucoseUnitsAsync(_tenantId, "mmol");

        (await Service().GetDefaultGlucoseUnitsAsync(_tenantId)).Should().Be("mmol");
        (await Service().GetDefaultGlucoseUnitsAsync(_otherTenantId)).Should().BeNull();
    }
}
