using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Nocturne.API.Configuration;

/// <summary>
/// Result filter, registered globally, that serialises v1-v3 responses with
/// <see cref="NightscoutJsonOptions.Create"/>.
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
            context.Result = new JsonResult(objectResult.Value, NightscoutOptions)
            {
                StatusCode = objectResult.StatusCode
            };
        }

        await next();
    }
}
