using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;

namespace Nocturne.API.Configuration;

/// <summary>
/// Custom JSON output formatter for Nightscout-compatible endpoints (v1, v2, v3).
/// This formatter:
/// - Ignores null values (Nightscout doesn't include null fields)
/// - Excludes properties marked with [NocturneOnly] attribute
/// - Uses camelCase property naming
/// </summary>
public class NightscoutJsonOutputFormatter : SystemTextJsonOutputFormatter
{
    public NightscoutJsonOutputFormatter() : base(NightscoutJsonOptions.Create())
    {
    }
}

/// <summary>
/// Extension methods for configuring Nightscout-compatible JSON serialization
/// </summary>
public static class NightscoutJsonExtensions
{
    /// <summary>
    /// Configures MVC to use Nightscout-compatible JSON formatting for v1-v3 endpoints.
    /// V4+ endpoints will use standard JSON serialization.
    /// </summary>
    public static IMvcBuilder AddNightscoutJsonFormatters(this IMvcBuilder builder)
    {
        builder.AddMvcOptions(options =>
        {
            // Insert our custom formatter at the beginning for Nightscout endpoints
            options.OutputFormatters.Insert(0, new NightscoutJsonOutputFormatter());
        });

        return builder;
    }
}
