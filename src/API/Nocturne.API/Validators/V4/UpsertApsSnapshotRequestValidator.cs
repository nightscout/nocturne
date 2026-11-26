using FluentValidation;
using Nocturne.API.Models.Requests.V4;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates <see cref="UpsertApsSnapshotRequest"/> for the V4 APS snapshot endpoint: a timestamp,
/// the header caps of <see cref="RecordColumnRules"/>, and AidVersion at its column length.
/// </summary>
/// <seealso cref="Controllers.V4.Devices.ApsSnapshotController"/>
public class UpsertApsSnapshotRequestValidator : AbstractValidator<UpsertApsSnapshotRequest>
{
    public UpsertApsSnapshotRequestValidator()
    {
        RuleFor(x => x.Timestamp).TimestampRequired();
        RuleFor(x => x.Device).WithinUploaderLabel();
        RuleFor(x => x.App).WithinUploaderLabel();
        RuleFor(x => x.DataSource).WithinProvenance();
        RuleFor(x => x.SyncIdentifier).WithinSyncIdentifier();
        RuleFor(x => x.DataSource).DataSourceRequiredWithSyncIdentifier(x => x.SyncIdentifier);
        RuleFor(x => x.AidVersion).MaximumLength(64);
    }
}
