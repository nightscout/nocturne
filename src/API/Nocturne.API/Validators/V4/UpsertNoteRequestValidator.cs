using FluentValidation;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Constants;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates <see cref="UpsertNoteRequest"/> for the V4 note upsert endpoint.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>Timestamp must be a valid non-default <see cref="DateTimeOffset"/>.</description></item>
/// <item><description>The header rules of <see cref="RecordColumnRules"/>: caps on DataSource, SyncIdentifier, Device and App, and DataSource required when SyncIdentifier is supplied.</description></item>
/// <item><description>Text capped at 10,000 characters; EventType at 200.</description></item>
/// </list>
/// </remarks>
/// <seealso cref="UpsertNoteRequest"/>
/// <seealso cref="Controllers.V4.Treatments.NoteController"/>
public class UpsertNoteRequestValidator : AbstractValidator<UpsertNoteRequest>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UpsertNoteRequestValidator"/> class
    /// and configures all validation rules for note upserts.
    /// </summary>
    public UpsertNoteRequestValidator()
    {
        RuleFor(x => x.Timestamp).TimestampRequired();
        RuleFor(x => x.Device).WithinProvenance();
        RuleFor(x => x.App).WithinProvenance();
        RuleFor(x => x.DataSource).WithinProvenance();
        RuleFor(x => x.Text).MaximumLength(10000).When(x => x.Text is not null);
        RuleFor(x => x.EventType).MaximumLength(ColumnLengths.Note.EventType).When(x => x.EventType is not null);
        RuleFor(x => x.SyncIdentifier).WithinSyncIdentifier();
        RuleFor(x => x.DataSource).DataSourceRequiredWithSyncIdentifier(x => x.SyncIdentifier);
    }
}
