using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace Nocturne.Infrastructure.Data;

/// <summary>PostgreSQL <c>jsonb</c> operators for LINQ queries over string-mapped jsonb columns.</summary>
public static class Jsonb
{
    /// <summary>
    /// A top-level member of a jsonb object as text (<c>jsonb_extract_path_text</c>): null where
    /// the key is absent, the member is JSON null, or <paramref name="json"/> is null.
    /// </summary>
    public static string? Text(string? json, string key) =>
        throw new InvalidOperationException($"{nameof(Jsonb)}.{nameof(Text)} runs only inside a database query.");

    internal static void Register(ModelBuilder modelBuilder) =>
        modelBuilder.HasDbFunction(typeof(Jsonb).GetMethod(nameof(Text))!)
            .HasTranslation(args => new SqlFunctionExpression(
                "jsonb_extract_path_text",
                args,
                nullable: true,
                // Declared not to propagate: EF would otherwise rewrite "Text(json, key) IS NULL"
                // as "json IS NULL", and a missing key would read as present.
                argumentsPropagateNullability: [false, false],
                typeof(string),
                typeMapping: null));
}
