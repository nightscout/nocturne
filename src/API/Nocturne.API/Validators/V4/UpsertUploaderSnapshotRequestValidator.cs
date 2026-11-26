using FluentValidation;
using Nocturne.API.Models.Requests.V4;

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
        RuleFor(x => x.Device).WithinUploaderLabel();
        RuleFor(x => x.App).WithinUploaderLabel();
        RuleFor(x => x.DataSource).WithinProvenance();
        RuleFor(x => x.SyncIdentifier).WithinSyncIdentifier();
        RuleFor(x => x.DataSource).DataSourceRequiredWithSyncIdentifier(x => x.SyncIdentifier);
        RuleFor(x => x.Name).MaximumLength(256);
        RuleFor(x => x.Type).MaximumLength(128);
    }
}
