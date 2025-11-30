using System.Linq.Expressions;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Nocturne.Core.Contracts;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Services;

/// <summary>
/// Service for parsing Nightscout-style queries (legacy MongoDB format) into Entity Framework Core expressions
/// Maintains 1:1 compatibility with legacy Nightscout query behavior
/// </summary>
public class QueryParser : IQueryParser
{
    private static readonly Dictionary<string, Func<string, object>> DefaultEntryConverters = new()
    {
        ["date"] = s => long.Parse(s),
        ["mills"] = s => long.Parse(s),
        ["sgv"] = s => int.Parse(s),
        ["filtered"] = s => int.Parse(s),
        ["unfiltered"] = s => int.Parse(s),
        ["rssi"] = s => int.Parse(s),
        ["noise"] = s => int.Parse(s),
        ["mgdl"] = s => int.Parse(s),
        ["mbg"] = s => int.Parse(s),
        ["type"] = s => s.Trim('\'', '"'), // Handle quoted strings
        ["direction"] = s => s.Trim('\'', '"'),
        ["device"] = s => s.Trim('\'', '"'),
        ["is_demo"] = s => bool.Parse(s)
    };

    private static readonly Dictionary<string, Func<string, object>> DefaultTreatmentConverters = new()
    {
        ["date"] = s => long.Parse(s),
        ["mills"] = s => long.Parse(s),
        ["insulin"] = s => double.Parse(s),
        ["carbs"] = s => double.Parse(s),
        ["glucose"] = s => int.Parse(s),
        ["notes"] = ParseRegexOrString,
        ["eventType"] = ParseRegexOrString,
        ["enteredBy"] = ParseRegexOrString,
        ["reason"] = ParseRegexOrString
    };

    /// <inheritdoc />
    public Task<IQueryable<T>> ApplyQueryAsync<T>(
        IQueryable<T> queryable, 
        string findQuery, 
        QueryOptions options,
        CancellationToken cancellationToken = default) where T : class
    {
        if (string.IsNullOrWhiteSpace(findQuery))
        {
            return Task.FromResult(queryable);
        }

        var filter = ParseFilterAsync<T>(findQuery, options, cancellationToken).Result;
        if (filter != null)
        {
            queryable = queryable.Where(filter);
        }

        return Task.FromResult(queryable);
    }

