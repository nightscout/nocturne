using FluentAssertions;
using Moq;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

[Trait("Category", "Unit")]
public class TenantMemberServiceTests
{
    private readonly Guid _subject = Guid.NewGuid();
    private readonly Guid _tenant = Guid.NewGuid();

    private static readonly TenantMembershipFacts Membership = new(
        Guid.NewGuid(), new TenantMemberAccess(new HashSet<string> { "glucose.read" }, false, null), null, null);

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task AnInactiveSubject_ReadsAsNoMember(bool isActive, bool readsMembership)
    {
        var facts = await Service(new SubjectMembershipRead(false, isActive, Membership))
            .GetMembershipFactsAsync(_subject, _tenant, CancellationToken.None);

        (facts is not null).Should().Be(readsMembership);
    }

    [Fact]
    public async Task ASubjectWithNoMembership_ReadsAsNoMember()
    {
        (await Service(new SubjectMembershipRead(false, true, null))
            .GetMembershipFactsAsync(_subject, _tenant, CancellationToken.None)).Should().BeNull();
        (await Service(null)
            .GetMembershipFactsAsync(_subject, _tenant, CancellationToken.None)).Should().BeNull();
    }

    private TenantMemberService Service(SubjectMembershipRead? read)
    {
        var members = new Mock<ITenantMembershipRepository>();
        members.Setup(m => m.ReadSubjectMembershipAsync(_subject, _tenant, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(read);
        return new TenantMemberService(members.Object, TimeProvider.System);
    }
}
