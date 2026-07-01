using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using Nocturne.API.Controllers.V4.Treatments;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Services.Platform;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Xunit;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.API.Tests.Controllers.V4;

[Trait("Category", "Unit")]
public class NutritionControllerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly NocturneDbContext _dbContext;
    private readonly Mock<ICarbIntakeRepository> _repoMock = new();
    private readonly Mock<IBolusRepository> _bolusRepoMock = new();
    private readonly Mock<ITreatmentFoodService> _foodServiceMock = new();
    private readonly Mock<IDemoModeService> _demoModeMock = new();

    public NutritionControllerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        _dbContext = new NocturneDbContext(options) { TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001") };
        _dbContext.Database.EnsureCreated();
        _dbContext.Tenants.Add(new Nocturne.Infrastructure.Data.Entities.TenantEntity
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            Slug = "test"
        });
        _dbContext.SaveChanges();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    private NutritionController CreateController()
    {
        var controller = new NutritionController(
            _repoMock.Object,
            _bolusRepoMock.Object,
            _foodServiceMock.Object,
            _demoModeMock.Object,
            _dbContext);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }

    private void SetupCreatePassthrough(Action<CarbIntake> onCreate)
    {
        _repoMock
            .Setup(r => r.CreateAsync(It.IsAny<CarbIntake>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<CarbIntake, WriteOrigin, CancellationToken>((c, _, _) => onCreate(c))
            .ReturnsAsync((CarbIntake c, WriteOrigin origin, CancellationToken _) => c);
    }

    [Fact]
    public async Task CreateCarbIntake_PassesThroughCorrelationId()
    {
        var cid = Guid.NewGuid();
        CarbIntake? captured = null;
        SetupCreatePassthrough(c => captured = c);

        var controller = CreateController();
        var request = new CreateCarbIntakeRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Carbs = 40,
            CorrelationId = cid,
        };

        await controller.CreateCarbIntake(request);

        captured.Should().NotBeNull();
        captured!.CorrelationId.Should().Be(cid);
    }

    [Fact]
    public async Task CreateCarbIntake_WithoutCorrelationId_ServerMintsNonEmptyGuid()
    {
        CarbIntake? captured = null;
        SetupCreatePassthrough(c => captured = c);

        var controller = CreateController();
        var request = new CreateCarbIntakeRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Carbs = 40,
            // CorrelationId intentionally omitted
        };

        await controller.CreateCarbIntake(request);

        captured.Should().NotBeNull();
        captured!.CorrelationId.Should().NotBeNull().And.NotBe(Guid.Empty);
    }

    [Fact]
    public async Task UpdateCarbIntake_RequestCorrelationIdWins_WhenSupplied()
    {
        var existingCid = Guid.NewGuid();
        var requestCid = Guid.NewGuid();
        var id = Guid.NewGuid();
        var existing = new CarbIntake
        {
            Id = id,
            Timestamp = DateTime.UtcNow,
            Carbs = 20,
            CorrelationId = existingCid,
        };
        CarbIntake? captured = null;

        _repoMock
            .Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        _repoMock
            .Setup(r => r.UpdateAsync(id, It.IsAny<CarbIntake>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, CarbIntake, WriteOrigin, CancellationToken>((_, c, _, _) => captured = c)
            .ReturnsAsync((Guid _, CarbIntake c, WriteOrigin origin, CancellationToken _) => c);

        var controller = CreateController();
        var request = new UpdateCarbIntakeRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Carbs = 30,
            CorrelationId = requestCid,
        };

        await controller.UpdateCarbIntake(id, request);

        captured.Should().NotBeNull();
        captured!.CorrelationId.Should().Be(requestCid);
    }

    [Fact]
    public async Task UpdateCarbIntake_PreservesExistingCorrelationId_WhenRequestOmits()
    {
        var existingCid = Guid.NewGuid();
        var id = Guid.NewGuid();
        var existing = new CarbIntake
        {
            Id = id,
            Timestamp = DateTime.UtcNow,
            Carbs = 20,
            CorrelationId = existingCid,
        };
        CarbIntake? captured = null;

        _repoMock
            .Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        _repoMock
            .Setup(r => r.UpdateAsync(id, It.IsAny<CarbIntake>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, CarbIntake, WriteOrigin, CancellationToken>((_, c, _, _) => captured = c)
            .ReturnsAsync((Guid _, CarbIntake c, WriteOrigin origin, CancellationToken _) => c);

        var controller = CreateController();
        var request = new UpdateCarbIntakeRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Carbs = 30,
            // CorrelationId intentionally omitted
        };

        await controller.UpdateCarbIntake(id, request);

        captured.Should().NotBeNull();
        captured!.CorrelationId.Should().Be(existingCid);
    }

    [Fact]
    public async Task UpdateCarbIntake_ReturnsNotFound_WhenExistingMissing()
    {
        var id = Guid.NewGuid();
        _repoMock
            .Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((CarbIntake?)null);

        var controller = CreateController();
        var request = new UpdateCarbIntakeRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Carbs = 30,
        };

        var result = await controller.UpdateCarbIntake(id, request);

        result.Result.Should().BeOfType<NotFoundResult>();
    }
}
