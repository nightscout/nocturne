using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.Infrastructure.Data.Tests.Extensions;

/// <summary>
/// A share link runs as the Public system subject. A person who names themself "Public" is not it,
/// or their membership would be bounded to the share vocabulary.
/// </summary>
[Trait("Category", "Unit")]
public class PublicSubjectFilterTests
{
    [Theory]
    [InlineData("Public", true, true)]
    [InlineData("Public", false, false)]
    [InlineData("Devices", true, false)]
    [InlineData("public", true, false)]
    [InlineData(null, true, false)]
    public void Only_the_system_subject_with_the_name_is_the_public_subject(string? name, bool system, bool expected)
    {
        MemberNamed(name, system).IsPublicSubject().Should().Be(expected);
    }

    private static TenantMemberRecord MemberNamed(string? name, bool system) =>
        new(
            Id: Guid.CreateVersion7(),
            TenantId: Guid.CreateVersion7(),
            SubjectId: Guid.CreateVersion7(),
            SubjectName: name,
            SubjectUsername: null,
            SubjectIsActive: true,
            IsSystemSubject: system,
            IsDemoSubject: false,
            IsPlatformAdmin: false,
            Roles: [],
            DirectPermissions: null,
            Label: null,
            Username: null,
            LimitTo24Hours: false,
            LastUsedAt: null,
            CreatedFromInviteId: null,
            SysCreatedAt: DateTime.UtcNow);
}