    /// <inheritdoc />
    public Task<Expression<Func<T, bool>>?> ParseFilterAsync<T>(
        string findQuery, 
        QueryOptions options,
        CancellationToken cancellationToken = default) where T : class
    {
        if (string.IsNullOrWhiteSpace(findQuery))
        {
            return Task.FromResult<Expression<Func<T, bool>>?>(null);
        }

        // Parse the find query from URL parameters format
        var queryParams = ParseUrlEncodedQuery(findQuery);
        if (!queryParams.Any())
        {
            return Task.FromResult<Expression<Func<T, bool>>?>(null);
        }

        // Determine type converters based on entity type
        var typeConverters = GetTypeConverters<T>(options);
        
        // Build the expression tree
        var parameter = Expression.Parameter(typeof(T), "x");
        Expression? combinedExpression = null;

        foreach (var kvp in queryParams)
        {
            var fieldPath = kvp.Key;
            var conditions = kvp.Value;

            foreach (var condition in conditions)
            {
                var expr = BuildFieldExpression<T>(parameter, fieldPath, condition.Operator, condition.Value, typeConverters);
                if (expr != null)
                {
                    combinedExpression = combinedExpression == null 
                        ? expr 
                        : Expression.AndAlso(combinedExpression, expr);
                }
            }
        }

        var result = combinedExpression != null 
            ? Expression.Lambda<Func<T, bool>>(combinedExpression, parameter)
            : null;
            
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public IQueryable<T> ApplyDefaultDateFilter<T>(
        IQueryable<T> queryable, 
        string? findQuery, 
        string? dateString, 
        QueryOptions options) where T : class
    {
        if (options.DisableDefaultDateFilter)
        {
            return queryable;
        }

        // Check if there's already a date constraint
        var hasDateConstraint = !string.IsNullOrEmpty(dateString) || 
                               (!string.IsNullOrEmpty(findQuery) && 
                                (findQuery.Contains("date") || findQuery.Contains("mills") || findQuery.Contains(options.DateField.ToLower())));

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

    private static Dictionary<string, Func<string, object>> GetTypeConverters<T>(QueryOptions options)
    {
        if (options.TypeConverters.Any())
        {
            return options.TypeConverters;
        }

        // Return appropriate converters based on entity type
        return typeof(T) == typeof(EntryEntity) 
            ? DefaultEntryConverters 
            : DefaultTreatmentConverters;
    }

    private static Dictionary<string, List<MongoCondition>> ParseUrlEncodedQuery(string findQuery)
    {
        var result = new Dictionary<string, List<MongoCondition>>();
        
        try
        {
            // Handle both URL-encoded and JSON-style queries
            string decodedQuery = HttpUtility.UrlDecode(findQuery);
            
            // If it looks like JSON, try to parse as JSON first
            if (decodedQuery.TrimStart().StartsWith("{"))
            {
                return ParseJsonQuery(decodedQuery);
            }

            // Parse URL parameters (find[field][$op]=value format)
            var queryParams = HttpUtility.ParseQueryString(findQuery);
            
            foreach (string? key in queryParams.AllKeys)
            {
                if (string.IsNullOrEmpty(key))
                    continue;

                var values = queryParams.GetValues(key);
                if (values == null)
                    continue;

                foreach (var value in values)
                {
                    var (fieldPath, mongoOperator) = ParseFieldPath(key);
                    
                    if (!result.ContainsKey(fieldPath))
                    {
                        result[fieldPath] = new List<MongoCondition>();
                    }

                    result[fieldPath].Add(new MongoCondition
                    {
                        Operator = mongoOperator,
                        Value = value
                    });
                }
            }
        }
        catch (Exception)
        {
            // If parsing fails, return empty dictionary to avoid breaking queries
            return new Dictionary<string, List<MongoCondition>>();
        }

        return result;
    }

    private static Dictionary<string, List<MongoCondition>> ParseJsonQuery(string jsonQuery)
    {
        var result = new Dictionary<string, List<MongoCondition>>();
        
        try
        {
            using var doc = JsonDocument.Parse(jsonQuery);
            ParseJsonElement(doc.RootElement, result, string.Empty);
        }
        catch (JsonException)
        {
            // If JSON parsing fails, return empty dictionary
            return new Dictionary<string, List<MongoCondition>>();
        }

        return result;
    }

    private static void ParseJsonElement(JsonElement element, Dictionary<string, List<MongoCondition>> result, string currentPath)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var newPath = string.IsNullOrEmpty(currentPath) ? property.Name : $"{currentPath}.{property.Name}";
                    
                    if (property.Name.StartsWith("$"))
                    {
                        // This is an operator
                        var parentPath = currentPath;
                        if (!result.ContainsKey(parentPath))
                        {
                            result[parentPath] = new List<MongoCondition>();
                        }

                        result[parentPath].Add(new MongoCondition
                        {
                            Operator = property.Name,
                            Value = GetJsonElementValue(property.Value)
                        });
                    }
                    else
                    {
                        ParseJsonElement(property.Value, result, newPath);
                    }
                }
                break;
            
            case JsonValueKind.Array:
                // Handle array values (for $in, $nin operators)
                var arrayValues = element.EnumerateArray().Select(GetJsonElementValue).ToArray();
                if (!result.ContainsKey(currentPath))
                {
                    result[currentPath] = new List<MongoCondition>();
                }
                result[currentPath].Add(new MongoCondition
                {
                    Operator = "$in", // Default for array values
                    Value = string.Join("|", arrayValues) // Pipe-separated like Nightscout
                });
                break;
            
            default:
                // Direct value assignment (implies $eq)
                if (!result.ContainsKey(currentPath))
                {
                    result[currentPath] = new List<MongoCondition>();
                }
                result[currentPath].Add(new MongoCondition
                {
                    Operator = "$eq",
                    Value = GetJsonElementValue(element)
                });
                break;
        }
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
            _ => element.GetRawText()
        };
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

    private static Expression? BuildFieldExpression<T>(
        ParameterExpression parameter,
        string fieldPath,
        string mongoOperator,
        string value,
        Dictionary<string, Func<string, object>> typeConverters)
    {
        try
        {
            // Get the property expression
            var propertyExpr = GetPropertyExpression(parameter, fieldPath);
            if (propertyExpr == null)
            {
                return null;
            }

            // Convert value using type converter if available
            var convertedValue = ConvertValue(value, fieldPath.ToLower(), typeConverters);
            
            // Build the expression based on operator
            return mongoOperator switch
            {
                "$eq" or "" => BuildEqualExpression(propertyExpr, convertedValue),
                "$ne" => BuildNotEqualExpression(propertyExpr, convertedValue),
                "$gt" => BuildGreaterThanExpression(propertyExpr, convertedValue),
                "$gte" => BuildGreaterThanOrEqualExpression(propertyExpr, convertedValue),
                "$lt" => BuildLessThanExpression(propertyExpr, convertedValue),
                "$lte" => BuildLessThanOrEqualExpression(propertyExpr, convertedValue),
                "$in" => BuildInExpression(propertyExpr, value), // Pass original value for splitting
                "$nin" => BuildNotInExpression(propertyExpr, value),
                "$regex" => BuildRegexExpression(propertyExpr, convertedValue),
                _ => null
            };
        }
        catch
        {
            // If expression building fails, return null to skip this condition
            return null;
        }
    }

    private static Expression? GetPropertyExpression(ParameterExpression parameter, string fieldPath)
    {
        try
        {
            var propertyInfo = parameter.Type.GetProperty(MapFieldName(fieldPath));
            if (propertyInfo == null)
            {
                return null;
            }

            return Expression.Property(parameter, propertyInfo);
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
            "date" or "mills" => "Mills",
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
            "is_demo" => "IsDemo",
            _ => fieldName // Use as-is for unknown fields
        };
    }

    private static object ConvertValue(string value, string fieldName, Dictionary<string, Func<string, object>> typeConverters)
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
        var regexPattern = @"^/(.*)/(.*)?$";
        var match = Regex.Match(value, regexPattern);
        
        if (match.Success)
        {
            var pattern = match.Groups[1].Value;
            var flags = match.Groups[2].Value;
            
            var options = RegexOptions.None;
            if (flags.Contains('i')) options |= RegexOptions.IgnoreCase;
            if (flags.Contains('m')) options |= RegexOptions.Multiline;
            
            return new Regex(pattern, options);
        }
        
        return value.Trim('\'', '"');
    }

    private static Expression BuildEqualExpression(Expression property, object value)
    {
        var constant = Expression.Constant(value, value.GetType());
        var convertedProperty = Expression.Convert(property, value.GetType());
        return Expression.Equal(convertedProperty, constant);
    }

    private static Expression BuildNotEqualExpression(Expression property, object value)
    {
        var constant = Expression.Constant(value, value.GetType());
        var convertedProperty = Expression.Convert(property, value.GetType());
        return Expression.NotEqual(convertedProperty, constant);
    }

    private static Expression BuildGreaterThanExpression(Expression property, object value)
    {
        var constant = Expression.Constant(value, value.GetType());
        var convertedProperty = Expression.Convert(property, value.GetType());
        return Expression.GreaterThan(convertedProperty, constant);
    }

    private static Expression BuildGreaterThanOrEqualExpression(Expression property, object value)
    {
        var constant = Expression.Constant(value, value.GetType());
        var convertedProperty = Expression.Convert(property, value.GetType());
        return Expression.GreaterThanOrEqual(convertedProperty, constant);
    }

    private static Expression BuildLessThanExpression(Expression property, object value)
    {
        var constant = Expression.Constant(value, value.GetType());
        var convertedProperty = Expression.Convert(property, value.GetType());
        return Expression.LessThan(convertedProperty, constant);
    }

    private static Expression BuildLessThanOrEqualExpression(Expression property, object value)
    {
        var constant = Expression.Constant(value, value.GetType());
        var convertedProperty = Expression.Convert(property, value.GetType());
        return Expression.LessThanOrEqual(convertedProperty, constant);
    }

    private static Expression BuildInExpression(Expression property, string value)
    {
        // Split pipe-separated values
        var values = value.Split('|', StringSplitOptions.RemoveEmptyEntries);
        var propertyType = property.Type;
        
        // Convert values to proper type
        var convertedValues = values.Select(v => Convert.ChangeType(v.Trim('\'', '"'), propertyType)).ToList();
        
        // Create Contains expression
        var listConstant = Expression.Constant(convertedValues);
        var containsMethod = typeof(List<object>).GetMethod("Contains");
        var convertedProperty = Expression.Convert(property, typeof(object));
        
        return Expression.Call(listConstant, containsMethod!, convertedProperty);
    }

    private static Expression BuildNotInExpression(Expression property, string value)
    {
        var inExpression = BuildInExpression(property, value);
        return Expression.Not(inExpression);
    }

    private static Expression? BuildRegexExpression(Expression property, object value)
    {
        if (value is Regex regex)
        {
            var stringProperty = Expression.Convert(property, typeof(string));
            var regexConstant = Expression.Constant(regex);
            var isMatchMethod = typeof(Regex).GetMethod("IsMatch", new[] { typeof(string) });
            
            return Expression.Call(regexConstant, isMatchMethod!, stringProperty);
        }
        
        if (value is string pattern)
        {
            var stringProperty = Expression.Convert(property, typeof(string));
            var patternConstant = Expression.Constant(pattern);
            var containsMethod = typeof(string).GetMethod("Contains", new[] { typeof(string) });
            
            return Expression.Call(stringProperty, containsMethod!, patternConstant);
        }

        return null;
    }

    private class MongoCondition
    {
        public string Operator { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }
}