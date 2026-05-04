using System.Linq.Expressions;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Infrastructure;

namespace Nocturne.Infrastructure.Data.Services;

/// <summary>
/// Service for parsing Nightscout-style queries (legacy MongoDB format) into Entity Framework Core expressions
/// Maintains 1:1 compatibility with legacy Nightscout query behavior
/// Supports: $eq, $ne, $gt, $gte, $lt, $lte, $in, $nin, $regex, $exists, $and, $or
/// </summary>
public class QueryParser : IQueryParser
{
    private readonly ILogger<QueryParser> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryParser"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public QueryParser(ILogger<QueryParser> logger)
    {
        _logger = logger;
    }

    private const int MaxQueryDepth = 10;

    private static readonly Dictionary<string, Func<string, object>> DefaultTreatmentConverters =
        new()
        {
            ["date"] = s => long.Parse(s),
            ["mills"] = s => long.Parse(s),
            ["created_at"] = ParseIsoDateToMills,
            ["insulin"] = s => double.Parse(s),
            ["carbs"] = s => double.Parse(s),
            ["glucose"] = s => int.Parse(s),
            ["notes"] = ParseRegexOrString,
            ["eventType"] = ParseRegexOrString,
            ["enteredBy"] = ParseRegexOrString,
            ["reason"] = ParseRegexOrString,
        };

    /// <inheritdoc />
    public Task<IQueryable<T>> ApplyQueryAsync<T>(
        IQueryable<T> queryable,
        string findQuery,
        QueryOptions options,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        if (string.IsNullOrWhiteSpace(findQuery))
        {
            return Task.FromResult(queryable);
        }

        _logger.LogDebug("ApplyQueryAsync called with findQuery: {FindQuery}", findQuery);

        var filter = ParseFilterAsync<T>(findQuery, options, cancellationToken).Result;
        if (filter != null)
        {
            _logger.LogDebug("Filter expression created successfully for type {EntityType}", typeof(T).Name);
            queryable = queryable.Where(filter);
        }
        else
        {
            _logger.LogWarning("Filter parsing returned null for query: {FindQuery}", findQuery);
        }

        return Task.FromResult(queryable);
    }

    /// <inheritdoc />
    public Task<Expression<Func<T, bool>>?> ParseFilterAsync<T>(
        string findQuery,
        QueryOptions options,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        if (string.IsNullOrWhiteSpace(findQuery))
        {
            return Task.FromResult<Expression<Func<T, bool>>?>(null);
        }

        try
        {
            // Determine type converters based on entity type
            var typeConverters = GetTypeConverters<T>(options);

            // Build the expression tree
            var parameter = Expression.Parameter(typeof(T), "x");

            // Handle both URL-encoded and JSON-style queries
            string decodedQuery = HttpUtility.UrlDecode(findQuery);

            Expression? combinedExpression;
            if (decodedQuery.TrimStart().StartsWith("{"))
            {
                // JSON-style query - use new recursive parser
                using var doc = JsonDocument.Parse(decodedQuery);
                combinedExpression = ParseJsonElementRecursive(
                    doc.RootElement,
                    parameter,
                    typeConverters,
                    depth: 0
                );
            }
            else
            {
                // URL-encoded query - parse to conditions first
                var queryParams = ParseUrlEncodedQuery(findQuery);
                combinedExpression = BuildExpressionFromConditions(
                    queryParams,
                    parameter,
                    typeConverters
                );
            }

            var result =
                combinedExpression != null
                    ? Expression.Lambda<Func<T, bool>>(combinedExpression, parameter)
                    : null;

            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse filter query: {FindQuery}", findQuery);
            // If parsing fails, return null to avoid breaking queries
            return Task.FromResult<Expression<Func<T, bool>>?>(null);
        }
    }

