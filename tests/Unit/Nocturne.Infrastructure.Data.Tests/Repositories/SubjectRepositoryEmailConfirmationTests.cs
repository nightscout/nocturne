using FluentAssertions;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// Whether a stored address counts as confirmed: set only by a confirmation, cleared by every
/// write that changes the address.
/// </summary>
public class SubjectRepositoryEmailConfirmationTests : IDisposable
{
    private static readonly DateTime First = new(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = First.AddDays(3);

    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly SqliteTestDatabase _db;

    public SubjectRepositoryEmailConfirmationTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(_tenant);
    }

    public void Dispose() => _db.Dispose();

    private SubjectRepository Subjects() =>
        new(new TestNocturneDbContextFactory(_db.CreateContext(_tenant)));

    private async Task<Guid> ConfirmedSubjectAsync(string email = "a@example.test")
    {
        var id = await Subjects().CreateAsync(new NewSubject("Person", Email: email), CancellationToken.None);
        (await Subjects().ConfirmEmailAsync(id, email, First, CancellationToken.None))
            .Should().BeOfType<EmailConfirmation.Confirmed>();
        return id;
    }

    private async Task<SubjectRecord> GetAsync(Guid id) => (await Subjects().GetAsync(id, CancellationToken.None))!;

    [Fact]
    public async Task UpdateAsync_WithADifferentAddress_ClearsTheConfirmation()
    {
        var id = await ConfirmedSubjectAsync();

        await Subjects().UpdateAsync(id, new SubjectEdit(Email: "b@example.test"), CancellationToken.None);

        var record = await GetAsync(id);
        record.Email.Should().Be("b@example.test");
        record.EmailConfirmedAt.Should().BeNull();
    }

    [Fact]
    public async Task UpdateAsync_WithTheSameAddressInAnotherCase_KeepsTheConfirmation()
    {
        var id = await ConfirmedSubjectAsync();

        await Subjects().UpdateAsync(id, new SubjectEdit(Email: " A@Example.TEST "), CancellationToken.None);

        (await GetAsync(id)).EmailConfirmedAt.Should().Be(First);
    }

    [Fact]
    public async Task UpdateAsync_WithoutAnAddress_LeavesTheConfirmation()
    {
        var id = await ConfirmedSubjectAsync();

        await Subjects().UpdateAsync(id, new SubjectEdit(Name: "Renamed"), CancellationToken.None);

        (await GetAsync(id)).EmailConfirmedAt.Should().Be(First);
    }

    [Fact]
    public async Task UpdateProfileAsync_WithADifferentAddress_ClearsTheConfirmation()
    {
        var id = await ConfirmedSubjectAsync();

        await Subjects().UpdateProfileAsync(id, new SubjectProfile("Person", "b@example.test", null), CancellationToken.None);

        (await GetAsync(id)).EmailConfirmedAt.Should().BeNull();
    }

    [Fact]
    public async Task UpdateProfileAsync_WithTheSameAddressInAnotherCase_KeepsTheConfirmation()
    {
        var id = await ConfirmedSubjectAsync();

        await Subjects().UpdateProfileAsync(id, new SubjectProfile("Person", "A@EXAMPLE.test", null), CancellationToken.None);

        (await GetAsync(id)).EmailConfirmedAt.Should().Be(First);
    }

    [Fact]
    public async Task UpdateProfileAsync_WithNoAddress_ClearsTheConfirmation()
    {
        var id = await ConfirmedSubjectAsync();

        await Subjects().UpdateProfileAsync(id, new SubjectProfile("Person", null, null), CancellationToken.None);

        var record = await GetAsync(id);
        record.Email.Should().BeNull();
        record.EmailConfirmedAt.Should().BeNull();
    }

    [Fact]
    public void SetEmail_OnTheEntity_FollowsTheSameRule()
    {
        var entity = new SubjectEntity();
        entity.SetEmail("a@example.test");
        entity.ConfirmEmail("a@example.test", First).Should().BeTrue();

        entity.SetEmail("A@example.test");
        entity.EmailConfirmedAt.Should().Be(First);

        entity.SetEmail("b@example.test");
        entity.EmailConfirmedAt.Should().BeNull();
    }

    [Fact]
    public async Task ConfirmEmailAsync_ForTheAddressOnTheAccount_Confirms()
    {
        var id = await Subjects().CreateAsync(new NewSubject("Person", Email: "a@example.test"), CancellationToken.None);
        (await GetAsync(id)).EmailConfirmedAt.Should().BeNull();

        var result = await Subjects().ConfirmEmailAsync(id, "A@example.test ", First, CancellationToken.None);

        result.Should().Be(new EmailConfirmation.Confirmed(First));
        (await GetAsync(id)).EmailConfirmedAt.Should().Be(First);
    }

