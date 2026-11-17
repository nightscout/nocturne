using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// The three credential stores over SQLite: <see cref="PasskeyCredentialRepository"/>,
/// <see cref="TotpCredentialRepository"/> and <see cref="RecoveryCodeRepository"/>, each a
/// subject's sign-in material of one kind, and the single-use claims the TOTP step and the
/// recovery code are consumed by.
/// </summary>
public class CredentialRepositoryTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 31, 9, 0, 0, DateTimeKind.Utc);

    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly Guid _holder = Guid.CreateVersion7();
    private readonly Guid _stranger = Guid.CreateVersion7();
    private readonly Guid _system = Guid.CreateVersion7();
    private readonly SqliteTestDatabase _db;

    public CredentialRepositoryTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(_tenant);
        using var db = _db.CreateContext();
        db.Subjects.Add(new SubjectEntity { Id = _holder, Name = "Holder", Username = "holder", Email = "h@x.example" });
        db.Subjects.Add(new SubjectEntity { Id = _stranger, Name = "Stranger" });
        db.Subjects.Add(new SubjectEntity { Id = _system, Name = "Public", IsSystemSubject = true });
        db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private PasskeyCredentialRepository Passkeys() => new(new TestNocturneDbContextFactory(_db.CreateContext()));

    private TotpCredentialRepository Totp() => new(new TestNocturneDbContextFactory(_db.CreateContext()));

    private RecoveryCodeRepository Codes() => new(new TestNocturneDbContextFactory(_db.CreateContext()));

    private static NewPasskeyCredential Passkey(Guid subject, byte[] credentialId, string? label = null, DateTime? createdAt = null) =>
        new(subject, credentialId, [9, 9], SignCount: 3, ["internal"], label, null, createdAt ?? Now);

    [Fact]
    public async Task APasskeyIsStoredFoundByItsAuthenticatorIdAndListedNewestFirst()
    {
        var older = await Passkeys().CreateAsync(Passkey(_holder, [1, 2], "Laptop", Now.AddDays(-1)), CancellationToken.None);
        var newer = await Passkeys().CreateAsync(Passkey(_holder, [3, 4], "Phone"), CancellationToken.None);
        await Passkeys().CreateAsync(Passkey(_stranger, [5, 6]), CancellationToken.None);

        var stored = await Passkeys().FindByCredentialIdAsync([1, 2], CancellationToken.None);
        stored!.Credential.Should().BeEquivalentTo(new { Id = older, SubjectId = _holder, Label = "Laptop", SignCount = 3u });
        stored.Credential.Transports.Should().Equal("internal");
        stored.Holder.Should().BeEquivalentTo(new { Id = _holder, Username = "holder", Name = "Holder" });
        (await Passkeys().FindByCredentialIdAsync([7], CancellationToken.None)).Should().BeNull();
        (await Passkeys().ExistsAsync([3, 4], CancellationToken.None)).Should().BeTrue();
        (await Passkeys().ExistsAsync([7], CancellationToken.None)).Should().BeFalse();
        (await Passkeys().CountAsync(_holder, CancellationToken.None)).Should().Be(2);
        (await Passkeys().GetCredentialIdsAsync(_holder, CancellationToken.None)).Should().BeEquivalentTo([new byte[] { 1, 2 }, new byte[] { 3, 4 }]);
        (await Passkeys().ListAsync(_holder, CancellationToken.None)).Select(c => c.Id).Should().Equal(newer, older);
    }

    [Fact]
    public async Task AnAssertionIsRecordedAndAPasskeyIsRemovedByItsHolderOnly()
    {
        var id = await Passkeys().CreateAsync(Passkey(_holder, [1]), CancellationToken.None);

        (await Passkeys().RecordUseAsync(id, 7, Now, CancellationToken.None)).Should().BeTrue();
        (await Passkeys().RecordUseAsync(Guid.CreateVersion7(), 7, Now, CancellationToken.None)).Should().BeFalse();
        var used = (await Passkeys().ListAsync(_holder, CancellationToken.None)).Single();
        used.SignCount.Should().Be(7);
        used.LastUsedAt.Should().Be(Now);

        (await Passkeys().RemoveAsync(id, _stranger, CancellationToken.None)).Should().BeFalse();
        (await Passkeys().RemoveAsync(id, _holder, CancellationToken.None)).Should().BeTrue();
        (await Passkeys().CountAsync(_holder, CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task TheHoldersListGroupsPasskeysBySubjectAndLeavesOutSystemAndInactiveOnes()
    {
        var inactive = Guid.CreateVersion7();
        await using (var db = _db.CreateContext())
        {
            db.Subjects.Add(new SubjectEntity { Id = inactive, Name = "Inactive", IsActive = false });
            await db.SaveChangesAsync();
        }
        await Passkeys().CreateAsync(Passkey(_holder, [1]), CancellationToken.None);
        await Passkeys().CreateAsync(Passkey(_holder, [2]), CancellationToken.None);
        await Passkeys().CreateAsync(Passkey(_system, [3]), CancellationToken.None);
        await Passkeys().CreateAsync(Passkey(inactive, [4]), CancellationToken.None);

        var holders = await Passkeys().ListHoldersAsync(CancellationToken.None);
        holders.Should().ContainSingle();
        holders[0].Subject.Id.Should().Be(_holder);
        holders[0].Credentials.Should().HaveCount(2);
    }

    [Fact]
    public async Task ATotpSecretIsReadBackInClearOnlyWhereVerificationNeedsIt()
    {
        var secret = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var id = await Totp().CreateAsync(new NewTotpCredential(_holder, secret, "App", Now, 100, Now), CancellationToken.None);
        var second = await Totp().CreateAsync(new NewTotpCredential(_holder, [9], null, Now.AddMinutes(1), null, null), CancellationToken.None);
        await Totp().CreateAsync(new NewTotpCredential(_stranger, [8], null, Now, null, null), CancellationToken.None);

        var listed = await Totp().ListAsync(_holder, CancellationToken.None);
        listed.Select(c => c.Id).Should().Equal(second, id);
        listed[1].Should().Be(new TotpCredentialSummary(id, "App", Now, Now));

        var secrets = await Totp().ListSecretsAsync(_holder, CancellationToken.None);
        secrets.Should().HaveCount(2);
        secrets.Single(s => s.Id == id).Should().BeEquivalentTo(new { SecretKey = secret, LastUsedStep = 100L });
        (await Totp().CountAsync(_holder, CancellationToken.None)).Should().Be(2);

        await using var check = _db.CreateContext();
        var raw = await check.Database.SqlQueryRaw<byte[]>("SELECT secret_key AS \"Value\" FROM totp_credentials WHERE id = {0}", id).SingleAsync();
        raw.Should().NotEqual(secret, "the column holds the protected payload");
    }

    [Fact]
    public async Task ATotpStepIsConsumedOnceAndACredentialIsRemovedWithoutReadingItsSecret()
    {
        var id = await Totp().CreateAsync(new NewTotpCredential(_holder, [1, 2, 3], null, Now, 100, Now), CancellationToken.None);

        (await Totp().TryConsumeStepAsync(id, 100, Now, CancellationToken.None)).Should().BeFalse("the setup step is already spent");
        (await Totp().TryConsumeStepAsync(id, 101, Now.AddSeconds(30), CancellationToken.None)).Should().BeTrue();
        (await Totp().TryConsumeStepAsync(id, 101, Now.AddSeconds(40), CancellationToken.None)).Should().BeFalse("a step is taken once");
        (await Totp().ListSecretsAsync(_holder, CancellationToken.None)).Single().LastUsedStep.Should().Be(101);
        (await Totp().ListAsync(_holder, CancellationToken.None)).Single().LastUsedAt.Should().Be(Now.AddSeconds(30));

        await using (var db = _db.CreateContext())
        {
            // A column the converter cannot decrypt: removal must still succeed.
            await db.Database.ExecuteSqlRawAsync("UPDATE totp_credentials SET secret_key = X'00' WHERE id = {0}", id);
        }

        (await Totp().RemoveAsync(id, _stranger, CancellationToken.None)).Should().BeFalse();
        (await Totp().RemoveAsync(id, _holder, CancellationToken.None)).Should().BeTrue();
        (await Totp().CountAsync(_holder, CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task RecoveryCodesAreReplacedAsASetConsumedOnceAndInvalidatedTogether()
    {
        await Codes().ReplaceAsync(_holder, ["h1", "h2", "h3"], Now, CancellationToken.None);
        await Codes().ReplaceAsync(_stranger, ["s1"], Now, CancellationToken.None);
        var first = await Codes().GetLiveAsync(_holder, CancellationToken.None);
        first.Select(c => c.CodeHash).Should().BeEquivalentTo("h1", "h2", "h3");
        first.Select(c => c.Id).Should().BeInAscendingOrder("live codes come back in one fixed order");

        (await Codes().TryConsumeAsync(first[0].Id, Now, CancellationToken.None)).Should().BeTrue();
        (await Codes().TryConsumeAsync(first[0].Id, Now, CancellationToken.None)).Should().BeFalse("a code is spent once");
        (await Codes().CountLiveAsync(_holder, CancellationToken.None)).Should().Be(2);
        (await Codes().HasCodesAsync(_holder, CancellationToken.None)).Should().BeTrue();
        (await Codes().AnyInvalidatedAsync(_holder, CancellationToken.None)).Should().BeFalse();

        (await Codes().InvalidateLiveAsync(_holder, Now.AddHours(1), CancellationToken.None)).Should().Be(2);
        (await Codes().CountLiveAsync(_holder, CancellationToken.None)).Should().Be(0);
        (await Codes().GetLiveAsync(_holder, CancellationToken.None)).Should().BeEmpty();
        (await Codes().AnyInvalidatedAsync(_holder, CancellationToken.None)).Should().BeTrue();
        (await Codes().HasCodesAsync(_holder, CancellationToken.None)).Should().BeTrue("the used code was not invalidated");
        (await Codes().TryConsumeAsync(first[1].Id, Now, CancellationToken.None)).Should().BeFalse("an invalidated code is not live");
        (await Codes().CountLiveAsync(_stranger, CancellationToken.None)).Should().Be(1, "another subject's codes are untouched");

        await Codes().ReplaceAsync(_holder, ["n1"], Now.AddHours(2), CancellationToken.None);
        (await Codes().AnyInvalidatedAsync(_holder, CancellationToken.None)).Should().BeFalse("a replacement drops every old code, live or not");
        (await Codes().GetLiveAsync(_holder, CancellationToken.None)).Select(c => c.CodeHash).Should().Equal("n1");
        (await Codes().GetLiveAsync(Guid.Empty, CancellationToken.None)).Should().BeEmpty("an unknown subject holds no codes");
    }
}
