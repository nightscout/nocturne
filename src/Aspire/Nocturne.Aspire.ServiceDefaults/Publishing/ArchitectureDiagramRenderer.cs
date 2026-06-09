using System.Text;

namespace Nocturne.Aspire.Publishing;

public static class ArchitectureDiagramRenderer
{
    public static string Render(AspirePublishModel model)
    {
        var sb = new StringBuilder();
        sb.AppendLine("flowchart LR");
        sb.AppendLine();

        // Internet node — always the external entry point
        sb.AppendLine("    Internet((Internet))");
        sb.AppendLine();

        // External subgraph: services with host-port bindings
        var externalServices = model.Services.Where(s => s.HostPorts.Count > 0).ToList();
        sb.AppendLine("    subgraph ext[\"External\"]");
        foreach (var svc in externalServices)
        {
            var port = svc.HostPorts[0].HostPort;
            sb.AppendLine($"        {NodeId(svc.Name)}[\"{svc.Name}\\n:{port}\"]");
        }
        sb.AppendLine("    end");
        sb.AppendLine();

        // Internal subgraph: all other services
        var internalServices = model.Services.Where(s => s.HostPorts.Count == 0).ToList();
        sb.AppendLine("    subgraph net[\"aspire network\"]");
        foreach (var svc in internalServices)
        {
            var portSuffix = svc.InternalPorts.Count > 0 ? "\\n:" + svc.InternalPorts[0] : string.Empty;

            if (svc.Kind == ServiceKind.Web)
            {
                // Render as a nested subgraph with BFF and Frontend child nodes so the
                // SvelteKit server-side (BFF) and client bundle are architecturally distinct.
                var groupId = NodeId(svc.Name) + "_group";
                sb.AppendLine($"        subgraph {groupId}[\"{svc.Name}\"]");
                sb.AppendLine($"            {NodeId(svc.Name)}_frontend[\"Frontend\"]");
                sb.AppendLine($"            {NodeId(svc.Name)}_bff[\"BFF\"]");
                sb.AppendLine($"        end");
            }
            else if (svc.Kind == ServiceKind.Database)
            {
                sb.AppendLine($"        {NodeId(svc.Name)}[(\"{svc.Name}{portSuffix}\")]");
            }
            else
            {
                sb.AppendLine($"        {NodeId(svc.Name)}[\"{svc.Name}\"]");
            }
        }
        sb.AppendLine("    end");
        sb.AppendLine();

        // Edge filtering helpers
        var webNames = model.Services
            .Where(s => s.Kind == ServiceKind.Web)
            .Select(s => s.Name)
            .ToHashSet();

        var representedNames = new HashSet<string>(model.Services.Select(s => s.Name));
        representedNames.Add("internet");

        var serviceKinds = model.Services.ToDictionary(s => s.Name, s => s.Kind);

        // Edge node IDs:
        // - Outbound from web service → BFF (the server process makes the call)
        // - Inbound to web service from Gateway → Frontend (user browser traffic enters here)
        // - Inbound to web service from any other service → BFF (server-to-server calls)
        string EdgeFromId(string name) =>
            webNames.Contains(name) ? NodeId(name) + "_bff" : NodeId(name);

        string EdgeToId(string fromName, string toName)
        {
            if (!webNames.Contains(toName)) return NodeId(toName);
            return serviceKinds.TryGetValue(fromName, out var k) && k == ServiceKind.Gateway
                ? NodeId(toName) + "_frontend"
                : NodeId(toName) + "_bff";
        }

        // Internet → gateway
        var gateway = model.Services.FirstOrDefault(s => s.Kind == ServiceKind.Gateway);
        if (gateway != null)
            sb.AppendLine($"    Internet --> {NodeId(gateway.Name)}");

        // Reference edges only — both endpoints must be represented services
        foreach (var edge in model.Edges.Where(e =>
            e.Kind == EdgeKind.Reference &&
            representedNames.Contains(e.From) &&
            representedNames.Contains(e.To)))
            sb.AppendLine($"    {EdgeFromId(edge.From)} --> {EdgeToId(edge.From, edge.To)}");

        // Within each web group: frontend routes inbound requests through to the BFF
        foreach (var webSvc in model.Services.Where(s => s.Kind == ServiceKind.Web))
            sb.AppendLine($"    {NodeId(webSvc.Name)}_frontend --> {NodeId(webSvc.Name)}_bff");

        return sb.ToString().TrimEnd();
    }

    private static string NodeId(string name) => name.Replace("-", "_");
}
