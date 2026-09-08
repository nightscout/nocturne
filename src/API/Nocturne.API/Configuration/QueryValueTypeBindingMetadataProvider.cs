using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;

namespace Nocturne.API.Configuration;

/// <summary>
/// Makes an action parameter of a non-nullable value type that declares no default value
/// mandatory, so omitting it answers 400 instead of running the action.
/// </summary>
/// <remarks>
/// MVC reports a missing query value as "not set" rather than an error and then falls back to
/// <c>default</c> for a value type, so an omitted <c>DateTime</c> reads as 0001-01-01. The
/// <c>[FromQuery]</c> attribute is load-bearing: binding metadata carries no source for a
/// parameter whose query binding is inferred, and this leaves those alone.
/// </remarks>
public sealed class QueryValueTypeBindingMetadataProvider : IBindingMetadataProvider
{
    public void CreateBindingMetadata(BindingMetadataProviderContext context)
    {
        if (context.Key.ParameterInfo is not { HasDefaultValue: false }
            || context.BindingMetadata.BindingSource != BindingSource.Query
            || !context.Key.ModelType.IsValueType
            || Nullable.GetUnderlyingType(context.Key.ModelType) is not null)
        {
            return;
        }

        context.BindingMetadata.IsBindingRequired = true;
    }
}

public static class BindingConventions
{
    /// <summary>
    /// Applies the binding conventions to an MVC host. Both hosts in this assembly need them:
    /// the one that serves requests, and <c>NSwagStartup</c>, whose generated spec reads its
    /// <c>required</c> flags off the same binding metadata.
    /// </summary>
    public static void AddNocturneBindingConventions(this MvcOptions options) =>
        options.ModelMetadataDetailsProviders.Add(new QueryValueTypeBindingMetadataProvider());
}
