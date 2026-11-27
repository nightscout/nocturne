using FluentValidation;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Constants;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates <see cref="UpsertUploaderSnapshotRequest"/> for the V4 uploader snapshot endpoint: a timestamp,
/// the header caps of <see cref="RecordColumnRules"/>, and Name and Type at their column lengths.
/// </summary>
/// <seealso cref="Controllers.V4.Devices.UploaderSnapshotController"/>
public class UpsertUploaderSnapshotRequestValidator : AbstractValidator<UpsertUploaderSnapshotRequest>
{
    public UpsertUploaderSnapshotRequestValidator()
    {
        RuleFor(x => x.Timestamp).TimestampRequired();
        RuleFor(x => x.Device).WithinProvenance();
        RuleFor(x => x.App).WithinProvenance();
        RuleFor(x => x.DataSource).WithinProvenance();
        RuleFor(x => x.SyncIdentifier).WithinSyncIdentifier();
        RuleFor(x => x.DataSource).DataSourceRequiredWithSyncIdentifier(x => x.SyncIdentifier);
        RuleFor(x => x.Name).MaximumLength(ColumnLengths.UploaderSnapshot.Name);
        RuleFor(x => x.Type).MaximumLength(ColumnLengths.UploaderSnapshot.Type);
    }
}
