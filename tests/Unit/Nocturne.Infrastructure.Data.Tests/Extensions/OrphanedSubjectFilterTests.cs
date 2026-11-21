using Microsoft.EntityFrameworkCore;
using Moq;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Extensions;

/// <summary>
/// This set decides recovery mode, the members an administrator is shown and who a recovery code
/// may be issued for, so a subject it names wrongly can take a site offline and one it misses leaves
/// somebody locked out with no way back in.
/// </summary>
[Trait("Category", "Unit")]
public class OrphanedSubjectFilterTests : IDisposable
{
    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly Guid _otherTenant = Guid.CreateVersion7();
    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;

    public OrphanedSubjectFilterTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(_tenant, "site").SeedTenant(_otherTenant, "other");
        _context = _db.CreateContext(_tenant);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private Guid SeedMember(
        string name,
        Guid? tenantId = null,
        bool isActive = true,
        bool isSystemSubject = false,
        bool isDemoSubject = false,
        bool withPasskey = false,
        bool withOidc = false,
        bool oidcProviderEnabled = true,
        Email email = Email.None)
    {
        var subjectId = Guid.CreateVersion7();
        var subject = new SubjectEntity
        {
            Id = subjectId,
            Name = name,
            IsActive = isActive,
            IsSystemSubject = isSystemSubject,
            IsDemoSubject = isDemoSubject,
            ApprovalStatus = "Approved",
        };
        if (email != Email.None)
            subject.SetEmail($"{name.Replace(' ', '-').ToLowerInvariant()}@example.test");
        if (email == Email.Confirmed)
            subject.ConfirmEmail(subject.Email, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));
        _context.Subjects.Add(subject);
        _context.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId ?? _tenant,
            SubjectId = subjectId,
        });

        if (withPasskey)
        {
            _context.PasskeyCredentials.Add(new PasskeyCredentialEntity
            {
                Id = Guid.CreateVersion7(),
                SubjectId = subjectId,
                CredentialId = [1],
                PublicKey = [2],
                SignCount = 0,
            });
        }

        if (withOidc)
        {
            var providerId = Guid.CreateVersion7();
            _context.OidcProviders.Add(new OidcProviderEntity
            {
                Id = providerId,
                Name = "Test provider",
                IssuerUrl = "https://idp.invalid",
                ClientId = "client",
                IsEnabled = oidcProviderEnabled,
            });
            _context.SubjectOidcIdentities.Add(new SubjectOidcIdentityEntity
            {
                Id = Guid.CreateVersion7(),
                SubjectId = subjectId,
                ProviderId = providerId,
                Issuer = "https://idp.invalid",
                OidcSubjectId = $"ext-{subjectId:N}",
            });
        }

        _context.SaveChanges();
        return subjectId;
    }

    public enum Factor { Passkey, ProviderIdentity, Neither }

    public enum Email { Confirmed, Unconfirmed, None }

    private static IAccountLinkRoute Route(bool configured) =>
        Mock.Of<IAccountLinkRoute>(r => r.IsConfigured == configured);

    private async Task<List<string>> OrphansAsync(bool delivery = false) =>
        await _context.OrphanedSubjectsOf(_tenant, Route(delivery)).Select(s => s.Name).ToListAsync();

    [Theory]
    [InlineData(Factor.Passkey, Email.Confirmed, true, false)]
    [InlineData(Factor.Passkey, Email.Confirmed, false, false)]
    [InlineData(Factor.Passkey, Email.Unconfirmed, true, false)]
    [InlineData(Factor.Passkey, Email.Unconfirmed, false, false)]
    [InlineData(Factor.Passkey, Email.None, true, false)]
    [InlineData(Factor.Passkey, Email.None, false, false)]
    [InlineData(Factor.ProviderIdentity, Email.Confirmed, true, false)]
    [InlineData(Factor.ProviderIdentity, Email.Confirmed, false, false)]
    [InlineData(Factor.ProviderIdentity, Email.Unconfirmed, true, false)]
    [InlineData(Factor.ProviderIdentity, Email.Unconfirmed, false, false)]
    [InlineData(Factor.ProviderIdentity, Email.None, true, false)]
    [InlineData(Factor.ProviderIdentity, Email.None, false, false)]
    // A recovery link reaches a confirmed address only while there is a route to deliver it.
    [InlineData(Factor.Neither, Email.Confirmed, true, false)]
    [InlineData(Factor.Neither, Email.Confirmed, false, true)]
    [InlineData(Factor.Neither, Email.Unconfirmed, true, true)]
    [InlineData(Factor.Neither, Email.Unconfirmed, false, true)]
    [InlineData(Factor.Neither, Email.None, true, true)]
    [InlineData(Factor.Neither, Email.None, false, true)]
    public async Task An_account_cannot_sign_in_when_it_has_no_factor_and_no_deliverable_confirmed_address(
        Factor factor, Email email, bool delivery, bool cannotSignIn)
    {
        SeedMember(
            "Member",
            withPasskey: factor == Factor.Passkey,
            withOidc: factor == Factor.ProviderIdentity,
            email: email);

        (await OrphansAsync(delivery)).Should().HaveCount(cannotSignIn ? 1 : 0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_predicate_leaves_to_email_exactly_the_accounts_a_recovery_link_would_be_sent_to(bool delivery)
    {
        var ids = Enum.GetValues<Email>().ToDictionary(e => SeedMember(e.ToString(), email: e), e => e.ToString());

        var sentTo = new List<string>();
        var lookup = new SubjectRepository(new TestNocturneDbContextFactory(_db.CreateContext(_tenant)), AccountLinkRoutes.Off);
        foreach (var subject in _context.Subjects.AsNoTracking().Where(s => s.Email != null).ToList())
        {
            var recipients = await lookup.FindMembersByConfirmedEmailAsync(_tenant, subject.Email!.ToUpperInvariant(), CancellationToken.None);
            sentTo.AddRange(recipients.Select(r => ids[r.Id]));
        }

        var stuck = await OrphansAsync(delivery);
        ids.Values.Except(stuck).Should().BeEquivalentTo(delivery ? sentTo : []);
        sentTo.Should().NotBeEmpty("the table holds an account a link would reach");
    }

    [Fact]
    public async Task A_member_with_no_passkey_and_no_provider_is_locked_out()
    {
        SeedMember("Stranded");

        (await OrphansAsync()).Should().ContainSingle().Which.Should().Be("Stranded");
    }

    [Fact]
    public async Task A_demo_visitor_is_not_locked_out()
    {
        // Credential-less by design and standing for nobody. TenantSetupMiddleware bypasses demo
        // tenants before it ever asks, so reporting it here is only harmless until somebody asks
        // without that bypass in front of them.
        SeedMember("Demo Visitor", isDemoSubject: true);

        (await OrphansAsync()).Should().BeEmpty();
    }

    [Theory]
    // The Public subject a share link runs as: a system row, not a person.
    [InlineData(true, false, false, false)]
    // Deactivated, so not somebody waiting to get back in.
    [InlineData(false, true, false, false)]
    // Can sign in, by either factor.
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public async Task A_subject_who_is_not_a_locked_out_person_is_not_reported(
        bool isSystemSubject, bool deactivated, bool withPasskey, bool withOidc)
    {
        SeedMember(
            "Not an orphan",
            isActive: !deactivated,
            isSystemSubject: isSystemSubject,
            withPasskey: withPasskey,
            withOidc: withOidc);

        (await OrphansAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_provider_identity_on_a_disabled_provider_does_not_count_as_a_way_in()
    {
        SeedMember("Locked out", withOidc: true, oidcProviderEnabled: false);

        (await OrphansAsync()).Should().ContainSingle().Which.Should().Be("Locked out");
    }

    [Fact]
    public async Task Another_tenants_locked_out_member_does_not_brick_this_one()
    {
        SeedMember("Elsewhere", tenantId: _otherTenant);

        (await OrphansAsync()).Should().BeEmpty();
    }
}
