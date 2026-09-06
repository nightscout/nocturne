using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Nocturne.API.Configuration;

/// <summary>
/// Action filter that applies Nightscout-compatible JSON serialization to v1-v3 endpoints.
/// This filter modifies the JsonSerializerOptions to:
/// - Ignore null values
/// - Exclude properties marked with [NocturneOnly]
/// </summary>
public class NightscoutJsonFilter : IAsyncResultFilter
{
    private static readonly JsonSerializerOptions NightscoutOptions = NightscoutJsonOptions.Create();

    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        var isNightscoutEndpoint =
            NightscoutApiPath.Version(context.HttpContext.Request.Path) is not null;

        if (isNightscoutEndpoint && context.Result is ObjectResult objectResult)
        {
            // Replace with JsonResult using Nightscout options
            context.Result = new JsonResult(objectResult.Value, NightscoutOptions)
            {
                StatusCode = objectResult.StatusCode
            };
        }

        await next();
    }
}

/// <summary>
/// Attribute to apply Nightscout JSON formatting to a controller or action
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class NightscoutJsonAttribute : TypeFilterAttribute
{
    public NightscoutJsonAttribute() : base(typeof(NightscoutJsonFilter))
    {
    }
}
