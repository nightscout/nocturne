using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// What the member list reads of a member's recovery email: whether it is confirmed, from the
/// subject row the list already joins, and never the address.
/// </summary>
public class TenantMembershipRecoveryEmailTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 31, 9, 0, 0, DateTimeKind.Utc);

    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly StatementRecorder _statements = new();
    private readonly SqliteTestDatabase _db;

    public TenantMembershipRecoveryEmailTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(_tenant, "home", _statements);
    }

    public void Dispose() => _db.Dispose();

    private TenantMembershipRepository Members() =>
        new(new TestNocturneDbContextFactory(_db.CreateContext(_tenant)), AccountLinkRoutes.Off);

    private SubjectRepository Subjects() =>
        new(new TestNocturneDbContextFactory(_db.CreateContext(Guid.Empty)), AccountLinkRoutes.Off);

    private async Task JoinAsync(string name, string? email = null, DateTime? confirmedAt = null)
    {
        var subject = await Subjects().CreateAsync(
            new NewSubject(name, Email: email, EmailConfirmedAt: confirmedAt), CancellationToken.None);
        (await Members().AddMemberAsync(new NewTenantMember(_tenant, subject, []) { How = MembershipJoinHow.Direct, By = null }, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AMemberHasAConfirmedEmailOnlyWhenTheAddressWasConfirmed()
    {
        await JoinAsync("confirmed", "a@example.test", Now);
        await JoinAsync("unconfirmed", "b@example.test");
        await JoinAsync("none");

        var members = (await Members().ListMembersAsync(_tenant, CancellationToken.None))
            .ToDictionary(m => m.SubjectName!, m => m.HasConfirmedEmail);

        members.Should().Equal(new Dictionary<string, bool>
        {
            ["confirmed"] = true,
            ["unconfirmed"] = false,
            ["none"] = false,
        });
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TheListCarriesNoAddress()
    {
        await JoinAsync("someone", "private.person@example.test", Now);

        var json = JsonSerializer.Serialize(await Members().ListMembersAsync(_tenant, CancellationToken.None));

        json.Should().NotContain("private.person").And.NotContain("@");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TheListStaysOneStatementWhateverTheMemberCount()
    {
        await JoinAsync("first", "a@example.test", Now);
        _statements.Clear();
        await Members().ListMembersAsync(_tenant, CancellationToken.None);
        _statements.All.Should().ContainSingle("the list is one statement");

        for (var i = 0; i < 6; i++)
            await JoinAsync($"member-{i}", $"m{i}@example.test", i % 2 == 0 ? Now : null);
        _statements.Clear();
        await Members().ListMembersAsync(_tenant, CancellationToken.None);

        _statements.All.Should().ContainSingle("a member's flag rides on the subject row the list joins");
    }
}
