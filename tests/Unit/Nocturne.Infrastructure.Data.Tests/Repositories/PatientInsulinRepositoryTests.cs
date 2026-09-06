using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class PatientInsulinRepositoryTests : IDisposable
{
    private readonly NocturneDbContext _context;
    private readonly PatientInsulinRepository _repository;

    public PatientInsulinRepositoryTests()
    {
        _context = TestDbContextFactory.CreateInMemoryContext();
        _context.TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var factory = new TestTenantDbContextFactory(_context);
        _repository = new PatientInsulinRepository(
            factory, NullLogger<PatientInsulinRepository>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private PatientInsulin CreateInsulin(string name, InsulinRole role, bool isPrimary = false, bool isCurrent = true)
    {
        return new PatientInsulin
        {
            Name = name,
            Role = role,
            IsPrimary = isPrimary,
            IsCurrent = isCurrent,
            InsulinCategory = InsulinCategory.RapidActing,
            Dia = 4.0,
            Peak = 75,
            Curve = "rapid-acting",
            Concentration = 100,
        };
    }

    [Fact]
    public async Task SetPrimaryAsync_BolusInsulin_ClearsPreviousPrimaryBolus()
    {
        // Arrange
        var first = await _repository.CreateAsync(CreateInsulin("Humalog", InsulinRole.Bolus, isPrimary: true), WriteOrigin.Live);
        await _repository.SetPrimaryAsync(first.Id);

        var second = await _repository.CreateAsync(CreateInsulin("NovoRapid", InsulinRole.Bolus, isPrimary: false), WriteOrigin.Live);

        // Act
        await _repository.SetPrimaryAsync(second.Id);

        // Assert
        var primaryBolus = await _repository.GetPrimaryBolusInsulinAsync();
        primaryBolus.Should().NotBeNull();
        primaryBolus!.Id.Should().Be(second.Id);

        var firstUpdated = await _repository.GetByIdAsync(first.Id);
        firstUpdated!.IsPrimary.Should().BeFalse();
    }

    [Fact]
    public async Task SetPrimaryAsync_BasalInsulin_ClearsPreviousPrimaryBasal()
    {
        // Arrange
        var first = await _repository.CreateAsync(CreateInsulin("Lantus", InsulinRole.Basal, isPrimary: true), WriteOrigin.Live);
        await _repository.SetPrimaryAsync(first.Id);

        var second = await _repository.CreateAsync(CreateInsulin("Levemir", InsulinRole.Basal, isPrimary: false), WriteOrigin.Live);

        // Act
        await _repository.SetPrimaryAsync(second.Id);

        // Assert
        var primaryBasal = await _repository.GetPrimaryBasalInsulinAsync();
        primaryBasal.Should().NotBeNull();
        primaryBasal!.Id.Should().Be(second.Id);

        var firstUpdated = await _repository.GetByIdAsync(first.Id);
        firstUpdated!.IsPrimary.Should().BeFalse();
    }

    [Fact]
    public async Task SetPrimaryAsync_BothRole_SatisfiesBolusAndBasalLookups()
    {
        // Arrange
        var insulin = await _repository.CreateAsync(CreateInsulin("Fiasp", InsulinRole.Both, isPrimary: false), WriteOrigin.Live);

        // Act
        await _repository.SetPrimaryAsync(insulin.Id);

        // Assert
        var primaryBolus = await _repository.GetPrimaryBolusInsulinAsync();
        primaryBolus.Should().NotBeNull();
        primaryBolus!.Id.Should().Be(insulin.Id);

        var primaryBasal = await _repository.GetPrimaryBasalInsulinAsync();
        primaryBasal.Should().NotBeNull();
        primaryBasal!.Id.Should().Be(insulin.Id);
    }

    [Fact]
    public async Task SetPrimaryAsync_BothRole_ClearsBolusAndBasalPrimaries()
    {
        // Arrange - set up separate bolus and basal primaries
        var bolus = await _repository.CreateAsync(CreateInsulin("Humalog", InsulinRole.Bolus, isPrimary: true), WriteOrigin.Live);
        await _repository.SetPrimaryAsync(bolus.Id);
        var basal = await _repository.CreateAsync(CreateInsulin("Lantus", InsulinRole.Basal, isPrimary: true), WriteOrigin.Live);
        await _repository.SetPrimaryAsync(basal.Id);

        var both = await _repository.CreateAsync(CreateInsulin("Fiasp", InsulinRole.Both, isPrimary: false), WriteOrigin.Live);

        // Act
        await _repository.SetPrimaryAsync(both.Id);

        // Assert - both previous primaries should be cleared
        var bolusUpdated = await _repository.GetByIdAsync(bolus.Id);
        bolusUpdated!.IsPrimary.Should().BeFalse();

        var basalUpdated = await _repository.GetByIdAsync(basal.Id);
        basalUpdated!.IsPrimary.Should().BeFalse();

        var primaryBolus = await _repository.GetPrimaryBolusInsulinAsync();
        primaryBolus!.Id.Should().Be(both.Id);

        var primaryBasal = await _repository.GetPrimaryBasalInsulinAsync();
        primaryBasal!.Id.Should().Be(both.Id);
    }

    [Fact]
    public async Task GetPrimaryBolusInsulinAsync_NoPrimarySet_ReturnsNull()
    {
        // Arrange - create insulins but none are primary
        await _repository.CreateAsync(CreateInsulin("Humalog", InsulinRole.Bolus, isPrimary: false), WriteOrigin.Live);
        await _repository.CreateAsync(CreateInsulin("NovoRapid", InsulinRole.Bolus, isPrimary: false), WriteOrigin.Live);

        // Act
        var result = await _repository.GetPrimaryBolusInsulinAsync();

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetPrimaryBasalInsulinAsync_NoPrimarySet_ReturnsNull()
    {
        // Arrange
        await _repository.CreateAsync(CreateInsulin("Lantus", InsulinRole.Basal, isPrimary: false), WriteOrigin.Live);

        // Act
        var result = await _repository.GetPrimaryBasalInsulinAsync();

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetPrimaryBolusInsulinAsync_IgnoresNonCurrentInsulins()
    {
        // Arrange - create a primary bolus that is not current
        var insulin = CreateInsulin("Humalog", InsulinRole.Bolus, isPrimary: true);
        insulin.IsCurrent = false;
        await _repository.CreateAsync(insulin, WriteOrigin.Live);
        // Manually set primary since SetPrimaryAsync doesn't check IsCurrent
        var entity = await _context.PatientInsulins.FindAsync(
            (await _repository.GetAllAsync()).First().Id);
        entity!.IsPrimary = true;
        await _context.SaveChangesAsync();

        // Act
        var result = await _repository.GetPrimaryBolusInsulinAsync();

        // Assert
        result.Should().BeNull();
    }
}
