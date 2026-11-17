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
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Uploads;

[Trait("Category", "Unit")]
public class RejectedUploadRecorderTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    private readonly Mock<IRejectedUploadStore> _store = new();
    private readonly Mock<IInAppNotificationService> _notifications = new();
    private readonly Mock<ITenantOwnerResolver> _owners = new();
    private readonly List<RejectedUpload> _kept = [];
    private readonly RejectedUploadRecorder _recorder;

    public RejectedUploadRecorderTests()
    {
        var tenant = new Mock<ITenantAccessor>();
        tenant.Setup(t => t.TenantId).Returns(Tenant);
        _owners.Setup(o => o.GetOwnerSubjectIdAsync(Tenant, It.IsAny<CancellationToken>())).ReturnsAsync("owner");
        _store.Setup(s => s.UpsertAsync(It.IsAny<RejectedUpload>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((RejectedUpload upload, CancellationToken _) =>
            {
                var isNew = _kept.All(k => k.Identity != upload.Identity);
                _kept.Add(upload);
                return isNew;
            });
        _recorder = new RejectedUploadRecorder(
            _store.Object, _notifications.Object, _owners.Object, tenant.Object,
            TimeProvider.System, NullLogger<RejectedUploadRecorder>.Instance);
    }

    [Fact]
    public async Task Keeps_a_refused_treatment_as_received_and_tells_the_owner()
    {
        var treatment = new Treatment { Id = "t-1", EventType = "Note", Notes = "hi", EnteredBy = "xDrip", DataSource = "nightscout" };

        await _recorder.RecordTreatmentsAsync([Refused(treatment)]);

        var upload = _kept.Should().ContainSingle().Subject;
        upload.RecordType.Should().Be("treatment");
        upload.Reason.Should().Be("too long");
        upload.App.Should().Be("xDrip");
        upload.Origin.Should().Be("nightscout");
        JsonDocument.Parse(upload.Payload).RootElement.GetProperty("notes").GetString().Should().Be("hi");
        VerifyNotified(Times.Once());
    }

    [Fact]
    public async Task A_resend_of_a_kept_upload_does_not_tell_the_owner_again()
    {
        var treatment = new Treatment { Id = "t-1", EventType = "Note", Notes = "hi" };

        await _recorder.RecordTreatmentsAsync([Refused(treatment)]);
        await _recorder.RecordTreatmentsAsync([Refused(treatment)]);

        _kept.Select(k => k.Identity).Distinct().Should().ContainSingle();
        VerifyNotified(Times.Once());
    }

    [Fact]
    public async Task Without_a_client_id_the_payload_is_the_identity()
    {
        await _recorder.RecordTreatmentsAsync([Refused(new Treatment { EventType = "Note", Notes = "a" })]);
        await _recorder.RecordTreatmentsAsync([Refused(new Treatment { EventType = "Note", Notes = "a" })]);
        await _recorder.RecordTreatmentsAsync([Refused(new Treatment { EventType = "Note", Notes = "b" })]);

        _kept.Select(k => k.Identity).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task Text_jsonb_cannot_hold_is_replaced_so_the_refusal_can_be_kept()
    {
        var escaped = JsonDocument.Parse("{\"x\":\"\\ud800\"}").RootElement.GetProperty("x");
        var treatment = new Treatment
        {
            Id = "t-1",
            EventType = "Note",
            Notes = "a\uD800b\0c\U0001F600",
            AdditionalProperties = new() { ["raw"] = escaped },
        };

        await _recorder.RecordTreatmentsAsync([Refused(treatment)]);

        var payload = JsonDocument.Parse(_kept.Single().Payload).RootElement;
        payload.GetProperty("notes").GetString().Should().Be("a\uFFFDb\uFFFDc\U0001F600");
        payload.GetProperty("raw").GetString().Should().Be("\uFFFD");
    }

    [Theory]
    [InlineData("pair", "\\ud83d\\ude00", "\\ud83d\\ude00")]
    [InlineData("lone high", "x\\ud800y", "x\\ufffdy")]
    [InlineData("lone low", "x\\ude00y", "x\\ufffdy")]
    [InlineData("nul", "x\\u0000y", "x\\ufffdy")]
    [InlineData("escaped backslash", "x\\\\ud800y", "x\\\\ud800y")]
    public void Escapes_jsonb_refuses_are_replaced_and_no_others(string escape, string value, string expected)
    {
        Nocturne.Infrastructure.Data.StorableText.RepairJsonEscapes("{\"a\":\"" + value + "\"}")
            .Should().Be("{\"a\":\"" + expected + "\"}", escape);
    }

    [Fact]
    public async Task A_large_record_is_kept_bounded_with_its_original_size()
    {
        var treatment = new Treatment { Id = "t-1", EventType = "Note", Notes = new string('n', 100_000) };

        await _recorder.RecordTreatmentsAsync([Refused(treatment)]);

        var upload = _kept.Single();
        upload.PayloadBytes.Should().BeGreaterThan(100_000);
        upload.Payload.Length.Should().BeLessThan(RejectedUploadRecorder.MaxPayloadLength);
        JsonDocument.Parse(upload.Payload).RootElement.GetProperty("notes").GetString()
            .Should().HaveLength(RejectedUploadRecorder.MaxStringLength + 1).And.EndWith("…");
    }

    [Fact]
    public async Task One_refusal_that_cannot_be_kept_does_not_lose_the_others()
    {
        _store.Setup(s => s.UpsertAsync(It.Is<RejectedUpload>(u => u.Payload.Contains("bad")), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException("store refused"));

        await _recorder.RecordTreatmentsAsync(
        [
            Refused(new Treatment { Id = "t-1", Notes = "bad" }),
            Refused(new Treatment { Id = "t-2", Notes = "good" }),
        ]);

        _kept.Should().ContainSingle().Which.Payload.Should().Contain("good");
        VerifyNotified(Times.Once());
    }

    [Fact]
    public async Task Clearing_the_last_ones_resolves_the_notification()
    {
        var ids = new[] { Guid.NewGuid() };
        _store.Setup(s => s.DeleteAsync(ids, It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _store.Setup(s => s.GetAsync(1, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaginatedResponse<RejectedUpload> { Pagination = new PaginationInfo(1, 0, 0) });

        (await _recorder.ClearAsync(ids)).Should().Be(1);

        _notifications.Verify(n => n.ArchiveBySourceAsync(
            "owner", RejectedUploadRecorder.NotificationType, It.IsAny<string>(), NotificationArchiveReason.Completed, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static BulkWriteRejection<Treatment> Refused(Treatment treatment) => new(0, treatment, "too long");

    private void VerifyNotified(Times times) =>
        _notifications.Verify(n => n.CreateNotificationAsync(
            "owner", RejectedUploadRecorder.NotificationType, "rejected_uploads", It.IsAny<NotificationCategory?>(),
            It.IsAny<NotificationUrgency?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<List<NotificationActionDto>?>(), It.IsAny<ResolutionConditions?>(), It.IsAny<Dictionary<string, object>?>(),
            It.IsAny<CancellationToken>()),
            times);
}
