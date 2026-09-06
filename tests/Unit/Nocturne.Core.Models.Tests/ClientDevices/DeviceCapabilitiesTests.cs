using FluentAssertions;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.ClientDevices;
using Xunit;

namespace Nocturne.Core.Models.Tests.ClientDevices;

[Trait("Category", "Unit")]
public class DeviceCapabilitiesTests
{
    private static readonly IReadOnlySet<string> NotifyAndActuate =
        new HashSet<string> { Scope.DeviceNotify, Scope.DeviceActuate };

    [Fact]
    public void Accept_keeps_known_kind_allowed_and_scoped_capabilities()
    {
        var result = DeviceCapabilities.Accept(
            DeviceKinds.Companion,
            [DeviceCapabilities.Notify, DeviceCapabilities.TrayFlash],
            NotifyAndActuate);

        result.Should().BeEquivalentTo([DeviceCapabilities.Notify, DeviceCapabilities.TrayFlash]);
    }

    [Fact]
    public void Accept_drops_capabilities_not_allowed_for_kind()
    {
        // Torch is a Prelude-only capability; a Companion advertising it is dropped.
        var result = DeviceCapabilities.Accept(
            DeviceKinds.Companion,
            [DeviceCapabilities.Notify, DeviceCapabilities.Torch],
            NotifyAndActuate);

        result.Should().Equal(DeviceCapabilities.Notify);
    }

    [Fact]
    public void Accept_drops_unknown_capabilities()
    {
        var result = DeviceCapabilities.Accept(
            DeviceKinds.Prelude,
            [DeviceCapabilities.Notify, "laser"],
            NotifyAndActuate);

        result.Should().Equal(DeviceCapabilities.Notify);
    }

    [Fact]
    public void Accept_drops_hardware_capabilities_without_actuate_scope()
    {
        var notifyOnly = new HashSet<string> { Scope.DeviceNotify };

        var result = DeviceCapabilities.Accept(
            DeviceKinds.Prelude,
            [DeviceCapabilities.Notify, DeviceCapabilities.Torch, DeviceCapabilities.Vibrate],
            notifyOnly);

        result.Should().Equal(DeviceCapabilities.Notify);
    }

    [Fact]
    public void Accept_full_access_scope_satisfies_all()
    {
        var star = new HashSet<string> { Scope.FullAccess };

        var result = DeviceCapabilities.Accept(
            DeviceKinds.Prelude,
            [DeviceCapabilities.Notify, DeviceCapabilities.Torch],
            star);

        result.Should().BeEquivalentTo([DeviceCapabilities.Notify, DeviceCapabilities.Torch]);
    }

    [Fact]
    public void Accept_dedups_and_preserves_order()
    {
        var result = DeviceCapabilities.Accept(
            DeviceKinds.Prelude,
            [DeviceCapabilities.Torch, DeviceCapabilities.Notify, DeviceCapabilities.Torch],
            NotifyAndActuate);

        result.Should().Equal(DeviceCapabilities.Torch, DeviceCapabilities.Notify);
    }

    [Theory]
    [InlineData(DeviceKinds.Prelude, true)]
    [InlineData(DeviceKinds.Companion, false)]
    public void HasLocalEngine_matches_design(string kind, bool expected)
    {
        DeviceKinds.HasLocalEngine(kind).Should().Be(expected);
    }

    [Fact]
    public void BuildCatalog_orders_push_mode_kinds_before_local_engine_kinds()
    {
        // The rule builder seeds a new channel from Kinds[0]; that default must be a kind the
        // server can push intents to, not a local-engine kind it suppresses at runtime.
        var catalog = DeviceCapabilities.BuildCatalog();

        catalog.Kinds.Should().Equal(DeviceKinds.Companion, DeviceKinds.Prelude);
    }

    [Fact]
    public void BuildCatalog_lists_all_kinds()
    {
        var catalog = DeviceCapabilities.BuildCatalog();

        catalog.Kinds.Should().BeEquivalentTo(DeviceKinds.All);
    }
}
