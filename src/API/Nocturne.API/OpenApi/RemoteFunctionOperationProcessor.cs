using System.Reflection;
using Nocturne.API.Attributes;
using NSwag.Generation.Processors;
using NSwag.Generation.Processors.Contexts;

namespace Nocturne.API.OpenApi;

/// <summary>
/// NSwag operation processor that emits x-remote-type, x-remote-invalidates,
/// and x-client-property extensions based on Remote* and ClientPropertyName attributes.
/// </summary>
public class RemoteFunctionOperationProcessor : IOperationProcessor
{
    public bool Process(OperationProcessorContext context)
    {
        var methodInfo = context.MethodInfo;

        var queryAttr = methodInfo.GetCustomAttribute<RemoteQueryAttribute>();
        var commandAttr = methodInfo.GetCustomAttribute<RemoteCommandAttribute>();

        if (queryAttr != null && !queryAttr.Skip)
        {
            context.OperationDescription.Operation.ExtensionData ??= new Dictionary<string, object?>();
            context.OperationDescription.Operation.ExtensionData["x-remote-type"] = "query";
        }
        else if (commandAttr != null && !commandAttr.Skip)
        {
            context.OperationDescription.Operation.ExtensionData ??= new Dictionary<string, object?>();
            context.OperationDescription.Operation.ExtensionData["x-remote-type"] = "command";

            if (commandAttr.Invalidates.Length > 0)
            {
                context.OperationDescription.Operation.ExtensionData["x-remote-invalidates"] = commandAttr.Invalidates;
            }
        }

        var clientPropertyAttr = methodInfo.DeclaringType?.GetCustomAttribute<ClientPropertyNameAttribute>();
        if (clientPropertyAttr != null)
        {
            context.OperationDescription.Operation.ExtensionData ??= new Dictionary<string, object?>();
            context.OperationDescription.Operation.ExtensionData["x-client-property"] = clientPropertyAttr.PropertyName;
        }

        return true;
    }
}
