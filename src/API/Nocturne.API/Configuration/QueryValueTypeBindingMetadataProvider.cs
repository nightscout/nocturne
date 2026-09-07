using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;

namespace Nocturne.API.Configuration;

/// <summary>
/// Makes every <c>[FromQuery]</c> action parameter of a non-nullable value type that declares no
/// default value mandatory, so omitting it answers 400 instead of running the action.
/// </summary>
/// <remarks>
/// MVC reports a missing query value as "not set" rather than an error, and then falls back to
/// <c>default</c> for a value type — an omitted <c>DateTime</c> reads as 0001-01-01. A parameter
/// opts out by being nullable or by declaring a default.
/// </remarks>
public sealed class QueryValueTypeBindingMetadataProvider : IBindingMetadataProvider
{
    public void CreateBindingMetadata(BindingMetadataProviderContext context)
    {
        if (context.Key.MetadataKind != ModelMetadataKind.Parameter
            || context.Key.ParameterInfo is not { HasDefaultValue: false }
            || context.BindingMetadata.BindingSource != BindingSource.Query
            || !context.BindingMetadata.IsBindingAllowed
            || !context.Key.ModelType.IsValueType
            || Nullable.GetUnderlyingType(context.Key.ModelType) is not null)
        {
            return;
        }

        context.BindingMetadata.IsBindingRequired = true;
    }
}
