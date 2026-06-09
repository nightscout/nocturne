using System.Text;

namespace Nocturne.Aspire.Publishing;

public static class RoutingDiagramRenderer
{
    public static string Render(AspirePublishModel model)
    {
        var sb = new StringBuilder();
        sb.AppendLine("flowchart TD");
        sb.AppendLine("    Internet((Internet))");
        sb.AppendLine();

        var externalServices = model.Services.Where(s => s.HostPorts.Count > 0).ToList();
        var internalServices = model.Services.Where(s => s.HostPorts.Count == 0).ToList();

        // External subgraph
        sb.AppendLine("    subgraph ext[\"External\"]");
        foreach (var svc in externalServices)
            sb.AppendLine($"        {NodeId(svc.Name)}[{NodeLabel(svc)}]");
        sb.AppendLine("    end");
        sb.AppendLine();

        // Internal subgraph
        sb.AppendLine("    subgraph net[\"Internal (aspire network)\"]");
        foreach (var svc in internalServices)
            sb.AppendLine($"        {NodeId(svc.Name)}{NodeShape(svc)}");
        sb.AppendLine("    end");
        sb.AppendLine();

        // Internet → gateway
        var gateway = model.Services.FirstOrDefault(s => s.Kind == ServiceKind.Gateway);
        if (gateway != null)
            sb.AppendLine($"    Internet --> {NodeId(gateway.Name)}");

        // YARP routes: group by target, one labeled edge per target
        var routesByTarget = model.Routes
            .GroupBy(r => r.TargetServiceName)
            .ToList();

        foreach (var group in routesByTarget)
        {
            var paths = group
                .Select(r => r.PathPattern == null ? "/** (fallback)" : r.PathPattern)
                .ToList();
            var label = string.Join("\\n", paths);
            if (gateway != null)
                sb.AppendLine($"    {NodeId(gateway.Name)} -->|\"{label}\"| {NodeId(group.Key)}");
        }

        // Service-to-service edges (Reference only, both endpoints must be declared)
        var declaredIds = new HashSet<string>(model.Services.Select(s => NodeId(s.Name)));
        declaredIds.Add("Internet");
        foreach (var edge in model.Edges.Where(e =>
            e.Kind == EdgeKind.Reference &&
            declaredIds.Contains(NodeId(e.From)) &&
            declaredIds.Contains(NodeId(e.To))))
            sb.AppendLine($"    {NodeId(edge.From)} -->|Reference| {NodeId(edge.To)}");

        return sb.ToString().TrimEnd();
    }

    private static string NodeId(string name) => name.Replace("-", "_");

    private static string NodeLabel(ServiceNode svc)
    {
        var ports = svc.HostPorts.Count > 0
            ? "\\n:" + string.Join(", :", svc.HostPorts.Select(p => p.HostPort))
            : svc.InternalPorts.Count > 0
                ? "\\n:" + string.Join(", :", svc.InternalPorts)
                : string.Empty;
        return $"\"{svc.Name}{ports}\"";
    }

    private static string NodeShape(ServiceNode svc)
    {
        var label = NodeLabel(svc);
        return svc.Kind == ServiceKind.Database ? $"[({label})]" : $"[{label}]";
    }
}
