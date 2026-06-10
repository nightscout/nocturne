using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace Nocturne.Aspire.Host.Publishing;

/// <summary>
/// Carries deployment UI metadata for the environment variables a resource
/// contributes to published output (.env.example, Portainer template, etc.).
/// </summary>
public sealed class EnvVarMetadataAnnotation : IResourceAnnotation
{
    /// <summary>Human-readable label shown in deployment UIs.</summary>
    public required string Label { get; init; }

    /// <summary>Optional hint shown beneath the field in deployment UIs.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Pre-filled default value. For project/container resources, applies to
    /// the generated <c>_IMAGE</c> variable.
    /// </summary>
    public string? Default { get; init; }

    /// <summary>
    /// For project/container resources: label for the generated <c>_PORT</c>
    /// variable. Omit to exclude the port var from deployment templates.
    /// </summary>
    public string? PortLabel { get; init; }
}

/// <summary>Serializable entry written to <c>env-metadata.json</c> during publish.</summary>
public sealed record EnvVarMetadataEntry(
    string Name,
    string Label,
    string? Description,
    string? Default);

public static class EnvVarMetadataExtensions
{
    /// <summary>
    /// Attaches deployment UI metadata to a parameter resource. The env var
    /// name is derived from the parameter's Aspire name (uppercased,
    /// hyphens → underscores).
    /// </summary>
    public static IResourceBuilder<ParameterResource> WithPublishMetadata(
        this IResourceBuilder<ParameterResource> builder,
        string label,
        string? description = null,
        string? defaultValue = null)
        => builder.WithAnnotation(
            new EnvVarMetadataAnnotation
            {
                Label = label,
                Description = description,
                Default = defaultValue,
            },
            ResourceAnnotationMutationBehavior.Replace);

    /// <summary>
    /// Attaches deployment UI metadata for the <c>_IMAGE</c> (and optionally
    /// <c>_PORT</c>) env vars generated for a containerised project or service.
    /// </summary>
    public static IResourceBuilder<T> WithPublishImageMetadata<T>(
        this IResourceBuilder<T> builder,
        string imageLabel,
        string? imageDefault = null,
        string? portLabel = null)
        where T : IResource
        => builder.WithAnnotation(
            new EnvVarMetadataAnnotation
            {
                Label = imageLabel,
                Default = imageDefault,
                PortLabel = portLabel,
            },
            ResourceAnnotationMutationBehavior.Replace);
}
