using System.Reflection;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Tests.Infrastructure;

internal static class RemoteAttributes
{
    public static RemoteCommandAttribute Command<TController>(string action) =>
        Command(typeof(TController), action);

    public static RemoteCommandAttribute Command(Type controller, string action) =>
        Method(controller, action).GetCustomAttribute<RemoteCommandAttribute>()
        ?? throw new InvalidOperationException(
            $"{controller.Name}.{action} has no [RemoteCommand] attribute.");

    public static RemoteQueryAttribute? Query<TController>(string action) =>
        Query(typeof(TController), action);

    public static RemoteQueryAttribute? Query(Type controller, string action) =>
        Query(Method(controller, action));

    public static RemoteQueryAttribute? Query(MethodInfo method) =>
        method.GetCustomAttribute<RemoteQueryAttribute>();

    public static RemoteFormAttribute Form<TController>(string action) =>
        Method(typeof(TController), action).GetCustomAttribute<RemoteFormAttribute>()
        ?? throw new InvalidOperationException(
            $"{typeof(TController).Name}.{action} has no [RemoteForm] attribute.");

    public static MethodInfo Method(Type controller, string action) =>
        controller.GetMethod(action)
        ?? throw new InvalidOperationException($"{controller.Name} has no public method {action}.");
}
