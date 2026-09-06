using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// SQLite-backed tests assert on rows the production filters hide (other tenants',
/// soft-deleted ones), so their contexts drop every global filter the model declares.
/// </summary>
internal static class QueryFilterTestExtensions
{
    /// <summary>
    /// Removes every global query filter declared on the entity type. <see cref="NocturneDbContext"/>
    /// registers keyed filters, which <c>HasQueryFilter(null)</c> does not touch — that clears only an
    /// anonymous filter.
    /// </summary>
    internal static EntityTypeBuilder ClearQueryFilters(this EntityTypeBuilder builder)
    {
        foreach (var filter in builder.Metadata.GetDeclaredQueryFilters().ToList())
        {
            if (filter.IsAnonymous)
                builder.HasQueryFilter(null as LambdaExpression);
            else
                builder.HasQueryFilter(filter.Key, null);
        }

        return builder;
    }
}
