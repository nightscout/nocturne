using FluentValidation;
using Nocturne.Core.Constants;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// The rules every V4 record request shares for the header a record carries: who reported it and
/// the key it syncs on. A cap reads the constant of the column the value is stored in, except
/// <see cref="WithinUploaderLabel{T}"/>, whose columns are cut to length on save.
/// </summary>
public static class RecordColumnRules
{
    /// <summary>
    /// The longest <c>Device</c> or <c>App</c> a request may carry into a column that is cut to length
    /// on save. A column that is not cut uses <see cref="WithinProvenance{T}"/> or its own length.
    /// </summary>
    public const int UploaderLabelMaxLength = 500;

    /// <summary>The message of <see cref="DataSourceRequiredWithSyncIdentifier{T}"/>.</summary>
    public const string DataSourceRequiredMessage = "DataSource is required when SyncIdentifier is supplied.";

    /// <summary>Caps a <c>DataSource</c>, <c>Device</c> or <c>App</c> at its <c>RecordColumnLengths.Provenance</c> column.</summary>
    public static IRuleBuilderOptions<T, string?> WithinProvenance<T>(this IRuleBuilder<T, string?> rule)
        => rule.MaximumLength(RecordColumnLengths.Provenance);

    /// <summary>Caps a <c>SyncIdentifier</c> at its column.</summary>
    public static IRuleBuilderOptions<T, string?> WithinSyncIdentifier<T>(this IRuleBuilder<T, string?> rule)
        => rule.MaximumLength(RecordColumnLengths.SyncIdentifier);

    /// <summary>Caps a <c>Device</c> or <c>App</c> that is cut to its column on save at <see cref="UploaderLabelMaxLength"/>.</summary>
    public static IRuleBuilderOptions<T, string?> WithinUploaderLabel<T>(this IRuleBuilder<T, string?> rule)
        => rule.MaximumLength(UploaderLabelMaxLength);

    /// <summary>
    /// Requires a <c>DataSource</c> when the record carries a <c>SyncIdentifier</c>, since a sync key
    /// is the pair of the two.
    /// </summary>
    public static IRuleBuilderOptions<T, string?> DataSourceRequiredWithSyncIdentifier<T>(
        this IRuleBuilder<T, string?> rule, Func<T, string?> syncIdentifier)
        => rule.NotEmpty()
            .When(x => !string.IsNullOrEmpty(syncIdentifier(x)))
            .WithMessage(DataSourceRequiredMessage);
}
