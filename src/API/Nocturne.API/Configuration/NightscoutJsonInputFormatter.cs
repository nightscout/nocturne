using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Extensions.Options;
using Nocturne.Core.Models.Serializers;

namespace Nocturne.API.Configuration;

/// <summary>
/// Reads request bodies on Nightscout-compatible paths (v1-v3) with MVC's default JSON options,
/// except that an uploader's lowercase <c>id</c> stays a plain field instead of binding to
/// <c>_id</c> (<see cref="UploaderIdJsonModifier"/>).
/// </summary>
public sealed class NightscoutJsonInputFormatter : SystemTextJsonInputFormatter
{
    public NightscoutJsonInputFormatter(ILogger<SystemTextJsonInputFormatter> logger)
        : base(CreateOptions(), logger)
    {
    }

    public override bool CanRead(InputFormatterContext context) =>
        NightscoutApiPath.Version(context.HttpContext.Request.Path) is not null && base.CanRead(context);

    private static JsonOptions CreateOptions()
    {
        var options = new JsonOptions();
        options.JsonSerializerOptions.TypeInfoResolver =
            (options.JsonSerializerOptions.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver())
            .WithAddedModifier(UploaderIdJsonModifier.RemoveBaseIdProperty);
        return options;
    }
}

/// <summary>Puts <see cref="NightscoutJsonInputFormatter"/> ahead of MVC's own JSON input formatter.</summary>
public sealed class NightscoutJsonInputFormatterSetup(ILoggerFactory loggerFactory) : IConfigureOptions<MvcOptions>
{
    public void Configure(MvcOptions options) =>
        options.InputFormatters.Insert(0,
            new NightscoutJsonInputFormatter(loggerFactory.CreateLogger<SystemTextJsonInputFormatter>()));
}
