using System.Collections;
using System.Reflection;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Yarp;
using Nocturne.Aspire.Publishing;
using Yarp.ReverseProxy.Configuration;
using PublishYarpRoute = Nocturne.Aspire.Publishing.YarpRoute;

namespace Nocturne.Aspire.Host.Publishing;

internal static class AspireModelExtractor
{
    public static AspirePublishModel Extract(DistributedApplicationModel model)
    {
        var services = model.Resources
            .Select(ToServiceNode)
            .Where(s => s is not null)
            .Cast<ServiceNode>()
            .ToList();

        var edges = ExtractEdges(model);
        var routes = ExtractYarpRoutes(model);

        return new AspirePublishModel(services, edges, routes);
    }

    private static ServiceNode? ToServiceNode(IResource resource)
    {
        var kind = ClassifyKind(resource);
        if (kind is null) return null;

        var endpointAnnotations = resource.Annotations
            .OfType<EndpointAnnotation>()
            .ToList();

        var internalPorts = endpointAnnotations
            .Where(e => !e.IsExternal)
            .Select(e => e.Port?.ToString() ?? string.Empty)
            .Where(p => p.Length > 0)
            .ToList();

        var hostPorts = endpointAnnotations
            .Where(e => e.IsExternal)
            .Select(e => new HostPortMapping(
                e.Port?.ToString() ?? "?",
                e.TargetPort?.ToString() ?? "?"))
            .ToList();

        return new ServiceNode(resource.Name, kind.Value, internalPorts, hostPorts);
    }

    private static ServiceKind? ClassifyKind(IResource resource) => resource switch
    {
        YarpResource                                                   => ServiceKind.Gateway,
        PostgresDatabaseResource                                       => null, // skip — child resource
        PostgresServerResource                                         => ServiceKind.Database,
        ProjectResource r when r.Name.Contains("web",
            StringComparison.OrdinalIgnoreCase)                        => ServiceKind.Web,
        ProjectResource                                                => ServiceKind.Api,
        ContainerResource r when r.Name.Contains("web",
            StringComparison.OrdinalIgnoreCase)                        => ServiceKind.Web,
        ContainerResource r when r.Name.Contains("api",
            StringComparison.OrdinalIgnoreCase)                        => ServiceKind.Api,
        ContainerResource                                              => ServiceKind.Container,
        ParameterResource                                              => null,
        _                                                              => null,
    };

    private static IReadOnlyList<ServiceEdge> ExtractEdges(DistributedApplicationModel model)
    {
        var edges = new List<ServiceEdge>();
        var seen = new HashSet<(string, string)>();

        foreach (var resource in model.Resources)
        {
            foreach (var rel in resource.Annotations.OfType<ResourceRelationshipAnnotation>())
            {
                var key = (resource.Name, rel.Resource.Name);
                if (seen.Add(key))
                    edges.Add(new ServiceEdge(resource.Name, rel.Resource.Name, EdgeKind.Reference));
            }

            foreach (var wait in resource.Annotations.OfType<WaitAnnotation>())
            {
                var key = (resource.Name, wait.Resource.Name);
                if (seen.Add(key))
                    edges.Add(new ServiceEdge(resource.Name, wait.Resource.Name, EdgeKind.WaitFor));
            }
        }

        return edges;
    }

    private static IReadOnlyList<PublishYarpRoute> ExtractYarpRoutes(DistributedApplicationModel model)
    {
        var yarp = model.Resources.OfType<YarpResource>().FirstOrDefault();
        if (yarp is null) return [];

        // YarpResource.Routes is internal — access via reflection.
        // RouteConfig is from Yarp.ReverseProxy.Configuration and is public.
        var routesProp = typeof(YarpResource).GetProperty(
            "Routes",
            BindingFlags.Instance | BindingFlags.NonPublic);

        if (routesProp is null) return [];

        var routesList = routesProp.GetValue(yarp) as IEnumerable;
        if (routesList is null) return [];

        var result = new List<PublishYarpRoute>();
        var routeConfigProp = default(PropertyInfo);

        foreach (var route in routesList)
        {
            routeConfigProp ??= route.GetType().GetProperty(
                "RouteConfig",
                BindingFlags.Instance | BindingFlags.NonPublic);

            if (routeConfigProp?.GetValue(route) is not RouteConfig rc) continue;
            if (string.IsNullOrEmpty(rc.ClusterId)) continue;

            // "/{**catchall}" is stored for catch-all routes; map to null per our model contract
            var path = rc.Match.Path is "/{**catchall}" ? null : rc.Match.Path;
            var serviceName = rc.ClusterId.StartsWith("cluster_", StringComparison.Ordinal)
                ? rc.ClusterId["cluster_".Length..]
                : rc.ClusterId;

            result.Add(new PublishYarpRoute(path, serviceName));
        }

        return result;
    }
}
