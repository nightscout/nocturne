using FluentAssertions;
using Nocturne.Core.Models.AppBuilds;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class AppBuildLinkRepositoryTests : IDisposable
{
    private static readonly Guid Subject = Guid.NewGuid();

    private readonly NocturneDbContext _context;
    private readonly AppBuildLinkRepository _repository;

    public AppBuildLinkRepositoryTests()
    {
        _context = TestDbContextFactory.CreateInMemoryContext();
        _context.TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        _repository = new AppBuildLinkRepository(new TestNocturneDbContextFactory(_context));
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private static AppBuildLink Link(long installation = 42, string? repository = null) =>
        new("aaps", installation, "alice", false, repository, Subject, default, default);

    [Fact]
    public async Task A_saved_link_reads_back_with_its_tenant_and_timestamps()
    {
        await _repository.SaveAsync(Link(repository: "alice/my-aaps"), default);

        var stored = (await _repository.GetAsync("aaps", default))!;
        stored.InstallationId.Should().Be(42);
        stored.RepositoryFullName.Should().Be("alice/my-aaps");
        stored.LinkedBySubjectId.Should().Be(Subject);
        stored.CreatedAt.Should().NotBe(default);
        _context.AppBuildLinks.Single().TenantId.Should().Be(_context.TenantId);
    }

    [Fact]
    public async Task Saving_again_replaces_the_targets_link()
    {
        await _repository.SaveAsync(Link(42, "alice/my-aaps"), default);
        await _repository.SaveAsync(Link(43), default);

        var all = await _repository.GetAllAsync(default);
        all.Should().ContainSingle().Which.Should().Match<AppBuildLink>(l => l.InstallationId == 43 && l.RepositoryFullName == null);
    }

    [Fact]
    public async Task A_repository_is_set_and_a_link_deleted_only_when_it_exists()
    {
        (await _repository.SetRepositoryAsync("aaps", "alice/my-aaps", default)).Should().BeFalse();
        (await _repository.DeleteAsync("aaps", default)).Should().BeFalse();

        await _repository.SaveAsync(Link(), default);

        (await _repository.SetRepositoryAsync("aaps", "alice/my-aaps", default)).Should().BeTrue();
        (await _repository.GetAsync("aaps", default))!.RepositoryFullName.Should().Be("alice/my-aaps");
        (await _repository.DeleteAsync("aaps", default)).Should().BeTrue();
        (await _repository.GetAllAsync(default)).Should().BeEmpty();
    }
}
