using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Nocturne.Core.Contracts.AppBuilds;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.AppBuilds;

namespace Nocturne.API.Services.AppBuilds;

/// <summary>
/// Links a tenant to the GitHub App installation on the user's build repository, and relays secret
/// writes, build dispatches and run reads to it.
/// </summary>
/// <remarks>
/// Nocturne never holds a plaintext secret: the browser seals it with the repository's public key and
/// <see cref="PutSecretAsync"/> relays the ciphertext. Every write and dispatch first checks the
/// repository against its <see cref="BuildTarget"/> (<see cref="Problems"/>).
/// </remarks>
public sealed partial class AppBuildService(
    IAppBuildLinkRepository links,
    GitHubBuildAppClient github,
    AppBuildInstallState installState,
    IOptions<GitHubBuildAppOptions> options,
    ITenantAccessor tenant,
    ILogger<AppBuildService> logger)
{
    private const int RunCount = 10;

    public bool Configured => options.Value.IsConfigured;

    public async Task<AppBuildsStatus> StatusAsync(CancellationToken ct)
    {
        if (!Configured)
            return new AppBuildsStatus { Configured = false, Targets = BuildTargets.All };

        var statuses = new List<AppBuildLinkStatus>();
        foreach (var link in await links.GetAllAsync(ct))
        {
            if (BuildTargets.Find(link.TargetId) is { } target)
                statuses.Add(await LinkStatusAsync(link, target, ct));
        }

        return new AppBuildsStatus { Configured = true, Targets = BuildTargets.All, Links = statuses };
    }

    /// <summary>The slug is the tenant subdomain the browser is on, or null on a tenantless host.</summary>
    public AppBuildLinkStart BeginLink(string targetId, Guid subjectId, string? tenantSlug)
    {
        RequireConfigured();
        var target = RequireTarget(targetId);
        var state = installState.Protect(TenantId, tenantSlug, subjectId, target.Id);

        return new AppBuildLinkStart
        {
            InstallUrl = $"https://github.com/apps/{Uri.EscapeDataString(options.Value.Slug!)}/installations/new?state={Uri.EscapeDataString(state)}",
            CreateRepositoryUrl = "https://github.com/new?" + string.Join('&',
                Query("template_owner", target.TemplateOwner),
                Query("template_name", target.TemplateName),
                Query("owner", "@me"),
                Query("name", target.SuggestedRepositoryName),
                Query("visibility", "private")),
        };
    }

    /// <summary>
    /// Completes the installation callback. <paramref name="installationId"/> comes from the browser, so
    /// the signed state must name this tenant and the signed-in subject. The GitHub user who authorized
    /// the App must also reach that installation.
    /// </summary>
    /// <returns>The linked target's id.</returns>
    public async Task<string> CompleteLinkAsync(string? code, long? installationId, string? state, Guid subjectId, CancellationToken ct)
    {
        RequireConfigured();
        var payload = installState.TryRead(state);
        if (payload is null || payload.Tenant != TenantId || payload.Subject != subjectId
            || BuildTargets.Find(payload.Target) is not { } target)
            throw new AppBuildException(AppBuildException.InvalidState);

        if (string.IsNullOrEmpty(code) || installationId is not > 0)
            throw new AppBuildException(AppBuildException.AuthorizationDenied);

        if (!installState.TryConsume(payload))
            throw new AppBuildException(AppBuildException.InvalidState);

        var account = await github.FindUserInstallationAsync(code, installationId.Value, ct)
            ?? throw new AppBuildException(AppBuildException.InstallationNotYours);

        var repositories = await github.ListInstallationRepositoriesAsync(installationId.Value, ct);
        await links.SaveAsync(new AppBuildLink(
            target.Id,
            installationId.Value,
            account.Login,
            account.IsOrganization,
            repositories.Count == 1 ? repositories[0].FullName : null,
            subjectId,
            default,
            default), ct);

        logger.LogInformation("Linked build target {Target} to GitHub App installation {InstallationId}", target.Id, installationId.Value);
        return target.Id;
    }

    public async Task<IReadOnlyList<AppBuildRepositoryOption>> ListRepositoriesAsync(string targetId, CancellationToken ct)
    {
        var (link, _) = await RequireLinkAsync(targetId, ct);
        var repositories = await github.ListInstallationRepositoriesAsync(link.InstallationId, ct);
        return repositories
            .Select(repository => new AppBuildRepositoryOption { FullName = repository.FullName, IsPrivate = repository.Private })
            .ToList();
    }

    public async Task ChooseRepositoryAsync(string targetId, string fullName, CancellationToken ct)
    {
        var (link, _) = await RequireLinkAsync(targetId, ct);
        var repositories = await github.ListInstallationRepositoriesAsync(link.InstallationId, ct);
        var chosen = repositories.FirstOrDefault(repository =>
            string.Equals(repository.FullName, fullName, StringComparison.OrdinalIgnoreCase))
            ?? throw new AppBuildException(AppBuildException.RepositoryNotInInstallation);

        await links.SetRepositoryAsync(targetId, chosen.FullName, ct);
    }

    public async Task<AppBuildSecretKey> GetSecretKeyAsync(string targetId, CancellationToken ct)
    {
        var (link, repository, _) = await RequireValidRepositoryAsync(targetId, ct);
        var key = await github.GetSecretPublicKeyAsync(link.InstallationId, repository.FullName, ct);
        return new AppBuildSecretKey { KeyId = key.KeyId, Key = key.Key };
    }

    public async Task PutSecretAsync(string targetId, string name, SealedAppBuildSecret secret, CancellationToken ct)
    {
        RequireConfigured();
        if (RequireTarget(targetId).FindSecret(name) is null)
            throw new AppBuildException(AppBuildException.SecretNotAllowed);
        if (!IsBase64(secret.EncryptedValue) || string.IsNullOrWhiteSpace(secret.KeyId))
            throw new AppBuildException(AppBuildException.InputInvalid);

        var (link, repository, _) = await RequireValidRepositoryAsync(targetId, ct);
        await github.PutSecretAsync(link.InstallationId, repository.FullName, name, secret.EncryptedValue, secret.KeyId, ct);
    }

    public async Task<AppBuildDispatchResult> DispatchAsync(string targetId, DispatchAppBuildRequest request, CancellationToken ct)
    {
        RequireConfigured();
        var inputs = ResolveInputs(RequireTarget(targetId), request.Inputs);
        var (link, repository, target) = await RequireValidRepositoryAsync(targetId, ct);

        await github.DispatchWorkflowAsync(link.InstallationId, repository.FullName, target.WorkflowFile,
            repository.DefaultBranch ?? "main", inputs, ct);
        return new AppBuildDispatchResult { RunsUrl = RunsUrl(repository.FullName, target) };
    }

    public async Task<IReadOnlyList<AppBuildRun>> ListRunsAsync(string targetId, CancellationToken ct)
    {
        var (link, target) = await RequireLinkAsync(targetId, ct);
        if (!IsRepositoryFullName(link.RepositoryFullName))
            throw new AppBuildException(AppBuildException.RepositoryNotChosen);
        var runs = await github.ListWorkflowRunsAsync(link.InstallationId, link.RepositoryFullName!, target.WorkflowFile, RunCount, ct);
        return runs.Select(run => new AppBuildRun
        {
            Id = run.Id,
            RunNumber = run.RunNumber,
            Title = run.DisplayTitle,
            State = RunState(run.Status, run.Conclusion),
            Status = run.Status,
            Conclusion = run.Conclusion,
            CreatedAt = run.CreatedAt,
            UpdatedAt = run.UpdatedAt,
            HtmlUrl = run.HtmlUrl,
        }).ToList();
    }

    /// <summary>Forgets the link. The App stays installed on GitHub until the user removes it there.</summary>
    public async Task<AppBuildUnlinkResult> UnlinkAsync(string targetId, CancellationToken ct)
    {
        var (link, _) = await RequireLinkAsync(targetId, ct);
        await links.DeleteAsync(targetId, ct);
        return new AppBuildUnlinkResult
        {
            UninstallUrl = link.AccountIsOrganization
                ? $"https://github.com/organizations/{Uri.EscapeDataString(link.AccountLogin)}/settings/installations/{link.InstallationId}"
                : $"https://github.com/settings/installations/{link.InstallationId}",
        };
    }

    /// <summary>The target checks a repository must pass before Nocturne writes a secret to it or dispatches its workflow.</summary>
    internal static IReadOnlyList<AppBuildRepositoryProblem> Problems(GitHubRepository repository, BuildTarget target)
    {
        var problems = new List<AppBuildRepositoryProblem>();
        if (!repository.Private) problems.Add(AppBuildRepositoryProblem.NotPrivate);
        if (repository.IsTemplate) problems.Add(AppBuildRepositoryProblem.IsTemplate);
        if (!string.Equals(repository.TemplateRepository?.FullName, target.TemplateFullName, StringComparison.OrdinalIgnoreCase))
            problems.Add(AppBuildRepositoryProblem.WrongTemplate);
        return problems;
    }

    /// <summary>The dispatch inputs: every declared input, defaulted when omitted. An undeclared input or a value the target does not allow is refused.</summary>
    internal static Dictionary<string, string> ResolveInputs(BuildTarget target, IReadOnlyDictionary<string, string>? requested)
    {
        requested ??= new Dictionary<string, string>();
        if (requested.Keys.Any(name => target.Inputs.All(input => input.Name != name)))
            throw new AppBuildException(AppBuildException.InputInvalid);

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var input in target.Inputs)
        {
            var value = requested.TryGetValue(input.Name, out var given) ? given : input.Default;
            if (value is null || !input.Accepts(value))
                throw new AppBuildException(AppBuildException.InputInvalid);
            resolved[input.Name] = value;
        }

        return resolved;
    }

    internal static AppBuildRunState RunState(string? status, string? conclusion) => status switch
    {
        "completed" => conclusion switch
        {
            "success" => AppBuildRunState.Succeeded,
            "cancelled" or "skipped" => AppBuildRunState.Cancelled,
            _ => AppBuildRunState.Failed,
        },
        "in_progress" => AppBuildRunState.Running,
        _ => AppBuildRunState.Queued,
    };

    internal static bool IsRepositoryFullName(string? value) =>
        value is not null && RepositoryFullNamePattern().IsMatch(value) && !value.EndsWith("/.", StringComparison.Ordinal)
        && !value.EndsWith("/..", StringComparison.Ordinal);

    private async Task<AppBuildLinkStatus> LinkStatusAsync(AppBuildLink link, BuildTarget target, CancellationToken ct)
    {
        var status = new AppBuildLinkStatus
        {
            TargetId = target.Id,
            AccountLogin = link.AccountLogin,
            RepositoryFullName = link.RepositoryFullName,
            RepositoryUrl = link.RepositoryFullName is null ? null : $"https://github.com/{link.RepositoryFullName}",
            LinkedAt = link.CreatedAt,
            State = AppBuildLinkState.RepositoryNotChosen,
        };
        if (link.RepositoryFullName is null)
            return status;

        try
        {
            var repository = await github.GetRepositoryAsync(link.InstallationId, link.RepositoryFullName, ct);
            var problems = Problems(repository, target);
            var secretsSet = (await github.ListSecretNamesAsync(link.InstallationId, link.RepositoryFullName, ct))
                .Where(name => target.FindSecret(name) is not null)
                .ToList();
            var missing = target.Secrets.Where(secret => secret.Required && !secretsSet.Contains(secret.Name)).Select(secret => secret.Name).ToList();

            return new AppBuildLinkStatus
            {
                TargetId = status.TargetId,
                AccountLogin = status.AccountLogin,
                RepositoryFullName = status.RepositoryFullName,
                RepositoryUrl = repository.HtmlUrl,
                LinkedAt = status.LinkedAt,
                State = problems.Count > 0 ? AppBuildLinkState.Invalid
                    : missing.Count > 0 ? AppBuildLinkState.SecretsMissing
                    : AppBuildLinkState.Ready,
                Validation = new AppBuildRepositoryValidation
                {
                    IsPrivate = repository.Private,
                    IsTemplate = repository.IsTemplate,
                    TemplateFullName = repository.TemplateRepository?.FullName,
                    Problems = problems,
                    SecretsSet = secretsSet,
                    MissingRequiredSecrets = missing,
                },
            };
        }
        catch (AppBuildException ex)
        {
            return new AppBuildLinkStatus
            {
                TargetId = status.TargetId,
                AccountLogin = status.AccountLogin,
                RepositoryFullName = status.RepositoryFullName,
                RepositoryUrl = status.RepositoryUrl,
                LinkedAt = status.LinkedAt,
                State = AppBuildLinkState.Unreachable,
                Error = ex.Code,
            };
        }
    }

    private async Task<(AppBuildLink Link, BuildTarget Target)> RequireLinkAsync(string targetId, CancellationToken ct)
    {
        RequireConfigured();
        var target = RequireTarget(targetId);
        var link = await links.GetAsync(target.Id, ct) ?? throw new AppBuildException(AppBuildException.NotLinked);
        return (link, target);
    }

    /// <remarks>The repository is read again on every call, so a repository made public since it was linked is refused.</remarks>
    private async Task<(AppBuildLink Link, GitHubRepository Repository, BuildTarget Target)> RequireValidRepositoryAsync(string targetId, CancellationToken ct)
    {
        var (link, target) = await RequireLinkAsync(targetId, ct);
        if (!IsRepositoryFullName(link.RepositoryFullName))
            throw new AppBuildException(AppBuildException.RepositoryNotChosen);

        var repository = await github.GetRepositoryAsync(link.InstallationId, link.RepositoryFullName!, ct);
        if (Problems(repository, target).Count > 0)
            throw new AppBuildException(AppBuildException.RepositoryInvalid);
        return (link, repository with { FullName = link.RepositoryFullName! }, target);
    }

    private void RequireConfigured()
    {
        if (!Configured) throw new AppBuildException(AppBuildException.NotConfigured);
    }

    private static BuildTarget RequireTarget(string targetId) =>
        BuildTargets.Find(targetId) ?? throw new AppBuildException(AppBuildException.UnknownTarget);

    private Guid TenantId => tenant.TenantId is { } id && id != Guid.Empty
        ? id
        : throw new AppBuildException(AppBuildException.InvalidState);

    private static string RunsUrl(string repository, BuildTarget target) =>
        $"https://github.com/{repository}/actions/workflows/{Uri.EscapeDataString(target.WorkflowFile)}";

    private static string Query(string name, string value) => $"{name}={Uri.EscapeDataString(value)}";

    private static bool IsBase64(string value)
    {
        Span<byte> buffer = new byte[value.Length];
        return value.Length > 0 && Convert.TryFromBase64String(value, buffer, out _);
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex RepositoryFullNamePattern();
}
