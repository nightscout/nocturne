using System.Data.Common;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Sqlite.Query.Internal;
using Nocturne.Infrastructure.Data;

namespace Nocturne.Tests.Shared.Infrastructure;

/// <summary>
/// What a SQLite test database needs to run the queries PostgreSQL runs: the <c>"C"</c> collation
/// (the byte order), <c>array_position</c> over the JSON array a parameter array is bound as, and
/// translators for the Npgsql JSON functions and <see cref="Array.IndexOf{T}(T[], T)"/>. Every
/// SQLite harness applies it through <see cref="Apply"/>, and defines it on a connection it opened
/// itself through <see cref="Define"/>.
/// </summary>
public static class SqliteNpgsqlCompat
{
    private const string ArrayPositionFunction = "array_position";

    private static readonly CollationInterceptor Interceptor = new();

    /// <summary>
    /// Defines the collation and the function on every connection EF opens, and replaces the method
    /// call translators. A connection handed to <c>UseSqlite</c> already open needs <see cref="Define"/>.
    /// </summary>
    public static DbContextOptionsBuilder<NocturneDbContext> Apply(DbContextOptionsBuilder<NocturneDbContext> options) =>
        options
            .AddInterceptors(Interceptor)
            .ReplaceService<IMethodCallTranslatorProvider, TranslatingProvider>();

    public static void Define(SqliteConnection connection)
    {
        connection.CreateCollation("C", string.CompareOrdinal);
        connection.CreateFunction<string?, string?, int?>(ArrayPositionFunction, ArrayPosition, isDeterministic: true);
    }

    /// <summary>
    /// PostgreSQL's <c>array_position</c>: the one-based position of the first element equal to the
    /// value, or null. Elements are compared as text.
    /// </summary>
    private static int? ArrayPosition(string? array, string? value)
    {
        if (array is null || value is null)
            return null;

        using var json = JsonDocument.Parse(array);
        var position = 1;
        foreach (var element in json.RootElement.EnumerateArray())
        {
            if ((element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText()) == value)
                return position;
            position++;
        }

        return null;
    }

    private sealed class CollationInterceptor : DbConnectionInterceptor
    {
        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
            DefineOn(connection);

        public override Task ConnectionOpenedAsync(
            DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            DefineOn(connection);
            return Task.CompletedTask;
        }

        private static void DefineOn(DbConnection connection)
        {
            if (connection is SqliteConnection sqlite)
                Define(sqlite);
        }
    }

#pragma warning disable EF1001
    private sealed class TranslatingProvider : SqliteMethodCallTranslatorProvider
    {
        public TranslatingProvider(RelationalMethodCallTranslatorProviderDependencies dependencies)
            : base(dependencies) =>
            AddTranslators(
            [
                new NpgsqlJsonTranslator(dependencies.SqlExpressionFactory),
                new ArrayIndexOfTranslator(dependencies.SqlExpressionFactory),
            ]);
    }
#pragma warning restore EF1001

    private sealed class ArrayIndexOfTranslator(ISqlExpressionFactory sql) : IMethodCallTranslator
    {
        public SqlExpression? Translate(
            SqlExpression? instance, MethodInfo method, IReadOnlyList<SqlExpression> arguments,
            IDiagnosticsLogger<DbLoggerCategory.Query> logger)
        {
            if (method.DeclaringType != typeof(Array) || method.Name != nameof(Array.IndexOf) || arguments.Count != 2)
                return null;

            var position = sql.Function(ArrayPositionFunction, arguments, nullable: true,
                argumentsPropagateNullability: [true, true], typeof(int));
            return sql.Coalesce(sql.Subtract(position, sql.Constant(1)), sql.Constant(-1));
        }
    }

    /// <summary>
    /// Translates <c>JsonExists</c> and <c>JsonContains</c> into SQLite's <c>json_type</c> and
    /// <c>json_extract</c>, for a constant key or a constant object of scalar values.
    /// </summary>
    private sealed class NpgsqlJsonTranslator(ISqlExpressionFactory sql) : IMethodCallTranslator
    {
        public SqlExpression? Translate(
            SqlExpression? instance, MethodInfo method, IReadOnlyList<SqlExpression> arguments,
            IDiagnosticsLogger<DbLoggerCategory.Query> logger)
        {
            if (method.DeclaringType != typeof(NpgsqlJsonDbFunctionsExtensions)
                || method.Name is not (nameof(NpgsqlJsonDbFunctionsExtensions.JsonExists)
                    or nameof(NpgsqlJsonDbFunctionsExtensions.JsonContains)))
                return null;

            var json = arguments[1];
            if (arguments[2] is not SqlConstantExpression { Value: string operand })
                throw new NotSupportedException(
                    $"{nameof(SqliteNpgsqlCompat)} translates {method.Name} only for a constant string operand; "
                    + "a parameter or a column operand cannot be turned into a json_type or json_extract path.");

            return method.Name == nameof(NpgsqlJsonDbFunctionsExtensions.JsonExists)
                ? sql.IsNotNull(At("json_type", json, operand, typeof(string)))
                : Contains(json, operand);
        }

        private SqlExpression Contains(SqlExpression json, string operand) =>
            JsonNode.Parse(operand)!.AsObject()
                .Select(p => Matches(json, p.Key, p.Value))
                .Aggregate(sql.AndAlso);

        private SqlExpression Matches(SqlExpression json, string key, JsonNode? value)
        {
            if (value is null)
                return sql.Equal(At("json_type", json, key, typeof(string)), sql.Constant("null"));

            if (value is not JsonValue scalar)
                throw new NotSupportedException(
                    $"{nameof(SqliteNpgsqlCompat)} translates JsonContains only for scalar values; "
                    + $"key '{key}' holds {value.GetValueKind()}.");

            return scalar.GetValueKind() switch
            {
                JsonValueKind.String => Equal(json, key, scalar.GetValue<string>()),
                JsonValueKind.True => Equal(json, key, true),
                JsonValueKind.False => Equal(json, key, false),
                _ when scalar.TryGetValue(out long whole) => Equal(json, key, whole),
                _ => Equal(json, key, scalar.GetValue<double>()),
            };
        }

        private SqlExpression Equal<T>(SqlExpression json, string key, T value) =>
            sql.Equal(At("json_extract", json, key, typeof(T)), sql.Constant(value));

        private SqlExpression At(string function, SqlExpression json, string key, Type type) =>
            sql.Function(function, [json, sql.Constant("$." + key)], nullable: true,
                // A missing key is null too, so a null result does not follow from a null document alone.
                argumentsPropagateNullability: [false, false], type);
    }
}
