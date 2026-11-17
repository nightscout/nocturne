using System.Text.Json;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.Infrastructure.Data.Mappers.V4;

public static class ProfileActivationMapper
{
    public static ProfileActivationEntity ToEntity(ProfileActivation model)
    {
        var entity = new ProfileActivationEntity().WithHeaderFrom(model);
        ApplyFields(entity, model);
        return entity;
    }

    public static ProfileActivation ToDomainModel(ProfileActivationEntity entity) =>
        new ProfileActivation
        {
            EndTimestamp = entity.EndTimestamp,
            Phase = Enum.TryParse<ProfileActivationPhase>(entity.Phase, out var phase) ? phase : ProfileActivationPhase.Requested,
            Origin = Enum.TryParse<ProfileActivationOrigin>(entity.Origin, out var origin) ? origin : ProfileActivationOrigin.Uploader,
            BaseProfileName = entity.BaseProfileName,
            Percentage = entity.Percentage,
            TimeshiftMs = entity.TimeshiftMs,
            RequestedDurationMs = entity.RequestedDurationMs,
            RequestedActivationId = entity.RequestedActivationId,
            Snapshot = MapperHelpers.DeserializeJson<ProfileActivationSnapshot>(entity.SnapshotJson),
            SnapshotIsCustomized = entity.SnapshotIsCustomized,
            EnteredBy = entity.EnteredBy,
            EndedAt = entity.EndedAt,
            EndedBy = entity.EndedBy,
            SyncIdentifier = entity.SyncIdentifier,
            DeviceId = entity.DeviceId,
            PatientDeviceId = entity.PatientDeviceId,
        }.WithHeaderFrom(entity);

    public static void UpdateEntity(ProfileActivationEntity entity, ProfileActivation model)
    {
        V4RecordHeaderMapper.UpdateHeader(entity, model);
        ApplyFields(entity, model);
    }

    private static void ApplyFields(ProfileActivationEntity entity, ProfileActivation model)
    {
        entity.EndTimestamp = model.EndTimestamp;
        entity.Phase = model.Phase.ToString();
        entity.Origin = model.Origin.ToString();
        entity.BaseProfileName = model.BaseProfileName;
        entity.Percentage = model.Percentage;
        entity.TimeshiftMs = model.TimeshiftMs;
        entity.RequestedDurationMs = model.RequestedDurationMs;
        entity.RequestedActivationId = model.RequestedActivationId;
        entity.SnapshotJson = model.Snapshot is null ? null : JsonSerializer.Serialize(model.Snapshot);
        entity.SnapshotIsCustomized = model.SnapshotIsCustomized;
        entity.EnteredBy = model.EnteredBy;
        entity.EndedAt = model.EndedAt;
        entity.EndedBy = model.EndedBy;
        entity.SyncIdentifier = model.SyncIdentifier;
        entity.DeviceId = model.DeviceId;
        entity.PatientDeviceId = model.PatientDeviceId;
    }
}
