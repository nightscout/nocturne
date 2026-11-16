using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Nocturne.Core.Models.AppBuilds;

/// <summary>
/// An app a tenant owner builds in their own GitHub repository. It names the template that repository
/// is generated from and the <c>workflow_dispatch</c> workflow that builds it. Its inputs and secrets
/// are the only ones Nocturne may send to it. The web app keys its display strings on <see cref="Id"/>,
/// each input's <see cref="BuildInput.Name"/> and each secret's <see cref="BuildTargetSecret.Name"/>.
/// </summary>
public sealed class BuildTarget
{
    public required string Id { get; init; }
    public required string TemplateOwner { get; init; }
    public required string TemplateName { get; init; }

    /// <summary>The name the create-from-template page suggests for the user's repository.</summary>
    public required string SuggestedRepositoryName { get; init; }

    /// <summary>The workflow file name under <c>.github/workflows</c>.</summary>
    public required string WorkflowFile { get; init; }

    public required IReadOnlyList<BuildInput> Inputs { get; init; }
    public required IReadOnlyList<BuildTargetSecret> Secrets { get; init; }

    [JsonIgnore]
    public string TemplateFullName => $"{TemplateOwner}/{TemplateName}";

    public BuildTargetSecret? FindSecret(string name) =>
        Secrets.FirstOrDefault(secret => string.Equals(secret.Name, name, StringComparison.Ordinal));
}

[JsonConverter(typeof(JsonStringEnumConverter<BuildInputKind>))]
public enum BuildInputKind
{
    [EnumMember(Value = "text"), JsonStringEnumMemberName("text")]
    Text,

    [EnumMember(Value = "choice"), JsonStringEnumMemberName("choice")]
    Choice,
}

public sealed class BuildInput
{
    public required string Name { get; init; }
    public required BuildInputKind Kind { get; init; }
    public required string Default { get; init; }

    /// <summary>The allowed values of a <see cref="BuildInputKind.Choice"/> input; empty for text.</summary>
    public IReadOnlyList<string> Options { get; init; } = [];

    /// <summary>The whole-value pattern a <see cref="BuildInputKind.Text"/> input must match.</summary>
    public string? Pattern { get; init; }

    public bool Accepts(string value) => Kind switch
    {
        BuildInputKind.Choice => Options.Contains(value, StringComparer.Ordinal),
        _ => Pattern is not null && Regex.IsMatch(value, Pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100)),
    };
}

public sealed class BuildTargetSecret
{
    public required string Name { get; init; }
    public required bool Required { get; init; }
}

/// <summary>The build targets Nocturne knows. A target not listed here cannot be linked or built.</summary>
public static class BuildTargets
{
    /// <summary>
    /// AAPS Builder (<c>nightscout/aaps-builder</c>): its <c>build.yml</c> takes a version and a variant
    /// and reads one secret, <c>KEYSTORE_SET</c>. The optional <c>GDRIVE_OAUTH2</c> is left out: the
    /// APK is an Actions artifact of the user's private repository.
    /// </summary>
    public static readonly BuildTarget Aaps = new()
    {
        Id = "aaps",
        TemplateOwner = "nightscout",
        TemplateName = "aaps-builder",
        SuggestedRepositoryName = "my-aaps",
        WorkflowFile = "build.yml",
        Inputs =
        [
            new BuildInput
            {
                Name = "version",
                Kind = BuildInputKind.Text,
                Default = "latest",
                // "latest" or a git tag of nightscout/AndroidAPS; the workflow checks that the tag exists.
                Pattern = @"^(latest|[0-9][0-9A-Za-z._-]{0,63})$",
            },
            new BuildInput
            {
                Name = "variant",
                Kind = BuildInputKind.Choice,
                Default = "fullRelease",
                Options =
                [
                    "fullRelease", "aapsclientRelease", "aapsclient2Release", "aapsclient3Release",
                    "pumpcontrolRelease", "fullDebug",
                ],
            },
        ],
        Secrets = [new BuildTargetSecret { Name = "KEYSTORE_SET", Required = true }],
    };

    public static readonly IReadOnlyList<BuildTarget> All = [Aaps];

    public static BuildTarget? Find(string? id) =>
        All.FirstOrDefault(target => string.Equals(target.Id, id, StringComparison.Ordinal));
}
