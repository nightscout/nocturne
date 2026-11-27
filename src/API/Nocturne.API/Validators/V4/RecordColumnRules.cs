using FluentValidation;
using Nocturne.Core.Constants;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// The rules every V4 record request shares for the header a record carries: who reported it and
/// the key it syncs on. A cap reads the constant of the column the value is stored in.
/// </summary>
public static class RecordColumnRules
{
    /// <summary>The message of <see cref="DataSourceRequiredWithSyncIdentifier{T}"/>.</summary>
    public const string DataSourceRequiredMessage = "DataSource is required when SyncIdentifier is supplied.";

    /// <summary>Caps a <c>DataSource</c>, <c>Device</c> or <c>App</c> at its <c>RecordColumnLengths.Provenance</c> column.</summary>
    public static IRuleBuilderOptions<T, string?> WithinProvenance<T>(this IRuleBuilder<T, string?> rule)
        => rule.MaximumLength(RecordColumnLengths.Provenance);

    /// <summary>Caps a <c>SyncIdentifier</c> at its column.</summary>
    public static IRuleBuilderOptions<T, string?> WithinSyncIdentifier<T>(this IRuleBuilder<T, string?> rule)
        => rule.MaximumLength(RecordColumnLengths.SyncIdentifier);

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
