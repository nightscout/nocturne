using Nocturne.Core.Contracts.AppBuilds;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.AppBuilds;
using Microsoft.Extensions.Options;

namespace Nocturne.API.Services.AppBuilds;

/// <summary>
/// Links a tenant to the GitHub App installation on the user's build repository, and relays secret
/// writes, build dispatches and run reads to it.
/// </summary>
/// <remarks>
/// Nocturne never holds a plaintext secret: the browser seals it with the repository's public key and
/// <see cref="PutSecretAsync"/> relays the ciphertext. Every write and dispatch first checks the
/// repository against its <see cref="BuildTarget"/> (<see cref="Problems"/>).
/// <para>
/// The installation acts in the linking person's GitHub account, so only that person
/// (<see cref="AppBuildLink.LinkedBySubjectId"/>) may choose its repository, write a secret or start a
/// build. Any caller the controller admits may read the status and runs, or unlink, which only forgets
/// the link in Nocturne. Another person takes over by linking with their own GitHub authorization.
/// </para>
/// </remarks>
public sealed class AppBuildService(
    IAppBuildLinkRepository links,
    GitHubBuildAppClient github,
    AppBuildInstallState installState,
    IOptions<GitHubBuildAppOptions> options,
    ITenantAccessor tenant,
    ILogger<AppBuildService> logger)
{
    private const int RunCount = 10;

    public bool Configured => github.IsConfigured;

    public async Task<AppBuildsStatus> StatusAsync(Guid callerSubjectId, CancellationToken ct)
    {
        if (!Configured)
            return new AppBuildsStatus { Configured = false, Targets = BuildTargets.All };

        var statuses = new List<AppBuildLinkStatus>();
        foreach (var link in await links.GetAllAsync(ct))
        {
            if (BuildTargets.Find(link.TargetId) is { } target)
                statuses.Add(await LinkStatusAsync(link, target, callerSubjectId, ct));
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
    /// the App must also reach that installation and administer at least one of its repositories.
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

        var installation = await github.AuthorizeUserInstallationAsync(code, installationId.Value, ct)
            ?? throw new AppBuildException(AppBuildException.InstallationNotYours);
        if (installation.AdminRepositories.Count == 0)
            throw new AppBuildException(AppBuildException.NoAdminRepository);

        await links.SaveAsync(new AppBuildLink(
            target.Id,
            installationId.Value,
            installation.Login,
            installation.IsOrganization,
            installation.AdminRepositories.Count == 1 ? installation.AdminRepositories[0] : null,
            installation.AdminRepositories,
            subjectId,
            default,
            default), ct);

        logger.LogInformation("Linked build target {Target} to GitHub App installation {InstallationId}", target.Id, installationId.Value);
        return target.Id;
    }

    /// <summary>The installation's repositories the linking person may choose: those they administer.</summary>
    public async Task<IReadOnlyList<AppBuildRepositoryOption>> ListRepositoriesAsync(string targetId, CancellationToken ct)
    {
        var (link, _) = await RequireLinkAsync(targetId, ct);
        return (await ChoosableRepositoriesAsync(link, ct))
            .Select(repository => new AppBuildRepositoryOption { FullName = repository.FullName, IsPrivate = repository.Private })
            .ToList();
    }

    public async Task ChooseRepositoryAsync(string targetId, string fullName, Guid callerSubjectId, CancellationToken ct)
    {
        var (link, _) = await RequireLinkerAsync(targetId, callerSubjectId, ct);
        var chosen = (await ChoosableRepositoriesAsync(link, ct)).FirstOrDefault(repository =>
                string.Equals(repository.FullName, fullName, StringComparison.OrdinalIgnoreCase))
            ?? throw new AppBuildException(AppBuildException.RepositoryNotInInstallation);

        await links.SetRepositoryAsync(targetId, chosen.FullName, ct);
    }

    public async Task<AppBuildSecretKey> GetSecretKeyAsync(string targetId, CancellationToken ct)
    {
        var (link, target) = await RequireLinkAsync(targetId, ct);
        var repository = await RequireValidRepositoryAsync(link, target, ct);
        var key = await github.GetSecretPublicKeyAsync(link.InstallationId, repository.FullName, ct);
        return new AppBuildSecretKey { KeyId = key.KeyId, Key = key.Key };
    }

    /// <summary>
    /// Relays a sealed secret. The base64 check only refuses a malformed request; whether the value is
    /// sealed is the browser's part (<see cref="SealedAppBuildSecret"/>).
    /// </summary>
    public async Task PutSecretAsync(string targetId, string name, SealedAppBuildSecret secret, Guid callerSubjectId, CancellationToken ct)
    {
        RequireConfigured();
        if (RequireTarget(targetId).FindSecret(name) is null)
            throw new AppBuildException(AppBuildException.SecretNotAllowed);
        if (!IsBase64(secret.EncryptedValue) || string.IsNullOrWhiteSpace(secret.KeyId))
            throw new AppBuildException(AppBuildException.InputInvalid);

        var (link, target) = await RequireLinkerAsync(targetId, callerSubjectId, ct);
        var repository = await RequireValidRepositoryAsync(link, target, ct);
        await github.PutSecretAsync(link.InstallationId, repository.FullName, name, secret.EncryptedValue, secret.KeyId, ct);
    }

    public async Task<AppBuildDispatchResult> DispatchAsync(string targetId, DispatchAppBuildRequest request, Guid callerSubjectId, CancellationToken ct)
    {
        RequireConfigured();
        var inputs = ResolveInputs(RequireTarget(targetId), request.Inputs);
        var (link, target) = await RequireLinkerAsync(targetId, callerSubjectId, ct);
        var repository = await RequireValidRepositoryAsync(link, target, ct);

        await github.DispatchWorkflowAsync(link.InstallationId, repository.FullName, target.WorkflowFile,
            repository.DefaultBranch ?? "main", inputs, ct);
        return new AppBuildDispatchResult { RunsUrl = RunsUrl(repository.FullName, target) };
    }

    public async Task<IReadOnlyList<AppBuildRun>> ListRunsAsync(string targetId, CancellationToken ct)
    {
        var (link, target) = await RequireLinkAsync(targetId, ct);
        var repository = link.RepositoryFullName ?? throw new AppBuildException(AppBuildException.RepositoryNotChosen);
        var runs = await github.ListWorkflowRunsAsync(link.InstallationId, repository, target.WorkflowFile, RunCount, ct);
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

    private async Task<AppBuildLinkStatus> LinkStatusAsync(AppBuildLink link, BuildTarget target, Guid callerSubjectId, CancellationToken ct)
    {
        var status = new AppBuildLinkStatus
        {
            TargetId = target.Id,
            AccountLogin = link.AccountLogin,
            RepositoryFullName = link.RepositoryFullName,
            RepositoryUrl = link.RepositoryFullName is null ? null : $"https://github.com/{link.RepositoryFullName}",
            LinkedAt = link.CreatedAt,
            LinkedByCaller = link.LinkedBySubjectId == callerSubjectId,
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

            return status with
            {
                RepositoryUrl = repository.HtmlUrl,
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
            return status with { State = AppBuildLinkState.Unreachable, Error = ex.Code };
        }
    }

    private async Task<IReadOnlyList<GitHubRepository>> ChoosableRepositoriesAsync(AppBuildLink link, CancellationToken ct) =>
        (await github.ListInstallationRepositoriesAsync(link.InstallationId, ct))
            .Where(repository => link.AdminRepositories.Contains(repository.FullName, StringComparer.OrdinalIgnoreCase))
            .ToList();

    private async Task<(AppBuildLink Link, BuildTarget Target)> RequireLinkAsync(string targetId, CancellationToken ct)
    {
        RequireConfigured();
        var target = RequireTarget(targetId);
        var link = await links.GetAsync(target.Id, ct) ?? throw new AppBuildException(AppBuildException.NotLinked);
        return (link, target);
    }

    private async Task<(AppBuildLink Link, BuildTarget Target)> RequireLinkerAsync(string targetId, Guid callerSubjectId, CancellationToken ct)
    {
        var (link, target) = await RequireLinkAsync(targetId, ct);
        if (link.LinkedBySubjectId != callerSubjectId)
            throw new AppBuildException(AppBuildException.NotLinker);
        return (link, target);
    }

    /// <remarks>The repository is read again on every call, so a repository made public since it was linked is refused.</remarks>
    private async Task<GitHubRepository> RequireValidRepositoryAsync(AppBuildLink link, BuildTarget target, CancellationToken ct)
    {
        if (link.RepositoryFullName is not { } fullName)
            throw new AppBuildException(AppBuildException.RepositoryNotChosen);
        if (!link.AdminRepositories.Contains(fullName, StringComparer.OrdinalIgnoreCase))
            throw new AppBuildException(AppBuildException.RepositoryNotInInstallation);

        var repository = await github.GetRepositoryAsync(link.InstallationId, fullName, ct);
        if (Problems(repository, target).Count > 0)
            throw new AppBuildException(AppBuildException.RepositoryInvalid);
        return repository with { FullName = fullName };
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
}