    /// <inheritdoc />
    public IQueryable<T> ApplyDefaultDateFilter<T>(
        IQueryable<T> queryable,
        string? findQuery,
        string? dateString,
        QueryOptions options
    )
        where T : class
    {
        if (options.DisableDefaultDateFilter)
        {
            return queryable;
        }

        // Check if there's already a date constraint
        var hasDateConstraint =
            !string.IsNullOrEmpty(dateString)
            || (
                !string.IsNullOrEmpty(findQuery)
                && (
                    findQuery.Contains("date")
                    || findQuery.Contains("mills")
                    || findQuery.Contains(options.DateField.ToLower())
                )
            );

        if (hasDateConstraint)
        {
            return queryable;
        }

        // Apply default date range
        var cutoffTime = DateTimeOffset.UtcNow.Subtract(options.DefaultDateRange);
        var cutoffMills = cutoffTime.ToUnixTimeMilliseconds();

        // Build expression for date filtering
        var parameter = Expression.Parameter(typeof(T), "x");
        var property = Expression.Property(parameter, options.DateField);
        var constant = Expression.Constant(cutoffMills);
        var comparison = Expression.GreaterThanOrEqual(property, constant);
        var lambda = Expression.Lambda<Func<T, bool>>(comparison, parameter);

        return queryable.Where(lambda);
    }

    #region JSON Recursive Parser

    /// <summary>
    /// Recursively parse a JSON element into an Expression tree
    /// Handles $and, $or, $exists and all comparison operators
    /// </summary>
    private Expression? ParseJsonElementRecursive(
        JsonElement element,
        ParameterExpression parameter,
        Dictionary<string, Func<string, object>> typeConverters,
        int depth,
        string? currentFieldPath = null
    )
    {
        if (depth > MaxQueryDepth)
        {
            // Prevent stack overflow from malicious queries
            return null;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                return ParseJsonObject(element, parameter, typeConverters, depth, currentFieldPath);

            case JsonValueKind.Array:
                // Standalone array at field level implies $in
                if (!string.IsNullOrEmpty(currentFieldPath))
                {
                    var values = element.EnumerateArray().Select(GetJsonElementValue).ToArray();
                    var propertyExpr = GetPropertyExpression(parameter, currentFieldPath);
                    if (propertyExpr != null)
                    {
                        return BuildInExpression(propertyExpr, string.Join("|", values));
                    }
                }
                return null;

            default:
                // Direct value at field level implies $eq
                if (!string.IsNullOrEmpty(currentFieldPath))
                {
                    var propertyExpr = GetPropertyExpression(parameter, currentFieldPath);
                    if (propertyExpr != null)
                    {
                        var value = GetJsonElementValue(element);
                        var convertedValue = ConvertValue(value, currentFieldPath.ToLower(), typeConverters);
                        return BuildEqualExpression(propertyExpr, convertedValue);
                    }
                }
                return null;
        }
    }

    /// <summary>
    /// Parse a JSON object which may contain fields, operators, or logical operators
    /// </summary>
    private Expression? ParseJsonObject(
        JsonElement element,
        ParameterExpression parameter,
        Dictionary<string, Func<string, object>> typeConverters,
        int depth,
        string? currentFieldPath
    )
    {
        var expressions = new List<Expression>();

        foreach (var property in element.EnumerateObject())
        {
            Expression? expr = null;

            switch (property.Name)
            {
                case "$and":
                    expr = ParseLogicalOperator(property.Value, parameter, typeConverters, depth, isAnd: true);
                    break;

                case "$or":
                    expr = ParseLogicalOperator(property.Value, parameter, typeConverters, depth, isAnd: false);
                    break;

                case "$eq":
                case "$ne":
                case "$gt":
                case "$gte":
                case "$lt":
                case "$lte":
                case "$in":
                case "$nin":
                case "$regex":
                case "$exists":
                    // Operator applied to current field path
                    if (!string.IsNullOrEmpty(currentFieldPath))
                    {
                        expr = BuildOperatorExpression(
                            parameter,
                            currentFieldPath,
                            property.Name,
                            property.Value,
                            typeConverters
                        );
                    }
                    break;

                default:
                    // Regular field - might have nested value or operators
                    var newPath = string.IsNullOrEmpty(currentFieldPath)
                        ? property.Name
                        : $"{currentFieldPath}.{property.Name}";

                    expr = ParseJsonElementRecursive(
                        property.Value,
                        parameter,
                        typeConverters,
                        depth + 1,
                        newPath
                    );
                    break;
            }

            if (expr != null)
            {
                expressions.Add(expr);
            }
        }

        // Combine all expressions with AND (implicit for object properties at same level)
        return CombineExpressions(expressions, isAnd: true);
    }

