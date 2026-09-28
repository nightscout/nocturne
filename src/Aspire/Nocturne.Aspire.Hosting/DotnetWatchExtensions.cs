using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// A .NET project run under <c>dotnet watch</c> by DCP. A <see cref="ProjectResource"/> always
/// runs as <c>dotnet run</c>, so the only watch Aspire offers is the whole AppHost under watch,
/// where a rude edit restarts every resource. Implements <see cref="IResourceWithServiceDiscovery"/>
/// so it stands in for the project in <c>WithReference</c>.
/// </summary>
public sealed class DotnetWatchProjectResource(string name, string workingDirectory)
    : ExecutableResource(name, "dotnet", workingDirectory), IResourceWithServiceDiscovery;

public static class DotnetWatchExtensions
{
    /// <summary>
    /// Adds <paramref name="projectPath"/> under <c>dotnet watch</c>, hot-reloading edits in place
    /// and restarting only this process on a rude edit. The builds run without analyzers, the
    /// build server, node reuse or the OpenAPI XML comment generator; CI, the IDE and plain
    /// <c>dotnet build</c> keep them.
    /// </summary>
    public static IResourceBuilder<DotnetWatchProjectResource> AddDotnetWatchProject(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        string projectPath,
        string devTargetsPath)
    {
        var resource = new DotnetWatchProjectResource(name, Path.GetDirectoryName(projectPath)!);

        return builder
            .AddResource(resource)
            .WithArgs("watch", "--project", projectPath, "--non-interactive", "run", "--no-launch-profile")
            .WithEnvironment("DOTNET_WATCH_RESTART_ON_RUDE_EDIT", "1")
            .WithEnvironment("UseSharedCompilation", "false")
            .WithEnvironment("MSBUILDDISABLENODEREUSE", "1")
            .WithEnvironment("RunAnalyzersDuringBuild", "false")
            .WithEnvironment("CustomAfterMicrosoftCommonTargets", devTargetsPath)
            .WithEnvironment("DOTNET_gcServer", "0")
            .WithEnvironment("DOTNET_GCConserveMemory", "5")
            .WithEnvironment("DOTNET_TieredPGO", "0");
    }

    /// <summary>
    /// Binds Kestrel to the target port of <paramref name="endpointName"/>, which
    /// <see cref="ProjectResource"/> derives on its own from its endpoints.
    /// </summary>
    public static IResourceBuilder<DotnetWatchProjectResource> WithAspNetCoreUrls(
        this IResourceBuilder<DotnetWatchProjectResource> builder,
        string endpointName)
    {
        var endpoint = builder.GetEndpoint(endpointName);
        return builder.WithEnvironment(
            "ASPNETCORE_URLS",
            ReferenceExpression.Create(
                $"{endpoint.Property(EndpointProperty.Scheme)}://localhost:{endpoint.Property(EndpointProperty.TargetPort)}"));
    }
}
