namespace Nocturne.API.Services.AppBuilds;

/// <summary>A refusal or failure of an app-build action. <see cref="Exception.Message"/> is the code the web app reads.</summary>
public sealed class AppBuildException(string code) : Exception(code)
{
    public const string NotConfigured = "not_configured";
    public const string UnknownTarget = "unknown_target";
    public const string NotLinked = "not_linked";
    public const string RepositoryNotChosen = "repository_not_chosen";
    public const string RepositoryNotInInstallation = "repository_not_in_installation";
    public const string RepositoryInvalid = "repository_invalid";
    public const string RepositoryNotFound = "repository_not_found";
    public const string SecretNotAllowed = "secret_not_allowed";
    public const string InputInvalid = "input_invalid";
    public const string InvalidState = "invalid_state";
    public const string InstallationNotYours = "installation_not_yours";
    public const string NoAdminRepository = "no_admin_repository";
    public const string NotLinker = "not_linker";
    public const string LinkerNotAdmin = "linker_not_admin";
    public const string SignedOut = "signed_out";
    public const string Forbidden = "forbidden";
    public const string InstallationGone = "installation_gone";
    public const string AuthorizationDenied = "authorization_denied";
    public const string GitHubRefused = "github_refused";
    public const string GitHubRejected = "github_rejected";
    public const string GitHubRateLimited = "github_rate_limited";
    public const string GitHubUnavailable = "github_unavailable";

    public string Code => Message;

    public int StatusCode => Code switch
    {
        NotConfigured or UnknownTarget or NotLinked => StatusCodes.Status404NotFound,
        RepositoryNotChosen or RepositoryInvalid or InstallationGone or RepositoryNotFound => StatusCodes.Status409Conflict,
        InstallationNotYours or NoAdminRepository or NotLinker or LinkerNotAdmin => StatusCodes.Status403Forbidden,
        GitHubRateLimited => StatusCodes.Status429TooManyRequests,
        GitHubRefused or GitHubUnavailable => StatusCodes.Status502BadGateway,
        _ => StatusCodes.Status400BadRequest,
    };
}
