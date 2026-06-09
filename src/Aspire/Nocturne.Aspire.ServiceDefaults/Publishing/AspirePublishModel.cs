namespace Nocturne.Aspire.Publishing;

public record AspirePublishModel(
    IReadOnlyList<ServiceNode> Services,
    IReadOnlyList<ServiceEdge> Edges,
    IReadOnlyList<YarpRoute> Routes
);

public record ServiceNode(
    string Name,
    ServiceKind Kind,
    IReadOnlyList<string> InternalPorts,
    IReadOnlyList<HostPortMapping> HostPorts
);

public record HostPortMapping(string HostPort, string ContainerPort);

public record ServiceEdge(string From, string To, EdgeKind Kind);

/// <param name="PathPattern">null means catch-all (no path constraint)</param>
public record YarpRoute(string? PathPattern, string TargetServiceName);

public enum ServiceKind { Gateway, Api, Web, Database, Container }
public enum EdgeKind { Reference, WaitFor }
