using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Services.AppBuilds;

namespace Nocturne.API.Tests.Services.AppBuilds;

[Trait("Category", "Unit")]
public class AppBuildInstallStateTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Subject = Guid.NewGuid();

    private readonly EphemeralDataProtectionProvider _protection = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    private AppBuildInstallState CreateState() => new(_protection, new MemoryCache(new MemoryCacheOptions()), _time);

    [Fact]
    public void A_state_reads_back_its_tenant_subject_slug_and_target()
    {
        var state = CreateState();

        var payload = state.TryRead(state.Protect(Tenant, "alice", Subject, "aaps"));

        payload.Should().NotBeNull();
        payload!.Tenant.Should().Be(Tenant);
        payload.Subject.Should().Be(Subject);
        payload.Slug.Should().Be("alice");
        payload.Target.Should().Be("aaps");
        state.TryReadTenantSlug(state.Protect(Tenant, "alice", Subject, "aaps")).Should().Be("alice");
    }

    [Fact]
    public void A_tampered_state_is_refused()
    {
        var state = CreateState();
        var issued = state.Protect(Tenant, "alice", Subject, "aaps");
        var tampered = issued[..^4] + (issued[^4] == 'A' ? 'B' : 'A') + issued[^3..];

        state.TryRead(tampered).Should().BeNull();
        state.TryRead("not-a-state").Should().BeNull();
        state.TryRead(null).Should().BeNull();
    }

    [Fact]
    public void A_state_from_another_purpose_is_refused()
    {
        var other = _protection.CreateProtector("Nocturne.GoogleHealth.OAuthState").ToTimeLimitedDataProtector()
            .Protect("{\"Tenant\":\"" + Tenant + "\"}", DateTimeOffset.UtcNow.AddMinutes(5));

        CreateState().TryRead(other).Should().BeNull();
    }

    [Fact]
    public void An_expired_state_is_refused()
    {
        var state = CreateState();
        var issued = state.Protect(Tenant, "alice", Subject, "aaps");

        _time.Advance(AppBuildInstallState.Lifetime + TimeSpan.FromSeconds(1));

        state.TryRead(issued).Should().BeNull();
    }

    [Fact]
    public void A_state_is_consumed_once()
    {
        var state = CreateState();
        var payload = state.TryRead(state.Protect(Tenant, "alice", Subject, "aaps"))!;

        state.TryConsume(payload).Should().BeTrue();
        state.TryConsume(payload).Should().BeFalse();
    }
}
