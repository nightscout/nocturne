using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Configuration;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Uploads;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Uploads;

/// <inheritdoc cref="IRejectedUploadRecorder"/>
/// <remarks>
/// The owner gets one notification at a time for them: each recording replaces the last, so a
/// misbehaving uploader cannot flood the list. Its title and subtitle are keys the frontend's
/// <c>notification-labels.ts</c> resolves.
/// </remarks>
public sealed class RejectedUploadRecorder(
    IRejectedUploadStore store,
    IInAppNotificationService notifications,
    ITenantOwnerResolver owners,
    ITenantAccessor tenant,
    TimeProvider clock,
    ILogger<RejectedUploadRecorder> logger) : IRejectedUploadRecorder
{
    public const string NotificationType = "data.rejected_uploads";

    private const string NotificationSource = "rejected-uploads";

    private static readonly JsonSerializerOptions PayloadOptions = NightscoutJsonOptions.Create();

    public async Task RecordTreatmentsAsync(
        IReadOnlyList<BulkWriteRejection<Treatment>> refused, CancellationToken ct = default)
    {
        if (refused.Count == 0)
            return;

        var receivedAt = clock.GetUtcNow().UtcDateTime;
        var uploads = refused.Select(r => new RejectedUpload
        {
            RecordType = "treatment",
            Payload = JsonSerializer.Serialize(r.Record, PayloadOptions),
            Reason = r.Reason,
            Origin = r.Record.DataSource ?? "api",
            App = r.Record.EnteredBy,
            Device = DeviceOf(r.Record),
            ReceivedAt = receivedAt,
        }).ToList();

        try
        {
            await store.AddAsync(uploads, CancellationToken.None);
        }
        catch (DbUpdateException ex) { LogUnrecorded(ex, uploads.Count); return; }
        catch (DbException ex) { LogUnrecorded(ex, uploads.Count); return; }
        catch (InvalidOperationException ex) { LogUnrecorded(ex, uploads.Count); return; }

        await NotifyOwnerAsync();
    }

    public async Task<int> ClearAsync(CancellationToken ct = default)
    {
        var cleared = await store.ClearAsync(ct);
        if (await owners.GetOwnerSubjectIdAsync(tenant.TenantId, ct) is { } owner)
            await notifications.ArchiveBySourceAsync(
                owner, NotificationType, NotificationSource, NotificationArchiveReason.Completed, ct);
        return cleared;
    }

    private void LogUnrecorded(Exception ex, int count) =>
        logger.LogError(ex, "Could not keep {Count} refused records in the rejected uploads", count);

    private async Task NotifyOwnerAsync()
    {
        try
        {
            if (await owners.GetOwnerSubjectIdAsync(tenant.TenantId, CancellationToken.None) is not { } owner)
                return;

            await notifications.ArchiveBySourceAsync(
                owner, NotificationType, NotificationSource, NotificationArchiveReason.Superseded, CancellationToken.None);
            await notifications.CreateNotificationAsync(
                userId: owner,
                type: NotificationType,
                title: "rejected_uploads",
                category: NotificationCategory.Informational,
                source: NotificationSource,
                subtitle: "rejected_uploads_subtitle",
                sourceId: NotificationSource,
                actions: [new NotificationActionDto { ActionId = "review", Label = "review", Icon = "eye", Variant = "default" }],
                cancellationToken: CancellationToken.None);
        }
        catch (DbUpdateException ex) { LogUnnotified(ex); }
        catch (DbException ex) { LogUnnotified(ex); }
        catch (InvalidOperationException ex) { LogUnnotified(ex); }
    }

    private void LogUnnotified(Exception ex) =>
        logger.LogError(ex, "Could not tell the owner about rejected uploads");

    private static string? DeviceOf(Treatment treatment) =>
        treatment.AdditionalProperties?.TryGetValue("device", out var device) == true ? device?.ToString() : null;
}