    [Fact]
    public async Task ConfirmEmailAsync_ForAnotherAddress_ConfirmsNothing()
    {
        var id = await Subjects().CreateAsync(new NewSubject("Person", Email: "a@example.test"), CancellationToken.None);

        var result = await Subjects().ConfirmEmailAsync(id, "b@example.test", First, CancellationToken.None);

        result.Should().BeOfType<EmailConfirmation.AddressMismatch>();
        (await GetAsync(id)).EmailConfirmedAt.Should().BeNull();
    }

    [Fact]
    public async Task ConfirmEmailAsync_ForAnAccountWithNoAddress_ConfirmsNothing()
    {
        var id = await Subjects().CreateAsync(new NewSubject("Person"), CancellationToken.None);

        var result = await Subjects().ConfirmEmailAsync(id, "a@example.test", First, CancellationToken.None);

        result.Should().BeOfType<EmailConfirmation.AddressMismatch>();
        (await GetAsync(id)).EmailConfirmedAt.Should().BeNull();
    }

    [Fact]
    public async Task ConfirmEmailAsync_ForAnUnknownSubject_ReportsNotFound()
    {
        var result = await Subjects().ConfirmEmailAsync(Guid.CreateVersion7(), "a@example.test", First, CancellationToken.None);

        result.Should().BeOfType<EmailConfirmation.NotFound>();
    }

    [Fact]
    public async Task ConfirmEmailAsync_ForAnAddressAlreadyConfirmed_KeepsTheFirstTime()
    {
        var id = await ConfirmedSubjectAsync();

        var result = await Subjects().ConfirmEmailAsync(id, "a@example.test", Later, CancellationToken.None);

        result.Should().Be(new EmailConfirmation.Confirmed(First));
        (await GetAsync(id)).EmailConfirmedAt.Should().Be(First);
    }

