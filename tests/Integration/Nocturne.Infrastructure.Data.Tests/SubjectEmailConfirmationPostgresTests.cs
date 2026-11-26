using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Tests.Rls;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// An account's email confirmation against real Postgres, under the runtime role: the column
/// round-trips, and a confirmation racing an address change never outlives the address it was for.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class SubjectEmailConfirmationPostgresTests
{
    private static readonly DateTime First = new(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly RlsCompletenessFixture _fx;

    public SubjectEmailConfirmationPostgresTests(RlsCompletenessFixture fx) => _fx = fx;

    [Fact]
    public async Task The_confirmation_round_trips_and_every_answer_is_the_time_the_row_holds()
    {
        await using var provider = TestContexts.BuildProvider(_fx.AppConnectionString);
        var id = await SeedAsync(provider, "a@example.test");
        await using var db = await ContextAsync(provider);
        var repository = new SubjectRepository(DbUnitOfWork.Own(db), AccountLinkRoutes.Off);
        var withTicks = First.AddTicks(1234567);

        var first = (await repository.ConfirmEmailAsync(id, " A@Example.TEST", withTicks, CancellationToken.None))
            .Should().BeOfType<EmailConfirmation.Confirmed>().Subject;
        var second = (await repository.ConfirmEmailAsync(id, "a@example.test", First.AddDays(2), CancellationToken.None))
            .Should().BeOfType<EmailConfirmation.Confirmed>().Subject;

        var stored = (await ReadAsync(provider, id)).EmailConfirmedAt;
        stored.Should().NotBeNull();
        first.At.Should().Be(stored!.Value);
        second.At.Should().Be(first.At);
    }

    [Theory]
    [InlineData("kate@example.test", "Kate@example.test")]
    [InlineData("Kate@example.test", "kate@example.test")]
    public async Task A_confirmation_for_an_address_that_only_folds_to_the_stored_one_confirms_nothing(string stored, string named)
    {
        await using var provider = TestContexts.BuildProvider(_fx.AppConnectionString);
        var id = await SeedAsync(provider, stored);
        await using var db = await ContextAsync(provider);
        var repository = new SubjectRepository(DbUnitOfWork.Own(db), AccountLinkRoutes.Off);

        (await repository.ConfirmEmailAsync(id, named, First, CancellationToken.None))
            .Should().BeOfType<EmailConfirmation.AddressMismatch>();

        (await ReadAsync(provider, id)).EmailConfirmedAt.Should().BeNull();
    }

    [Fact]
    public async Task A_confirmation_refuses_an_address_the_account_does_not_hold()
    {
        await using var provider = TestContexts.BuildProvider(_fx.AppConnectionString);
        var id = await SeedAsync(provider, "a@example.test");
        await using var db = await ContextAsync(provider);
        var repository = new SubjectRepository(DbUnitOfWork.Own(db), AccountLinkRoutes.Off);

        (await repository.ConfirmEmailAsync(id, "b@example.test", First, CancellationToken.None))
            .Should().BeOfType<EmailConfirmation.AddressMismatch>();

        (await ReadAsync(provider, id)).EmailConfirmedAt.Should().BeNull();
    }

    [Fact]
    public async Task An_address_change_staged_before_a_confirmation_commits_ends_unconfirmed()
    {
        await using var provider = TestContexts.BuildProvider(_fx.AppConnectionString);
        var id = await SeedAsync(provider, "a@example.test");
        await using var admin = await ContextAsync(provider);
        await using var confirming = await ContextAsync(provider);
        await new SubjectRepository(DbUnitOfWork.Own(admin), AccountLinkRoutes.Off).UpdateProfileAsync(
            id, new SubjectProfile("Person", "z@example.test", null), CancellationToken.None);

        (await new SubjectRepository(DbUnitOfWork.Own(confirming), AccountLinkRoutes.Off).ConfirmEmailAsync(
                id, "a@example.test", First, CancellationToken.None))
            .Should().BeOfType<EmailConfirmation.Confirmed>();
        await admin.SaveChangesAsync();

        var row = await ReadAsync(provider, id);
        row.Email.Should().Be("z@example.test");
        row.EmailConfirmedAt.Should().BeNull();
    }

    private static async Task<NocturneDbContext> ContextAsync(ServiceProvider provider)
    {
        var context = await provider.GetRequiredService<IDbContextFactory<NocturneDbContext>>().CreateDbContextAsync();
        context.TenantId = Guid.NewGuid();
        return context;
    }

    private static async Task<Guid> SeedAsync(ServiceProvider provider, string email)
    {
        await using var db = await ContextAsync(provider);
        var subject = new SubjectEntity { Id = Guid.CreateVersion7(), Name = "Person" };
        subject.SetEmail(email);
        db.Subjects.Add(subject);
        await db.SaveChangesAsync();
        return subject.Id;
    }

    private static async Task<SubjectEntity> ReadAsync(ServiceProvider provider, Guid id)
    {
        await using var db = await ContextAsync(provider);
        return await db.Subjects.AsNoTracking().SingleAsync(s => s.Id == id);
    }
}
