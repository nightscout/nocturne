using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.ClientDevices;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.ClientDevices;
using Nocturne.Infrastructure.Data;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.ClientDevices;

[Trait("Category", "Unit")]
public class ClientDeviceServiceTests
{
    private static readonly IReadOnlySet<string> FullDeviceScopes =
        new HashSet<string> { OAuthScopes.DeviceNotify, OAuthScopes.DeviceActuate };

    private static ClientDeviceService CreateService(NocturneDbContext ctx)
        => new(ctx, NullLogger<ClientDeviceService>.Instance);

    private static NocturneDbContext CreateContext()
    {
        var ctx = TestDbContextFactory.CreateInMemoryContext();
        ctx.TenantId = Guid.NewGuid();
        return ctx;
    }

    private static RegisterDeviceRequest Req(string installId, string kind, string? label = null) => new()
    {
        InstallId = installId,
        Kind = kind,
        Label = label,
        Capabilities = [DeviceCapabilities.Notify],
    };

    [Fact]
    public async Task RegisterAsync_inserts_new_device_with_filtered_capabilities()
    {
        using var ctx = CreateContext();
        var svc = CreateService(ctx);

        var dto = await svc.RegisterAsync(Guid.NewGuid(), new RegisterDeviceRequest
        {
            InstallId = "install-1",
            Kind = DeviceKinds.Companion,
            Label = "Desk PC",
            Capabilities =
            [
                DeviceCapabilities.Notify,
                DeviceCapabilities.TrayFlash,
                DeviceCapabilities.Torch, // Prelude-only -> dropped
                "bogus",                  // unknown -> dropped
            ],
        }, FullDeviceScopes);

        dto.Kind.Should().Be(DeviceKinds.Companion);
        dto.Label.Should().Be("Desk PC");
        dto.Capabilities.Should().BeEquivalentTo([DeviceCapabilities.Notify, DeviceCapabilities.TrayFlash]);
        ctx.ClientDevices.Should().HaveCount(1);
    }

    [Fact]
    public async Task RegisterAsync_is_idempotent_on_install_id()
    {
        using var ctx = CreateContext();
        var subject = Guid.NewGuid();
        var svc = CreateService(ctx);

        var first = await svc.RegisterAsync(subject, Req("install-x", DeviceKinds.Prelude, "Old"), FullDeviceScopes);
        var second = await svc.RegisterAsync(subject, Req("install-x", DeviceKinds.Prelude, "New label"), FullDeviceScopes);

        ctx.ClientDevices.Should().HaveCount(1);
        second.Id.Should().Be(first.Id);
        second.Label.Should().Be("New label");
    }

    [Fact]
    public async Task RegisterAsync_drops_hardware_capabilities_without_actuate_scope()
    {
        using var ctx = CreateContext();
        var svc = CreateService(ctx);
        var notifyOnly = new HashSet<string> { OAuthScopes.DeviceNotify };

        var dto = await svc.RegisterAsync(Guid.NewGuid(), new RegisterDeviceRequest
        {
            InstallId = "p1",
            Kind = DeviceKinds.Prelude,
            Capabilities = [DeviceCapabilities.Notify, DeviceCapabilities.Torch, DeviceCapabilities.Vibrate],
        }, notifyOnly);

        dto.Capabilities.Should().Equal(DeviceCapabilities.Notify);
    }

    [Fact]
    public async Task RegisterAsync_rejects_unknown_kind()
    {
        using var ctx = CreateContext();
        var svc = CreateService(ctx);

        var act = () => svc.RegisterAsync(Guid.NewGuid(), Req("i", "smartfridge"), FullDeviceScopes);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RegisterAsync_rejects_missing_install_id()
    {
        using var ctx = CreateContext();
        var svc = CreateService(ctx);

        var act = () => svc.RegisterAsync(Guid.NewGuid(), Req("   ", DeviceKinds.Prelude), FullDeviceScopes);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task GetForSubjectAsync_returns_only_callers_devices()
    {
        using var ctx = CreateContext();
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        var svc = CreateService(ctx);

        await svc.RegisterAsync(me, Req("a", DeviceKinds.Prelude), FullDeviceScopes);
        await svc.RegisterAsync(other, Req("b", DeviceKinds.Companion), FullDeviceScopes);

        var mine = await svc.GetForSubjectAsync(me);

        mine.Should().ContainSingle().Which.InstallId.Should().Be("a");
    }

    [Fact]
    public async Task RegisterAsync_rejects_cross_subject_takeover()
    {
        using var ctx = CreateContext();
        var owner = Guid.NewGuid();
        var attacker = Guid.NewGuid();
        var svc = CreateService(ctx);

        await svc.RegisterAsync(owner, Req("shared-install", DeviceKinds.Prelude, "Owner"), FullDeviceScopes);

        var act = () => svc.RegisterAsync(attacker, Req("shared-install", DeviceKinds.Prelude, "Hijack"), FullDeviceScopes);

        await act.Should().ThrowAsync<InvalidOperationException>();
        ctx.ClientDevices.Should().ContainSingle().Which.SubjectId.Should().Be(owner);
    }

    [Fact]
    public async Task RegisterAsync_isolates_install_id_across_tenants()
    {
        using var ctx = TestDbContextFactory.CreateInMemoryContext();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var svc = CreateService(ctx);

        ctx.TenantId = tenantA;
        await svc.RegisterAsync(Guid.NewGuid(), Req("shared", DeviceKinds.Prelude), FullDeviceScopes);

        ctx.TenantId = tenantB;
        await svc.RegisterAsync(Guid.NewGuid(), Req("shared", DeviceKinds.Prelude), FullDeviceScopes);

        var all = ctx.ClientDevices.IgnoreQueryFilters().ToList();
        all.Should().HaveCount(2);
        all.Select(d => d.TenantId).Should().BeEquivalentTo([tenantA, tenantB]);
    }
}
