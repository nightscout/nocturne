using FluentAssertions;
using Nocturne.Connectors.Tandem.Models;
using Nocturne.Connectors.Tandem.Services;
using Xunit;

namespace Nocturne.Connectors.Tandem.Tests.Services;

public class TandemDeviceChooserTests
{
    private static TandemBffPump Device(string id, string serial, string? maxDateOfEvents) =>
        new() { AssignmentId = id, SerialNumber = serial, MaxDateOfEvents = maxDateOfEvents };

    [Fact]
    public void Returns_null_when_no_devices()
    {
        TandemConnectorService.ChooseDevice([], null).Should().BeNull();
    }

    [Fact]
    public void Selects_most_recent_pump_when_no_serial_configured()
    {
        var older = Device("a", "111", "2024-01-01T00:00:00");
        var newer = Device("b", "222", "2024-06-01T00:00:00");

        TandemConnectorService.ChooseDevice([older, newer], serialNumber: null)
            .Should().BeSameAs(newer);
    }

    [Fact]
    public void Selects_configured_serial_even_if_not_most_recent()
    {
        var older = Device("a", "111", "2024-01-01T00:00:00");
        var newer = Device("b", "222", "2024-06-01T00:00:00");

        TandemConnectorService.ChooseDevice([older, newer], serialNumber: "111")
            .Should().BeSameAs(older);
    }

    [Fact]
    public void Ignores_placeholder_serial()
    {
        var older = Device("a", "111", "2024-01-01T00:00:00");
        var newer = Device("b", "222", "2024-06-01T00:00:00");

        // "11111111" is tconnectsync's sentinel meaning "no serial chosen".
        TandemConnectorService.ChooseDevice([older, newer], serialNumber: "11111111")
            .Should().BeSameAs(newer);
    }

    [Fact]
    public void Skips_pumps_that_have_never_uploaded()
    {
        var neverUploaded = Device("a", "111", maxDateOfEvents: null);
        var uploaded = Device("b", "222", "2024-01-01T00:00:00");

        TandemConnectorService.ChooseDevice([neverUploaded, uploaded], serialNumber: null)
            .Should().BeSameAs(uploaded);
    }

    [Fact]
    public void Falls_back_to_first_pump_when_none_has_events()
    {
        var first = Device("a", "111", maxDateOfEvents: null);
        var second = Device("b", "222", maxDateOfEvents: null);

        TandemConnectorService.ChooseDevice([first, second], serialNumber: null)
            .Should().BeSameAs(first);
    }
}
