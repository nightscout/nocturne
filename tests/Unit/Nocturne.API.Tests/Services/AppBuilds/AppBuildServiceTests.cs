using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.API.Services.AppBuilds;
using Nocturne.Core.Contracts.AppBuilds;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.AppBuilds;

namespace Nocturne.API.Tests.Services.AppBuilds;

[Trait("Category", "Unit")]
public class AppBuildServiceTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Subject = Guid.NewGuid();

    private readonly FakeGitHub _github = new();
    private readonly InMemoryLinks _links = new();
    private readonly RecordingLogger _log = new();
    private readonly AppBuildInstallState _state;

    public AppBuildServiceTests()
    {
        _state = new AppBuildInstallState(new EphemeralDataProtectionProvider(), new MemoryCache(new MemoryCacheOptions()), _github.Time);
    }

    private AppBuildService CreateService(GitHubBuildAppOptions? options = null, Guid? tenant = null)
    {
        var accessor = new Mock<ITenantAccessor>();
        accessor.SetupGet(a => a.TenantId).Returns(tenant ?? Tenant);
        return new AppBuildService(_links, _github.CreateClient(_log.For<GitHubBuildAppClient>()), _state, Options.Create(options ?? _github.Options),
            accessor.Object, _log.For<AppBuildService>());
    }

    private string StateFor(Guid? tenant = null, Guid? subject = null, string target = "aaps") =>
        _state.Protect(tenant ?? Tenant, "alice", subject ?? Subject, target);

    private void Linked(string? repository = FakeGitHub.Repository) =>
        _links.Rows["aaps"] = new AppBuildLink("aaps", FakeGitHub.InstallationId, "alice", false, repository, Subject, default, default);

    [Fact]
    public async Task Without_configuration_the_feature_reports_unconfigured_and_every_action_refuses()
    {
        var service = CreateService(new GitHubBuildAppOptions());
        Linked();

        var status = await service.StatusAsync(default);

        status.Configured.Should().BeFalse();
        status.Targets.Should().ContainSingle(t => t.Id == "aaps");
        status.Links.Should().BeEmpty();

        var refusals = new Func<Task>[]
        {
            () => Task.FromResult(service.BeginLink("aaps", Subject, null)),
            () => service.CompleteLinkAsync("code", FakeGitHub.InstallationId, StateFor(), Subject, default),
            () => service.ListRepositoriesAsync("aaps", default),
            () => service.ChooseRepositoryAsync("aaps", FakeGitHub.Repository, default),
            () => service.GetSecretKeyAsync("aaps", default),
            () => service.PutSecretAsync("aaps", "KEYSTORE_SET", Sealed(), default),
            () => service.DispatchAsync("aaps", new DispatchAppBuildRequest(), default),
            () => service.ListRunsAsync("aaps", default),
            () => service.UnlinkAsync("aaps", default),
        };
        foreach (var refusal in refusals)
            await refusal.Should().ThrowAsync<AppBuildException>().WithMessage(AppBuildException.NotConfigured);
        _github.Requests.Should().BeEmpty();
    }

    [Fact]
    public void Begin_link_returns_the_install_url_with_the_state_and_a_private_template_url()
    {
        var start = CreateService().BeginLink("aaps", Subject, "alice");

        start.InstallUrl.Should().StartWith("https://github.com/apps/nocturne-builds/installations/new?state=");
        var state = Uri.UnescapeDataString(start.InstallUrl[(start.InstallUrl.IndexOf("state=", StringComparison.Ordinal) + 6)..]);
        _state.TryRead(state)!.Subject.Should().Be(Subject);
        start.CreateRepositoryUrl.Should().Be(
            "https://github.com/new?template_owner=nightscout&template_name=aaps-builder&owner=%40me&name=my-aaps&visibility=private");
    }

    [Fact]
    public async Task A_callback_links_the_installation_and_its_only_repository_without_keeping_the_user_token()
    {
        _github.OnUserFlow(FakeGitHub.InstallationId).OnInstallationRepositories(FakeGitHub.Repository);

        var target = await CreateService().CompleteLinkAsync("code", FakeGitHub.InstallationId, StateFor(), Subject, default);

        target.Should().Be("aaps");
        var link = _links.Rows["aaps"];
        link.Should().BeEquivalentTo(new AppBuildLink("aaps", FakeGitHub.InstallationId, "alice", false, FakeGitHub.Repository, Subject, default, default));
        JsonSerializer.Serialize(link).Should().NotContain("ghu_").And.NotContain("ghs_");
        _log.Messages.Should().NotContain(m => m.Contains("ghu_") || m.Contains("ghs_") || m.Contains("client-secret"));
        _github.Requests.Should().Contain(r => r.Method == HttpMethod.Delete && r.Path == "/applications/Iv23client/token",
            "the user token is revoked once the installation is confirmed");
    }

    [Fact]
    public async Task A_callback_for_an_installation_the_user_cannot_reach_is_refused()
    {
        _github.OnUserFlow(9999);

        await FluentActions.Awaiting(() => CreateService().CompleteLinkAsync("code", FakeGitHub.InstallationId, StateFor(), Subject, default))
            .Should().ThrowAsync<AppBuildException>().WithMessage(AppBuildException.InstallationNotYours);
        _links.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task A_callback_with_several_repositories_leaves_the_choice_to_the_owner()
    {
        _github.OnUserFlow(FakeGitHub.InstallationId).OnInstallationRepositories(FakeGitHub.Repository, "alice/other");

        await CreateService().CompleteLinkAsync("code", FakeGitHub.InstallationId, StateFor(), Subject, default);

        _links.Rows["aaps"].RepositoryFullName.Should().BeNull();
        (await CreateService().StatusAsync(default)).Links.Single().State.Should().Be(AppBuildLinkState.RepositoryNotChosen);
    }

    [Theory]
    [InlineData("other-tenant")]
    [InlineData("other-subject")]
    [InlineData("unknown-target")]
    [InlineData("garbage")]
    public async Task A_callback_whose_state_does_not_bind_this_tenant_and_subject_is_refused_before_GitHub(string variant)
    {
        var state = variant switch
        {
            "other-tenant" => StateFor(tenant: Guid.NewGuid()),
            "other-subject" => StateFor(subject: Guid.NewGuid()),
            "unknown-target" => StateFor(target: "trio"),
            _ => "garbage",
        };

        await FluentActions.Awaiting(() => CreateService().CompleteLinkAsync("code", FakeGitHub.InstallationId, state, Subject, default))
            .Should().ThrowAsync<AppBuildException>().WithMessage(AppBuildException.InvalidState);
        _github.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_replayed_state_is_refused()
    {
        _github.OnUserFlow(FakeGitHub.InstallationId).OnInstallationRepositories(FakeGitHub.Repository);
        var state = StateFor();
        await CreateService().CompleteLinkAsync("code", FakeGitHub.InstallationId, state, Subject, default);

        await FluentActions.Awaiting(() => CreateService().CompleteLinkAsync("code-2", FakeGitHub.InstallationId, state, Subject, default))
            .Should().ThrowAsync<AppBuildException>().WithMessage(AppBuildException.InvalidState);
    }

    [Fact]
    public async Task A_ready_repository_reports_the_allowed_secrets_it_holds()
    {
        Linked();
        _github.OnRepository().OnSecrets("KEYSTORE_SET", "UNRELATED");

        var link = (await CreateService().StatusAsync(default)).Links.Single();

        link.State.Should().Be(AppBuildLinkState.Ready);
        link.Validation!.SecretsSet.Should().Equal("KEYSTORE_SET");
        link.Validation.MissingRequiredSecrets.Should().BeEmpty();
    }

    [Fact]
    public async Task A_repository_without_the_required_secret_reports_it_missing()
    {
        Linked();
        _github.OnRepository().OnSecrets();

        var link = (await CreateService().StatusAsync(default)).Links.Single();

        link.State.Should().Be(AppBuildLinkState.SecretsMissing);
        link.Validation!.MissingRequiredSecrets.Should().Equal("KEYSTORE_SET");
    }

    public static TheoryData<bool, bool, string?, AppBuildRepositoryProblem> InvalidRepositories => new()
    {
        { false, false, "nightscout/aaps-builder", AppBuildRepositoryProblem.NotPrivate },
        { true, true, "nightscout/aaps-builder", AppBuildRepositoryProblem.IsTemplate },
        { true, false, "someone/aaps-builder", AppBuildRepositoryProblem.WrongTemplate },
        { true, false, null, AppBuildRepositoryProblem.WrongTemplate },
    };

    [Theory]
    [MemberData(nameof(InvalidRepositories))]
    public async Task An_invalid_repository_gets_no_secret_and_no_dispatch(
        bool isPrivate, bool isTemplate, string? template, AppBuildRepositoryProblem problem)
    {
        Linked();
        _github.OnRepository(isPrivate, isTemplate, template).OnSecrets();
        var service = CreateService();

        (await service.StatusAsync(default)).Links.Single().Validation!.Problems.Should().Contain(problem);
        await FluentActions.Awaiting(() => service.PutSecretAsync("aaps", "KEYSTORE_SET", Sealed(), default))
            .Should().ThrowAsync<AppBuildException>().WithMessage(AppBuildException.RepositoryInvalid);
        await FluentActions.Awaiting(() => service.DispatchAsync("aaps", new DispatchAppBuildRequest(), default))
            .Should().ThrowAsync<AppBuildException>().WithMessage(AppBuildException.RepositoryInvalid);
        await FluentActions.Awaiting(() => service.GetSecretKeyAsync("aaps", default))
            .Should().ThrowAsync<AppBuildException>().WithMessage(AppBuildException.RepositoryInvalid);

        _github.Requests.Should().NotContain(r => r.Method == HttpMethod.Put || r.Path.EndsWith("/dispatches", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_secret_outside_the_targets_allow_list_is_refused_before_GitHub()
    {
        Linked();

        await FluentActions.Awaiting(() => CreateService().PutSecretAsync("aaps", "GDRIVE_OAUTH2", Sealed(), default))
            .Should().ThrowAsync<AppBuildException>().WithMessage(AppBuildException.SecretNotAllowed);
        _github.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_sealed_secret_is_relayed_unchanged()
    {
        Linked();
        _github.OnRepository().On(HttpMethod.Put, $"/repos/{FakeGitHub.Repository}/actions/secrets/KEYSTORE_SET",
            _ => new HttpResponseMessage(HttpStatusCode.Created));

        await CreateService().PutSecretAsync("aaps", "KEYSTORE_SET", Sealed(), default);

        var body = JsonDocument.Parse(_github.Requests.Single(r => r.Method == HttpMethod.Put).Body!).RootElement;
        body.GetProperty("encrypted_value").GetString().Should().Be(Sealed().EncryptedValue);
        body.GetProperty("key_id").GetString().Should().Be("568250167242549743");
    }

    [Fact]
    public async Task A_value_that_is_not_a_sealed_box_is_refused()
    {
        Linked();

        await FluentActions.Awaiting(() => CreateService().PutSecretAsync("aaps", "KEYSTORE_SET",
                new SealedAppBuildSecret { EncryptedValue = "plain text keystore", KeyId = "1" }, default))
            .Should().ThrowAsync<AppBuildException>().WithMessage(AppBuildException.InputInvalid);
        _github.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("variant", "fullRelease; rm -rf /")]
    [InlineData("variant", "someVariant")]
    [InlineData("version", "../../etc")]
    [InlineData("version", "")]
    [InlineData("branch", "main")]
    public async Task A_dispatch_input_the_target_does_not_allow_is_refused_before_GitHub(string name, string value)
    {
        Linked();

        await FluentActions.Awaiting(() => CreateService().DispatchAsync("aaps",
                new DispatchAppBuildRequest { Inputs = new() { [name] = value } }, default))
            .Should().ThrowAsync<AppBuildException>().WithMessage(AppBuildException.InputInvalid);
        _github.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_dispatch_fills_defaults_and_targets_the_default_branch()
    {
        Linked();
        _github.OnRepository().On(HttpMethod.Post, $"/repos/{FakeGitHub.Repository}/actions/workflows/build.yml/dispatches",
            _ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var result = await CreateService().DispatchAsync("aaps",
            new DispatchAppBuildRequest { Inputs = new() { ["version"] = "3.3.2.1" } }, default);

        result.RunsUrl.Should().Be("https://github.com/alice/my-aaps/actions/workflows/build.yml");
        var body = JsonDocument.Parse(_github.Requests.Single(r => r.Path.EndsWith("/dispatches", StringComparison.Ordinal)).Body!).RootElement;
        body.GetProperty("ref").GetString().Should().Be("main");
        body.GetProperty("inputs").GetProperty("version").GetString().Should().Be("3.3.2.1");
        body.GetProperty("inputs").GetProperty("variant").GetString().Should().Be("fullRelease");
    }

    [Fact]
    public async Task Choosing_a_repository_outside_the_installation_is_refused()
    {
        Linked(repository: null);
        _github.OnInstallationRepositories(FakeGitHub.Repository);

        await FluentActions.Awaiting(() => CreateService().ChooseRepositoryAsync("aaps", "mallory/other", default))
            .Should().ThrowAsync<AppBuildException>().WithMessage(AppBuildException.RepositoryNotInInstallation);
        await CreateService().ChooseRepositoryAsync("aaps", FakeGitHub.Repository, default);
        _links.Rows["aaps"].RepositoryFullName.Should().Be(FakeGitHub.Repository);
    }

    [Theory]
    [InlineData("completed", "success", AppBuildRunState.Succeeded)]
    [InlineData("completed", "failure", AppBuildRunState.Failed)]
    [InlineData("completed", "timed_out", AppBuildRunState.Failed)]
    [InlineData("completed", "cancelled", AppBuildRunState.Cancelled)]
    [InlineData("in_progress", null, AppBuildRunState.Running)]
    [InlineData("queued", null, AppBuildRunState.Queued)]
    [InlineData("waiting", null, AppBuildRunState.Queued)]
    public void A_run_state_is_derived_from_GitHubs_status_and_conclusion(string status, string? conclusion, AppBuildRunState expected) =>
        AppBuildService.RunState(status, conclusion).Should().Be(expected);

    [Fact]
    public async Task Unlinking_forgets_the_link_and_points_at_the_uninstall_page()
    {
        Linked();

        var result = await CreateService().UnlinkAsync("aaps", default);

        result.UninstallUrl.Should().Be($"https://github.com/settings/installations/{FakeGitHub.InstallationId}");
        _links.Rows.Should().BeEmpty();
        _github.Requests.Should().BeEmpty();
    }

    private static SealedAppBuildSecret Sealed() => new() { EncryptedValue = "c2VhbGVkLWJveA==", KeyId = "568250167242549743" };

    private sealed class InMemoryLinks : IAppBuildLinkRepository
    {
        public Dictionary<string, AppBuildLink> Rows { get; } = [];

        public Task<IReadOnlyList<AppBuildLink>> GetAllAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AppBuildLink>>(Rows.Values.ToList());

        public Task<AppBuildLink?> GetAsync(string targetId, CancellationToken ct) =>
            Task.FromResult(Rows.GetValueOrDefault(targetId));

        public Task SaveAsync(AppBuildLink link, CancellationToken ct)
        {
            Rows[link.TargetId] = link;
            return Task.CompletedTask;
        }

        public Task<bool> SetRepositoryAsync(string targetId, string repositoryFullName, CancellationToken ct)
        {
            if (!Rows.TryGetValue(targetId, out var link)) return Task.FromResult(false);
            Rows[targetId] = link with { RepositoryFullName = repositoryFullName };
            return Task.FromResult(true);
        }

        public Task<bool> DeleteAsync(string targetId, CancellationToken ct) => Task.FromResult(Rows.Remove(targetId));
    }

    private sealed class RecordingLogger
    {
        public List<string> Messages { get; } = [];

        public ILogger<T> For<T>() => new Logger<T>(Messages);

        private sealed class Logger<T>(List<string> messages) : ILogger<T>
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => messages.Add(formatter(state, exception));
        }
    }
}
