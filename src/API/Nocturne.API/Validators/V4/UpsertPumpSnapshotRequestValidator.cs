using FluentValidation;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Constants;

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
        RuleFor(x => x.Device).WithinProvenance();
        RuleFor(x => x.App).WithinProvenance();
        RuleFor(x => x.DataSource).WithinProvenance();
        RuleFor(x => x.SyncIdentifier).WithinSyncIdentifier();
        RuleFor(x => x.DataSource).DataSourceRequiredWithSyncIdentifier(x => x.SyncIdentifier);
        RuleFor(x => x.Manufacturer).MaximumLength(ColumnLengths.PumpSnapshot.Manufacturer);
        RuleFor(x => x.Model).MaximumLength(ColumnLengths.PumpSnapshot.Model);
        RuleFor(x => x.ReservoirDisplay).MaximumLength(ColumnLengths.PumpSnapshot.ReservoirDisplay);
        RuleFor(x => x.PumpStatus).MaximumLength(ColumnLengths.PumpSnapshot.PumpStatus);
        RuleFor(x => x.PumpMode).MaximumLength(ColumnLengths.PumpSnapshot.PumpMode);
        RuleFor(x => x.Clock).MaximumLength(ColumnLengths.PumpSnapshot.Clock);
    }
}
