using FluentAssertions;
using Microsoft.AspNetCore.Http;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Controllers.V4.Treatments;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Validators.V4;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

/// <summary>
/// Unit tests for the temp basal controller: the bulk write (per-item upsert dispatch, cancel
/// truncation, pre-persist validation) and the record actions it takes from the V4 record base.
/// </summary>
[Trait("Category", "Unit")]
public class TempBasalControllerTests
{
    private readonly Mock<ITempBasalRepository> _repo = new();
    private readonly Mock<IPatientDeviceStamper> _stamper = new();
    private readonly TempBasalController _controller;

    private static readonly DateTimeOffset T0 = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    public TempBasalControllerTests()
    {
        _controller = new TempBasalController(_repo.Object, _stamper.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        _repo.Setup(r => r.CreateAsync(It.IsAny<TempBasal>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TempBasal m, WriteOrigin _, CancellationToken _) => m);
        _repo.Setup(r => r.UpdateAsync(It.IsAny<Guid>(), It.IsAny<TempBasal>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, TempBasal m, WriteOrigin _, CancellationToken _) => m);
    }

    private static readonly IServiceProvider Validators = BuildValidatorProvider();

    private static IServiceProvider BuildValidatorProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMvcCore();
        services.AddValidatorsFromAssemblyContaining<CreateTempBasalRequestValidator>();
        return services.BuildServiceProvider();
    }

    private static CreateTempBasalRequest Request(
        double rate = 1.5, double? durationMinutes = 30, bool isCancel = false, DateTimeOffset? timestamp = null)
        => new()
        {
            Timestamp = timestamp ?? T0,
            Rate = rate,
            DurationMinutes = durationMinutes,
            IsCancel = isCancel,
            DataSource = "trio",
            SyncIdentifier = Guid.NewGuid().ToString(),
            Origin = TempBasalOrigin.Algorithm,
        };

    [Fact]
    public async Task CreateBulk_MapsDurationToEndTimestamp()
    {
        var result = await _controller.CreateBulk([Request(rate: 2.0, durationMinutes: 30)], CancellationToken.None);

        var created = ((ObjectResult)result.Result!).Value.Should().BeOfType<TempBasal[]>().Subject;
        created.Should().HaveCount(1);
        created[0].Rate.Should().Be(2.0);
        created[0].StartTimestamp.Should().Be(T0.UtcDateTime);
        created[0].EndTimestamp.Should().Be(T0.UtcDateTime.AddMinutes(30));
        created[0].Origin.Should().Be(TempBasalOrigin.Algorithm);
    }

    [Theory]
    [InlineData(TempBasalOrigin.Algorithm, true)]
    [InlineData(TempBasalOrigin.Manual, false)]
    [InlineData(TempBasalOrigin.Scheduled, null)]
    public async Task CreateBulk_StampsAutomaticFromAnExplicitOrigin(TempBasalOrigin origin, bool? expected)
    {
        var request = Request();
        request.Origin = origin;

        var result = await _controller.CreateBulk([request], CancellationToken.None);

        var created = ((ObjectResult)result.Result!).Value.Should().BeOfType<TempBasal[]>().Subject;
        TempBasalAutomaticFlag.Of(created[0].AdditionalProperties).Should().Be(expected);
    }

    [Theory]
    [InlineData("Trio", TempBasalOrigin.Algorithm)]
    [InlineData("openaps://AndroidAPS", TempBasalOrigin.Algorithm)]
    [InlineData("xdrip", TempBasalOrigin.Manual)]
    [InlineData(null, TempBasalOrigin.Manual)]
    public async Task CreateBulk_DefaultsAnOmittedOriginByUploaderAndLeavesAutomaticUnstamped(
        string? app, TempBasalOrigin expected)
    {
        var request = Request();
        request.Origin = null;
        request.App = app;

        var result = await _controller.CreateBulk([request], CancellationToken.None);

        var created = ((ObjectResult)result.Result!).Value.Should().BeOfType<TempBasal[]>().Subject;
        created[0].Origin.Should().Be(expected);
        created[0].OriginEvidence.Should().Be(TempBasalOriginEvidence.Uploader);
        TempBasalAutomaticFlag.Of(created[0].AdditionalProperties).Should().BeNull();
    }