    [Fact]
    public async Task ConfirmEmailAsync_WithAProofForTheAddressTheAccountHasLeft_ConfirmsNothing()
    {
        var id = await ConfirmedSubjectAsync();
        await Subjects().UpdateAsync(id, new SubjectEdit(Email: "b@example.test"), CancellationToken.None);

        var result = await Subjects().ConfirmEmailAsync(id, "a@example.test", Later, CancellationToken.None);

        result.Should().BeOfType<EmailConfirmation.AddressMismatch>();
        (await GetAsync(id)).EmailConfirmedAt.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_WithoutAConfirmationTime_StoresTheAddressUnconfirmed()
    {
        var id = await Subjects().CreateAsync(new NewSubject("Person", Email: "a@example.test"), CancellationToken.None);

        (await GetAsync(id)).EmailConfirmedAt.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_WithAConfirmationTime_StoresTheAddressConfirmed()
    {
        var id = await Subjects().CreateAsync(
            new NewSubject("Person", Email: "a@example.test", EmailConfirmedAt: First), CancellationToken.None);

        (await GetAsync(id)).EmailConfirmedAt.Should().Be(First);
    }

    [Fact]
    public async Task CreateAsync_WithAConfirmationTimeButNoAddress_ConfirmsNothing()
    {
        var id = await Subjects().CreateAsync(new NewSubject("Person", EmailConfirmedAt: First), CancellationToken.None);

        (await GetAsync(id)).EmailConfirmedAt.Should().BeNull();
    }

    [Fact]
    public async Task UpdateAsync_AdoptingAnAddressWithAConfirmationTime_StoresItConfirmedInOneSave()
    {
        var id = await Subjects().CreateAsync(new NewSubject("Person", Email: "a@example.test"), CancellationToken.None);

        await Subjects().UpdateAsync(id, new SubjectEdit(Email: "b@example.test", EmailConfirmedAt: Later), CancellationToken.None);

        var record = await GetAsync(id);
        record.Email.Should().Be("b@example.test");
        record.EmailConfirmedAt.Should().Be(Later);
    }

    [Fact]
    public async Task UpdateAsync_ConfirmingAnAddressAlreadyConfirmed_KeepsTheFirstTime()
    {
        var id = await ConfirmedSubjectAsync();

        await Subjects().UpdateAsync(id, new SubjectEdit(Email: "a@example.test", EmailConfirmedAt: Later), CancellationToken.None);

        (await GetAsync(id)).EmailConfirmedAt.Should().Be(First);
    }

    private async Task<DbUnitOfWork> OpenUnitAsync() =>
        await DbUnitOfWork.OpenAsync(new TestNocturneDbContextFactory(_db.CreateContext(_tenant)));

    [Fact]
    public async Task UpdateAsync_WithAConfirmationTimeAndNoAddress_ConfirmsNothing()
    {
        var id = await Subjects().CreateAsync(new NewSubject("Person", Email: "a@example.test"), CancellationToken.None);

        await Subjects().UpdateAsync(id, new SubjectEdit(EmailConfirmedAt: Later), CancellationToken.None);

        (await GetAsync(id)).EmailConfirmedAt.Should().BeNull();
    }

    [Theory]
    [InlineData("a@example.test", "a@example.test", true)]
    [InlineData("a@example.test", "A@EXAMPLE.TEST", true)]
    [InlineData(" a@example.test ", "a@example.test", true)]
    [InlineData("a@example.test", "  a@example.test", true)]
    [InlineData("a@example.test", "b@example.test", false)]
    [InlineData("a@example.test", "a@example.test.au", false)]
    [InlineData(null, "a@example.test", false)]
    public async Task ConfirmEmailAsync_ConfirmsOnlyTheSameAddress(string? stored, string named, bool matches)
    {
        var id = await Subjects().CreateAsync(new NewSubject("Person", Email: stored), CancellationToken.None);

        var result = await Subjects().ConfirmEmailAsync(id, named, First, CancellationToken.None);

        (result is EmailConfirmation.Confirmed).Should().Be(matches);
        (await GetAsync(id)).EmailConfirmedAt.Should().Be(matches ? First : null);
    }

    [Theory]
    [InlineData("kate@example.test", "Kate@example.test")]
    [InlineData("Kate@example.test", "kate@example.test")]
    public async Task ConfirmEmailAsync_ForAnAddressThatOnlyFoldsToTheStoredOne_ConfirmsNothing(string stored, string named)
    {
        var id = await Subjects().CreateAsync(new NewSubject("Person", Email: stored), CancellationToken.None);

        var result = await Subjects().ConfirmEmailAsync(id, named, First, CancellationToken.None);

        result.Should().BeOfType<EmailConfirmation.AddressMismatch>();
        (await GetAsync(id)).EmailConfirmedAt.Should().BeNull();
    }

    [Fact]
    public async Task ConfirmEmailAsync_InAUnitOfWork_RefreshesTheCopyTheUnitHolds()
    {
        var id = await Subjects().CreateAsync(new NewSubject("Person", Email: "a@example.test"), CancellationToken.None);
        await using var unit = await OpenUnitAsync();
        var inUnit = new SubjectRepository(unit);
        await inUnit.UpdateAsync(id, new SubjectEdit(Name: "Renamed"), CancellationToken.None);

        await inUnit.ConfirmEmailAsync(id, "a@example.test", First, CancellationToken.None);
        unit.Context.Subjects.Local.Single().EmailConfirmedAt.Should().Be(First);
        unit.Context.ChangeTracker.Entries<SubjectEntity>().Single().Property(s => s.EmailConfirmedAt)
            .IsModified.Should().BeFalse();
        await unit.Context.SaveChangesAsync();

        var record = await GetAsync(id);
        record.Name.Should().Be("Renamed");
        record.EmailConfirmedAt.Should().Be(First);
    }

    [Fact]
    public async Task AnAddressChangeStagedBeforeAConfirmationCommits_EndsWithTheNewAddressUnconfirmed()
    {
        var id = await Subjects().CreateAsync(new NewSubject("Person", Email: "a@example.test"), CancellationToken.None);
        await using var admin = await OpenUnitAsync();
        await new SubjectRepository(admin).UpdateProfileAsync(
            id, new SubjectProfile("Person", "z@example.test", null), CancellationToken.None);

        (await Subjects().ConfirmEmailAsync(id, "a@example.test", First, CancellationToken.None))
            .Should().BeOfType<EmailConfirmation.Confirmed>();
        await admin.Context.SaveChangesAsync();

        var record = await GetAsync(id);
        record.Email.Should().Be("z@example.test");
        record.EmailConfirmedAt.Should().BeNull();
    }

    [Fact]
    public async Task AConfirmationForTheOldAddressAfterTheChangeCommits_IsRefused()
    {
        var id = await Subjects().CreateAsync(new NewSubject("Person", Email: "a@example.test"), CancellationToken.None);
        await Subjects().UpdateProfileAsync(id, new SubjectProfile("Person", "z@example.test", null), CancellationToken.None);

        (await Subjects().ConfirmEmailAsync(id, "a@example.test", First, CancellationToken.None))
            .Should().BeOfType<EmailConfirmation.AddressMismatch>();

        (await GetAsync(id)).EmailConfirmedAt.Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AProviderEditRacingAnAdministratorsChange_EndsAsAConsistentPair(bool providerSavesLast)
    {
        var id = await Subjects().CreateAsync(new NewSubject("Person", Email: "a@example.test"), CancellationToken.None);
        await using var provider = await OpenUnitAsync();
        await using var admin = await OpenUnitAsync();
        await new SubjectRepository(provider).UpdateAsync(
            id, new SubjectEdit(Email: "v@example.test", EmailConfirmedAt: Later), CancellationToken.None);
        await new SubjectRepository(admin).UpdateProfileAsync(
            id, new SubjectProfile("Person", "m@example.test", null), CancellationToken.None);

        var (first, last) = providerSavesLast ? (admin, provider) : (provider, admin);
        await first.Context.SaveChangesAsync();
        await last.Context.SaveChangesAsync();

        var record = await GetAsync(id);
        if (providerSavesLast)
        {
            record.Email.Should().Be("v@example.test");
            record.EmailConfirmedAt.Should().Be(Later);
        }
        else
        {
            record.Email.Should().Be("m@example.test");
            record.EmailConfirmedAt.Should().BeNull();
        }
    }
}
