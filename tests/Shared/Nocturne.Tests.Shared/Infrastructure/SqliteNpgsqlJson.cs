using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Sqlite.Query.Internal;
using Nocturne.Infrastructure.Data;

namespace Nocturne.Tests.Shared.Infrastructure;

/// <summary>
/// Translates the PostgreSQL JSON functions the served state-span filter uses into SQLite's
/// <c>json_type</c> and <c>json_extract</c>, for the constant keys and objects the filter passes, so
/// a SQLite test database runs the same span queries PostgreSQL does.
/// </summary>
public static class SqliteNpgsqlJson
{
    /// <summary>Pass to <see cref="TestDbContextFactory.CreateSqliteWithTenant(Guid, Action{DbContextOptionsBuilder{NocturneDbContext}}, IInterceptor[])"/>.</summary>
    public static void Translate(DbContextOptionsBuilder<NocturneDbContext> options) =>
        options.ReplaceService<IMethodCallTranslatorProvider, TranslatingProvider>();

#pragma warning disable EF1001
    private sealed class TranslatingProvider : SqliteMethodCallTranslatorProvider
    {
        public TranslatingProvider(RelationalMethodCallTranslatorProviderDependencies dependencies)
            : base(dependencies) =>
            AddTranslators([new NpgsqlJsonTranslator(dependencies.SqlExpressionFactory)]);
    }
#pragma warning restore EF1001

    private sealed class NpgsqlJsonTranslator(ISqlExpressionFactory sql) : IMethodCallTranslator
    {
        public SqlExpression? Translate(
            SqlExpression? instance, MethodInfo method, IReadOnlyList<SqlExpression> arguments,
            IDiagnosticsLogger<DbLoggerCategory.Query> logger)
        {
            if (method.DeclaringType != typeof(NpgsqlJsonDbFunctionsExtensions))
                return null;

            var json = arguments[1];
            var operand = (string)((SqlConstantExpression)arguments[2]).Value!;

            return method.Name switch
            {
                nameof(NpgsqlJsonDbFunctionsExtensions.JsonExists) => sql.IsNotNull(At("json_type", json, operand)),
                nameof(NpgsqlJsonDbFunctionsExtensions.JsonContains) => JsonNode.Parse(operand)!.AsObject()
                    .Select(p => (SqlExpression)sql.Equal(
                        At("json_extract", json, p.Key), sql.Constant(p.Value!.GetValue<string>())))
                    .Aggregate(sql.AndAlso),
                _ => null,
            };
        }

        private SqlExpression At(string function, SqlExpression json, string key) =>
            sql.Function(function, [json, sql.Constant("$." + key)], nullable: true,
                // A missing key is null too, so a null result does not follow from a null document alone.
                argumentsPropagateNullability: [false, false], typeof(string));
    }
}