    /// <summary>
    /// Parse $and or $or logical operator with array of conditions
    /// </summary>
    private Expression? ParseLogicalOperator(
        JsonElement arrayElement,
        ParameterExpression parameter,
        Dictionary<string, Func<string, object>> typeConverters,
        int depth,
        bool isAnd
    )
    {
        if (arrayElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var expressions = new List<Expression>();

        foreach (var item in arrayElement.EnumerateArray())
        {
            var expr = ParseJsonElementRecursive(
                item,
                parameter,
                typeConverters,
                depth + 1,
                currentFieldPath: null
            );

            if (expr != null)
            {
                expressions.Add(expr);
            }
        }

        return CombineExpressions(expressions, isAnd);
    }

    /// <summary>
    /// Build an expression for a specific operator applied to a field
    /// </summary>
    private Expression? BuildOperatorExpression(
        ParameterExpression parameter,
        string fieldPath,
        string mongoOperator,
        JsonElement valueElement,
        Dictionary<string, Func<string, object>> typeConverters
    )
    {
        var propertyExpr = GetPropertyExpression(parameter, fieldPath);
        if (propertyExpr == null)
        {
            return null;
        }

        var value = GetJsonElementValue(valueElement);
        var convertedValue = ConvertValue(value, fieldPath.ToLower(), typeConverters);

        return mongoOperator switch
        {
            "$eq" => BuildEqualExpression(propertyExpr, convertedValue),
            "$ne" => BuildNotEqualExpression(propertyExpr, convertedValue),
            "$gt" => BuildGreaterThanExpression(propertyExpr, convertedValue),
            "$gte" => BuildGreaterThanOrEqualExpression(propertyExpr, convertedValue),
            "$lt" => BuildLessThanExpression(propertyExpr, convertedValue),
            "$lte" => BuildLessThanOrEqualExpression(propertyExpr, convertedValue),
            "$in" => BuildInExpressionFromJson(propertyExpr, valueElement, fieldPath, typeConverters),
            "$nin" => BuildNotInExpressionFromJson(propertyExpr, valueElement, fieldPath, typeConverters),
            "$regex" => BuildRegexExpression(propertyExpr, convertedValue),
            "$exists" => BuildExistsExpression(propertyExpr, value),
            _ => null,
        };
    }

    /// <summary>
    /// Combine multiple expressions using AND or OR
    /// </summary>
    private static Expression? CombineExpressions(List<Expression> expressions, bool isAnd)
    {
        if (expressions.Count == 0)
        {
            return null;
        }

        if (expressions.Count == 1)
        {
            return expressions[0];
        }

        Expression combined = expressions[0];
        for (int i = 1; i < expressions.Count; i++)
        {
            combined = isAnd
                ? Expression.AndAlso(combined, expressions[i])
                : Expression.OrElse(combined, expressions[i]);
        }

        return combined;
    }

    #endregion

    #region URL-Encoded Query Parser

    private static Dictionary<string, List<MongoCondition>> ParseUrlEncodedQuery(string findQuery)
    {
        var result = new Dictionary<string, List<MongoCondition>>();

        try
        {
            // Parse URL parameters (find[field][$op]=value format)
            var queryParams = HttpUtility.ParseQueryString(findQuery);

            // Track logical operators separately
            var andConditions = new List<(int index, string field, string op, string value)>();
            var orConditions = new List<(int index, string field, string op, string value)>();

            foreach (string? key in queryParams.AllKeys)
            {
                if (string.IsNullOrEmpty(key))
                    continue;

                var values = queryParams.GetValues(key);
                if (values == null)
                    continue;

                // Check for $and/$or patterns like find[$or][0][eventType]=value
                var logicalMatch = Regex.Match(key, @"find\[\$(and|or)\]\[(\d+)\]\[([^\]]+)\](?:\[(\$\w+)\])?");
                if (logicalMatch.Success)
                {
                    var logicalOp = logicalMatch.Groups[1].Value;
                    var index = int.Parse(logicalMatch.Groups[2].Value);
                    var field = logicalMatch.Groups[3].Value;
                    var op = logicalMatch.Groups[4].Success ? logicalMatch.Groups[4].Value : "$eq";

                    foreach (var value in values)
                    {
                        if (logicalOp == "and")
                        {
                            andConditions.Add((index, field, op, value));
                        }
                        else
                        {
                            orConditions.Add((index, field, op, value));
                        }
                    }
                    continue;
                }

                // Regular field conditions
                foreach (var value in values)
                {
                    var (fieldPath, mongoOperator) = ParseFieldPath(key);

                    if (!result.ContainsKey(fieldPath))
                    {
                        result[fieldPath] = new List<MongoCondition>();
                    }

                    result[fieldPath].Add(new MongoCondition { Operator = mongoOperator, Value = value });
                }
            }

            // Add logical operators as special entries
            if (orConditions.Count > 0)
            {
                result["$or"] = orConditions
                    .Select(c => new MongoCondition
                    {
                        Operator = c.op,
                        Value = c.value,
                        Field = c.field,
                        LogicalIndex = c.index
                    })
                    .ToList();
            }

            if (andConditions.Count > 0)
            {
                result["$and"] = andConditions
                    .Select(c => new MongoCondition
                    {
                        Operator = c.op,
                        Value = c.value,
                        Field = c.field,
                        LogicalIndex = c.index
                    })
                    .ToList();
            }
        }
        catch (Exception)
        {
            // If parsing fails, return empty dictionary to avoid breaking queries
            return new Dictionary<string, List<MongoCondition>>();
        }

        return result;
    }

    /// <summary>
    /// Build expression tree from parsed URL-encoded conditions
    /// </summary>
    private Expression? BuildExpressionFromConditions(
        Dictionary<string, List<MongoCondition>> queryParams,
        ParameterExpression parameter,
        Dictionary<string, Func<string, object>> typeConverters
    )
    {
        var expressions = new List<Expression>();

        foreach (var kvp in queryParams)
        {
            var fieldPath = kvp.Key;
            var conditions = kvp.Value;

            if (fieldPath == "$or")
            {
                // Build OR expression from grouped conditions
                var orExpr = BuildLogicalFromConditions(conditions, parameter, typeConverters, isAnd: false);
                if (orExpr != null)
                {
                    expressions.Add(orExpr);
                }
                continue;
            }

            if (fieldPath == "$and")
            {
                // Build AND expression from grouped conditions
                var andExpr = BuildLogicalFromConditions(conditions, parameter, typeConverters, isAnd: true);
                if (andExpr != null)
                {
                    expressions.Add(andExpr);
                }
                continue;
            }

            // Regular field conditions
            foreach (var condition in conditions)
            {
                var expr = BuildFieldExpression(
                    parameter,
                    fieldPath,
                    condition.Operator,
                    condition.Value,
                    typeConverters
                );
                if (expr != null)
                {
                    expressions.Add(expr);
                }
            }
        }

        return CombineExpressions(expressions, isAnd: true);
    }

    /// <summary>
    /// Build logical expression from grouped conditions (for URL-encoded $and/$or)
    /// </summary>
    private Expression? BuildLogicalFromConditions(
        List<MongoCondition> conditions,
        ParameterExpression parameter,
        Dictionary<string, Func<string, object>> typeConverters,
        bool isAnd
    )
    {
        // Group by logical index to create separate conditions
        var groupedByIndex = conditions
            .GroupBy(c => c.LogicalIndex)
            .OrderBy(g => g.Key);

        var indexExpressions = new List<Expression>();

        foreach (var group in groupedByIndex)
        {
            var groupExpressions = new List<Expression>();

            foreach (var condition in group)
            {
                var fieldPath = condition.Field ?? string.Empty;
                var expr = BuildFieldExpression(
                    parameter,
                    fieldPath,
                    condition.Operator,
                    condition.Value,
                    typeConverters
                );
                if (expr != null)
                {
                    groupExpressions.Add(expr);
                }
            }

            // Conditions within same index are AND'd together
            var groupExpr = CombineExpressions(groupExpressions, isAnd: true);
            if (groupExpr != null)
            {
                indexExpressions.Add(groupExpr);
            }
        }

        // Different indexes are combined with the logical operator
        return CombineExpressions(indexExpressions, isAnd);
    }

    private static (string fieldPath, string mongoOperator) ParseFieldPath(string key)
    {
        // Parse formats like:
        // find[sgv][$gte] -> sgv, $gte
        // find[date][$lte] -> date, $lte
        // find[type] -> type, $eq

        var match = Regex.Match(key, @"find\[([^\]]+)\](?:\[(\$\w+)\])?");
        if (match.Success)
        {
            var fieldPath = match.Groups[1].Value;
            var mongoOperator = match.Groups[2].Success ? match.Groups[2].Value : "$eq";
            return (fieldPath, mongoOperator);
        }

        // Fallback for direct field names
        return (key, "$eq");
    }

    #endregion

    #region Type Converters

    private static Dictionary<string, Func<string, object>> GetTypeConverters<T>(
        QueryOptions options
    )
    {
        if (options.TypeConverters.Any())
        {
            return options.TypeConverters;
        }

        return DefaultTreatmentConverters;
    }

    private static object ConvertValue(
        string value,
        string fieldName,
        Dictionary<string, Func<string, object>> typeConverters
    )
    {
        if (typeConverters.ContainsKey(fieldName))
        {
            try
            {
                return typeConverters[fieldName](value);
            }
            catch
            {
                // If conversion fails, use string value
                return value;
            }
        }

        // Default string value
        return value.Trim('\'', '"');
    }

    private static object ParseRegexOrString(string value)
    {
        // Handle regex patterns like /pattern/flags
        var regexPattern = @"^/(.*)/(.*)$";
        var match = Regex.Match(value, regexPattern);

        if (match.Success)
        {
            var pattern = match.Groups[1].Value;
            var flags = match.Groups[2].Value;

            var options = RegexOptions.None;
            if (flags.Contains('i'))
                options |= RegexOptions.IgnoreCase;
            if (flags.Contains('m'))
                options |= RegexOptions.Multiline;

            return new Regex(pattern, options);
        }

        return value.Trim('\'', '"');
    }

    private static object ParseIsoDateToMills(string value)
    {
        // Handle ISO 8601 date strings and convert to epoch milliseconds
        if (DateTimeOffset.TryParse(value, out var dateTime))
        {
            return dateTime.ToUnixTimeMilliseconds();
        }

        // If it's already a number (mills), parse it directly
        if (long.TryParse(value, out var mills))
        {
            return mills;
        }

        // Fallback: try trimming quotes
        var trimmed = value.Trim('\'', '"');
        if (DateTimeOffset.TryParse(trimmed, out dateTime))
        {
            return dateTime.ToUnixTimeMilliseconds();
        }

        return value;
    }

    private static string GetJsonElementValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => string.Empty,
            _ => element.GetRawText(),
        };
    }

    #endregion

    #region Property Expression Builders

    private static Expression? GetPropertyExpression(
        ParameterExpression parameter,
        string fieldPath
    )
    {
        try
        {
            // Handle nested field paths (dot notation like "boluscalc.cob")
            var parts = fieldPath.Split('.');
            Expression currentExpr = parameter;

            foreach (var part in parts)
            {
                var mappedName = MapFieldName(part);
                var propertyInfo = currentExpr.Type.GetProperty(mappedName);

                if (propertyInfo == null)
                {
                    // Property not found - could be in JSONB ExtendedData
                    // For now, return null to skip unknown properties
                    return null;
                }

                currentExpr = Expression.Property(currentExpr, propertyInfo);
            }

            return currentExpr;
        }
        catch
        {
            return null;
        }
    }

    private static string MapFieldName(string fieldName)
    {
        // Map Nightscout field names to Entity property names
        return fieldName.ToLower() switch
        {
            "date" or "mills" or "created_at" => "Mills",
            "sgv" => "Sgv",
            "mbg" => "Mbg",
            "mgdl" => "Mgdl",
            "type" => "Type",
            "direction" => "Direction",
            "device" => "Device",
            "filtered" => "Filtered",
            "unfiltered" => "Unfiltered",
            "rssi" => "Rssi",
            "noise" => "Noise",
            "eventtype" => "EventType",
            "insulin" => "Insulin",
            "carbs" => "Carbs",
            "glucose" => "Glucose",
            "notes" => "Notes",
            "enteredby" => "EnteredBy",
            "reason" => "Reason",
            "data_source" => "DataSource",
            "identifier" => "OriginalId",
            _ => fieldName, // Use as-is for unknown fields
        };
    }

    #endregion

    #region Expression Builders

    private static Expression? BuildFieldExpression(
        ParameterExpression parameter,
        string fieldPath,
        string mongoOperator,
        string value,
        Dictionary<string, Func<string, object>> typeConverters
    )
    {
        try
        {
            var propertyExpr = GetPropertyExpression(parameter, fieldPath);
            if (propertyExpr == null)
            {
                return null;
            }

            var convertedValue = ConvertValue(value, fieldPath.ToLower(), typeConverters);

            return mongoOperator switch
            {
                "$eq" or "" => BuildEqualExpression(propertyExpr, convertedValue),
                "$ne" => BuildNotEqualExpression(propertyExpr, convertedValue),
                "$gt" => BuildGreaterThanExpression(propertyExpr, convertedValue),
                "$gte" => BuildGreaterThanOrEqualExpression(propertyExpr, convertedValue),
                "$lt" => BuildLessThanExpression(propertyExpr, convertedValue),
                "$lte" => BuildLessThanOrEqualExpression(propertyExpr, convertedValue),
                "$in" => BuildInExpression(propertyExpr, value),
                "$nin" => BuildNotInExpression(propertyExpr, value),
                "$regex" => BuildRegexExpression(propertyExpr, convertedValue),
                "$exists" => BuildExistsExpression(propertyExpr, value),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    private static Expression BuildEqualExpression(Expression property, object value)
    {
        // Handle nullable types
        var propertyType = property.Type;
        var underlyingType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

        // Convert value to target type
        object? convertedValue;
        try
        {
            if (value is string strValue && underlyingType != typeof(string))
            {
                convertedValue = Convert.ChangeType(strValue, underlyingType);
            }
            else
            {
                convertedValue = Convert.ChangeType(value, underlyingType);
            }
        }
        catch
        {
            convertedValue = value;
        }

        var constant = Expression.Constant(convertedValue, propertyType.IsValueType && Nullable.GetUnderlyingType(propertyType) != null
            ? propertyType
            : convertedValue?.GetType() ?? typeof(object));

        // For nullable types, we need to handle the comparison carefully
        if (Nullable.GetUnderlyingType(propertyType) != null)
        {
            var convertedProperty = Expression.Convert(property, underlyingType);
            var convertedConstant = Expression.Constant(convertedValue, underlyingType);
            var hasValue = Expression.Property(property, "HasValue");
            var comparison = Expression.Equal(convertedProperty, convertedConstant);
            return Expression.AndAlso(hasValue, comparison);
        }

        return Expression.Equal(property, constant);
    }

    private static Expression BuildNotEqualExpression(Expression property, object value)
    {
        var propertyType = property.Type;
        var underlyingType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

        object? convertedValue;
        try
        {
            convertedValue = Convert.ChangeType(value, underlyingType);
        }
        catch
        {
            convertedValue = value;
        }

        var constant = Expression.Constant(convertedValue, underlyingType);

        if (Nullable.GetUnderlyingType(propertyType) != null)
        {
            var convertedProperty = Expression.Convert(property, underlyingType);
            return Expression.NotEqual(convertedProperty, constant);
        }

        return Expression.NotEqual(property, constant);
    }

    private static Expression BuildGreaterThanExpression(Expression property, object value)
    {
        var propertyType = property.Type;
        var underlyingType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

        object? convertedValue;
        try
        {
            convertedValue = Convert.ChangeType(value, underlyingType);
        }
        catch
        {
            convertedValue = value;
        }

        var constant = Expression.Constant(convertedValue, underlyingType);

        if (Nullable.GetUnderlyingType(propertyType) != null)
        {
            var convertedProperty = Expression.Convert(property, underlyingType);
            return Expression.GreaterThan(convertedProperty, constant);
        }

        return Expression.GreaterThan(property, constant);
    }

    private static Expression BuildGreaterThanOrEqualExpression(Expression property, object value)
    {
        var propertyType = property.Type;
        var underlyingType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

        object? convertedValue;
        try
        {
            convertedValue = Convert.ChangeType(value, underlyingType);
        }
        catch
        {
            convertedValue = value;
        }

        var constant = Expression.Constant(convertedValue, underlyingType);

        if (Nullable.GetUnderlyingType(propertyType) != null)
        {
            var convertedProperty = Expression.Convert(property, underlyingType);
            return Expression.GreaterThanOrEqual(convertedProperty, constant);
        }

        return Expression.GreaterThanOrEqual(property, constant);
    }

    private static Expression BuildLessThanExpression(Expression property, object value)
    {
        var propertyType = property.Type;
        var underlyingType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

        object? convertedValue;
        try
        {
            convertedValue = Convert.ChangeType(value, underlyingType);
        }
        catch
        {
            convertedValue = value;
        }

        var constant = Expression.Constant(convertedValue, underlyingType);

        if (Nullable.GetUnderlyingType(propertyType) != null)
        {
            var convertedProperty = Expression.Convert(property, underlyingType);
            return Expression.LessThan(convertedProperty, constant);
        }

        return Expression.LessThan(property, constant);
    }

    private static Expression BuildLessThanOrEqualExpression(Expression property, object value)
    {
        var propertyType = property.Type;
        var underlyingType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

        object? convertedValue;
        try
        {
            convertedValue = Convert.ChangeType(value, underlyingType);
        }
        catch
        {
            convertedValue = value;
        }

        var constant = Expression.Constant(convertedValue, underlyingType);

        if (Nullable.GetUnderlyingType(propertyType) != null)
        {
            var convertedProperty = Expression.Convert(property, underlyingType);
            return Expression.LessThanOrEqual(convertedProperty, constant);
        }

        return Expression.LessThanOrEqual(property, constant);
    }

    private static Expression BuildInExpression(Expression property, string value)
    {
        // Split pipe-separated values (Nightscout format)
        var values = value.Split('|', StringSplitOptions.RemoveEmptyEntries);
        var propertyType = property.Type;
        var underlyingType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

        // Convert values to proper type
        var convertedValues = new List<object>();
        foreach (var v in values)
        {
            try
            {
                var converted = Convert.ChangeType(v.Trim('\'', '"'), underlyingType);
                convertedValues.Add(converted);
            }
            catch
            {
                convertedValues.Add(v.Trim('\'', '"'));
            }
        }

        // Build Contains expression using Any pattern
        // WHERE field IN (values...) becomes values.Contains(field)
        var listType = typeof(List<>).MakeGenericType(underlyingType);
        var list = Activator.CreateInstance(listType);
        var addMethod = listType.GetMethod("Add");

        foreach (var cv in convertedValues)
        {
            addMethod!.Invoke(list, new[] { cv });
        }

        var listConstant = Expression.Constant(list);
        var containsMethod = listType.GetMethod("Contains", new[] { underlyingType });

        Expression propertyToCheck = property;
        if (Nullable.GetUnderlyingType(propertyType) != null)
        {
            propertyToCheck = Expression.Convert(property, underlyingType);
        }

        return Expression.Call(listConstant, containsMethod!, propertyToCheck);
    }

    private static Expression? BuildInExpressionFromJson(
        Expression property,
        JsonElement valueElement,
        string fieldPath,
        Dictionary<string, Func<string, object>> typeConverters
    )
    {
        if (valueElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = valueElement.EnumerateArray().Select(GetJsonElementValue).ToArray();
        return BuildInExpression(property, string.Join("|", values));
    }

    private static Expression BuildNotInExpression(Expression property, string value)
    {
        var inExpression = BuildInExpression(property, value);
        return Expression.Not(inExpression);
    }

    private static Expression? BuildNotInExpressionFromJson(
        Expression property,
        JsonElement valueElement,
        string fieldPath,
        Dictionary<string, Func<string, object>> typeConverters
    )
    {
        if (valueElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = valueElement.EnumerateArray().Select(GetJsonElementValue).ToArray();
        return BuildNotInExpression(property, string.Join("|", values));
    }

    private static Expression? BuildRegexExpression(Expression property, object value)
    {
        // For EF Core / PostgreSQL, we use string.Contains or LIKE patterns
        // Full regex support would require EF.Functions.ILike or raw SQL

        if (value is Regex regex)
        {
            // Convert to Contains for now - this will work for simple patterns
            var regexPattern = regex.ToString();
            var stringProperty = property.Type == typeof(string)
                ? property
                : Expression.Convert(property, typeof(string));
            var patternConstant = Expression.Constant(regexPattern);
            var containsMethod = typeof(string).GetMethod("Contains", new[] { typeof(string) });

            return Expression.Call(stringProperty, containsMethod!, patternConstant);
        }

        if (value is string stringPattern)
        {
            var stringProperty = property.Type == typeof(string)
                ? property
                : Expression.Convert(property, typeof(string));
            var patternConstant = Expression.Constant(stringPattern);
            var containsMethod = typeof(string).GetMethod("Contains", new[] { typeof(string) });

            return Expression.Call(stringProperty, containsMethod!, patternConstant);
        }

        return null;
    }

    /// <summary>
    /// Build $exists expression - checks if field is null or not null
    /// </summary>
    private static Expression BuildExistsExpression(Expression property, string value)
    {
        // $exists: true means field IS NOT NULL
        // $exists: false means field IS NULL
        var existsValue = value.ToLower() == "true" || value == "1";

        // Handle nullable types
        var propertyType = property.Type;

        if (!propertyType.IsValueType || Nullable.GetUnderlyingType(propertyType) != null)
        {
            // Reference type or nullable value type - compare to null
            var nullConstant = Expression.Constant(null, propertyType);

            return existsValue
                ? Expression.NotEqual(property, nullConstant)
                : Expression.Equal(property, nullConstant);
        }
        else
        {
            // Non-nullable value type - always exists, return true/false constant
            return Expression.Constant(existsValue);
        }
    }

    #endregion

    #region Internal Types

    private class MongoCondition
    {
        public string Operator { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public string? Field { get; set; }
        public int LogicalIndex { get; set; }
    }

    #endregion
}
