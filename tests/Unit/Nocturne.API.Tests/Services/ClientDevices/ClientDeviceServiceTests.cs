using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.ClientDevices;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.ClientDevices;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
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

    private static void SeedDeviceActionExcursion(
        NocturneDbContext ctx,
        string targetKind,
        string metadataJson,
        bool open = true,
        bool acknowledged = false,
        DateTime? endedAt = null)
    {
        var rule = new AlertRuleEntity
        {
            Id = Guid.NewGuid(),
            Name = "Urgent Low",
            Severity = AlertRuleSeverity.Critical,
            IsEnabled = true,
        };
        rule.Channels.Add(new AlertRuleChannelEntity
        {
            Id = Guid.NewGuid(),
            ChannelType = ChannelType.DeviceAction,
            Destination = targetKind,
            Metadata = metadataJson,
        });
        ctx.AlertRules.Add(rule);
        ctx.AlertExcursions.Add(new AlertExcursionEntity
        {
            Id = Guid.NewGuid(),
            AlertRuleId = rule.Id,
            StartedAt = DateTime.UtcNow,
            EndedAt = endedAt ?? (open ? null : DateTime.UtcNow),
            AcknowledgedAt = acknowledged ? DateTime.UtcNow : null,
        });
    }

    [Fact]
    public async Task GetActiveIntentsAsync_returns_intent_with_capabilities_narrowed_to_device()
    {
        using var ctx = CreateContext();
        var subject = Guid.NewGuid();
        var svc = CreateService(ctx);
        var device = await svc.RegisterAsync(subject, new RegisterDeviceRequest
        {
            InstallId = "c1",
            Kind = DeviceKinds.Companion,
            Capabilities = [DeviceCapabilities.Notify, DeviceCapabilities.TrayFlash],
        }, FullDeviceScopes);
        SeedDeviceActionExcursion(ctx, DeviceKinds.Companion, "{\"capabilities\":[\"notify\",\"tray_flash\",\"torch\"]}");
        await ctx.SaveChangesAsync();

        var intents = await svc.GetActiveIntentsAsync(device.Id, subject);

        intents.Should().ContainSingle();
        intents[0].RuleName.Should().Be("Urgent Low");
        intents[0].Severity.Should().Be(AlertRuleSeverity.Critical);
        // torch dropped — the device only has notify + tray_flash
        intents[0].Capabilities.Should().BeEquivalentTo(["notify", "tray_flash"]);
    }

    [Fact]
    public async Task GetActiveIntentsAsync_empty_for_local_engine_device()
    {
        using var ctx = CreateContext();
        var subject = Guid.NewGuid();
        var svc = CreateService(ctx);
        var device = await svc.RegisterAsync(subject, new RegisterDeviceRequest
        {
            InstallId = "p1",
            Kind = DeviceKinds.Prelude,
            Capabilities = [DeviceCapabilities.Notify],
        }, FullDeviceScopes);
        SeedDeviceActionExcursion(ctx, DeviceKinds.Prelude, "{\"capabilities\":[\"notify\"]}");
        await ctx.SaveChangesAsync();

        var intents = await svc.GetActiveIntentsAsync(device.Id, subject);

        intents.Should().BeEmpty();
    }

    [Fact]
    public async Task GetActiveIntentsAsync_empty_when_device_not_owned_by_caller()
    {
        using var ctx = CreateContext();
        var svc = CreateService(ctx);
        var device = await svc.RegisterAsync(Guid.NewGuid(), new RegisterDeviceRequest
        {
            InstallId = "c1",
            Kind = DeviceKinds.Companion,
            Capabilities = [DeviceCapabilities.Notify],
        }, FullDeviceScopes);
        SeedDeviceActionExcursion(ctx, DeviceKinds.Companion, "{\"capabilities\":[\"notify\"]}");
        await ctx.SaveChangesAsync();

        var intents = await svc.GetActiveIntentsAsync(device.Id, Guid.NewGuid());

        intents.Should().BeEmpty();
    }

    [Fact]
    public async Task GetActiveIntentsAsync_excludes_resolved_excursions()
    {
        using var ctx = CreateContext();
        var subject = Guid.NewGuid();
        var svc = CreateService(ctx);
        var device = await svc.RegisterAsync(subject, new RegisterDeviceRequest
        {
            InstallId = "c1",
            Kind = DeviceKinds.Companion,
            Capabilities = [DeviceCapabilities.Notify],
        }, FullDeviceScopes);
        SeedDeviceActionExcursion(ctx, DeviceKinds.Companion, "{\"capabilities\":[\"notify\"]}", open: false);
        await ctx.SaveChangesAsync();

        var intents = await svc.GetActiveIntentsAsync(device.Id, subject);

        intents.Should().BeEmpty();
    }

    [Fact]
    public async Task GetActiveIntentsAsync_includes_test_fire_excursion_within_window()
    {
        // A device_action test fire ends its excursion in the near future
        // (AlertDeliveryService.DeviceActionTestFireWindow) so it surfaces here until the
        // window passes.
        using var ctx = CreateContext();
        var subject = Guid.NewGuid();
        var svc = CreateService(ctx);
        var device = await svc.RegisterAsync(subject, new RegisterDeviceRequest
        {
            InstallId = "c1",
            Kind = DeviceKinds.Companion,
            Capabilities = [DeviceCapabilities.Notify],
        }, FullDeviceScopes);
        SeedDeviceActionExcursion(ctx, DeviceKinds.Companion, "{\"capabilities\":[\"notify\"]}",
            endedAt: DateTime.UtcNow.AddSeconds(90));
        await ctx.SaveChangesAsync();

        var intents = await svc.GetActiveIntentsAsync(device.Id, subject);

        intents.Should().ContainSingle();
        intents[0].Capabilities.Should().Equal("notify");
    }

    [Fact]
    public async Task GetActiveIntentsAsync_excludes_test_fire_excursion_after_window()
    {
        using var ctx = CreateContext();
        var subject = Guid.NewGuid();
        var svc = CreateService(ctx);
        var device = await svc.RegisterAsync(subject, new RegisterDeviceRequest
        {
            InstallId = "c1",
            Kind = DeviceKinds.Companion,
            Capabilities = [DeviceCapabilities.Notify],
        }, FullDeviceScopes);
        SeedDeviceActionExcursion(ctx, DeviceKinds.Companion, "{\"capabilities\":[\"notify\"]}",
            endedAt: DateTime.UtcNow.AddSeconds(-1));
        await ctx.SaveChangesAsync();

        var intents = await svc.GetActiveIntentsAsync(device.Id, subject);

        intents.Should().BeEmpty();
    }

    [Fact]
    public async Task GetActiveIntentsAsync_marks_acknowledged_excursion()
    {
        using var ctx = CreateContext();
        var subject = Guid.NewGuid();
        var svc = CreateService(ctx);
        var device = await svc.RegisterAsync(subject, new RegisterDeviceRequest
        {
            InstallId = "c1",
            Kind = DeviceKinds.Companion,
            Capabilities = [DeviceCapabilities.Notify],
        }, FullDeviceScopes);
        SeedDeviceActionExcursion(ctx, DeviceKinds.Companion, "{\"capabilities\":[\"notify\"]}", acknowledged: true);
        await ctx.SaveChangesAsync();

        var intents = await svc.GetActiveIntentsAsync(device.Id, subject);

        intents.Should().ContainSingle();
        intents[0].Acknowledged.Should().BeTrue();
        intents[0].Intent.Should().Be("acknowledged");
    }

    [Fact]
    public async Task GetActiveIntentsAsync_tolerates_malformed_channel_metadata()
    {
        using var ctx = CreateContext();
        var subject = Guid.NewGuid();
        var svc = CreateService(ctx);
        var device = await svc.RegisterAsync(subject, new RegisterDeviceRequest
        {
            InstallId = "c1",
            Kind = DeviceKinds.Companion,
            Capabilities = [DeviceCapabilities.Notify],
        }, FullDeviceScopes);
        SeedDeviceActionExcursion(ctx, DeviceKinds.Companion, "not valid json");
        await ctx.SaveChangesAsync();

        var intents = await svc.GetActiveIntentsAsync(device.Id, subject);

        intents.Should().ContainSingle();
        intents[0].Capabilities.Should().BeEmpty();
    }

    [Fact]
    public async Task GetActiveIntentsAsync_isolates_excursions_across_tenants()
    {
        using var ctx = TestDbContextFactory.CreateInMemoryContext();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var subject = Guid.NewGuid();
        var svc = CreateService(ctx);

        ctx.TenantId = tenantA;
        var device = await svc.RegisterAsync(subject, new RegisterDeviceRequest
        {
            InstallId = "c1",
            Kind = DeviceKinds.Companion,
            Capabilities = [DeviceCapabilities.Notify],
        }, FullDeviceScopes);

        // Seed an open excursion under a DIFFERENT tenant.
        ctx.TenantId = tenantB;
        SeedDeviceActionExcursion(ctx, DeviceKinds.Companion, "{\"capabilities\":[\"notify\"]}");
        await ctx.SaveChangesAsync();

        // Query as the device's tenant — tenant B's excursion must be invisible.
        ctx.TenantId = tenantA;
        var intents = await svc.GetActiveIntentsAsync(device.Id, subject);

        intents.Should().BeEmpty();
    }

    [Fact]
    public async Task RenameAsync_updates_label_for_owner()
    {
        using var ctx = CreateContext();
        var subject = Guid.NewGuid();
        var svc = CreateService(ctx);
        var device = await svc.RegisterAsync(subject, Req("c1", DeviceKinds.Companion, "Old"), FullDeviceScopes);

        var updated = await svc.RenameAsync(device.Id, subject, "New");

        updated.Should().NotBeNull();
        updated!.Label.Should().Be("New");
    }

    [Fact]
    public async Task RenameAsync_returns_null_for_non_owner()
    {
        using var ctx = CreateContext();
        var svc = CreateService(ctx);
        var device = await svc.RegisterAsync(Guid.NewGuid(), Req("c1", DeviceKinds.Companion), FullDeviceScopes);

        var updated = await svc.RenameAsync(device.Id, Guid.NewGuid(), "Hijack");

        updated.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_removes_owned_device()
    {
        using var ctx = CreateContext();
        var subject = Guid.NewGuid();
        var svc = CreateService(ctx);
        var device = await svc.RegisterAsync(subject, Req("c1", DeviceKinds.Companion), FullDeviceScopes);

        var removed = await svc.DeleteAsync(device.Id, subject);

        removed.Should().BeTrue();
        ctx.ClientDevices.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteAsync_refuses_non_owner()
    {
        using var ctx = CreateContext();
        var svc = CreateService(ctx);
        var device = await svc.RegisterAsync(Guid.NewGuid(), Req("c1", DeviceKinds.Companion), FullDeviceScopes);

        var removed = await svc.DeleteAsync(device.Id, Guid.NewGuid());

        removed.Should().BeFalse();
        ctx.ClientDevices.Should().ContainSingle();
    }
}
