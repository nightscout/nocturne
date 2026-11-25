using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Controllers.V4.Treatments;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.API.Tests.Controllers.V4;

[Trait("Category", "Unit")]
public class CarbIntakeControllerTests
{
    private readonly Mock<ICarbIntakeRepository> _repoMock = new();
    private readonly Mock<ITreatmentFoodService> _foodServiceMock = new();

    private CarbIntakeController CreateController()
    {
        var controller = new CarbIntakeController(_repoMock.Object, _foodServiceMock.Object);
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

    private void SetupFoodLine(Guid carbId, string note) =>
        _foodServiceMock
            .Setup(f => f.GetByCarbIntakeIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new TreatmentFood { CarbIntakeId = carbId, Note = note }]);

    private void SetupList(params CarbIntake[] page) =>
        _repoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(page);

    [Fact]
    public async Task Create_PassesThroughCorrelationId()
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

        await controller.Create(request);

        captured.Should().NotBeNull();
        captured!.CorrelationId.Should().Be(cid);
    }

    [Fact]
    public async Task Create_WithoutCorrelationId_ServerMintsNonEmptyGuid()
    {
        CarbIntake? captured = null;
        SetupCreatePassthrough(c => captured = c);

        var controller = CreateController();
        var request = new CreateCarbIntakeRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Carbs = 40,
        };

        await controller.Create(request);

        captured.Should().NotBeNull();
        captured!.CorrelationId.Should().NotBeNull().And.NotBe(Guid.Empty);
    }

    [Fact]
    public async Task Update_RequestCorrelationIdWins_WhenSupplied()
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

        await controller.Update(id, request);

        captured.Should().NotBeNull();
        captured!.CorrelationId.Should().Be(requestCid);
    }

    [Fact]
    public async Task Update_PreservesExistingCorrelationId_WhenRequestOmits()
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
        };

        await controller.Update(id, request);

        captured.Should().NotBeNull();
        captured!.CorrelationId.Should().Be(existingCid);
    }

    [Fact]
    public async Task Update_ReturnsNotFound_WhenExistingMissing()
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

        var result = await controller.Update(id, request);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Update_UnknownIdWithNoTimestamp_ReturnsNotFoundBeforeTheTimestampCheck()
    {
        var id = Guid.NewGuid();
        _repoMock
            .Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((CarbIntake?)null);

        var result = await CreateController().Update(id, new UpdateCarbIntakeRequest { Carbs = 30 });

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Update_KnownIdWithNoTimestamp_ReturnsBadRequestWithoutWriting()
    {
        var id = Guid.NewGuid();
        _repoMock
            .Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CarbIntake { Id = id, Timestamp = DateTime.UtcNow });

        var result = await CreateController().Update(id, new UpdateCarbIntakeRequest { Carbs = 30 });

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        _repoMock.Verify(
            r => r.UpdateAsync(It.IsAny<Guid>(), It.IsAny<CarbIntake>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetAll_LimitAtCeiling_ReachesRepositoryUnchanged()
    {
        await CreateController().GetAll(null, null, V4ReadLimits.MaxPageSize, 0);

        VerifyCarbIntakesFetched(V4ReadLimits.MaxPageSize, 0);
    }

    [Fact]
    public async Task GetAll_LimitAboveCeiling_IsClamped()
    {
        await CreateController().GetAll(null, null, V4ReadLimits.MaxPageSize + 1, -1);

        VerifyCarbIntakesFetched(V4ReadLimits.MaxPageSize, 0);
    }

    [Fact]
    public async Task GetAll_FullPageWithoutIncludeTotal_LeavesTotalNullWithoutCounting()
    {
        SetupList(new CarbIntake());

        var result = await CreateController().GetAll(null, null, 1, 0);

        var response = ((OkObjectResult)result.Result!).Value.Should().BeOfType<PaginatedResponse<CarbIntake>>().Subject;
        response.Pagination.Total.Should().BeNull();
        _repoMock.Verify(r => r.CountAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetAll_IncludeTotal_CountsWithTheDeviceAndSourceTheDataQueryUses()
    {
        _repoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), "pump-a", "tandem",
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new CarbIntake()]);
        _repoMock.Setup(r => r.CountAsync(null, null, "pump-a", "tandem", It.IsAny<CancellationToken>())).ReturnsAsync(4);

        var result = await CreateController().GetAll(null, null, 1, 0, "timestamp_desc", "pump-a", "tandem", includeTotal: true);

        var response = ((OkObjectResult)result.Result!).Value.Should().BeOfType<PaginatedResponse<CarbIntake>>().Subject;
        response.Pagination.Total.Should().Be(4);
    }

    [Fact]
    public async Task GetAll_FillsFoodTypeFromTheFoodLines_WithOneFoodReadForThePage()
    {
        var withLines = new CarbIntake { Id = Guid.NewGuid() };
        var withoutLines = new CarbIntake { Id = Guid.NewGuid() };
        var other = new CarbIntake { Id = Guid.NewGuid() };
        SetupList(withLines, withoutLines, other);
        _foodServiceMock
            .Setup(f => f.GetByCarbIntakeIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new TreatmentFood { CarbIntakeId = withLines.Id, FoodName = "Rice" },
                new TreatmentFood { CarbIntakeId = withLines.Id, Note = "pasta" },
                new TreatmentFood { CarbIntakeId = other.Id, Note = "taco" },
            ]);

        var result = await CreateController().GetAll(null, null);

        var data = ((OkObjectResult)result.Result!).Value.Should().BeOfType<PaginatedResponse<CarbIntake>>().Subject.Data;
        data.Select(c => c.FoodType).Should().Equal("Rice, pasta", null, "taco");
        _foodServiceMock.Verify(
            f => f.GetByCarbIntakeIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetById_FillsFoodTypeFromTheFoodLines()
    {
        var carbId = Guid.NewGuid();
        _repoMock.Setup(r => r.GetByIdAsync(carbId, It.IsAny<CancellationToken>())).ReturnsAsync(new CarbIntake { Id = carbId });
        SetupFoodLine(carbId, "taco");

        var result = await CreateController().GetById(carbId);

        ((OkObjectResult)result.Result!).Value.Should().BeOfType<CarbIntake>().Which.FoodType.Should().Be("taco");
    }

    [Fact]
    public async Task GetById_WithOnlyBlankNamesAndNotes_HasNoFoodType()
    {
        var carbId = Guid.NewGuid();
        _repoMock.Setup(r => r.GetByIdAsync(carbId, It.IsAny<CancellationToken>())).ReturnsAsync(new CarbIntake { Id = carbId });
        _foodServiceMock
            .Setup(f => f.GetByCarbIntakeIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new TreatmentFood { CarbIntakeId = carbId, FoodName = " ", Note = "x" },
                new TreatmentFood { CarbIntakeId = carbId, Note = "" },
                new TreatmentFood { CarbIntakeId = carbId },
            ]);

        var result = await CreateController().GetById(carbId);

        ((OkObjectResult)result.Result!).Value.Should().BeOfType<CarbIntake>().Which.FoodType.Should().BeNull();
    }

    [Fact]
    public async Task Update_FillsFoodTypeFromTheFoodLines()
    {
        var carbId = Guid.NewGuid();
        _repoMock.Setup(r => r.GetByIdAsync(carbId, It.IsAny<CancellationToken>())).ReturnsAsync(new CarbIntake { Id = carbId });
        _repoMock
            .Setup(r => r.UpdateAsync(carbId, It.IsAny<CarbIntake>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CarbIntake { Id = carbId });
        SetupFoodLine(carbId, "taco");

        var result = await CreateController().Update(
            carbId, new UpdateCarbIntakeRequest { Timestamp = DateTimeOffset.UtcNow, Carbs = 10 });

        ((OkObjectResult)result.Result!).Value.Should().BeOfType<CarbIntake>().Which.FoodType.Should().Be("taco");
    }

    [Fact]
    public async Task Create_ReturnsTheIntakeAsStored_AndReadsNoFoodLines()
    {
        var carbId = Guid.NewGuid();
        _repoMock
            .Setup(r => r.CreateAsync(It.IsAny<CarbIntake>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CarbIntake { Id = carbId, Timestamp = DateTime.UtcNow });
        SetupFoodLine(carbId, "taco");

        var result = await CreateController().Create(new CreateCarbIntakeRequest { Timestamp = DateTimeOffset.UtcNow, Carbs = 10 });

        ((CreatedAtActionResult)result.Result!).Value.Should().BeOfType<CarbIntake>().Which.FoodType.Should().BeNull();
        _foodServiceMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CreateBulk_FillsFoodTypeFromTheFoodLines()
    {
        var carbId = Guid.NewGuid();
        _repoMock
            .Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<CarbIntake>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new CarbIntake { Id = carbId, Timestamp = DateTime.UtcNow }]);
        SetupFoodLine(carbId, "taco");

        var result = await CreateController().CreateBulk([new CreateCarbIntakeRequest { Timestamp = DateTimeOffset.UtcNow, Carbs = 10 }]);

        ((ObjectResult)result.Result!).Value.Should().BeOfType<CarbIntake[]>()
            .Which.Should().ContainSingle().Which.FoodType.Should().Be("taco");
    }

    [Fact]
    public async Task ListDeleted_ReturnsTheDeletedIntakesWithTheirFoodType()
    {
        var carbId = Guid.NewGuid();
        _repoMock.Setup(r => r.GetDeletedAsync(100, 0, It.IsAny<CancellationToken>())).ReturnsAsync([new CarbIntake { Id = carbId }]);
        _repoMock.Setup(r => r.CountDeletedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        SetupFoodLine(carbId, "taco");

        var result = await CreateController().ListDeleted();

        var response = ((OkObjectResult)result.Result!).Value.Should().BeOfType<PaginatedResponse<CarbIntake>>().Subject;
        response.Data.Should().ContainSingle().Which.FoodType.Should().Be("taco");
        response.Pagination.Total.Should().Be(1);
    }

    [Fact]
    public async Task Restore_ReturnsTheRestoredIntakeWithItsFoodType()
    {
        var carbId = Guid.NewGuid();
        _repoMock
            .Setup(r => r.RestoreAsync(carbId, WriteOrigin.Live, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CarbIntake { Id = carbId, Timestamp = DateTime.UtcNow });
        SetupFoodLine(carbId, "taco");

        var result = await CreateController().Restore(carbId);

        ((OkObjectResult)result.Result!).Value.Should().BeOfType<CarbIntake>().Which.FoodType.Should().Be("taco");
    }

    [Fact]
    public async Task Restore_NoDeletedIntake_ReturnsNotFound()
    {
        var carbId = Guid.NewGuid();
        _repoMock
            .Setup(r => r.RestoreAsync(carbId, WriteOrigin.Live, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException());

        var result = await CreateController().Restore(carbId);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task BulkRestore_ReturnsTheRestoredIntakesWithTheirFoodType_AndTheConflicts()
    {
        var carbId = Guid.NewGuid();
        var conflicted = Guid.NewGuid();
        _repoMock
            .Setup(r => r.BulkRestoreAsync(It.IsAny<IEnumerable<Guid>>(), WriteOrigin.Live, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkRestoreResult<CarbIntake>
            {
                Restored = [new CarbIntake { Id = carbId, Timestamp = DateTime.UtcNow }],
                Conflicts = [conflicted],
            });
        SetupFoodLine(carbId, "taco");

        var result = await CreateController().BulkRestore([carbId, conflicted]);

        var response = ((OkObjectResult)result.Result!).Value.Should().BeOfType<BulkRestoreResult<CarbIntake>>().Subject;
        response.Restored.Should().ContainSingle().Which.FoodType.Should().Be("taco");
        response.Conflicts.Should().Equal(conflicted);
    }

    private void VerifyCarbIntakesFetched(int limit, int offset) =>
        _repoMock.Verify(r => r.GetAsync(
            null, null, null, null, limit, offset, true, It.IsAny<CancellationToken>()), Times.Once);
}
