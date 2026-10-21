using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Uploads;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Uploads;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.Uploads;

[Trait("Category", "Unit")]
public class RejectedUploadRecorderTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    private readonly Mock<IRejectedUploadStore> _store = new();
    private readonly Mock<IInAppNotificationService> _notifications = new();
    private readonly Mock<ITenantOwnerResolver> _owners = new();
    private readonly RejectedUploadRecorder _recorder;

    public RejectedUploadRecorderTests()
    {
        var tenant = new Mock<ITenantAccessor>();
        tenant.Setup(t => t.TenantId).Returns(Tenant);
        _owners.Setup(o => o.GetOwnerSubjectIdAsync(Tenant, It.IsAny<CancellationToken>())).ReturnsAsync("owner");
        _recorder = new RejectedUploadRecorder(
            _store.Object, _notifications.Object, _owners.Object, tenant.Object,
            TimeProvider.System, NullLogger<RejectedUploadRecorder>.Instance);
    }

    [Fact]
    public async Task Records_each_refused_treatment_as_received_and_tells_the_owner_once()
    {
        IReadOnlyList<RejectedUpload>? recorded = null;
        _store.Setup(s => s.AddAsync(It.IsAny<IReadOnlyList<RejectedUpload>>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyList<RejectedUpload> uploads, CancellationToken _) => recorded = uploads);
        var treatment = new Treatment { Id = "t-1", EventType = "Note", Notes = "hi", EnteredBy = "xDrip", DataSource = "nightscout" };

        await _recorder.RecordTreatmentsAsync([new BulkWriteRejection<Treatment>(0, treatment, "too long")]);

        var upload = recorded.Should().ContainSingle().Subject;
        upload.RecordType.Should().Be("treatment");
        upload.Reason.Should().Be("too long");
        upload.App.Should().Be("xDrip");
        upload.Origin.Should().Be("nightscout");
        JsonDocument.Parse(upload.Payload).RootElement.GetProperty("notes").GetString().Should().Be("hi");
        _notifications.Verify(n => n.ArchiveBySourceAsync(
            "owner", RejectedUploadRecorder.NotificationType, It.IsAny<string>(), NotificationArchiveReason.Superseded, It.IsAny<CancellationToken>()),
            Times.Once);
        _notifications.Verify(n => n.CreateNotificationAsync(
            "owner", RejectedUploadRecorder.NotificationType, "rejected_uploads", It.IsAny<NotificationCategory?>(),
            It.IsAny<NotificationUrgency?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<List<NotificationActionDto>?>(), It.IsAny<ResolutionConditions?>(), It.IsAny<Dictionary<string, object>?>(),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task A_store_failure_is_logged_rather_than_thrown()
    {
        _store.Setup(s => s.AddAsync(It.IsAny<IReadOnlyList<RejectedUpload>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException("store down"));

        var act = () => _recorder.RecordTreatmentsAsync(
            [new BulkWriteRejection<Treatment>(0, new Treatment { Id = "t-1" }, "too long")]);

        await act.Should().NotThrowAsync();
        _notifications.VerifyNoOtherCalls();
    }
}
