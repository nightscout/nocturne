using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.AppBuilds;

/// <summary>
/// A tenant's link to the GitHub App installation that builds one <see cref="BuildTarget"/>.
/// <see cref="RepositoryFullName"/> is null until the linking person picks one of
/// <see cref="AdminRepositories"/>: the installation's repositories that person administered on GitHub
/// when they linked it. <see cref="LinkerGitHubLogin"/> and <see cref="LinkerGitHubId"/> name that
/// person on GitHub, so their role can be checked again before each write.
/// </summary>
public sealed record AppBuildLink(
    string TargetId,
    long InstallationId,
    string AccountLogin,
    bool AccountIsOrganization,
    string? RepositoryFullName,
    IReadOnlyList<string> AdminRepositories,
    Guid LinkedBySubjectId,
    string LinkerGitHubLogin,
    long LinkerGitHubId,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed class AppBuildsStatus
{
    /// <summary>False when the instance has no <c>GitHub:BuildApp</c> configuration; every other endpoint then refuses.</summary>
    public bool Configured { get; init; }

    public IReadOnlyList<BuildTarget> Targets { get; init; } = [];
    public IReadOnlyList<AppBuildLinkStatus> Links { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter<AppBuildLinkState>))]
public enum AppBuildLinkState
{
    /// <summary>The installation exposes several repositories and none is chosen yet.</summary>
    [EnumMember(Value = "repository_not_chosen"), JsonStringEnumMemberName("repository_not_chosen")]
    RepositoryNotChosen,

    /// <summary>GitHub did not answer, or the installation or repository is gone.</summary>
    [EnumMember(Value = "unreachable"), JsonStringEnumMemberName("unreachable")]
    Unreachable,

    /// <summary>The repository fails a target check (<see cref="AppBuildRepositoryProblem"/>); nothing is written or dispatched.</summary>
    [EnumMember(Value = "invalid"), JsonStringEnumMemberName("invalid")]
    Invalid,

    /// <summary>The person who linked no longer has the admin role on the repository; they relink, or another admin does.</summary>
    [EnumMember(Value = "linker_not_admin"), JsonStringEnumMemberName("linker_not_admin")]
    LinkerNotAdmin,

    [EnumMember(Value = "secrets_missing"), JsonStringEnumMemberName("secrets_missing")]
    SecretsMissing,

    [EnumMember(Value = "ready"), JsonStringEnumMemberName("ready")]
    Ready,
}

[JsonConverter(typeof(JsonStringEnumConverter<AppBuildRepositoryProblem>))]
public enum AppBuildRepositoryProblem
{
    [EnumMember(Value = "not_private"), JsonStringEnumMemberName("not_private")]
    NotPrivate,

    [EnumMember(Value = "is_template"), JsonStringEnumMemberName("is_template")]
    IsTemplate,

    [EnumMember(Value = "wrong_template"), JsonStringEnumMemberName("wrong_template")]
    WrongTemplate,
}

public sealed record AppBuildLinkStatus
{
    public required string TargetId { get; init; }
    public required AppBuildLinkState State { get; init; }
    public required string AccountLogin { get; init; }
    public string? RepositoryFullName { get; init; }
    public string? RepositoryUrl { get; init; }
    public DateTime LinkedAt { get; init; }

    /// <summary>
    /// Whether the caller linked it. Only the linker may choose the repository, write a secret or start
    /// a build; <c>AppBuildService</c> states why.
    /// </summary>
    public bool LinkedByCaller { get; init; }

    /// <summary>Null when no repository is chosen or GitHub could not be read.</summary>
    public AppBuildRepositoryValidation? Validation { get; init; }

    /// <summary>The code of the failure behind <see cref="AppBuildLinkState.Unreachable"/>.</summary>
    public string? Error { get; init; }
}

public sealed class AppBuildRepositoryValidation
{
    public bool IsPrivate { get; init; }
    public bool IsTemplate { get; init; }

    /// <summary>The template the repository was generated from, or null when it was not.</summary>
    public string? TemplateFullName { get; init; }

    public IReadOnlyList<AppBuildRepositoryProblem> Problems { get; init; } = [];

    /// <summary>Whether the person who linked has the admin role on the repository now.</summary>
    public bool LinkerIsAdmin { get; init; }

    /// <summary>The target's secrets the repository holds, by name. GitHub never returns a value.</summary>
    public IReadOnlyList<string> SecretsSet { get; init; } = [];

    public IReadOnlyList<string> MissingRequiredSecrets { get; init; } = [];
}

public sealed class AppBuildLinkStart
{
    /// <summary>The GitHub App installation page, carrying the signed <c>state</c>.</summary>
    public required string InstallUrl { get; init; }

    /// <summary>GitHub's create-from-template page, preset to a private copy of the target's template.</summary>
    public required string CreateRepositoryUrl { get; init; }
}

public sealed class AppBuildRepositoryOption
{
    public required string FullName { get; init; }
    public bool IsPrivate { get; init; }
}

public sealed class ChooseAppBuildRepositoryRequest
{
    [Required, MaxLength(201)]
    public string FullName { get; set; } = string.Empty;
}

public sealed class AppBuildSecretKey
{
    /// <summary>GitHub's id for <see cref="Key"/>; sent back with the sealed value.</summary>
    public required string KeyId { get; init; }

    /// <summary>The repository's Curve25519 public key, base64. The browser seals the secret with it (libsodium sealed box).</summary>
    public required string Key { get; init; }
}

/// <summary>A secret already sealed in the browser. Nocturne relays it and cannot open it.</summary>
public sealed class SealedAppBuildSecret
{
    /// <summary>The libsodium sealed box, base64.</summary>
    [Required, MaxLength(65536)]
    public string EncryptedValue { get; set; } = string.Empty;

    [Required, MaxLength(64)]
    public string KeyId { get; set; } = string.Empty;
}

public sealed class DispatchAppBuildRequest
{
    /// <summary>Input values by name. An omitted input takes its <see cref="BuildInput.Default"/>.</summary>
    public Dictionary<string, string> Inputs { get; set; } = [];
}

public sealed class AppBuildDispatchResult
{
    /// <summary>The workflow's run list on github.com; the new run appears there within seconds.</summary>
    public required string RunsUrl { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<AppBuildRunState>))]
public enum AppBuildRunState
{
    [EnumMember(Value = "queued"), JsonStringEnumMemberName("queued")]
    Queued,

    [EnumMember(Value = "running"), JsonStringEnumMemberName("running")]
    Running,

    [EnumMember(Value = "succeeded"), JsonStringEnumMemberName("succeeded")]
    Succeeded,

    [EnumMember(Value = "failed"), JsonStringEnumMemberName("failed")]
    Failed,

    [EnumMember(Value = "cancelled"), JsonStringEnumMemberName("cancelled")]
    Cancelled,
}

public sealed class AppBuildRun
{
    public long Id { get; init; }
    public int RunNumber { get; init; }

    /// <summary>The run's title. The AAPS workflow names its version and variant here.</summary>
    public string? Title { get; init; }

    public AppBuildRunState State { get; init; }

    /// <summary>GitHub's own <c>status</c> and <c>conclusion</c>, as sent.</summary>
    public string? Status { get; init; }
    public string? Conclusion { get; init; }

    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    /// <summary>The run page on github.com, where the APK artifact is downloaded. Nocturne does not proxy it.</summary>
    public required string HtmlUrl { get; init; }
}

public sealed class AppBuildUnlinkResult
{
    /// <summary>Where the user removes the GitHub App from their account; unlinking only forgets it in Nocturne.</summary>
    public required string UninstallUrl { get; init; }
}