    [Fact]
    public async Task CreateBulk_Cancel_TruncatesActiveSpan()
    {
        var active = new TempBasal
        {
            Id = Guid.NewGuid(),
            StartTimestamp = T0.UtcDateTime.AddMinutes(-10),
            EndTimestamp = null,
            Rate = 1.0,
        };
        _repo.Setup(r => r.GetActiveAtAsync(T0.UtcDateTime, It.IsAny<CancellationToken>()))
            .ReturnsAsync(active);

        var result = await _controller.CreateBulk([Request(isCancel: true)], CancellationToken.None);

        var returned = ((ObjectResult)result.Result!).Value.Should().BeOfType<TempBasal[]>().Subject;
        returned.Should().HaveCount(1);
        returned[0].EndTimestamp.Should().Be(T0.UtcDateTime);
        _repo.Verify(
            r => r.UpdateAsync(active.Id, It.Is<TempBasal>(m => m.EndTimestamp == T0.UtcDateTime), WriteOrigin.Live, It.IsAny<CancellationToken>()),
            Times.Once);
        _repo.Verify(r => r.CreateAsync(It.IsAny<TempBasal>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateBulk_CancelWithNothingActive_IsNoOp()
    {
        _repo.Setup(r => r.GetActiveAtAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TempBasal?)null);

        var result = await _controller.CreateBulk([Request(isCancel: true)], CancellationToken.None);

        var returned = ((ObjectResult)result.Result!).Value.Should().BeOfType<TempBasal[]>().Subject;
        returned.Should().BeEmpty();
        _repo.Verify(r => r.UpdateAsync(It.IsAny<Guid>(), It.IsAny<TempBasal>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateBulk_ProcessesItemsInTimestampOrder()
    {
        // A batch that starts a temp basal and cancels it later must apply the start first,
        // even when the array arrives out of order.
        var processed = new List<string>();
        _repo.Setup(r => r.CreateAsync(It.IsAny<TempBasal>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback(() => processed.Add("create"))
            .ReturnsAsync((TempBasal m, WriteOrigin _, CancellationToken _) => m);
        _repo.Setup(r => r.GetActiveAtAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Callback(() => processed.Add("cancel"))
            .ReturnsAsync((TempBasal?)null);

        var cancelLater = Request(isCancel: true, timestamp: T0.AddMinutes(15));
        var startFirst = Request(timestamp: T0);

        await _controller.CreateBulk([cancelLater, startFirst], CancellationToken.None);

        processed.Should().Equal("create", "cancel");
    }

    [Fact]
    public async Task CreateBulk_SkipsDeletedSpanAndWritesTheRest()
    {
        var before = Request(rate: 1.0, timestamp: T0);
        var deleted = Request(rate: 2.0, timestamp: T0.AddMinutes(10));
        var after = Request(rate: 3.0, timestamp: T0.AddMinutes(20));
        _repo.Setup(r => r.CreateAsync(It.Is<TempBasal>(m => m.SyncIdentifier == deleted.SyncIdentifier), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(RecreationBlockedException.ForSyncKey(nameof(TempBasal), deleted.DataSource!, deleted.SyncIdentifier!));

        var result = await _controller.CreateBulk([after, deleted, before], CancellationToken.None);

        var objectResult = (ObjectResult)result.Result!;
        objectResult.StatusCode.Should().Be(StatusCodes.Status201Created);
        var written = objectResult.Value.Should().BeOfType<TempBasal[]>().Subject;
        written.Select(t => t.SyncIdentifier).Should().Equal(before.SyncIdentifier, after.SyncIdentifier);
        _repo.Verify(r => r.CreateAsync(It.IsAny<TempBasal>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task CreateBulk_SingleDeletedSpan_AnswersCreatedWithEmptyArray()
    {
        _repo.Setup(r => r.CreateAsync(It.IsAny<TempBasal>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(RecreationBlockedException.ForSyncKey(nameof(TempBasal), "trio", "tb-1"));

        var result = await _controller.CreateBulk([Request()], CancellationToken.None);

        var objectResult = (ObjectResult)result.Result!;
        objectResult.StatusCode.Should().Be(StatusCodes.Status201Created);
        objectResult.Value.Should().BeOfType<TempBasal[]>().Which.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateBulk_RejectsSyncIdentifierWithoutDataSource()
    {
        var request = Request();
        request.DataSource = null;

        var result = await _controller.CreateBulk([request], CancellationToken.None);

        ((ObjectResult)result.Result!).StatusCode.Should().Be(400);
        _repo.Verify(r => r.CreateAsync(It.IsAny<TempBasal>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(-0.5, 30.0, "Rate must be >= 0")]
    [InlineData(1.0, -5.0, "DurationMinutes must be >= 0")]
    public void CreateValidator_RejectsNegativeRateAndDuration(double rate, double duration, string message)
    {
        var result = new CreateTempBasalRequestValidator().Validate(Request(rate: rate, durationMinutes: duration));

        result.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Be(message);
    }

    [Fact]
    public void CreateValidator_IgnoresRateAndDurationOnACancel()
    {
        var result = new CreateTempBasalRequestValidator().Validate(Request(rate: -1, durationMinutes: -1, isCancel: true));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void CreateValidator_ExemptsACancelFromEveryRuleButTheTimestamp()
    {
        var cancel = Request(isCancel: true);
        cancel.CorrelationId = Guid.Empty;
        cancel.Device = new string('d', 257);
        cancel.App = new string('a', 257);
        cancel.DataSource = null;
        cancel.SyncIdentifier = new string('s', 257);

        var validator = new CreateTempBasalRequestValidator();

        validator.Validate(cancel).IsValid.Should().BeTrue();
        cancel.Timestamp = default;
        validator.Validate(cancel).Errors.Should().ContainSingle().Which.ErrorMessage.Should().Be("Timestamp is required");
    }

    [Fact]
    public void CreateValidator_RejectsAnEmptyCorrelationIdOnASpan()
    {
        var span = Request();
        span.CorrelationId = Guid.Empty;

        new CreateTempBasalRequestValidator().Validate(span).Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should().Be("CorrelationId must be a non-empty GUID when supplied.");
    }

    [Theory]
    [InlineData(nameof(CreateTempBasalRequest.Device))]
    [InlineData(nameof(CreateTempBasalRequest.App))]
    [InlineData(nameof(CreateTempBasalRequest.DataSource))]
    [InlineData(nameof(CreateTempBasalRequest.SyncIdentifier))]
    public void Validators_CapStringsAtTheColumnLength(string property)
    {
        var create = Request();
        var update = new UpdateTempBasalRequest { Timestamp = T0, DataSource = "trio" };
        foreach (var target in new object[] { create, update })
            target.GetType().GetProperty(property)!.SetValue(target, new string('x', 256));

        new CreateTempBasalRequestValidator().Validate(create).IsValid.Should().BeTrue();
        new UpdateTempBasalRequestValidator().Validate(update).IsValid.Should().BeTrue();

        foreach (var target in new object[] { create, update })
            target.GetType().GetProperty(property)!.SetValue(target, new string('x', 257));

        new CreateTempBasalRequestValidator().Validate(create).Errors.Should().ContainSingle()
            .Which.PropertyName.Should().Be(property);
        new UpdateTempBasalRequestValidator().Validate(update).Errors.Should().ContainSingle()
            .Which.PropertyName.Should().Be(property);
    }

    [Theory]
    [InlineData(-0.5, 30.0, "Rate: Rate must be >= 0")]
    [InlineData(1.0, -5.0, "DurationMinutes: DurationMinutes must be >= 0")]
    public async Task CreateBulk_RejectsANegativeRateOrDurationWithoutWriting(double rate, double duration, string message)
    {
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { RequestServices = Validators },
        };

        var result = await _controller.CreateBulk([Request(rate: rate, durationMinutes: duration)], CancellationToken.None);

        var objectResult = result.Result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(400);
        objectResult.Value.Should().BeOfType<ProblemDetails>().Which.Detail
            .Should().Be($"Temp basal at index 0 is invalid: {message}");
        _repo.Verify(r => r.CreateAsync(It.IsAny<TempBasal>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
        _repo.Verify(r => r.GetActiveAtAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void UpdateValidator_RejectsNegativeRateAndDuration()
    {
        var result = new UpdateTempBasalRequestValidator().Validate(
            new UpdateTempBasalRequest { Timestamp = T0, Rate = -1, DurationMinutes = -1 });

        result.Errors.Select(e => e.ErrorMessage).Should().BeEquivalentTo("Rate must be >= 0", "DurationMinutes must be >= 0");
    }

    [Fact]
    public async Task Create_WritesOneSpanAndStampsTheDevice()
    {
        var result = await _controller.Create(Request(rate: 2.0, durationMinutes: 30), CancellationToken.None);

        var created = (CreatedAtActionResult)result.Result!;
        created.Value.Should().BeOfType<TempBasal>().Which.EndTimestamp.Should().Be(T0.UtcDateTime.AddMinutes(30));
        _stamper.Verify(
            s => s.StampAsync(It.Is<IReadOnlyList<IDeviceAttributed>>(r => r.Count == 1), DeviceAttributionCategories.TempBasal, "trio", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Create_RejectsACancelWithoutWriting()
    {
        var result = await _controller.Create(Request(isCancel: true), CancellationToken.None);

        ((ObjectResult)result.Result!).StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        _repo.Verify(r => r.CreateAsync(It.IsAny<TempBasal>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
        _repo.Verify(r => r.GetActiveAtAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_KeepsTheStoredIdentityAndOriginWhenTheRequestOmitsThem()
    {
        var id = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var patientDeviceId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var existing = new TempBasal
        {
            Id = id,
            StartTimestamp = T0.UtcDateTime,
            LegacyId = "legacy-1",
            CreatedAt = T0.UtcDateTime.AddDays(-1),
            DeviceId = deviceId,
            PatientDeviceId = patientDeviceId,
            PumpRecordId = "pump-9",
            CorrelationId = correlationId,
            Origin = TempBasalOrigin.Algorithm,
            OriginEvidence = TempBasalOriginEvidence.Reported,
            AdditionalProperties = new() { ["automatic"] = true },
        };
        var stored = SetupUpdate(id, existing);

        var result = await _controller.Update(
            id, new UpdateTempBasalRequest { Timestamp = T0.AddMinutes(5), Rate = 0.5, DurationMinutes = 20 }, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        var written = stored.Single();
        written.StartTimestamp.Should().Be(T0.UtcDateTime.AddMinutes(5));
        written.EndTimestamp.Should().Be(T0.UtcDateTime.AddMinutes(25));
        written.Rate.Should().Be(0.5);
        written.LegacyId.Should().Be("legacy-1");
        written.CreatedAt.Should().Be(existing.CreatedAt);
        written.DeviceId.Should().Be(deviceId);
        written.PatientDeviceId.Should().Be(patientDeviceId);
        written.PumpRecordId.Should().Be("pump-9");
        written.CorrelationId.Should().Be(correlationId);
        written.Origin.Should().Be(TempBasalOrigin.Algorithm);
        written.OriginEvidence.Should().Be(TempBasalOriginEvidence.Reported);
        TempBasalAutomaticFlag.Of(written.AdditionalProperties).Should().BeTrue();
    }

    [Fact]
    public async Task Update_SuppliedOriginReplacesTheStoredOneAndItsAutomaticFlag()
    {
        var id = Guid.NewGuid();
        var existing = new TempBasal
        {
            Id = id,
            StartTimestamp = T0.UtcDateTime,
            Origin = TempBasalOrigin.Algorithm,
            AdditionalProperties = new() { ["automatic"] = true },
        };
        var stored = SetupUpdate(id, existing);

        await _controller.Update(
            id, new UpdateTempBasalRequest { Timestamp = T0, Rate = 1, Origin = TempBasalOrigin.Manual }, CancellationToken.None);

        var written = stored.Single();
        written.Origin.Should().Be(TempBasalOrigin.Manual);
        TempBasalAutomaticFlag.Of(written.AdditionalProperties).Should().BeFalse();
        TempBasalAutomaticFlag.Of(existing.AdditionalProperties).Should().BeTrue("the stored record is not mutated");
    }

    [Fact]
    public async Task Update_ChangedDevice_AttributesTheSpanAfresh()
    {
        var id = Guid.NewGuid();
        var newPatientDevice = Guid.NewGuid();
        var existing = new TempBasal
        {
            Id = id,
            StartTimestamp = T0.UtcDateTime,
            Device = "pump-a",
            DataSource = "trio",
            PatientDeviceId = Guid.NewGuid(),
        };
        var stored = SetupUpdate(id, existing);
        _stamper.Setup(s => s.StampAsync(It.IsAny<IReadOnlyList<IDeviceAttributed>>(), DeviceAttributionCategories.TempBasal, "trio", It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<IDeviceAttributed>, IReadOnlyList<DeviceCategory>, string?, CancellationToken>(
                (records, _, _, _) => records[0].PatientDeviceId = newPatientDevice)
            .Returns(Task.CompletedTask);

        await _controller.Update(
            id, new UpdateTempBasalRequest { Timestamp = T0, Rate = 1, Device = "pump-b", DataSource = "trio" }, CancellationToken.None);

        stored.Single().PatientDeviceId.Should().Be(newPatientDevice);
        _stamper.Verify(s => s.StampAsync(It.IsAny<IReadOnlyList<IDeviceAttributed>>(), It.IsAny<IReadOnlyList<DeviceCategory>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Update_SameDevice_KeepsTheStoredAttributionWithoutStamping()
    {
        var id = Guid.NewGuid();
        var patientDeviceId = Guid.NewGuid();
        var stored = SetupUpdate(id, new TempBasal
        {
            Id = id,
            StartTimestamp = T0.UtcDateTime,
            Device = "pump-a",
            PatientDeviceId = patientDeviceId,
        });

        await _controller.Update(id, new UpdateTempBasalRequest { Timestamp = T0, Rate = 1, Device = "pump-a" }, CancellationToken.None);

        stored.Single().PatientDeviceId.Should().Be(patientDeviceId);
        _stamper.Verify(s => s.StampAsync(It.IsAny<IReadOnlyList<IDeviceAttributed>>(), It.IsAny<IReadOnlyList<DeviceCategory>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_UnknownId_ReturnsNotFound()
    {
        var result = await _controller.Update(Guid.NewGuid(), new UpdateTempBasalRequest { Timestamp = T0 }, CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Update_NoTimestamp_ReturnsBadRequestWithoutWriting()
    {
        var id = Guid.NewGuid();
        _repo.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(new TempBasal { Id = id, StartTimestamp = T0.UtcDateTime });

        var result = await _controller.Update(id, new UpdateTempBasalRequest(), CancellationToken.None);

        ((ObjectResult)result.Result!).StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        _repo.Verify(r => r.UpdateAsync(It.IsAny<Guid>(), It.IsAny<TempBasal>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetById_UnknownId_ReturnsNotFound()
    {
        var result = await _controller.GetById(Guid.NewGuid(), CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Delete_SoftDeletesTheSpan_AndAnUnknownIdIsNotFound()
    {
        var known = Guid.NewGuid();
        var unknown = Guid.NewGuid();
        _repo.Setup(r => r.DeleteAsync(unknown, WriteOrigin.Live, It.IsAny<CancellationToken>())).ThrowsAsync(new KeyNotFoundException());

        (await _controller.Delete(known, CancellationToken.None)).Should().BeOfType<NoContentResult>();
        (await _controller.Delete(unknown, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        _repo.Verify(r => r.DeleteAsync(known, WriteOrigin.Live, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ListDeleted_ReturnsTheDeletedSpansWithTheirTotal()
    {
        var id = Guid.NewGuid();
        _repo.Setup(r => r.GetDeletedAsync(100, 0, It.IsAny<CancellationToken>())).ReturnsAsync([new TempBasal { Id = id }]);
        _repo.Setup(r => r.CountDeletedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var result = await _controller.ListDeleted(ct: CancellationToken.None);

        var response = ((OkObjectResult)result.Result!).Value.Should().BeOfType<PaginatedResponse<TempBasal>>().Subject;
        response.Data.Should().ContainSingle().Which.Id.Should().Be(id);
        response.Pagination.Total.Should().Be(1);
    }

    [Fact]
    public async Task Restore_ReturnsTheRestoredSpan_AndAnUnknownIdIsNotFound()
    {
        var id = Guid.NewGuid();
        var unknown = Guid.NewGuid();
        _repo.Setup(r => r.RestoreAsync(id, WriteOrigin.Live, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TempBasal { Id = id, StartTimestamp = T0.UtcDateTime });
        _repo.Setup(r => r.RestoreAsync(unknown, WriteOrigin.Live, It.IsAny<CancellationToken>())).ThrowsAsync(new KeyNotFoundException());

        ((OkObjectResult)(await _controller.Restore(id, CancellationToken.None)).Result!).Value
            .Should().BeOfType<TempBasal>().Which.Id.Should().Be(id);
        (await _controller.Restore(unknown, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task BulkRestore_ReturnsTheRestoredSpansAndTheConflicts()
    {
        var id = Guid.NewGuid();
        var conflicted = Guid.NewGuid();
        _repo.Setup(r => r.BulkRestoreAsync(It.IsAny<IEnumerable<Guid>>(), WriteOrigin.Live, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkRestoreResult<TempBasal>
            {
                Restored = [new TempBasal { Id = id, StartTimestamp = T0.UtcDateTime }],
                Conflicts = [conflicted],
            });

        var result = await _controller.BulkRestore([id, conflicted], CancellationToken.None);

        var response = ((OkObjectResult)result.Result!).Value.Should().BeOfType<BulkRestoreResult<TempBasal>>().Subject;
        response.Restored.Should().ContainSingle().Which.Id.Should().Be(id);
        response.Conflicts.Should().Equal(conflicted);
    }

    private List<TempBasal> SetupUpdate(Guid id, TempBasal existing)
    {
        var written = new List<TempBasal>();
        _repo.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        _repo.Setup(r => r.UpdateAsync(id, It.IsAny<TempBasal>(), WriteOrigin.Live, It.IsAny<CancellationToken>()))
            .Callback<Guid, TempBasal, WriteOrigin, CancellationToken>((_, m, _, _) => written.Add(m))
            .ReturnsAsync((Guid _, TempBasal m, WriteOrigin _, CancellationToken _) => m);
        return written;
    }

    [Fact]
    public async Task CreateBulk_RejectsOversizedBatch()
    {
        var requests = Enumerable.Range(0, 1001).Select(_ => Request()).ToArray();

        var result = await _controller.CreateBulk(requests, CancellationToken.None);

        ((ObjectResult)result.Result!).StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task GetAll_LimitAtCeiling_ReachesRepositoryUnchanged()
    {
        await _controller.GetAll(null, null, V4ReadLimits.MaxPageSize, 0);

        VerifyFetched(V4ReadLimits.MaxPageSize, 0);
    }

    [Fact]
    public async Task GetAll_LimitAboveCeiling_IsClamped()
    {
        await _controller.GetAll(null, null, V4ReadLimits.MaxPageSize + 1, -1);

        VerifyFetched(V4ReadLimits.MaxPageSize, 0);
    }

    [Fact]
    public async Task GetAll_FullPageWithoutIncludeTotal_LeavesTotalNullWithoutCounting()
    {
        ListOf(_repo).Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new TempBasal()]);

        var result = await _controller.GetAll(null, null, 1, 0);

        var response = ((OkObjectResult)result.Result!).Value.Should().BeOfType<PaginatedResponse<TempBasal>>().Subject;
        response.Pagination.Total.Should().BeNull();
        _repo.Verify(r => r.CountAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetAll_IncludeTotal_CountsWithTheDeviceAndSourceTheDataQueryUses()
    {
        ListOf(_repo).Setup(r => r.GetAsync(null, null, "pump-a", "tandem", 1, 0, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new TempBasal()]);
        _repo.Setup(r => r.CountAsync(null, null, "pump-a", "tandem", It.IsAny<CancellationToken>())).ReturnsAsync(6);

        var result = await _controller.GetAll(null, null, 1, 0, "timestamp_desc", "pump-a", "tandem", includeTotal: true);

        var response = ((OkObjectResult)result.Result!).Value.Should().BeOfType<PaginatedResponse<TempBasal>>().Subject;
        response.Pagination.Total.Should().Be(6);
    }

    private static Mock<IV4Repository<TempBasal>> ListOf(Mock<ITempBasalRepository> repo) =>
        repo.As<IV4Repository<TempBasal>>();

    private void VerifyFetched(int limit, int offset) =>
        ListOf(_repo).Verify(r => r.GetAsync(null, null, null, null, limit, offset, true, It.IsAny<CancellationToken>()), Times.Once);
}
