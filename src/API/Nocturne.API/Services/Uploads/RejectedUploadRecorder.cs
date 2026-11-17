using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Configuration;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Uploads;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.Uploads;

/// <inheritdoc cref="IRejectedUploadRecorder"/>
/// <remarks>
/// <para>
/// A record is the same rejected upload when it carries the same client id, or, without one, the
/// same payload, so a resend (a v1 uploader's, a v3 client retrying a 400, a connector re-reading a
/// page) counts an occurrence instead of adding a row.
/// </para>
/// <para>
/// The payload is kept bounded: string values longer than <see cref="MaxStringLength"/> are cut,
/// and a payload still longer than <see cref="MaxPayloadLength"/> is kept as an excerpt. Unpaired
/// surrogates and NUL, which PostgreSQL's jsonb refuses and which are what a 22021 or 22P05 refusal
/// is about, are replaced with U+FFFD first, so the refusal can be kept at all.
/// </para>
/// <para>
/// The owner is told once per new rejected upload, not per occurrence: each new one replaces the
/// last notification, and a resend of one already kept tells them nothing again. Its title and
/// subtitle are keys the frontend's <c>notification-labels.ts</c> resolves.
/// </para>
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

    internal const int MaxStringLength = 1024;

    internal const int MaxPayloadLength = 32 * 1024;

    private const string NotificationSource = "rejected-uploads";

    private static readonly JsonSerializerOptions PayloadOptions = NightscoutJsonOptions.Create();

    public async Task RecordTreatmentsAsync(
        IReadOnlyList<BulkWriteRejection<Treatment>> refused, CancellationToken ct = default)
    {
        var seenAt = clock.GetUtcNow().UtcDateTime;
        var anyNew = false;

        foreach (var rejection in refused)
        {
            var upload = ToUpload(rejection, seenAt);
            try
            {
                anyNew |= await store.UpsertAsync(upload, CancellationToken.None);
            }
            catch (DbUpdateException ex) { LogUnrecorded(ex); }
            catch (DbException ex) { LogUnrecorded(ex); }
            catch (InvalidOperationException ex) { LogUnrecorded(ex); }
        }

        if (anyNew)
            await NotifyOwnerAsync();
    }

    public async Task<int> ClearAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        var cleared = await store.DeleteAsync(ids, ct);
        var left = (await store.GetAsync(1, 0, ct)).Pagination.Total;
        if (left == 0 && await owners.GetOwnerSubjectIdAsync(tenant.TenantId, ct) is { } owner)
            await notifications.ArchiveBySourceAsync(
                owner, NotificationType, NotificationSource, NotificationArchiveReason.Completed, ct);
        return cleared;
    }

    internal static RejectedUpload ToUpload(BulkWriteRejection<Treatment> rejection, DateTime seenAt)
    {
        var (payload, bytes) = BoundedPayload(rejection.Record);
        return new RejectedUpload
        {
            RecordType = "treatment",
            Identity = Hash(rejection.Record.Id is { Length: > 0 } id ? "id:" + id : "payload:" + payload),
            Payload = payload,
            PayloadBytes = bytes,
            Reason = rejection.Reason,
            Origin = Clean(rejection.Record.DataSource) ?? "api",
            App = Clean(rejection.Record.EnteredBy),
            Device = Clean(DeviceOf(rejection.Record)),
            LastSeenAt = seenAt,
        };
    }

    /// <returns>The payload to keep, and the size in bytes of the record before it was bounded.</returns>
    internal static (string Payload, int Bytes) BoundedPayload(Treatment treatment)
    {
        var received = treatment.AdditionalProperties;
        JsonNode? node;
        try
        {
            treatment.AdditionalProperties = received?.ToDictionary(p => Clean(p.Key)!, p => StorableValue(p.Value));
            node = Cleaned(JsonSerializer.SerializeToNode(treatment, PayloadOptions));
        }
        finally
        {
            treatment.AdditionalProperties = received;
        }

        var whole = node?.ToJsonString() ?? "null";
        var bytes = Encoding.UTF8.GetByteCount(whole);

        var bounded = (Shortened(node)?.ToJsonString() ?? "null");
        if (bounded.Length > MaxPayloadLength)
            bounded = new JsonObject { ["excerpt"] = Cut(bounded, MaxPayloadLength) }.ToJsonString();

        return (bounded, bytes);
    }

    /// <summary>
    /// An extension value as jsonb can hold it. A <see cref="JsonElement"/> keeps the escapes the
    /// client sent, and one for an unpaired surrogate cannot even be read back as a string.
    /// </summary>
    private static object StorableValue(object value) => value switch
    {
        JsonElement element => JsonNode.Parse(StorableText.RepairJsonEscapes(element.GetRawText()))!,
        string text => Clean(text)!,
        _ => value,
    };

    private static JsonNode? Cleaned(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.Select(p =>
            KeyValuePair.Create(Clean(p.Key)!, Cleaned(p.Value?.DeepClone())))),
        JsonArray array => new JsonArray([.. array.Select(e => Cleaned(e?.DeepClone()))]),
        JsonValue value when value.TryGetValue<string>(out var text) => JsonValue.Create(Clean(text)),
        _ => node?.DeepClone(),
    };

    private static JsonNode? Shortened(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.Select(p => KeyValuePair.Create(p.Key, Shortened(p.Value?.DeepClone())))),
        JsonArray array => new JsonArray([.. array.Select(e => Shortened(e?.DeepClone()))]),
        JsonValue value when value.TryGetValue<string>(out var text) && text.Length > MaxStringLength =>
            JsonValue.Create(Cut(text, MaxStringLength) + "…"),
        _ => node?.DeepClone(),
    };

    private static string Cut(string text, int length) =>
        text[..(char.IsHighSurrogate(text[length - 1]) ? length - 1 : length)];

    /// <summary>Replaces unpaired surrogates and NUL, which jsonb refuses, with U+FFFD.</summary>
    internal static string? Clean(string? text) => text is null ? null : StorableText.ForJsonb(text);

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Clean(value)!)));

    private static string? DeviceOf(Treatment treatment) =>
        treatment.AdditionalProperties?.TryGetValue("device", out var device) == true ? device?.ToString() : null;

    private void LogUnrecorded(Exception ex) =>
        logger.LogError(ex, "Could not keep a refused record in the rejected uploads");

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
}
