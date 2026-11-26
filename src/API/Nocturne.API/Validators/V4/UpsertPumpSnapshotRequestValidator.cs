using FluentValidation;
using Nocturne.API.Models.Requests.V4;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates <see cref="UpsertPumpSnapshotRequest"/> for the V4 pump snapshot endpoint: a timestamp,
/// the header caps of <see cref="RecordColumnRules"/>, and the text fields at their column lengths.
/// </summary>
/// <seealso cref="Controllers.V4.Devices.PumpSnapshotController"/>
public class UpsertPumpSnapshotRequestValidator : AbstractValidator<UpsertPumpSnapshotRequest>
{
    public UpsertPumpSnapshotRequestValidator()
    {
        RuleFor(x => x.Timestamp).TimestampRequired();
        RuleFor(x => x.Device).WithinUploaderLabel();
        RuleFor(x => x.App).WithinUploaderLabel();
        RuleFor(x => x.DataSource).WithinProvenance();
        RuleFor(x => x.SyncIdentifier).WithinSyncIdentifier();
        RuleFor(x => x.DataSource).DataSourceRequiredWithSyncIdentifier(x => x.SyncIdentifier);
        RuleFor(x => x.Manufacturer).MaximumLength(128);
        RuleFor(x => x.Model).MaximumLength(128);
        RuleFor(x => x.ReservoirDisplay).MaximumLength(64);
        RuleFor(x => x.PumpStatus).MaximumLength(64);
        RuleFor(x => x.PumpMode).MaximumLength(64);
        RuleFor(x => x.Clock).MaximumLength(64);
    }
}
