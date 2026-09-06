using FluentAssertions;
using Nocturne.API.Services.Identity;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// Guards the owner-subject precedence rule in <see cref="TenantService.ProvisionWithOwnerAsync"/>.
///
/// Regression: a user who signed up with an OAuth account whose email differed from the
/// checkout email (an Apple Pay email) got a tenant whose owner was a brand-new,
/// credential-less subject, while their OAuth identity stayed attached to a different
/// subject — locking them out of all three login methods. The fix resolves the owner by
/// existing OIDC identity before the owner email; <see cref="TenantService.ChooseOwnerSubject"/>
/// is that decision.
/// </summary>
[Trait("Category", "Unit")]
public class TenantServiceProvisionOwnerTests
{
    private static SubjectEntity Subject(string email) => new()
    {
        Id = Guid.CreateVersion7(),
        Name = email,
        Email = email,
        IsActive = true,
        ApprovalStatus = "Approved",
    };

    [Fact]
    public void ChooseOwnerSubject_prefersOidcIdentitySubject_overDifferingEmailMatch()
    {
        // The person's real subject (owns the OAuth identity, OAuth email).
        var oidcSubject = Subject("owner@gmail.com");
        // A different subject that merely matches the checkout email (e.g. Apple Pay).
        var emailSubject = Subject("owner@mac.com");

        TenantService.ChooseOwnerSubject(oidcSubject, emailSubject)
            .Should().BeSameAs(oidcSubject);
    }

    [Fact]
    public void ChooseOwnerSubject_fallsBackToEmailMatch_whenNoExistingIdentity()
    {
        var emailSubject = Subject("owner@mac.com");

        TenantService.ChooseOwnerSubject(null, emailSubject)
            .Should().BeSameAs(emailSubject);
    }

    [Fact]
    public void ChooseOwnerSubject_returnsNull_whenNeitherExists_soCallerCreatesFresh()
    {
        TenantService.ChooseOwnerSubject(null, null).Should().BeNull();
    }
}
