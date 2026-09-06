using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Treatments;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Xunit;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.API.Tests.Controllers.V4.Treatments;

[Trait("Category", "Unit")]
public class BasalInjectionControllerTests
{
    private readonly Mock<IBasalInjectionRepository> _repoMock = new();
    private readonly Mock<IPatientInsulinRepository> _insulinRepoMock = new();

    private BasalInjectionController CreateController()
    {
        var controller = new BasalInjectionController(_repoMock.Object, _insulinRepoMock.Object);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }

    [Fact]
    public async Task CreateBulk_UnitsOutsideTheAllowedRange_RejectsTheWholeBatch()
    {
        var result = await CreateController().CreateBulk(
        [
            new CreateBasalInjectionRequest { Timestamp = DateTimeOffset.UtcNow, Units = 12 },
            new CreateBasalInjectionRequest { Timestamp = DateTimeOffset.UtcNow, Units = 0 },
        ]);

        var objectResult = result.Result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(400);
        objectResult.Value.Should().BeOfType<ProblemDetails>()
            .Which.Detail.Should().Be("Units must be > 0 and <= 500.");
        _repoMock.Verify(
            r => r.BulkCreateAsync(It.IsAny<IEnumerable<BasalInjection>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateBulk_ResolvesTheInsulinContextPerInjection()
    {
        var insulin = BasalInsulin();
        _insulinRepoMock.Setup(r => r.GetByIdAsync(insulin.Id, It.IsAny<CancellationToken>())).ReturnsAsync(insulin);
        IEnumerable<BasalInjection>? persisted = null;
        _repoMock
            .Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<BasalInjection>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<BasalInjection>, WriteOrigin, CancellationToken>((b, _, _) => persisted = b.ToList())
            .ReturnsAsync((IEnumerable<BasalInjection> b, WriteOrigin _, CancellationToken _) => b);

        await CreateController().CreateBulk(
        [
            new CreateBasalInjectionRequest { Timestamp = DateTimeOffset.UtcNow, Units = 12, PatientInsulinId = insulin.Id },
            new CreateBasalInjectionRequest { Timestamp = DateTimeOffset.UtcNow, Units = 10 },
        ]);

        persisted.Should().NotBeNull();
        persisted!.Should().SatisfyRespectively(
            referenced => referenced.InsulinContext!.PatientInsulinId.Should().Be(insulin.Id),
            unreferenced => unreferenced.InsulinContext.Should().BeNull());
    }

    private static PatientInsulin BasalInsulin(
        Guid? id = null,
        InsulinRole role = InsulinRole.Basal,
        DateOnly? startDate = null,
        DateOnly? endDate = null) => new()
        {
            Id = id ?? Guid.NewGuid(),
            Name = "Tresiba",
            Dia = 24,
            Peak = 720,
            Curve = "ultra-long",
            Concentration = 100,
            Role = role,
            StartDate = startDate,
            EndDate = endDate,
        };

    private void SetupCreatePassthrough(Action<BasalInjection> onCreate)
    {
        _repoMock
            .Setup(r => r.CreateAsync(It.IsAny<BasalInjection>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<BasalInjection, WriteOrigin, CancellationToken>((b, _, _) => onCreate(b))
            .ReturnsAsync((BasalInjection b, WriteOrigin origin, CancellationToken _) => b);
    }

    [Fact]
    public async Task Create_returns_400_when_units_is_zero_or_negative()
    {
        var controller = CreateController();
        var request = new CreateBasalInjectionRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            PatientInsulinId = Guid.NewGuid(),
            Units = 0,
        };

        var result = await controller.Create(request);

        var objectResult = result.Result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(400);
        _repoMock.Verify(r => r.CreateAsync(It.IsAny<BasalInjection>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);

        var negativeRequest = new CreateBasalInjectionRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            PatientInsulinId = Guid.NewGuid(),
            Units = -1.5,
        };

        var negativeResult = await controller.Create(negativeRequest);
        negativeResult.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task Create_returns_400_when_units_exceeds_500()
    {
        var controller = CreateController();
        var request = new CreateBasalInjectionRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            PatientInsulinId = Guid.NewGuid(),
            Units = 500.01,
        };

        var result = await controller.Create(request);

        var objectResult = result.Result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(400);
        _repoMock.Verify(r => r.CreateAsync(It.IsAny<BasalInjection>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// The ceiling is inclusive: 500 units is a legal dose, and only the next representable double
    /// above it is not. Paired with <see cref="Create_rejects_the_smallest_step_above_the_ceiling"/>
    /// these straddle the exact comparison, so widening or narrowing it by one step reddens one of them.
    /// </summary>
    [Fact]
    public async Task Create_accepts_units_exactly_at_the_ceiling()
    {
        BasalInjection? captured = null;
        SetupCreatePassthrough(b => captured = b);

        var request = new CreateBasalInjectionRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Units = 500,
        };

        var result = await CreateController().Create(request);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        captured.Should().NotBeNull();
        captured!.Units.Should().Be(500);
    }

    [Fact]
    public async Task Create_rejects_the_smallest_step_above_the_ceiling()
    {
        var request = new CreateBasalInjectionRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Units = Math.BitIncrement(500),
        };

        var result = await CreateController().Create(request);

        var objectResult = result.Result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(400);
        objectResult.Value.Should().BeOfType<ProblemDetails>()
            .Which.Detail.Should().Be("Units must be > 0 and <= 500.");
        _repoMock.Verify(r => r.CreateAsync(It.IsAny<BasalInjection>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_returns_400_when_timestamp_is_more_than_5_minutes_in_future()
    {
        var controller = CreateController();
        // No PatientInsulinId: an unresolvable reference would collect its own 400 and pass this
        // test whatever the future tolerance is.
        var request = new CreateBasalInjectionRequest
        {
            Timestamp = DateTimeOffset.UtcNow.AddMinutes(10),
            Units = 12,
        };

        var result = await controller.Create(request);

        var objectResult = result.Result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(400);
        objectResult.Value.Should().BeOfType<ProblemDetails>()
            .Which.Detail.Should().Be("Timestamp cannot be more than 5 minutes in the future.");
        _repoMock.Verify(r => r.CreateAsync(It.IsAny<BasalInjection>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// The bulk endpoint rejects an unset timestamp through <c>V4BulkValidation</c>; the single
    /// create carries the same guard, in the same wording, or it persists 0001-01-01 injections
    /// the bulk path refuses.
    /// </summary>
    [Fact]
    public async Task Create_returns_400_when_timestamp_is_unset()
    {
        var controller = CreateController();
        var request = new CreateBasalInjectionRequest
        {
            Timestamp = default,
            Units = 12,
        };

        var result = await controller.Create(request);

        var objectResult = result.Result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(400);
        objectResult.Value.Should().BeOfType<ProblemDetails>()
            .Which.Detail.Should().Be("Timestamp must be set");
        _repoMock.Verify(r => r.CreateAsync(It.IsAny<BasalInjection>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A request breaking both rules is answered for the timestamp, not the units: the CRUD base
    /// runs its unset-timestamp guard before the hook carrying the units rule, which is the order
    /// the bulk path already reported in.
    /// </summary>
    [Fact]
    public async Task Create_with_both_unset_timestamp_and_bad_units_reports_the_timestamp()
    {
        var request = new CreateBasalInjectionRequest
        {
            Timestamp = default,
            Units = 0,
        };

        var result = await CreateController().Create(request);

        var objectResult = result.Result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(400);
        objectResult.Value.Should().BeOfType<ProblemDetails>()
            .Which.Detail.Should().Be("Timestamp must be set");
        _repoMock.Verify(r => r.CreateAsync(It.IsAny<BasalInjection>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_returns_400_when_PatientInsulin_not_found()
    {
        var insulinId = Guid.NewGuid();
        _insulinRepoMock.Setup(r => r.GetByIdAsync(insulinId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PatientInsulin?)null);

        var controller = CreateController();
        var request = new CreateBasalInjectionRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            PatientInsulinId = insulinId,
            Units = 10,
        };

        var result = await controller.Create(request);

        var objectResult = result.Result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(400);
        _repoMock.Verify(r => r.CreateAsync(It.IsAny<BasalInjection>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_returns_400_when_PatientInsulin_role_is_BolusOnly()
    {
        var insulinId = Guid.NewGuid();
        var insulin = BasalInsulin(insulinId, role: InsulinRole.Bolus);
        _insulinRepoMock.Setup(r => r.GetByIdAsync(insulinId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(insulin);

        var controller = CreateController();
        var request = new CreateBasalInjectionRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            PatientInsulinId = insulinId,
            Units = 10,
        };

        var result = await controller.Create(request);

        var objectResult = result.Result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(400);
        _repoMock.Verify(r => r.CreateAsync(It.IsAny<BasalInjection>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_returns_400_when_PatientInsulin_inactive_at_timestamp()
    {
        var insulinId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        // EndDate before injection time -> inactive
        var insulin = BasalInsulin(
            insulinId,
            role: InsulinRole.Basal,
            startDate: today.AddDays(-30),
            endDate: today.AddDays(-1));
        _insulinRepoMock.Setup(r => r.GetByIdAsync(insulinId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(insulin);

        var controller = CreateController();
        var request = new CreateBasalInjectionRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            PatientInsulinId = insulinId,
            Units = 10,
        };

        var result = await controller.Create(request);

        var objectResult = result.Result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(400);
        _repoMock.Verify(r => r.CreateAsync(It.IsAny<BasalInjection>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_returns_existing_row_when_DataSource_and_SyncIdentifier_match_existing()
    {
        var existing = new BasalInjection
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTime.UtcNow.AddHours(-1),
            Units = 14,
            DataSource = "loop",
            SyncIdentifier = "abc-123",
        };

        _repoMock.Setup(r => r.FindBySyncIdentifierAsync("loop", "abc-123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var controller = CreateController();
        var request = new CreateBasalInjectionRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            PatientInsulinId = Guid.NewGuid(),
            Units = 14,
            DataSource = "loop",
            SyncIdentifier = "abc-123",
        };

        var result = await controller.Create(request);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(existing);

        _repoMock.Verify(r => r.CreateAsync(It.IsAny<BasalInjection>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
        _insulinRepoMock.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_creates_row_and_enriches_InsulinContext()
    {
        var insulinId = Guid.NewGuid();
        var insulin = new PatientInsulin
        {
            Id = insulinId,
            Name = "Tresiba",
            Dia = 24,
            Peak = 720,
            Curve = "ultra-long",
            Concentration = 100,
            Role = InsulinRole.Basal,
        };
        _insulinRepoMock.Setup(r => r.GetByIdAsync(insulinId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(insulin);

        BasalInjection? captured = null;
        SetupCreatePassthrough(b => captured = b);

        var controller = CreateController();
        var request = new CreateBasalInjectionRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            PatientInsulinId = insulinId,
            Units = 18,
        };

        var result = await controller.Create(request);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        captured.Should().NotBeNull();
        captured!.Units.Should().Be(18);
        captured.InsulinContext.Should().NotBeNull();
        captured.InsulinContext!.PatientInsulinId.Should().Be(insulinId);
        captured.InsulinContext.InsulinName.Should().Be("Tresiba");
        captured.InsulinContext.Dia.Should().Be(24);
        captured.InsulinContext.Peak.Should().Be(720);
        captured.InsulinContext.Curve.Should().Be("ultra-long");
        captured.InsulinContext.Concentration.Should().Be(100);
        captured.CorrelationId.Should().NotBeNull().And.NotBe(Guid.Empty);
    }

    [Fact]
    public async Task Create_without_PatientInsulinId_creates_row_with_null_InsulinContext()
    {
        BasalInjection? captured = null;
        SetupCreatePassthrough(b => captured = b);

        var controller = CreateController();
        var request = new CreateBasalInjectionRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Units = 22,
        };

        var result = await controller.Create(request);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        captured.Should().NotBeNull();
        captured!.Units.Should().Be(22);
        captured.InsulinContext.Should().BeNull(
            "an uploader that knows nothing about the insulin catalog stores no snapshot");

        // No reference to resolve, so the insulin catalog is never consulted.
        _insulinRepoMock.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Stores an existing injection carrying an insulin context, so an update that drops the
    /// reference has something to drop.
    /// </summary>
    private Guid SetupExistingWithInsulinContext()
    {
        var id = Guid.NewGuid();
        _repoMock.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BasalInjection
            {
                Id = id,
                Timestamp = DateTime.UtcNow.AddHours(-2),
                Units = 10,
                InsulinContext = new TreatmentInsulinContext
                {
                    PatientInsulinId = Guid.NewGuid(),
                    InsulinName = "Tresiba",
                },
            });
        return id;
    }

    private void SetupUpdatePassthrough(Guid id, Action<BasalInjection> onUpdate)
    {
        _repoMock
            .Setup(r => r.UpdateAsync(id, It.IsAny<BasalInjection>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, BasalInjection, WriteOrigin, CancellationToken>((_, b, _, _) => onUpdate(b))
            .ReturnsAsync((Guid _, BasalInjection b, WriteOrigin _, CancellationToken _) => b);
    }

    private void VerifyNoUpdate() => _repoMock.Verify(
        r => r.UpdateAsync(It.IsAny<Guid>(), It.IsAny<BasalInjection>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
        Times.Never);

    /// <summary>
    /// <c>InsulinContext</c> is not among the fields an update carries forward from the stored
    /// record, unlike <c>LegacyId</c> and <c>CreatedAt</c>: dropping the reference drops the snapshot.
    /// </summary>
    [Fact]
    public async Task Update_without_PatientInsulinId_does_not_carry_the_stored_InsulinContext_forward()
    {
        var id = SetupExistingWithInsulinContext();
        BasalInjection? captured = null;
        SetupUpdatePassthrough(id, b => captured = b);

        var request = new UpdateBasalInjectionRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Units = 11,
        };

        var result = await CreateController().Update(id, request);

        result.Result.Should().BeOfType<OkObjectResult>();
        captured.Should().NotBeNull();
        captured!.InsulinContext.Should().BeNull();
        _insulinRepoMock.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1.5)]
    [InlineData(500.01)]
    public async Task Update_returns_400_when_units_out_of_range(double units)
    {
        var id = SetupExistingWithInsulinContext();

        var request = new UpdateBasalInjectionRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Units = units,
        };

        var result = await CreateController().Update(id, request);

        var objectResult = result.Result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(400);
        objectResult.Value.Should().BeOfType<ProblemDetails>()
            .Which.Detail.Should().Be("Units must be > 0 and <= 500.");
        VerifyNoUpdate();
    }

    [Fact]
    public async Task Update_returns_400_when_timestamp_is_more_than_5_minutes_in_future()
    {
        var id = SetupExistingWithInsulinContext();

        // No PatientInsulinId: an unresolvable reference would collect its own 400 and pass this
        // test whatever the future tolerance is.
        var request = new UpdateBasalInjectionRequest
        {
            Timestamp = DateTimeOffset.UtcNow.AddMinutes(10),
            Units = 11,
        };

        var result = await CreateController().Update(id, request);

        var objectResult = result.Result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(400);
        objectResult.Value.Should().BeOfType<ProblemDetails>()
            .Which.Detail.Should().Be("Timestamp cannot be more than 5 minutes in the future.");
        VerifyNoUpdate();
    }

    [Fact]
    public async Task Update_returns_400_when_PatientInsulin_not_found()
    {
        var id = SetupExistingWithInsulinContext();
        var insulinId = Guid.NewGuid();
        _insulinRepoMock.Setup(r => r.GetByIdAsync(insulinId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PatientInsulin?)null);

        var request = new UpdateBasalInjectionRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            PatientInsulinId = insulinId,
            Units = 11,
        };

        var result = await CreateController().Update(id, request);

        var objectResult = result.Result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(400);
        objectResult.Value.Should().BeOfType<ProblemDetails>()
            .Which.Detail.Should().Be("PatientInsulin not found.");
        VerifyNoUpdate();
    }

    [Fact]
    public async Task DeleteBySyncIdentifier_returns_204_when_a_row_was_deleted()
    {
        _repoMock
            .Setup(r => r.DeleteBySyncIdentifierAsync("loop", "abc-123", It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var controller = CreateController();

        var result = await controller.DeleteBySyncIdentifier("loop", "abc-123");

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task DeleteBySyncIdentifier_returns_404_when_nothing_matched()
    {
        _repoMock
            .Setup(r => r.DeleteBySyncIdentifierAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var controller = CreateController();

        var result = await controller.DeleteBySyncIdentifier("loop", "does-not-exist");

        result.Should().BeOfType<NotFoundResult>();
    }

    [Theory]
    [InlineData("", "abc-123")]
    [InlineData("loop", "")]
    [InlineData("", "")]
    public async Task DeleteBySyncIdentifier_returns_400_when_a_parameter_is_missing(
        string dataSource, string syncIdentifier)
    {
        var controller = CreateController();

        var result = await controller.DeleteBySyncIdentifier(dataSource, syncIdentifier);

        result.Should().BeOfType<BadRequestObjectResult>()
            .Which.StatusCode.Should().Be(400);
        _repoMock.Verify(
            r => r.DeleteBySyncIdentifierAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
