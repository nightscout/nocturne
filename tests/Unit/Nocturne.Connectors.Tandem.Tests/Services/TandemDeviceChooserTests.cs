using FluentAssertions;
using Nocturne.Connectors.Tandem.Models;
using Nocturne.Connectors.Tandem.Services;
using Xunit;

namespace Nocturne.Connectors.Tandem.Tests.Services;

public class TandemDeviceChooserTests
{
    private static TandemPumpEventMetadata Device(string id, string serial, DateTimeOffset maxDate) =>
        new() { TconnectDeviceId = id, SerialNumber = serial, MaxDateWithEvents = maxDate };

    [Fact]
    public void Returns_null_when_no_devices()
    {
        TandemConnectorService.ChooseDevice([], null).Should().BeNull();
    }

    [Fact]
    public void Selects_most_recent_pump_when_no_serial_configured()
    {
        var older = Device("a", "111", new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var newer = Device("b", "222", new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero));

        TandemConnectorService.ChooseDevice([older, newer], serialNumber: null)
            .Should().BeSameAs(newer);
    }

    [Fact]
    public void Selects_configured_serial_even_if_not_most_recent()
    {
        var older = Device("a", "111", new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var newer = Device("b", "222", new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero));

        TandemConnectorService.ChooseDevice([older, newer], serialNumber: "111")
            .Should().BeSameAs(older);
    }

    [Fact]
    public void Ignores_placeholder_serial()
    {
        var older = Device("a", "111", new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var newer = Device("b", "222", new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero));

        // "11111111" is tconnectsync's sentinel meaning "no serial chosen".
        TandemConnectorService.ChooseDevice([older, newer], serialNumber: "11111111")
            .Should().BeSameAs(newer);
    }
}
